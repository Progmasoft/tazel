/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.tazel.dsl.plugin.PluginBuildStep
import com.progmasoft.tazel.dsl.plugin.PluginPreamble
import com.progmasoft.tazel.dsl.plugin.PluginRegistry
import com.progmasoft.tazel.dsl.plugin.PluginResolver
import com.progmasoft.tazel.dsl.plugin.ResolvedPlugin
import com.progmasoft.tazel.dsl.plugin.ViGetPlugins
import java.io.File
import java.net.URLClassLoader
import java.nio.file.Files
import java.nio.file.Path
import kotlin.script.experimental.api.CompiledScript
import kotlin.script.experimental.api.ResultValue
import kotlin.script.experimental.api.ResultWithDiagnostics
import kotlin.script.experimental.api.ScriptCompilationConfiguration
import kotlin.script.experimental.api.ScriptDiagnostic
import kotlin.script.experimental.api.ScriptEvaluationConfiguration
import kotlin.script.experimental.api.compilerOptions
import kotlin.script.experimental.api.defaultImports
import kotlin.script.experimental.host.toScriptSource
import kotlin.script.experimental.jvm.baseClassLoader
import kotlin.script.experimental.jvm.dependenciesFromClassContext
import kotlin.script.experimental.jvm.jvm
import kotlin.script.experimental.jvm.updateClasspath
import kotlin.script.experimental.jvmhost.BasicJvmScriptingHost

private const val MINIMUM_JAVA = 25

/**
 * The names a project file can have, in the order they are looked for. The first one that exists is
 * the project file; the others are not read. All of them hold the same Kotlin script.
 */
val PROJECT_FILE_NAMES = listOf("BUILD.kts", "BUILD.tzl", "BUILD.tazel", "BUILD", "Tazel.build")

/** A project file and the directory it describes. */
data class ProjectLocation(
  val root: Path,
  val file: Path,
)

/**
 * A project file after its script ran, with the steps its plugins added to the build.
 *
 * The classes of those steps come from the JARs of the plugins, which stay open while the project
 * is in use; [close] releases them.
 */
data class LoadedProject(
  val location: ProjectLocation,
  val plan: ProjectPlan,
  val buildSteps: List<PluginBuildStep> = emptyList(),
  private val pluginLoader: AutoCloseable? = null,
) : AutoCloseable {
  override fun close() {
    pluginLoader?.close()
  }
}

internal fun isSupportedJavaFeature(actual: Int) = actual >= MINIMUM_JAVA

internal fun requireSupportedJava() {
  val actual = Runtime.version().feature()
  if (!isSupportedJavaFeature(actual)) {
    throw ProjectConfigurationException("JRE 25 or newer is required; found Java $actual")
  }
}

/** The project file of [directory] itself, when it has one. */
fun projectFileIn(directory: Path): ProjectLocation? {
  val root = directory.toAbsolutePath().normalize()
  // Windows and macOS would find "build.kts" for "BUILD.kts"; the names are case-sensitive, so
  // the directory is listed and its entries are compared as they are spelled.
  val names =
    try {
      Files.newDirectoryStream(root).use { entries ->
        entries.filter(Files::isRegularFile).map { it.fileName.toString() }.toSet()
      }
    } catch (_: java.io.IOException) {
      return null
    }
  val name = PROJECT_FILE_NAMES.firstOrNull(names::contains) ?: return null
  return ProjectLocation(root, root.resolve(name))
}

/** The project that [start] belongs to: its own, or the nearest one of a directory above it. */
fun discoverProject(start: Path): ProjectLocation {
  var directory: Path? = start.toAbsolutePath().normalize()
  while (directory != null) {
    projectFileIn(directory)?.let {
      return it
    }
    directory = directory.parent
  }
  throw ProjectConfigurationException(
    "no project file found from ${start.toAbsolutePath().normalize()}; " +
      "a project has one of ${PROJECT_FILE_NAMES.joinToString()}"
  )
}

/**
 * Runs project files. The script of a project is compiled and run inside Tazel itself, so a machine
 * needs no Kotlin installation beside it.
 *
 * With a [cache], a project file whose compiled form is already on disk is not compiled again: see
 * [scriptCacheKey] for what decides that. [timings] receives the duration of each phase.
 */
