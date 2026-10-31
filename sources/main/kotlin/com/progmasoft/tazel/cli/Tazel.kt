/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.rendering.TextColors.brightBlue
import com.github.ajalt.mordant.rendering.TextColors.brightGreen
import com.github.ajalt.mordant.rendering.TextColors.brightRed
import com.github.ajalt.mordant.rendering.TextColors.brightYellow
import com.github.ajalt.mordant.rendering.TextStyles.bold
import com.github.ajalt.mordant.rendering.TextStyles.dim
import com.github.ajalt.mordant.terminal.Terminal
import com.progmasoft.api.tazel.dsl.plugin.BuildStepException
import com.progmasoft.api.tazel.dsl.plugin.CapturedRun
import com.progmasoft.tazel.driver.BuildCache
import com.progmasoft.tazel.driver.BuildInputs
import com.progmasoft.tazel.driver.BuildProfile
import com.progmasoft.tazel.driver.CacheServer
import com.progmasoft.tazel.driver.CacheServerSettings
import com.progmasoft.tazel.driver.DirectoryLocks
import com.progmasoft.tazel.driver.Emit
import com.progmasoft.tazel.driver.GatheredRun
import com.progmasoft.tazel.driver.HttpRemoteCompiler
import com.progmasoft.tazel.driver.HttpRemoteStore
import com.progmasoft.tazel.driver.JobFailures
import com.progmasoft.tazel.driver.JobPool
import com.progmasoft.tazel.driver.LoadedWorkspace
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.RUNTIME_LIBRARIES
import com.progmasoft.tazel.driver.RemoteCompilation
import com.progmasoft.tazel.driver.RemoteCompiler
import com.progmasoft.tazel.driver.RemoteServer
import com.progmasoft.tazel.driver.RemoteServerSettings
import com.progmasoft.tazel.driver.SelectedTargets
import com.progmasoft.tazel.driver.SourceFiles
import com.progmasoft.tazel.driver.TestTotals
import com.progmasoft.tazel.driver.Tool
import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.driver.UnknownTargetException
import com.progmasoft.tazel.driver.buildKey
import com.progmasoft.tazel.driver.compilerArguments
import com.progmasoft.tazel.driver.displayName
import com.progmasoft.tazel.driver.executableExtension
import com.progmasoft.tazel.driver.hasTargets
import com.progmasoft.tazel.driver.isPlainPath
import com.progmasoft.tazel.driver.loadWorkspace
import com.progmasoft.tazel.driver.select
import com.progmasoft.tazel.driver.toolIdentity
import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.DependencyScope
import com.progmasoft.tazel.dsl.ExecutableSourceTarget
import com.progmasoft.tazel.dsl.Host
import com.progmasoft.tazel.dsl.LoadedProject
import com.progmasoft.tazel.dsl.OperatingSystem
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.ProjectLoader
import com.progmasoft.tazel.dsl.ProjectLocation
import com.progmasoft.tazel.dsl.ResultKeys
import com.progmasoft.tazel.dsl.ScriptCache
import com.progmasoft.tazel.dsl.TestSuite
import com.progmasoft.tazel.dsl.Timings
import com.progmasoft.tazel.dsl.coordinate
import com.progmasoft.tazel.dsl.detectHost
import com.progmasoft.tazel.dsl.discoverProject
import com.progmasoft.tazel.dsl.effectiveJobs
import com.progmasoft.tazel.dsl.plugin.toPluginBuildMode
import com.progmasoft.tazel.dsl.plugin.toPluginHost
import com.progmasoft.tazel.dsl.plugin.toPluginSummary
import com.progmasoft.tazel.dsl.projectFileIn
import com.progmasoft.tazel.dsl.remoteExecutionSettings
import com.progmasoft.tazel.lock.LockedPlugin
import com.progmasoft.tazel.lock.LockedProject
import com.progmasoft.tazel.lock.LockfileDump
import com.progmasoft.tazel.lock.ProjectLockFile
import java.io.IOException
import java.nio.file.FileVisitResult
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.SimpleFileVisitor
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.nio.file.attribute.BasicFileAttributes
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlin.time.TimeSource

/** The exit codes of Tazel. A program run by `tazel run` hands its own code through. */
object ExitCode {
  const val SUCCESS = 0
  const val FAILURE = 1
  const val USAGE = 2
}

/**
 * A command that stops with a message; the message is all the user needs to see, with [hint] under
 * it when a word looks like a typo of a real one.
 */
private const val REMOTE_TOKEN = "TAZEL_REMOTE_TOKEN"

private class Stop(
  message: String,
  val hint: String? = null,
) : Exception(message)

/**
 * One compiler run as Tazel plans it. [slug] names its results in the build cache; [output] is
 * where the program lands.
 */
private class CompilerRun(
  val what: String,
  val slug: String,
  val inputs: List<String>,
  val arguments: List<String>,
  val output: Path,
)

/** The end of one test suite: its [totals], unless the runner stopped before it wrote them. */
private class SuiteResult(
  val name: String,
  val exitCode: Int,
  val totals: TestTotals?,
) {
  val passed: Boolean
    get() = exitCode == 0 && totals != null && totals.failed == 0
}

/**
 * Runs one command of Tazel.
 *
 * Tazel compiles nothing itself. It reads the project file, finds the sources and starts the
 * compiler, the formatter or the linter with the arguments the project asks for.
 */
