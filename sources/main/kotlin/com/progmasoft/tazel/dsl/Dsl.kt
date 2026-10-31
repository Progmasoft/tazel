/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.dsl.plugin.PluginState

@DslMarker annotation class TazelDsl

@TazelDsl
class CompilerScope internal constructor(private val settings: CompilerSettings) {
  /** The version of the compiler that will build the project; a project reads it, never sets it. */
  val version: String
    get() = settings.version

  var standard: TalaStandard
    get() = settings.standard
    set(value) {
      settings.standard = value
    }

  var backend: Backend
    get() = settings.backend
    set(value) {
      settings.backend = value
    }

  var buildMode: BuildMode
    get() = settings.buildMode
    set(value) {
      settings.buildMode = value
    }

  var werror: Boolean
    get() = settings.warningsAsErrors
    set(value) {
      settings.warningsAsErrors = value
    }

  var warnings: Warnings
    get() = settings.warnings
    set(value) {
      settings.warnings = value
    }

  var wshadow: Boolean
    get() = settings.shadowWarnings
    set(value) {
      settings.shadowWarnings = value
    }

  var xppOptimizationPasses: Boolean
    get() = settings.xppOptimizationPasses
    set(value) {
      settings.xppOptimizationPasses = value
    }

  /** Links TalaRT into the output (`true`) or beside it (`false`); `null` leaves it to Talac. */
  var staticTalaRTLibrary: Boolean?
    get() = settings.staticTalaRTLibrary
    set(value) {
      settings.staticTalaRTLibrary = value
    }

  var xmmOptimizationPasses: Boolean
    get() = settings.xmmOptimizationPasses
    set(value) {
      settings.xmmOptimizationPasses = value
    }

  var optLevel: OptLevel
    get() = settings.effectiveOptLevel
    set(value) {
      settings.optLevel = value
    }

  fun llvm(block: LlvmCompilerScope.() -> Unit) {
    LlvmCompilerScope(settings).apply(block)
  }
}

@TazelDsl
class LlvmCompilerScope internal constructor(private val settings: CompilerSettings) {
  var compiler: LlvmCompiler
    get() = settings.llvmCompiler
    set(value) {
      settings.llvmCompiler = value
    }

  var lto: LlvmLto
    get() = settings.llvmLto
    set(value) {
      settings.llvmLto = value
    }

  /** Arguments that go to LLVM as they are written. */
  fun args(vararg values: String) {
    settings.llvmArguments += values.map(::requireLlvmArgument)
  }
}

// The compiler receives these as one comma-separated value, so an argument can hold neither a
// comma nor whitespace without changing what the compiler reads.
private fun requireLlvmArgument(value: String): String {
  val argument = requireText(value, "LLVM argument")
  if (argument.any { it == ',' || it.isWhitespace() }) {
    throw ProjectConfigurationException(
      "an LLVM argument cannot hold a comma or whitespace: $argument"
    )
  }
  return argument
}

/**
 * The project one evaluation of a project file builds: the model its blocks fill, the plugins of
 * the project and what its `project` block wrote. Every evaluation has its own, so two project
 * files can be evaluated at the same time on two threads.
 */
