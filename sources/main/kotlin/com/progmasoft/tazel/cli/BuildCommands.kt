/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.BuildFailure
import com.progmasoft.tazel.driver.BuildPlan
import com.progmasoft.tazel.driver.Builder
import com.progmasoft.tazel.driver.CcToolchain
import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.TargetTriple
import com.progmasoft.tazel.label.LabelException
import com.progmasoft.tazel.label.Labels
import com.progmasoft.tazel.label.PackageId
import com.progmasoft.tazel.label.PackagePattern
import com.progmasoft.tazel.label.TargetLabel
import com.progmasoft.tazel.project.Module
import com.progmasoft.tazel.project.ModuleLoader

/** What `tazel build` and `tazel run` were given on the command line. */
internal data class BuildOptions(
  val mode: BuildMode,
  val triple: String?,
  val selections: List<String>,
  /** After `--`: what `tazel run` passes to the program. */
  val programArguments: List<String>,
)

/** Reads the options of `tazel build` and `tazel run`; [command] names it in messages. */
internal fun buildOptions(command: String, arguments: List<String>): BuildOptions {
  var mode = BuildMode.DEBUG
  var triple: String? = null
  val selections = mutableListOf<String>()
  var index = 0
  while (index < arguments.size) {
    when (val argument = arguments[index]) {
      "--" -> return BuildOptions(mode, triple, selections, arguments.drop(index + 1))
      "--release" -> mode = BuildMode.RELEASE
      "--triple" -> {
        triple =
          arguments.getOrNull(index + 1)
            ?: throw Usage("tazel $command --triple needs an LLVM target triple after it")
        index++
      }
      else ->
        if (argument.startsWith("-")) {
          throw Usage("tazel $command has no option $argument; write tazel help")
        } else {
          selections += argument
        }
    }
    index++
  }
  return BuildOptions(mode, triple, selections, emptyList())
}

/** A command line that is wrong; it exits with [ExitCode.USAGE]. */
internal class Usage(message: String) : Exception(message)

/** `tazel build` and `tazel run`. */
internal class BuildCommands(
  private val environment: Environment,
  private val out: Appendable,
  private val loader: ModuleLoader = ModuleLoader(),
) {
  private val host = TargetTriple.host()

  fun build(options: BuildOptions): Int {
    val (module, labels, triple) = select(options, run = false)
    build(module, labels, triple, options.mode)
    return ExitCode.SUCCESS
  }

  fun run(options: BuildOptions): Int {
    val (module, labels, triple) = select(options, run = true)
    if (triple.text != host.text) {
      throw Stop("tazel run runs programs of this machine ($host), not of $triple")
    }
    val label = labels.single()
    val products = build(module, labels, triple, options.mode)
    val settings = module.configure(label, triple, options.mode).settings
    val program = products.getValue(label)
    return environment.processes.runWith(
      listOf(program.toString()) + settings.args + options.programArguments,
      environment.directory,
      settings.env,
    )
  }

  private fun build(
    module: Module,
    labels: List<TargetLabel>,
    triple: TargetTriple,
    mode: BuildMode,
  ): Map<TargetLabel, java.nio.file.Path> {
    val plan = BuildPlan(module, triple, mode)
    labels.forEach(plan::add)
    val execution = module.file.execution
    val builder =
      Builder(
        plan,
        CcToolchain(environment.programs, host),
        environment.programs,
        environment.processes,
        out,
        host,
        execution.jobs ?: Runtime.getRuntime().availableProcessors(),
        execution.keepGoing,
      )
    val result =
      try {
        builder.build()
      } catch (failure: BuildFailure) {
        throw Stop(failure.message.orEmpty())
      }
    out.appendLine(
      "%12s %d target(s) for %s, %s; %d command(s) run"
        .format("Built", plan.targets.size, triple, mode.name.lowercase(), result.commandsRun)
    )
    return result.products
  }

  private data class Selected(
    val module: Module,
    val labels: List<TargetLabel>,
    val triple: TargetTriple,
  )

  private fun select(options: BuildOptions, run: Boolean): Selected {
    val root = loader.findRoot(environment.directory)
    val module = loader.load(root)
    val triple = options.triple?.let(TargetTriple::parse) ?: host
    val current =
      module.packageOf(environment.directory)?.id
        ?: if (options.selections.isEmpty()) {
          throw Stop(
            "${environment.directory} is in no package; name targets such as //... or //app/:app"
          )
        } else PackageId.ROOT
    val written = options.selections.ifEmpty { listOf(current.toString() + "...") }
    val labels = mutableListOf<TargetLabel>()
    for (text in written) {
      val selection =
        try {
          if (options.selections.isEmpty()) PackagePattern(current)
          else Labels.selection(text, current)
        } catch (error: LabelException) {
          throw Usage(error.message.orEmpty())
        }
      val selected = module.select(selection)
      val forTriple = selected.filter { triple in module.triplesOf(it, options.mode, host) }
      if (selection is TargetLabel && forTriple.isEmpty()) {
        throw Stop("$selection is not built for $triple; name one of its triples with --triple")
      }
      // Without a label, only the package of the directory is meant, not the ones below it.
      labels +=
        if (options.selections.isEmpty()) forTriple.filter { it.packageId == current }
        else forTriple
    }
    if (run) {
      val bins = labels.distinct().filter { module.declaration(it).second.kind == TargetKind.BIN }
      return when {
        options.selections.size > 1 -> throw Usage("tazel run takes one label")
        bins.size == 1 -> Selected(module, bins, triple)
        bins.isEmpty() -> throw Stop("no bin target to run among ${written.joinToString()}")
        else -> throw Stop("${bins.joinToString()} are bin targets; name the one to run")
      }
    }
    if (labels.isEmpty()) throw Stop("nothing to build for $triple in ${written.joinToString()}")
    return Selected(module, labels.distinct(), triple)
  }

  companion object {
    /** Runs [block], turning what the project or the command line got wrong into exit codes. */
    fun guarded(err: Appendable, block: () -> Int): Int =
      try {
        block()
      } catch (usage: Usage) {
        err.appendLine("error: ${usage.message}")
        ExitCode.USAGE
      } catch (error: ProjectException) {
        err.appendLine("error: ${error.message}")
        ExitCode.FAILURE
      } catch (stop: Stop) {
        err.appendLine("error: ${stop.message}")
        stop.hint?.let(err::appendLine)
        ExitCode.FAILURE
      }
  }
}