class Tazel(
  private val terminal: Terminal,
  private val directory: Path,
  private val os: OperatingSystem,
  private val toolchain: Toolchain,
  private val processes: Processes,
  private val version: String,
  /** Where compiled project files are kept between runs; `null` compiles every time. */
  private val scriptCache: ScriptCache? = null,
  private val timings: Timings = Timings.disabled,
  /** --color, --progress and --quiet; the terminal already has the colors they ask for. */
  private val output: OutputOptions = OutputOptions(),
  /** The environment of the command when this JVM does not have it, as in the daemon. */
  private val commandEnvironment: Map<String, String>? = null,
  /** The processors of the machine, which is how many jobs run unless told otherwise. */
  private val processors: Int = Runtime.getRuntime().availableProcessors(),
) {
  private val sources = SourceFiles(ignoreCase = os == OperatingSystem.WINDOWS)

  /** What the compiler reports; asked for once per command. */
  private val talacVersion by lazy { compilerVersion() }

  /** The caches of the projects at hand: a remote one that failed stays failed for the command. */
  private val caches = ConcurrentHashMap<Path, BuildCache>()

  /** Talac writes beside its first input, so runs in one directory take turns. */
  private val directories = DirectoryLocks()

  /** What the command line says about how to spend the machine. */
  private var execution = ExecutionOptions()

  /** The machines that compile for this one, by address, and whether they failed this command. */
  private val remoteCompilers = ConcurrentHashMap<String, RemoteCompiler>()
  private val remoteFailed = AtomicBoolean()

  /** Keeps what jobs that overlap print from running into each other. */
  private val printing = Any()

  /** Compiler runs and test runs of this command that were done, and those a result saved. */
  private val workDone = AtomicInteger()
  private val workSaved = AtomicInteger()

  /**
   * The compiler and the host as the keys of the build cache see them. The host counts because a
   * result can come from another machine, and a program only suits its own kind.
   */
  private fun compilerIdentity(project: LoadedProject): String {
    val memo = project.location.root.resolve(BuildCache.DIRECTORY).resolve(BuildCache.COMPILER_MEMO)
    val compiler =
      synchronized(printing) { toolIdentity(toolchain.require(Tool.TALAC), talacVersion, memo) }
    return listOf(compiler, os, detectHost().architecture).joinToString("\n")
  }

  /** Whether the compiler has `--incremental`; a released one may not. */
  private val compilerIsIncremental by lazy {
    toolchain
      .find(Tool.TALAC)
      ?.let { processes.capture(listOf(it.toString(), "--help")) }
      .orEmpty()
      .contains("--incremental=")
  }

  private fun settingsOf(workspace: LoadedWorkspace) = workspace.root.plan.execution

  private fun jobPool(
    workspace: LoadedWorkspace,
    profile: BuildProfile?,
  ): JobPool {
    val settings = settingsOf(workspace)
    val jobs =
      effectiveJobs(
        execution.jobs ?: settings.jobs,
        execution.cpus ?: settings.cpus,
        execution.memory ?: settings.memory,
        execution.memoryPerJob ?: settings.memoryPerJob,
        processors,
      )
    return JobPool(jobs, execution.keepGoing || settings.keepGoing, profile)
  }

  /** Where the trace of the jobs goes, if one was asked for, and what records it. */
  private fun profileOf(workspace: LoadedWorkspace): Pair<Path, BuildProfile>? {
    val file =
      execution.profile?.let(directory::resolve)
        ?: settingsOf(workspace).profile?.let(workspace.root.location.root::resolve)
    return file?.let { it to BuildProfile() }
  }

  /** The machine that compiles for this command, if any. */
  private fun remoteCompiler(workspace: LoadedWorkspace?): RemoteCompiler? {
    if (execution.noRemoteExecutor || remoteFailed.get()) return null
    val settings =
      execution.remoteExecutor?.let {
        val environment = commandEnvironment ?: System.getenv()
        remoteExecutionSettings(it, environment[REMOTE_TOKEN])
      } ?: workspace?.let { settingsOf(it).remote } ?: return null
    return remoteCompilers.computeIfAbsent(settings.url) { HttpRemoteCompiler(settings) }
  }

  /**
   * Compiles [run] on another machine. Anything that goes wrong there leaves the work to this one,
   * with one warning for the whole command, as a remote cache does.
   */
  private fun compileRemotely(
    remote: RemoteCompiler,
    project: LoadedProject,
    run: CompilerRun,
  ): RemoteCompilation? {
    if (run.inputs.any { !isPlainPath(it) }) return null
    val arguments = run.arguments.filterNot { it.startsWith("--incremental=") }
    try {
      return remote.compile(
        project.location.root,
        arguments,
        run.inputs,
        talacVersion,
        detectHost(),
      )
        ?: null.also {
          if (remoteFailed.compareAndSet(false, true)) {
            warn("the remote executor has another compiler or host; compiling here")
          }
        }
    } catch (failure: IOException) {
      if (remoteFailed.compareAndSet(false, true)) {
        warn(
          "the remote executor failed, compiling here: ${failure.message ?: failure.javaClass.simpleName}"
        )
      }
      return null
    }
  }

  fun run(command: Command): Int =
    try {
      execution =
        when (command) {
          is Command.Check -> command.execution
          is Command.Build -> command.execution
          is Command.Run -> command.execution
          is Command.Test -> command.execution
          else -> ExecutionOptions()
        }
      when (command) {
        is Command.Check ->
          compile(command.targets, Emit.METADATA, command.noCache).let { ExitCode.SUCCESS }
        is Command.Build ->
          compile(command.targets, Emit.EXECUTABLE, command.noCache).let { ExitCode.SUCCESS }
        is Command.Run -> runPrograms(command)
        is Command.Test -> test(command)
        Command.Fetch -> fetch()
        is Command.RemoteServe -> remoteServe(command)
        Command.LockResolve -> lockResolve()
        is Command.LockCreateDump -> lockCreateDump(command.path)
        is Command.LockGraph -> lockGraph(command)
        is Command.Fmt -> tool(Tool.TALAFMT, false, TargetSelection(), command.toolArguments)
        is Command.Lint ->
          tool(Tool.TALALINT, command.workspace, command.targets, command.toolArguments)
        Command.Version -> ExitCode.SUCCESS.also { terminal.println("tazel $version") }
        is Command.Help -> ExitCode.SUCCESS.also { terminal.println(helpWidget(command.topic)) }
        Command.Update,
        is Command.Install,
        Command.ViGetPush,
        Command.ViGetUpdate,
        is Command.ViGetVipkgDesc ->
          throw Stop(
            "tazel ${command.name.words} needs ViGet, and the ViGet client is not implemented yet"
          )
        Command.CacheStatus -> cacheStatus()
        Command.CacheClean -> cacheClean()
        Command.CachePush -> cachePush()
        is Command.CachePull -> cachePull(command.targets)
        Command.CacheKeygen -> cacheKeygen()
        is Command.CacheServe -> cacheServe(command)
        is Command.Daemon ->
          throw Stop("tazel ${command.name.words} manages the daemon from the command line itself")
        Command.ViPkgCreate ->
          throw Stop(
            "tazel vipkg create is not implemented yet: the compiler cannot write a library, " +
              "so a .vipkg would have nothing to carry"
          )
      }
    } catch (stop: Stop) {
      fail(stop.message.orEmpty(), stop.hint)
    } catch (several: JobFailures) {
      several.failures.dropLast(1).forEach { reportError(terminal, it.message.orEmpty()) }
      fail(several.failures.last().message.orEmpty())
    } catch (unknown: UnknownTargetException) {
      fail(
        unknown.message.orEmpty(),
        unknown.names.firstNotNullOfOrNull { name ->
          closestTo(name, unknown.known)?.let { didYouMean(name, "${unknown.flag} $it") }
        },
      )
    } catch (problem: ProjectConfigurationException) {
      fail(problem.message.orEmpty())
    } catch (problem: java.sql.SQLException) {
      fail("${ProjectLockFile.FILE_NAME} could not be used: ${problem.message}")
    }

  /**
   * Starts [command] on the streams of Tazel, or, while a progress bar is drawn, gathers what it
   * writes and prints that through the terminal so the bar stays below it.
   */
  private fun execute(
    command: List<String>,
    directory: Path,
    progress: StepProgress,
    environment: Map<String, String> = emptyMap(),
  ): Int {
    if (!progress.drawing) return processes.runWith(command, directory, environment)
    val run = processes.runGatheredWith(command, directory, environment)
    if (run.output.isNotBlank()) terminal.println(run.output.trimEnd(), stderr = true)
    return run.exitCode
  }

  /** The closing status line of a command, with the time it took. */
  private fun finished(
    what: String,
    started: TimeSource.Monotonic.ValueTimeMark,
  ) {
    val seconds = started.elapsedNow().inWholeMilliseconds / 1000.0
    val saved =
      workSaved.get().let { if (it > 0) ", $it of ${workDone.get() + it} from the cache" else "" }
    status("Finished", "$what$saved " + dim("in ${"%.2f".format(java.util.Locale.ROOT, seconds)}s"))
  }

  private fun fail(
    message: String,
    hint: String? = null,
  ): Int {
    reportError(terminal, message, hint)
    return ExitCode.FAILURE
  }

  /** A status line; --quiet leaves them out. */
  private fun status(
    verb: String,
    rest: String,
  ) {
    if (!output.quiet)
      terminal.println((bold + brightGreen)(verb.padStart(STATUS_WIDTH)) + " " + rest)
  }

  private fun warn(message: String) =
    terminal.println((bold + brightYellow)("warning") + ": " + message, stderr = true)

  private fun projectLoader(forBuild: Boolean = false) =
    ProjectLoader(
      compilerVersion(),
      scriptCache,
      timings,
      version,
      keepBuildSteps = forBuild,
      commandEnvironment = commandEnvironment,
    )

  private fun compilerVersion(): String =
    toolchain
      .find(Tool.TALAC)
      ?.let { processes.capture(listOf(it.toString(), "--version")) }
      ?.trim()
      ?.removePrefix("talac ")
      ?.takeIf(String::isNotBlank) ?: "unknown"

  /**
   * The workspace of the current directory. Only a build, which runs the steps of plugins, keeps
   * the plugin JARs open for them; it closes the workspace when it is done.
   */
  private fun load(forBuild: Boolean = false): LoadedWorkspace =
    loadWorkspace(projectLoader(forBuild), discoverProject(directory))

  private fun locked(project: LoadedProject) =
    LockedProject(project.plan.dependencies, project.plan.plugins.map(LockedPlugin::of))

  /** Renews the lockfile of [project] when it no longer records what the project declares. */
  private fun keepLockCurrent(project: LoadedProject) {
    val path = project.location.root.resolve(ProjectLockFile.FILE_NAME)
    if (!ProjectLockFile.isCurrent(path, locked(project))) {
      ProjectLockFile.write(project.location.root, locked(project))
    }
  }

  /** The outputs of the executables that were compiled, in the order they were compiled. */
  private fun compile(
    selection: TargetSelection,
    emit: Emit,
    noCache: Boolean,
  ): List<Path> {
    val started = TimeSource.Monotonic.markNow()
    load(forBuild = true).use { workspace ->
      val selected = select(workspace.all, selection)
      // A check only reads the Tala sources; the steps of plugins build, so only a build runs them.
      val runSteps = emit != Emit.METADATA
      val compiler =
        if (selected.any { it.executables.isNotEmpty() }) toolchain.require(Tool.TALAC) else null
      val outputs = mutableListOf<Path>()
      var stepsRun = 0
      val verb = if (emit == Emit.METADATA) "Checking" else "Building"
      val work = selected.sumOf {
        it.executables.size + if (runSteps) it.project.buildSteps.size else 0
      }
      val profile = profileOf(workspace)
      val pool = jobPool(workspace, profile?.second)
      val remote = remoteCompiler(workspace)
      StepProgress.start(terminal, verb, work, output.effectiveProgress).use { progress ->
        for (targets in selected) {
          val project = targets.project
          keepLockCurrent(project)
          requireNoDependencies(project, DependencyScope.BUILD)
          refuseLibraries(targets, selection)
          if (runSteps) stepsRun += runBuildSteps(project, progress)
        }
        val jobs = selected.flatMap { targets -> targets.executables.map { targets to it } }
        try {
          outputs +=
            pool.map(jobs, { (_, executable) -> executable.name }) { (targets, executable) ->
              val project = targets.project
              progress.working(executable.name)
              compile(compiler!!, project, executable, emit, noCache, progress, remote, workspace)
                .also { progress.done() }
            }
        } finally {
          profile?.let { (file, recorded) -> recorded.write(file) }
        }
      }
      if (outputs.isEmpty() && stepsRun == 0) {
        throw Stop("the selection holds no executable to compile")
      }
      finished(if (emit == Emit.METADATA) "check" else "build", started)
      return outputs
    }
  }

  /**
   * Runs the steps the plugins of [project] added to its build, in the order they were added, and
   * returns how many ran. Each step has a work directory of its own below `.tazel/build`.
   */
  private fun runBuildSteps(
    project: LoadedProject,
    progress: StepProgress,
  ): Int {
    val root = project.location.root
    val plan = project.plan
    val output = root.resolve(plan.outputDirectory).normalize()
    for (step in project.buildSteps) {
      progress.working("${step.owner} ${step.name}")
      val work = root.resolve(".tazel/build").resolve(step.owner).resolve(step.name)
      Files.createDirectories(work)
      Files.createDirectories(output)
      val services =
        object : StepServices {
          override fun status(
            verb: String,
            message: String,
          ) = this@Tazel.status(verb, message)

          override fun warn(message: String) = this@Tazel.warn(message)

          override fun run(
            command: List<String>,
            directory: Path,
            environment: Map<String, String>,
          ) = execute(command, directory, progress, environment)

          override fun capture(
            command: List<String>,
            directory: Path,
            environment: Map<String, String>,
          ) =
            processes.runGatheredWith(command, directory, environment).let {
              CapturedRun(it.exitCode, it.output)
            }

          override fun findProgram(name: String) = toolchain.findProgram(name)
        }
      val context =
        PluginStepContext(
          root,
          project.plan.toPluginSummary(),
          output,
          plan.compiler.buildMode.toPluginBuildMode(),
          Host(os, detectHost().architecture).toPluginHost(),
          work,
          services,
        )
      try {
        step.step.run(context)
      } catch (failure: BuildStepException) {
        throw Stop("${step.owner} ${step.name}: ${failure.message}")
      } catch (problem: ProjectConfigurationException) {
        throw problem
      } catch (error: Exception) {
        throw Stop(
          "build step '${step.name}' of plugin '${step.owner}' failed: " +
            (error.message ?: error.javaClass.name)
        )
      }
      progress.done()
    }
    return project.buildSteps.size
  }

  private fun compile(
    compiler: Path,
    project: LoadedProject,
    target: ExecutableSourceTarget,
    emit: Emit,
    noCache: Boolean,
    progress: StepProgress,
    remote: RemoteCompiler?,
    workspace: LoadedWorkspace,
  ): Path {
    val run = executableRun(project, target, emit, settingsOf(workspace).incremental)
    val extension = emit.extension ?: executableExtension(os)
    val verb = if (emit == Emit.METADATA) "Checking" else "Compiling"
    compile(compiler, project, run, verb, extension, noCache, progress, remote)
    return run.output
  }

  private fun executableRun(
    project: LoadedProject,
    target: ExecutableSourceTarget,
    emit: Emit,
    incremental: Boolean = false,
  ): CompilerRun {
    val root = project.location.root
    val inputs =
      sources.discover(
        root,
        target.srcDir,
        target.exclude.orEmpty(),
        "the srcDir of executable '${target.name}'",
      )
    if (inputs.isEmpty()) {
      throw Stop("executable '${target.name}' has no .tala source in ${target.srcDir}")
    }
    val extension = emit.extension ?: executableExtension(os)
    return CompilerRun(
      "executable '${target.name}'",
      "${emit.value}-${target.name}",
      inputs,
      compilerArguments(project.plan.compiler, target.entry, emit, inputs).let {
        val wanted = execution.incremental ?: incremental
        if (wanted && emit == Emit.EXECUTABLE && project.keepsObjects) {
          withIncrementalDirectory(it, ".tazel/incremental/${target.name}")
        } else {
          it
        }
      },
      root.resolve(project.plan.outputDirectory).resolve(target.name + extension),
    )
  }

  /** Objects are worth keeping in a debug build, and only a compiler that knows them keeps any. */
  private val LoadedProject.keepsObjects
    get() = plan.compiler.buildMode == BuildMode.DEBUG && compilerIsIncremental

  private fun withIncrementalDirectory(
    arguments: List<String>,
    path: String,
  ): List<String> {
    val separator = arguments.indexOf("--").takeIf { it >= 0 } ?: arguments.size
    return arguments.take(separator) + "--incremental=$path" + arguments.drop(separator)
  }

  private fun cacheOf(project: LoadedProject) =
    caches.computeIfAbsent(project.location.root) {
      val settings = project.plan.buildCache
      BuildCache(
        project.location.root.resolve(BuildCache.DIRECTORY),
        settings,
        settings.remote?.let(::HttpRemoteStore),
        ::warn,
      )
    }

  /**
   * Runs the compiler, or brings back what an earlier run with the same key left, warnings
   * included. A run that fails leaves no result, so it is repeated and shows its errors every time.
   * The key is returned for what follows from this result.
   */
  private fun compile(
    compiler: Path,
    project: LoadedProject,
    run: CompilerRun,
    verb: String,
    extension: String,
    noCache: Boolean,
    progress: StepProgress,
    remote: RemoteCompiler?,
  ): String {
    val root = project.location.root
    val cache = cacheOf(project)
    val inputs = BuildInputs.of(compilerIdentity(project), run.arguments, root, run.inputs)
    val key = inputs.key
    val hit =
      if (noCache) null
      else
        directories.withDirectory(run.output.parent) {
          cache.load(run.slug, key, run.output.parent)
        }
    val description = brightBlue("(${project.displayName}, ${run.inputs.size} sources)")
    val name = run.output.fileName.toString().removeSuffix(extension)
    if (hit != null) {
      if (hit.log.isNotBlank()) terminal.println(hit.log.trimEnd(), stderr = true)
      val state = if (hit.restored == BuildCache.Restored.FRESH) "Fresh" else "Restored"
      status(state, "$name $description")
      cache.remember(run.slug, inputs)
      workSaved.incrementAndGet()
      return key
    }
    val reason = if (noCache) "--no-cache" else cache.latest(run.slug)?.let(inputs::changeFrom)
    status(verb, "$name $description" + (reason?.let { " " + dim(it) } ?: ""))
    workDone.incrementAndGet()
    val compiled =
      remote?.takeUnless { remoteFailed.get() }?.let { compileRemotely(it, project, run) }
    // The compiler writes beside its first input, under that input's name, with TalaRT beside it;
    // the project decides where artifacts live and what they are called, so they are moved there.
    val written = root.resolve(run.inputs.first().removeSuffix(".tala") + extension)
    directories.withDirectory(written.parent) {
      val result =
        compiled?.use {
          it.unpack(root)
          GatheredRun(it.exitCode, it.log)
        } ?: processes.runGathered(listOf(compiler.toString()) + run.arguments, root)
      finishCompile(run, result, cache, written, key, inputs)
    }
    return key
  }

  private fun finishCompile(
    run: CompilerRun,
    result: GatheredRun,
    cache: BuildCache,
    written: Path,
    key: String,
    inputs: BuildInputs,
  ) {
    // Talac names the place it linked to, which Tazel is about to leave; the rest is diagnostics.
    val diagnostics =
      result.output.lineSequence().filterNot { it.startsWith(LINKED_LINE) }.joinToString("\n")
    if (diagnostics.isNotBlank()) terminal.println(diagnostics.trimEnd(), stderr = true)
    if (result.exitCode != 0) {
      throw Stop("talac rejected ${run.what} (exit code ${result.exitCode})")
    }
    if (!Files.isRegularFile(written)) {
      throw Stop("talac reported success and left no $written")
    }
    Files.createDirectories(run.output.parent)
    directories.withDirectory(run.output.parent) {
      Files.move(written, run.output, REPLACE_EXISTING)
      val outputs =
        listOf(run.output) +
          RUNTIME_LIBRARIES.map(written::resolveSibling).filter(Files::isRegularFile).map { library
            ->
            run.output.resolveSibling(library.fileName).also {
              Files.move(library, it, REPLACE_EXISTING)
            }
          }
      cache.store(run.slug, key, outputs, diagnostics)
    }
    cache.remember(run.slug, inputs)
  }

  private fun requireNoDependencies(
    project: LoadedProject,
    scope: DependencyScope,
  ) {
    val dependencies = project.plan.dependencies
    val names =
      dependencies.declarations.filter { it.scope == scope }.map { it.dependency.coordinate } +
        dependencies.local.filter { it.scope == scope }.map { it.path }
    if (names.isNotEmpty()) {
      throw Stop(
        "${project.displayName} depends on ${names.joinToString()}, and Tazel cannot hand a " +
          "dependency to the compiler yet"
      )
    }
  }

  private fun refuseLibraries(
    targets: SelectedTargets,
    selection: TargetSelection,
  ) {
    if (targets.libraries.isEmpty()) return
    val names = targets.libraries.joinToString { "'${it.name}'" }
    val reason = "the compiler cannot write a library yet"
    // A library that was asked for by name is the point of the command; one that merely sits in
    // the project beside an executable is passed over, and said so.
    if (selection.libraries.isNotEmpty() || targets.executables.isEmpty()) {
      throw Stop("library $names cannot be compiled: $reason")
    }
    warn("library $names is skipped: $reason")
  }

  private fun runPrograms(command: Command.Run): Int {
    val programs = compile(command.targets, Emit.EXECUTABLE, command.noCache)
    for (program in programs) {
      status("Running", program.toString())
      val exitCode = processes.run(listOf(program.toString()) + command.programArguments, directory)
      if (exitCode != 0) return exitCode
    }
    return ExitCode.SUCCESS
  }

  /**
   * Builds and runs every test suite of the selection. A failing test does not stop the suites
   * after it, so one run shows all that is wrong; only a suite that does not compile ends the
   * command.
   */
  private fun test(command: Command.Test): Int {
    val started = TimeSource.Monotonic.markNow()
    load(forBuild = true).use { workspace ->
      val selected = select(workspace.all, command.targets)
      val count = selected.sumOf { it.project.plan.testSuites.size }
      if (count == 0) {
        status("Finished", "test: the selection declares no test suite")
        return ExitCode.SUCCESS
      }
      val compiler = toolchain.require(Tool.TALAC)
      val results = mutableListOf<SuiteResult>()
      val work = selected.sumOf { it.project.plan.testSuites.size + it.project.buildSteps.size }
      val profile = profileOf(workspace)
      val pool = jobPool(workspace, profile?.second)
      val remote = remoteCompiler(workspace)
      StepProgress.start(terminal, "Testing", work, output.effectiveProgress).use { progress ->
        val jobs = selected.flatMap { targets ->
          val project = targets.project
          keepLockCurrent(project)
          requireNoDependencies(project, DependencyScope.BUILD)
          requireNoDependencies(project, DependencyScope.TEST)
          runBuildSteps(project, progress)
          // What failed last time is what is being worked on, so it answers first.
          val failed = cacheOf(project).failedSuites()
          project.plan.testSuites.sortedBy { it.name !in failed }.map { targets to it }
        }
        val ran =
          try {
            pool.map(jobs, { (_, suite) -> suite.name }) { (targets, suite) ->
              progress.working(suite.name)
              val result =
                runSuite(
                  compiler,
                  targets,
                  suite,
                  command.noCache,
                  progress,
                  remote,
                  pool.concurrent,
                )
              progress.done()
              targets.project to result
            }
          } finally {
            profile?.let { (file, recorded) -> recorded.write(file) }
          }
        ran.groupBy({ (project, _) -> project }, { (_, result) -> result }).forEach {
          (project, suites) ->
          cacheOf(project).rememberFailedSuites(suites.filterNot { it.passed }.map { it.name })
        }
        results += ran.map { (_, result) -> result }
      }
      return reportTests(results, started)
    }
  }

  /**
   * The inputs of a test build: what the selection compiles, then the sources of [suite]. The first
   * part leaves out what lies in the directory of any suite, so a suite never sees another one.
   */
  private fun testInputs(
    targets: SelectedTargets,
    suite: TestSuite,
  ): List<String> {
    val root = targets.project.location.root
    val suites = targets.project.plan.testSuites
    val tested =
      sourcesOf(targets, includeTests = false).filter { file ->
        suites.none { sources.matches("${it.testDir}/**", file) }
      }
    val inputs =
      tested +
        sources.discover(root, suite.testDir, suite.exclude.orEmpty(), "test '${suite.name}'")
    if (inputs.size == tested.size) {
      throw Stop("test suite '${suite.name}' has no .tala source in ${suite.testDir}")
    }
    return inputs
  }

  /**
   * Builds and runs one suite. A suite that passed is not run again while nothing it can see
   * changed: its sources, the settings, the compiler and every file of its test directory.
   */
  private fun runSuite(
    compiler: Path,
    targets: SelectedTargets,
    suite: TestSuite,
    noCache: Boolean,
    progress: StepProgress,
    remote: RemoteCompiler?,
    together: Boolean,
  ): SuiteResult {
    val project = targets.project
    val root = project.location.root
    val cache = cacheOf(project)
    val run = suiteRun(targets, suite)
    val passedSlug = passedSlugOf(suite)
    val passedKey = passedKeyOf(project, suite, run)
    if (!noCache) {
      val totals = cache.load(passedSlug, passedKey, null)?.log?.let(TestTotals::parse)
      if (totals != null) {
        status("Cached", "${suite.name} " + brightBlue("(${project.displayName}, $totals)"))
        workSaved.incrementAndGet()
        return SuiteResult(suite.name, ExitCode.SUCCESS, totals)
      }
    }
    val extension = executableExtension(os)
    val program = run.output
    compile(compiler, project, run, "Compiling", extension, noCache, progress, remote)
    status("Testing", "${suite.name} " + brightBlue("(${project.displayName})"))
    var totals: TestTotals? = null
    // Suites that run at once keep their lines together, so that each reads as one report.
    val held = mutableListOf<String>()
    val exitCode =
      processes.runLines(listOf(program.toString()), root) { line ->
        TestTotals.parse(line)?.let { totals = it }
        if (together) held += colored(line) else terminal.println(colored(line))
      }
    if (held.isNotEmpty()) synchronized(printing) { held.forEach(terminal::println) }
    val result = SuiteResult(suite.name, exitCode, totals)
    if (result.passed) cache.store(passedSlug, passedKey, emptyList(), totals.toString())
    return result
  }

  private fun suiteRun(
    targets: SelectedTargets,
    suite: TestSuite,
  ): CompilerRun {
    val project = targets.project
    val inputs = testInputs(targets, suite)
    val runner = suite.name + executableExtension(os)
    return CompilerRun(
      "test suite '${suite.name}'",
      "test-${suite.name}",
      inputs,
      compilerArguments(project.plan.compiler, null, Emit.EXECUTABLE, inputs),
      project.location.root.resolve(".tazel/tests").resolve(suite.name).resolve(runner),
    )
  }

  private fun passedSlugOf(suite: TestSuite) = "passed-${suite.name}"

  /** The key of a suite that passed: its build, and every file of its test directory. */
  private fun passedKeyOf(
    project: LoadedProject,
    suite: TestSuite,
    run: CompilerRun,
  ): String {
    val root = project.location.root
    val files = run.inputs + filesBelow(root, suite.testDir)
    return buildKey(compilerIdentity(project), run.arguments, root, files)
  }

  /** Every file below [directory] of [root], data files included, in a stable order. */
  private fun filesBelow(
    root: Path,
    directory: String,
  ): List<String> =
    Files.walk(root.resolve(directory)).use { paths ->
      paths
        .filter(Files::isRegularFile)
        .map { root.relativize(it).joinToString("/") }
        .sorted()
        .toList()
    }

  private fun colored(line: String): String =
    when {
      line.startsWith("PASS ") -> brightGreen("PASS") + line.removePrefix("PASS")
      line.startsWith("FAIL ") -> (bold + brightRed)("FAIL") + line.removePrefix("FAIL")
      else -> line
    }

  private fun reportTests(
    results: List<SuiteResult>,
    started: TimeSource.Monotonic.ValueTimeMark,
  ): Int {
    val totals = results.mapNotNull { it.totals }.fold(TestTotals(0, 0), TestTotals::plus)
    val suites = "${results.size} ${if (results.size == 1) "suite" else "suites"}"
    val failed = results.filterNot { it.passed }
    if (failed.isEmpty()) {
      finished("test: $suites, $totals", started)
      return ExitCode.SUCCESS
    }
    failed
      .filter { it.totals == null }
      .forEach {
        reportError(
          terminal,
          "test suite '${it.name}' stopped before it finished (exit code ${it.exitCode})",
          null,
        )
      }
    return fail("$suites, $totals: ${failed.joinToString { "'${it.name}'" }} failed")
  }

  private fun sizeText(bytes: Long): String =
    when {
      bytes < 1L shl 10 -> "$bytes B"
      bytes < 1L shl 20 -> "%.1f KiB".format(java.util.Locale.ROOT, bytes / 1024.0)
      bytes < 1L shl 30 -> "%.1f MiB".format(java.util.Locale.ROOT, bytes / 1048576.0)
      else -> "%.1f GiB".format(java.util.Locale.ROOT, bytes / 1073741824.0)
    }

  /** What the caches of the workspace hold and how they are set up. */
  private fun cacheStatus(): Int {
    for (project in load().all) {
      val settings = project.plan.buildCache
      val cache = cacheOf(project)
      val results = cache.results()
      terminal.println(bold(project.displayName))
      val local =
        if (settings.local.enabled) "at most ${settings.local.maxEntries} of each target"
        else "turned off"
      terminal.println(
        "  local   ${results.size} results, ${sizeText(results.sumOf { it.bytes })}, $local"
      )
      results
        .groupBy { it.slug }
        .forEach { (slug, kept) ->
          terminal.println("          $slug: ${kept.size}, ${sizeText(kept.sumOf { it.bytes })}")
        }
      val remote = settings.remote
      if (remote == null) {
        terminal.println("  remote  none")
        continue
      }
      val access =
        listOfNotNull("read".takeIf { remote.read }, "write".takeIf { remote.write })
          .ifEmpty { listOf("no access") }
          .joinToString(" and ")
      val trust = if (remote.publicKey == null) "results not verified" else "signed results only"
      val reach = cache.remoteProblem()?.let { "not reachable: $it" } ?: "reachable"
      terminal.println("  remote  ${remote.url}, $access, $trust, $reach")
    }
    return ExitCode.SUCCESS
  }

  /** Removes the local caches of the workspace. A remote cache is not touched. */
  private fun cacheClean(): Int {
    for (project in load().all) {
      status("Cleaned", "${project.displayName} ${sizeText(cacheOf(project).clean())}")
    }
    return ExitCode.SUCCESS
  }

  /** Sends what the local caches hold and the remote one does not. */
  private fun cachePush(): Int {
    var result = ExitCode.SUCCESS
    for (project in load().all) {
      val remote =
        project.plan.buildCache.remote
          ?: throw Stop("${project.displayName} has no buildCache.remote to push to")
      if (!remote.write) {
        throw Stop("${project.displayName} has buildCache.remote.write = false")
      }
      val pushed = cacheOf(project).push()
      status(
        "Pushed",
        "${project.displayName} " + "${pushed.sent} sent, ${pushed.present} already there",
      )
      if (pushed.failed) result = ExitCode.FAILURE
    }
    return result
  }

  /**
   * Fetches the results of the sources as they stand into the local caches, so that the work is
   * ready before a build, or before the network goes away. A server has no list of its results and
   * needs none: the keys follow from the sources.
   */
  private fun cachePull(selection: TargetSelection): Int {
    var found = 0
    var wanted = 0
    for (targets in select(load().all, selection)) {
      val project = targets.project
      val settings = project.plan.buildCache
      if (settings.remote?.read != true) {
        throw Stop("${project.displayName} has no buildCache.remote to read from")
      }
      if (!settings.local.enabled) {
        throw Stop(
          "${project.displayName} has buildCache.local.enabled = false: nothing can be kept"
        )
      }
      val root = project.location.root
      val cache = cacheOf(project)
      fun fetch(
        slug: String,
        key: String,
      ) {
        wanted++
        if (cache.load(slug, key, null) != null) found++
      }
      val identity = compilerIdentity(project)
      fun fetch(run: CompilerRun) =
        fetch(run.slug, buildKey(identity, run.arguments, root, run.inputs))
      for (executable in targets.executables) {
        fetch(executableRun(project, executable, Emit.EXECUTABLE))
        fetch(executableRun(project, executable, Emit.METADATA))
      }
      for (suite in project.plan.testSuites) {
        val run = suiteRun(targets, suite)
        fetch(run)
        fetch(passedSlugOf(suite), passedKeyOf(project, suite, run))
      }
    }
    status("Pulled", "$found of $wanted results")
    return ExitCode.SUCCESS
  }

  /** Prints a new pair of keys; the signing key is shown here and kept nowhere. */
  private fun cacheKeygen(): Int {
    val (public, signing) = ResultKeys.generate()
    terminal.println("publicKey = \"$public\"")
    terminal.println("signingKey = \"$signing\"")
    return ExitCode.SUCCESS
  }

  private fun cacheServe(command: Command.CacheServe): Int {
    fun token(name: String) = System.getenv(name)?.takeIf(String::isNotBlank)
    val settings =
      CacheServerSettings(
        directory.resolve(command.directory).toAbsolutePath().normalize(),
        command.host,
        command.port,
        command.maxGigabytes * (1L shl 30),
        token("TAZEL_CACHE_TOKEN"),
        token("TAZEL_CACHE_READ_TOKEN"),
      )
    CacheServer(settings).use { server ->
      server.start()
      status("Serving", "${settings.directory} on http://${settings.host}:${server.port}")
      if (settings.writeToken == null) {
        warn("TAZEL_CACHE_TOKEN is not set, so this cache takes no results")
      }
      Thread.currentThread().join()
    }
    return ExitCode.SUCCESS
  }

  /** Compiles for other machines until the process is stopped. */
  private fun remoteServe(command: Command.RemoteServe): Int {
    val token =
      (commandEnvironment ?: System.getenv())[REMOTE_TOKEN]?.takeIf(String::isNotBlank)
        ?: throw Stop(
          "tazel remote serve needs $REMOTE_TOKEN: it compiles for whoever has the token"
        )
    val settings =
      RemoteServerSettings(
        command.host,
        command.port,
        command.jobs ?: processors,
        token,
        toolchain.require(Tool.TALAC),
        talacVersion,
        detectHost(),
      )
    RemoteServer(settings, processes).use { server ->
      server.start()
      status(
        "Serving",
        "compilers at http://${command.host}:${server.port}/, ${settings.jobs} at a time",
      )
      Thread.currentThread().join()
    }
    return ExitCode.SUCCESS
  }

  /**
   * Brings what the projects depend on to this machine, so that later commands need no network.
   * Loading a project fetches its plugins of ViGet. A package that cannot be had is an error, since
   * success here means nothing is left to fetch.
   */
  private fun fetch(): Int {
    val workspace = load()
    val projects = listOf(workspace.root) + workspace.members
    val missing = projects.flatMap { project ->
      project.plan.dependencies.local
        .map { it.path }
        .filterNot { Files.isRegularFile(project.location.root.resolve(it)) }
    }
    if (missing.isNotEmpty()) {
      throw Stop("no local package at ${missing.joinToString()}")
    }
    val remote = projects.flatMap { project ->
      project.plan.dependencies.declarations.map { it.dependency.coordinate }
    }
    if (remote.isNotEmpty()) {
      throw Stop(
        "cannot fetch ${remote.distinct().joinToString()}: ViGet does not serve Tala packages yet"
      )
    }
    status("Fetched", "${projects.sumOf { it.plan.plugins.size }} plugins")
    return ExitCode.SUCCESS
  }

  private fun lockResolve(): Int {
    val workspace = load()
    for (project in listOf(workspace.root) + workspace.members) {
      ProjectLockFile.write(project.location.root, locked(project))
      status("Locked", project.location.root.resolve(ProjectLockFile.FILE_NAME).toString())
    }
    return ExitCode.SUCCESS
  }

  /**
   * Writes the lockfile as text: to [path], resolved against the directory Tazel runs in, or to
   * LOCK.dump beside the project file. The folder of the file has to exist; Tazel does not create
   * it, so a mistyped folder fails instead of leaving a dump somewhere unexpected.
   */
  private fun lockCreateDump(path: String?): Int {
    val root = discoverProject(directory).root
    val lockfile = root.resolve(ProjectLockFile.FILE_NAME)
    if (!Files.isRegularFile(lockfile)) {
      throw Stop("$lockfile does not exist; write it with tazel lock resolve")
    }
    val dump =
      if (path == null) root.resolve(ProjectLockFile.DUMP_FILE_NAME)
      else directory.resolve(path).toAbsolutePath().normalize()
    if (Files.isDirectory(dump)) {
      throw Stop("$dump is a folder; --path names the file to write, such as $dump/LOCK.dump")
    }
    val folder = dump.parent
    if (folder == null || !Files.isDirectory(folder)) {
      throw Stop("the folder $folder does not exist; create it first, Tazel does not make folders")
    }
    LockfileDump.write(lockfile, dump)
    status("Dumped", dump.toString())
    return ExitCode.SUCCESS
  }

  /**
   * Prints the dependency graph of the project, or of every project of the workspace at its root.
   * The graph is what the lockfiles record, so they are renewed first when they are out of date;
   * with --locked they are only compared, and one that is missing or out of date is an error.
   *
   * `--invert MyPackage` names a project rather than a package, so the graph is then made of every
   * project below the directory Tazel runs in, and of the workspace that directory belongs to.
   */
  private fun lockGraph(command: Command.LockGraph): Int {
    val invert = command.invert
    val loader = projectLoader()
    val workspace =
      if (invert is InvertTarget.Project) {
        projectFileAbove(directory)?.let { loadWorkspace(loader, it) }
      } else {
        loadWorkspace(loader, discoverProject(directory))
      }
    val shown =
      workspace
        ?.let {
          listOf(it.root).filter { root -> root.hasTargets || !root.plan.dependencies.isEmpty } +
            it.members
        }
        .orEmpty()
    val projects =
      if (invert is InvertTarget.Project) {
        (shown + projectsBelow(directory, loader)).distinctBy { it.location.root }
      } else {
        shown
      }
    projects.forEach { keepLockCurrent(it, command.locked) }
    val graph = projects.map {
      graphProject(
        it.displayName,
        it.plan.dependencies,
        it.plan.defaultFeatures,
        command.allFeatures,
      )
    }
    val lines =
      when (invert) {
        is InvertTarget.Project -> dependentsOf(invert.name, projects, graph, command.depth)
        is InvertTarget.Package -> dependentsOf(invert.coordinate.toString(), graph, command.depth)
        is InvertTarget.Local -> dependentsOf(invert.path, graph, command.depth)
        null -> renderGraph(graph, GraphView(command.duplicates, null, command.depth))
      }
    if (lines.isEmpty()) {
      terminal.println(dim("no package is there in more than one version"))
    } else {
      lines.forEach(terminal::println)
    }
    if (graph.any { project -> project.edges.any { it.target is GraphPackage.Remote } }) {
      terminal.println(
        dim("ViGet packages are shown without their own dependencies until Tazel can fetch them"),
        stderr = true,
      )
    }
    return ExitCode.SUCCESS
  }

  /** Renews the lockfile of [project] when it is out of date, or, when [locked], refuses to. */
  private fun keepLockCurrent(
    project: LoadedProject,
    locked: Boolean,
  ) {
    val path = project.location.root.resolve(ProjectLockFile.FILE_NAME)
    if (ProjectLockFile.isCurrent(path, locked(project))) return
    if (locked) {
      val state =
        if (Files.exists(path)) "does not record what the project declares" else "does not exist"
      throw Stop(
        "$path $state, and --locked leaves it as it is",
        "renew it with tazel lock resolve, or leave out --locked",
      )
    }
    ProjectLockFile.write(project.location.root, locked(project))
    terminal.println(
      (bold + brightGreen)("Locked".padStart(STATUS_WIDTH)) + " $path",
      stderr = true,
    )
  }

  /** The lines of `--invert` for a package: each version of [key] with its dependents. */
  private fun dependentsOf(
    key: String,
    graph: List<GraphProject>,
    depth: Int?,
  ): List<String> {
    if (graph.packagesWithKey(key).isEmpty()) {
      throw Stop(
        "no project here depends on $key",
        closestTo(key, graph.packageKeys())?.let { didYouMean(key, "--invert $it") },
      )
    }
    return renderGraph(graph, GraphView(invert = key, depth = depth))
  }

  /** The project file of [start] or of the nearest directory above it; null when there is none. */
  private fun projectFileAbove(start: Path): ProjectLocation? =
    generateSequence(start.toAbsolutePath().normalize()) { it.parent }
      .firstNotNullOfOrNull(::projectFileIn)

  /** The lines of `--invert MyPackage`: every project of that name with its dependents. */
  private fun dependentsOf(
    name: String,
    projects: List<LoadedProject>,
    graph: List<GraphProject>,
    depth: Int?,
  ): List<String> {
    val named = projects.filter { it.plan.identity?.name == name }
    if (named.isEmpty()) {
      val names = projects.mapNotNull { it.plan.identity?.name }.distinct()
      throw Stop(
        "no project below $directory has the name $name",
        closestTo(name, names)?.let { didYouMean(name, "--invert $it") },
      )
    }
    val targets = named.map {
      val place = directory.relativize(it.location.root).joinToString("/")
      NamedProject(name, it.plan.identity?.version, place.ifEmpty { "." })
    }
    return renderDependents(targets, graph, depth)
  }

  /**
   * Every project below [start], [start] included, in the order of their paths. A folder whose name
   * starts with a dot, such as .git or .tazel, is not looked into.
   */
  private fun projectsBelow(
    start: Path,
    loader: ProjectLoader,
  ): List<LoadedProject> {
    val locations = mutableListOf<ProjectLocation>()
    Files.walkFileTree(
      start,
      object : SimpleFileVisitor<Path>() {
        override fun preVisitDirectory(
          folder: Path,
          attributes: BasicFileAttributes,
        ): FileVisitResult {
          if (folder != start && folder.fileName.toString().startsWith(".")) {
            return FileVisitResult.SKIP_SUBTREE
          }
          projectFileIn(folder)?.let(locations::add)
          return FileVisitResult.CONTINUE
        }

        override fun visitFileFailed(
          file: Path,
          problem: java.io.IOException,
        ) = FileVisitResult.CONTINUE
      },
    )
    return locations.sortedBy { it.root.toString() }.map(loader::load)
  }

  /** Starts the formatter or the linter once per ViPkg, on every source the ViPkg owns. */
  private fun tool(
    tool: Tool,
    wholeWorkspace: Boolean,
    selection: TargetSelection,
    toolArguments: List<String>,
  ): Int {
    val workspace = load()
    val projects =
      if (wholeWorkspace || !selection.isEverything) workspace.all else workspace.default
    val selected = select(projects, selection)
    val program = toolchain.require(tool)
    val verb = if (tool == Tool.TALAFMT) "Formatting" else "Linting"
    var result = ExitCode.SUCCESS
    val started = TimeSource.Monotonic.markNow()
    StepProgress.start(terminal, verb, selected.size, output.effectiveProgress).use { progress ->
      for (targets in selected) {
        progress.working(targets.project.displayName)
        val files = sourcesOf(targets, includeTests = selection.isEverything)
        if (files.isNotEmpty()) {
          status(verb, "${targets.project.displayName} " + brightBlue("(${files.size} sources)"))
          val exitCode =
            execute(
              listOf(program.toString()) + toolArguments + "--" + files,
              targets.project.location.root,
              progress,
            )
          if (exitCode != 0) result = exitCode
        }
        progress.done()
      }
    }
    if (result == ExitCode.SUCCESS) finished(if (tool == Tool.TALAFMT) "fmt" else "lint", started)
    return result
  }

  private fun sourcesOf(
    targets: SelectedTargets,
    includeTests: Boolean,
  ): List<String> {
    val root = targets.project.location.root
    val files = sortedSetOf<String>()
    targets.executables.forEach {
      files += sources.discover(root, it.srcDir, it.exclude.orEmpty(), "executable '${it.name}'")
    }
    targets.libraries.forEach {
      files += sources.discover(root, it.srcDir, it.exclude.orEmpty(), "library '${it.name}'")
    }
    if (includeTests) {
      targets.project.plan.testSuites
        .filter { Files.isDirectory(root.resolve(it.testDir)) }
        .forEach {
          files += sources.discover(root, it.testDir, it.exclude.orEmpty(), "test '${it.name}'")
        }
    }
    return files.toList()
  }

  private companion object {
    const val STATUS_WIDTH = 12
    const val LINKED_LINE = "talac: linked native executable"
  }
}