class ProjectContext
internal constructor(
  compilerVersion: String = "unknown",
  val host: Host = detectHost(),
) {
  /** The plugins of this project, from activation to the finished plan. */
  internal val plugins = PluginState()

  /** What the `project` block wrote, for a project file that reads it back afterwards. */
  internal var projectView: ProjectScope? = null
  private var identity: ProjectIdentity? = null
  private val authors = mutableListOf<String>()
  private val defaultFeatures = mutableListOf<String>()
  private var dependencies = DependencyManifest(emptyList())
  private var releaseOutputDirectory = "build/release"
  private var debugOutputDirectory = "build/debug"
  private val targets = mutableListOf<String>()
  private var workspace: Workspace? = null
  private var pmlEnabled = true
  private var pushSources = false
  private var pushExcludes: List<String>? = null
  private val executables = mutableListOf<ExecutableSourceTarget>()
  private val libraries = mutableListOf<LibrarySourceTarget>()
  private val testSuites = mutableListOf<TestSuite>()
  private var buildCache: BuildCacheSettings? = null
  private var execution: ExecutionSettings? = null
  internal val compilerSettings = CompilerSettings().apply { version = compilerVersion }

  val projectName: String?
    get() = identity?.name

  internal fun configureIdentity(
    configured: ProjectIdentity,
    configuredAuthors: List<String>,
    configuredDefaultFeatures: List<String>,
  ) {
    if (identity != null) throw ProjectConfigurationException("project may be configured only once")
    identity =
      ProjectIdentity(
        configured.name?.let { requireModuleSegment(it, "project name") },
        configured.stability,
        configured.version?.let(::requirePackageVersion),
        configured.description?.let { requireText(it, "project description") },
      )
    authors += configuredAuthors.map { requireText(it, "project author") }.distinct()
    defaultFeatures += configuredDefaultFeatures.map(::requireFeatureName).distinct()
  }

  internal fun configureOutputDirectories(
    release: String,
    debug: String,
  ) {
    releaseOutputDirectory = requireProjectDirectory(release, "outdirs.release")
    debugOutputDirectory = requireProjectDirectory(debug, "outdirs.debug")
  }

  internal fun configureTargets(values: List<String>) {
    targets.clear()
    targets += values.distinct()
  }

  internal fun configureWorkspace(value: Workspace) {
    if (workspace != null) {
      throw ProjectConfigurationException("workspace may be configured only once")
    }
    workspace = value
  }

  internal fun configurePml(enabled: Boolean) {
    pmlEnabled = enabled
  }

  internal fun configurePublishing(
    push: Boolean,
    excludes: List<String>?,
  ) {
    pushSources = push
    pushExcludes = excludes?.distinct()
  }

  internal fun configureSourceTargets(
    executableTargets: List<ExecutableSourceTarget>,
    libraryTargets: List<LibrarySourceTarget>,
  ) {
    executables.clear()
    executables += executableTargets
    libraries.clear()
    libraries += libraryTargets
  }

  internal fun configureTestSuites(suites: List<TestSuite>) {
    testSuites.clear()
    testSuites += suites
  }

  fun dependencies(block: DependenciesScope.() -> Unit) {
    val scope = DependenciesScope().apply(block)
    dependencies =
      validateDependencies(
        DependencyManifest(
          dependencies.declarations + scope.declarations,
          dependencies.local + scope.local,
        )
      )
  }

  fun compiler(block: CompilerScope.() -> Unit) {
    CompilerScope(compilerSettings).apply(block)
  }

  fun execution(block: ExecutionScope.() -> Unit) {
    if (execution != null) {
      throw ProjectConfigurationException("execution may be configured only once")
    }
    execution = ExecutionScope().apply(block).build()
  }

  fun buildCache(block: BuildCacheScope.() -> Unit) {
    if (buildCache != null) {
      throw ProjectConfigurationException("buildCache may be configured only once")
    }
    buildCache = BuildCacheScope().apply(block).build()
  }

  fun build(): ProjectPlan {
    // A project that only gathers the members of a workspace has no sources of its own, and one
    // whose plugins build it, such as a C project, needs no Tala target.
    if (
      executables.isEmpty() &&
        libraries.isEmpty() &&
        workspace == null &&
        plugins.buildSteps.isEmpty()
    ) {
      throw ProjectConfigurationException(
        "sources requires at least one executable or library target"
      )
    }
    val project = identity
    if (pushSources) {
      val missingFields = buildList {
        if (project?.name == null) add("name")
        if (project?.version == null) add("version")
        if (project?.stability == null) add("stability")
        if (project?.description == null) add("description")
        if (authors.isEmpty()) add("authors")
      }
      if (missingFields.isNotEmpty()) {
        throw ProjectConfigurationException(
          "sources.viget.push requires every project field; missing: ${missingFields.joinToString()}"
        )
      }
    }
    if (compilerSettings.shadowWarnings && compilerSettings.warnings == Warnings.NONE) {
      throw ProjectConfigurationException(
        "compiler.wshadow asks for a warning and Warnings.NONE silences every warning; choose one"
      )
    }
    val plan =
      ProjectPlan(
        project,
        authors.toList(),
        defaultFeatures.toList(),
        dependencies,
        releaseOutputDirectory,
        debugOutputDirectory,
        targets.toList(),
        workspace,
        pmlEnabled,
        pushSources,
        pushExcludes,
        executables.toList(),
        libraries.toList(),
        testSuites.toList(),
        compilerSettings.copy(),
        emptyList(),
        buildCache ?: BuildCacheSettings(),
        execution ?: ExecutionSettings(),
      )
    return plugins.finish(plan)
  }
}

internal fun requireText(
  value: String,
  field: String,
): String {
  if (value.isBlank()) throw ProjectConfigurationException("$field cannot be empty")
  return value
}

internal fun requireModuleSegment(
  value: String,
  field: String,
): String {
  val name = requireText(value, field)
  if (!name.matches(Regex("[A-Za-z_][A-Za-z0-9_]*"))) {
    throw ProjectConfigurationException("$field must be one case-sensitive Tala identifier: $name")
  }
  return name
}