class ProjectLoader(
  private val compilerVersion: String = "unknown",
  private val cache: ScriptCache? = null,
  private val timings: Timings = Timings.disabled,
  private val tazelVersion: String = "development",
  /**
   * Whether a project keeps the JARs of its plugins open for the steps they added to its build. A
   * command that does not build reads the steps only to know the project builds something.
   */
  private val keepBuildSteps: Boolean = false,
  /** Where plugins missing from the plugin cache of a project are fetched from. */
  private val registry: PluginRegistry? = ViGetPlugins(),
  /**
   * The environment of the command when it is not the one of this JVM, which is the case in the
   * daemon. `System.getenv` in a project file reads the JVM's, so a difference stops the load.
   */
  private val commandEnvironment: Map<String, String>? = null,
) {
  private val host by lazy { timings.measure(Phase.SCRIPT_HOST) { BasicJvmScriptingHost() } }

  fun load(location: ProjectLocation): LoadedProject {
    requireSupportedJava()
    val source =
      try {
        Files.readString(location.file)
      } catch (error: java.io.IOException) {
        throw ProjectConfigurationException(
          "${location.file} could not be read as UTF-8 text: ${error.message}"
        )
      }
    commandEnvironment?.let { requireSameEnvironment(source, it) }
    val plugins =
      timings.measure(Phase.PLUGINS) {
        PluginResolver.resolve(location.root, PluginPreamble.parse(source), registry)
      }
    val pluginLoader =
      URLClassLoader(
        plugins.map { it.artifact.toUri().toURL() }.toTypedArray(),
        ProjectLoader::class.java.classLoader,
      )
    // Each evaluation fills its own project, so project files can be evaluated on several threads.
    val context = ProjectContext(compilerVersion)
    return ProjectRuntime.evaluate(context) {
      var loaded: LoadedProject? = null
      try {
        val implementations =
          timings.measure(Phase.PLUGINS) { plugins.map { instantiate(it, pluginLoader) } }
        context.plugins.activate(plugins, implementations, location.root, context.host)
        run(location, source, plugins, pluginLoader)
        val steps = context.plugins.buildSteps
        // The loader stays open only when a build step will still need classes of the plugins.
        val keep = keepBuildSteps && steps.isNotEmpty()
        LoadedProject(location, context.build(), steps, pluginLoader.takeIf { keep }).also {
          if (keep) loaded = it
        }
      } finally {
        if (loaded == null) pluginLoader.close()
      }
    }
  }

  /**
   * The instance of [plugin]: the class its service entry names, made with its public constructor.
   */
  private fun instantiate(
    plugin: ResolvedPlugin,
    loader: ClassLoader,
  ): ProjectPlugin {
    val type =
      try {
        Class.forName(plugin.pluginClass, true, loader)
      } catch (_: ClassNotFoundException) {
        throw ProjectConfigurationException(
          "plugin '${plugin.coordinate}' names ${plugin.pluginClass}, which its JAR does not hold"
        )
      }
    if (!ProjectPlugin::class.java.isAssignableFrom(type)) {
      throw ProjectConfigurationException(
        "plugin '${plugin.coordinate}': ${plugin.pluginClass} does not implement ProjectPlugin"
      )
    }
    return try {
      type.getConstructor().newInstance() as ProjectPlugin
    } catch (_: NoSuchMethodException) {
      throw ProjectConfigurationException(
        "plugin '${plugin.coordinate}': ${plugin.pluginClass} needs a public constructor without parameters"
      )
    } catch (error: ReflectiveOperationException) {
      throw ProjectConfigurationException(
        "plugin '${plugin.coordinate}': ${plugin.pluginClass} could not be created: " +
          (error.cause?.message ?: error.message ?: error.javaClass.name)
      )
    }
  }

  private fun run(
    location: ProjectLocation,
    source: String,
    plugins: List<ResolvedPlugin>,
    pluginLoader: ClassLoader,
  ) {
    val imports =
      (listOf("com.progmasoft.tazel.dsl.*", "com.progmasoft.tazel.dsl.plugin.plugins") +
          plugins.flatMap(ResolvedPlugin::imports))
        .distinct()
    val options = listOf("-jvm-target", MINIMUM_JAVA.toString())
    val compilation = ScriptCompilationConfiguration {
      defaultImports(imports)
      compilerOptions.append(options)
      jvm {
        dependenciesFromClassContext(ProjectLoader::class, wholeClasspath = true)
        updateClasspath(plugins.map { it.artifact.toFile() })
      }
    }
    val evaluation = ScriptEvaluationConfiguration { jvm { baseClassLoader(pluginLoader) } }
    val fileName = location.file.fileName.toString()
    // The script host tells a script by its extension; every project file is the same script.
    val scriptName = if (fileName.endsWith(".kts")) fileName else "$fileName.kts"
    val script =
      compile(
        location,
        ScriptCacheInputs(
          source = source,
          scriptName = scriptName,
          imports = imports,
          compilerOptions = options,
          jvmTarget = MINIMUM_JAVA,
          classpath = ownClasspath(),
          plugins = plugins,
          tazelVersion = tazelVersion,
        ),
        compilation,
      )
    val result =
      timings.measure(Phase.EVALUATION) {
        host.runInCoroutineContext { host.evaluator(script, evaluation) }
      }
    val errors =
      result.reports.filter {
        it.severity == ScriptDiagnostic.Severity.ERROR ||
          it.severity == ScriptDiagnostic.Severity.FATAL
      }
    if (result is ResultWithDiagnostics.Failure || errors.isNotEmpty()) {
      throw ProjectConfigurationException(
        errors.ifEmpty { result.reports }.joinToString("\n") { describe(location, it) }
      )
    }
    val value = (result as ResultWithDiagnostics.Success).value.returnValue
    if (value is ResultValue.Error) {
      when (val error = value.error) {
        is ProjectConfigurationException -> throw error
        is ProjectAbort ->
          throw ProjectConfigurationException("${location.file.fileName}: ${error.message}")
        else ->
          throw ProjectConfigurationException(
            "${location.file.fileName} failed: ${error.message ?: error.javaClass.name}"
          )
      }
    }
  }

  /**
   * The compiled form of a project file: from [cache] when an entry for the same inputs is there,
   * otherwise compiled now and kept for the next run.
   */
  private fun compile(
    location: ProjectLocation,
    inputs: ScriptCacheInputs,
    compilation: ScriptCompilationConfiguration,
  ): CompiledScript {
    val cache = this.cache
    val key = cache?.let { scriptCacheKey(inputs) }
    if (cache != null && key != null) {
      timings
        .measure(Phase.CACHE_LOAD) { cache.load(key) }
        ?.let {
          return it
        }
    }
    val result =
      timings.measure(Phase.COMPILATION) {
        // Evaluations run in parallel; the Kotlin compiler is used by one of them at a time.
        synchronized(compilerLock) {
          host.runInCoroutineContext {
            host.compiler(inputs.source.toScriptSource(inputs.scriptName), compilation)
          }
        }
      }
    val errors =
      result.reports.filter {
        it.severity == ScriptDiagnostic.Severity.ERROR ||
          it.severity == ScriptDiagnostic.Severity.FATAL
      }
    if (result !is ResultWithDiagnostics.Success || errors.isNotEmpty()) {
      throw ProjectConfigurationException(
        errors.ifEmpty { result.reports }.joinToString("\n") { describe(location, it) }
      )
    }
    if (cache != null && key != null) cache.store(key, result.value)
    return result.value
  }

  private fun describe(
    location: ProjectLocation,
    report: ScriptDiagnostic,
  ): String {
    val start = report.location?.start
    val position = if (start == null) "" else ":${start.line}:${start.col}"
    return "${location.file.fileName}$position: ${report.message}"
  }
}

/** The classpath Tazel runs with, which project files are compiled against. */
private fun ownClasspath(): List<Path> =
  System.getProperty("java.class.path")
    .split(File.pathSeparator)
    .filter(String::isNotBlank)
    .map(Path::of)

/** Serializes compilations of project files in this JVM. */
private val compilerLock = Any()

/** A project file reads a variable whose value here is not the one its command has. */
class StaleEnvironmentException(val name: String) : RuntimeException("$name differs")

private val GETENV = Regex("""getenv\(\s*"([^"$]+)"\s*\)""")

/**
 * Stops at the first variable [source] names whose value in [environment] is not the one this JVM
 * sees. Names are compared as the host compares them.
 */
internal fun requireSameEnvironment(
  source: String,
  environment: Map<String, String>,
  jvm: (String) -> String? = System::getenv,
  ignoreCase: Boolean = detectHost().os == OperatingSystem.WINDOWS,
) {
  for (match in GETENV.findAll(source)) {
    val name = match.groupValues[1]
    val value =
      environment[name]
        ?: environment.entries.firstOrNull { it.key.equals(name, ignoreCase) }?.value
    if (value != jvm(name)) throw StaleEnvironmentException(name)
  }
}
