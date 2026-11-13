/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.driver.SystemProcesses
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetTriple
import com.progmasoft.tazel.dsl.custom.MAX_CUSTOM_ARGUMENTS
import com.progmasoft.tazel.dsl.options.TazelOptionsException
import com.progmasoft.tazel.dsl.options.sandboxRefusal
import java.nio.file.Path
import kotlin.system.exitProcess

/** The exit codes of Tazel. */
object ExitCode {
  const val SUCCESS = 0
  const val FAILURE = 1
  const val USAGE = 2
}

/**
 * A command that stops with a message; the message is all the user needs to see, with [hint] under
 * it when a word looks like a typo of a real one.
 */
internal class Stop(message: String, val hint: String? = null) : Exception(message)

/** The version the JAR of Tazel was built as; a run from loose classes has none. */
internal fun tazelVersion(): String =
  object {}.javaClass.`package`?.implementationVersion ?: "development"

/** The help of the commands this build of Tazel has. */
internal val HELP =
  """
  Tazel builds C, C++, Objective-C, Objective-C++ and Zig with Kotlin project files.

  Usage: tazel <command> [options] [labels]

  Commands:
    build     builds targets, such as tazel build //app/:greeter or tazel build //...
    run       builds and runs one bin target; what follows -- goes to the program
    test      builds and runs test targets; what follows -- goes to every test
    custom    runs a command of tazel_custom.txt, or lists them
    version   prints the version of Tazel
    help      prints this help

  Options of build, run and test:
    --release           builds in release mode; without it, debug
    --triple <triple>   builds for that LLVM target triple; without it, this machine's

  Without a label, build, run and test take the package of the current directory.
  A pattern such as //... takes no test tagged MANUAL.
  """
    .trimIndent()

private val COMMANDS = listOf("build", "run", "test", "custom", "version", "help")

/** Where a command runs and what it starts programs with; tests give their own. */
internal class Environment(
  val directory: Path = Path.of("").toAbsolutePath(),
  val os: Os = TargetTriple.host().os,
  val programs: Programs = Programs(os),
  val processes: Processes = SystemProcesses,
)

/**
 * Runs one command line and returns its exit code: 0 for success, 1 for a command that failed, 2
 * for a command line that is wrong.
 */
internal fun run(
  arguments: List<String>,
  out: Appendable,
  err: Appendable,
  environment: Environment = Environment(),
): Int =
  when (arguments.firstOrNull()) {
    null,
    "help",
    "--help",
    "-h" ->
      if (arguments.size <= 1) {
        out.appendLine(HELP)
        ExitCode.SUCCESS
      } else usage(err, "tazel help takes no arguments")
    "version",
    "--version",
    "-v" ->
      if (arguments.size == 1) {
        out.appendLine("tazel ${tazelVersion()}")
        ExitCode.SUCCESS
      } else usage(err, "tazel version takes no arguments")
    "build",
    "run",
    "test" ->
      BuildCommands.guarded(err) {
        refuseWithoutSandbox(environment.directory)
        val command = arguments.first()
        val options = buildOptions(command, arguments.drop(1))
        val commands = BuildCommands(environment, out)
        if (command == "run") {
          commands.run(options)
        } else if (command == "test") {
          commands.test(options)
        } else {
          if (options.programArguments.isNotEmpty()) {
            throw Usage("tazel build takes nothing after --")
          }
          commands.build(options)
        }
      }
    // What follows `custom` belongs to the command it names, whatever it looks like.
    "custom" -> {
      val words = arguments.drop(1)
      if (words.size > MAX_CUSTOM_ARGUMENTS) {
        usage(
          err,
          "tazel custom takes at most $MAX_CUSTOM_ARGUMENTS arguments, and got ${words.size}",
        )
      } else {
        stopping(err) {
          refuseWithoutSandbox(environment.directory)
          CustomCommands(
              environment.directory,
              environment.os,
              environment.programs,
              environment.processes,
              out,
            )
            .run(words)
        }
      }
    }
    else -> {
      val written = arguments.first()
      err.appendLine("error: '$written' is not a command of tazel; write tazel help")
      closestTo(written, COMMANDS)?.let { err.appendLine(didYouMean(written, it)) }
      ExitCode.USAGE
    }
  }

/** Stops a command that would run what a project brings when a sandbox is asked for. */
private fun refuseWithoutSandbox(directory: Path) {
  try {
    sandboxRefusal(directory)?.let { throw Stop(it) }
  } catch (error: TazelOptionsException) {
    throw Stop(error.message.orEmpty())
  }
}

private fun usage(err: Appendable, message: String): Int {
  err.appendLine("error: $message")
  return ExitCode.USAGE
}

private fun stopping(err: Appendable, block: () -> Int): Int =
  try {
    block()
  } catch (stop: Stop) {
    err.appendLine("error: ${stop.message}")
    stop.hint?.let(err::appendLine)
    ExitCode.FAILURE
  } catch (error: ProjectException) {
    err.appendLine("error: ${error.message}")
    ExitCode.FAILURE
  }

fun main(arguments: Array<String>) {
  exitProcess(run(arguments.toList(), System.out, System.err))
}