/** A directory of the project, written relative to the project file and staying inside it. */
internal fun requireProjectDirectory(
  value: String,
  field: String,
): String {
  val directory = requireText(value, field).replace('\\', '/')
  val absolute = directory.startsWith('/') || Regex("^[A-Za-z]:").containsMatchIn(directory)
  if (absolute || directory.split('/').any { it == ".." }) {
    throw ProjectConfigurationException("$field must stay inside the project root: $value")
  }
  if (directory.any { it in "*?" }) {
    throw ProjectConfigurationException("$field must name a directory, not a glob: $value")
  }
  return directory.trimEnd('/').ifEmpty { "." }
}

/**
 * The project the DSL of the current thread fills. A project file is a script of top-level calls,
 * so the model they fill has to be reachable without a receiver: the loader binds a fresh
 * [ProjectContext] to the thread for the duration of one evaluation and unbinds it afterwards.
 * Nothing is shared between evaluations, and evaluations on different threads do not wait for each
 * other.
 */
internal object ProjectRuntime {
  private val active = ThreadLocal<ProjectContext>()

  /** Runs [block] with [context] as the project the DSL of this thread fills. */
  fun <T> evaluate(
    context: ProjectContext,
    block: () -> T,
  ): T {
    val previous = active.get()
    active.set(context)
    try {
      return block()
    } finally {
      if (previous == null) active.remove() else active.set(previous)
    }
  }

  /**
   * Binds a new project to this thread outside an evaluation, for code that calls the DSL directly
   * rather than through a project file.
   */
  fun reset(compilerVersion: String = "unknown") {
    active.set(ProjectContext(compilerVersion))
  }

  /** The project being evaluated on this thread. */
  fun current(): ProjectContext =
    active.get()
      ?: throw ProjectConfigurationException(
        "the project DSL can be used only while a project file is evaluated"
      )

  fun configureIdentity(
    identity: ProjectIdentity,
    authors: List<String>,
    defaultFeatures: List<String>,
  ) = current().configureIdentity(identity, authors, defaultFeatures)

  fun configureOutputDirectories(
    release: String,
    debug: String,
  ) = current().configureOutputDirectories(release, debug)

  fun configureTargets(values: List<String>) = current().configureTargets(values)

  fun configureWorkspace(value: Workspace) = current().configureWorkspace(value)

  fun configurePml(enabled: Boolean) = current().configurePml(enabled)

  fun configurePublishing(
    push: Boolean,
    excludes: List<String>?,
  ) = current().configurePublishing(push, excludes)

  fun configureSourceTargets(
    executables: List<ExecutableSourceTarget>,
    libraries: List<LibrarySourceTarget>,
  ) = current().configureSourceTargets(executables, libraries)

  fun dependencies(block: DependenciesScope.() -> Unit) = current().dependencies(block)

  fun configureTestSuites(suites: List<TestSuite>) = current().configureTestSuites(suites)

  fun compiler(block: CompilerScope.() -> Unit) = current().compiler(block)

  fun buildCache(block: BuildCacheScope.() -> Unit) = current().buildCache(block)

  fun execution(block: ExecutionScope.() -> Unit) = current().execution(block)

  fun build() = current().build()

  val compilerScope
    get() = CompilerScope(current().compilerSettings)

  val projectName
    get() = current().projectName

  val host
    get() = current().host
}

/** The operating system Tazel runs on. */
val OS
  get() = ProjectRuntime.host.os

/** The processor architecture Tazel runs on. */
val ARCH
  get() = ProjectRuntime.host.architecture

/** Stops the evaluation of the project file with [message]. */
fun panic(message: String): Nothing = throw ProjectAbort(message)

/** Writes [message] to the standard error stream, without a line ending. */
fun eprint(message: Any?) = System.err.print(message)

fun dependencies(block: DependenciesScope.() -> Unit) = ProjectRuntime.dependencies(block)

fun compiler(block: CompilerScope.() -> Unit) = ProjectRuntime.compiler(block)

/** Where results of earlier runs are kept: `local`, which is asked first, and `remote`. */
fun buildCache(block: BuildCacheScope.() -> Unit) = ProjectRuntime.buildCache(block)

fun execution(block: ExecutionScope.() -> Unit) = ProjectRuntime.execution(block)

/**
 * The compiler settings as they stand, so a project file can read them after its `compiler` block:
 * `println(compiler.standard)`.
 */
val compiler: CompilerScope
  get() = ProjectRuntime.compilerScope
