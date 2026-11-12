/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.custom.CUSTOM_COMMANDS_FILE
import com.progmasoft.tazel.dsl.custom.CustomCommand
import com.progmasoft.tazel.dsl.custom.CustomCommandsException
import com.progmasoft.tazel.dsl.custom.CustomStep
import com.progmasoft.tazel.dsl.custom.STRICT_PROCESSES_PROPERTY
import com.progmasoft.tazel.dsl.custom.matching
import com.progmasoft.tazel.dsl.custom.parser.parseCustomCommands
import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path

/** Runs Tazel Custom Commands: the commands of the nearest `tazel_custom.txt`. */
internal class CustomCommands(
  private val directory: Path,
  private val os: Os,
  private val programs: Programs,
  private val processes: Processes,
  private val out: Appendable,
) {
  /** Runs the command [words] name, in the directory of its file, or lists the commands. */
  fun run(words: List<String>): Int {
    val file =
      generateSequence(directory, Path::getParent)
        .map { it.resolve(CUSTOM_COMMANDS_FILE) }
        .firstOrNull(Files::isRegularFile)
        ?: throw Stop("no $CUSTOM_COMMANDS_FILE here or in a directory above")
    val commands =
      try {
        parseCustomCommands(Files.readString(file))
      } catch (error: CustomCommandsException) {
        throw Stop(error.message.orEmpty())
      } catch (error: IOException) {
        throw Stop("$file could not be read: ${error.message}")
      }
    if (words.isEmpty()) {
      commands.forEach { out.appendLine(it.name) }
      return ExitCode.SUCCESS
    }
    val written = words.joinToString(" ")
    val (command, arguments) =
      commands.matching(words)
        ?: throw Stop(
          "no command is named '$written' in $file",
          closestTo(written, commands.map(CustomCommand::name))?.let { didYouMean(written, it) },
        )
    if (arguments.size < command.required) {
      throw Stop("'${command.name}' needs ${command.required} argument(s), not ${arguments.size}")
    }
    if (arguments.size > command.required && !command.takesAll) {
      throw Stop(
        "'${command.name}' takes ${command.required} argument(s), not ${arguments.size}",
        closestTo(written, commands.map(CustomCommand::name))
          ?.takeIf { it != command.name }
          ?.let { didYouMean(written, it) },
      )
    }
    if (os == Os.WINDOWS && strictProcesses()) {
      command.lineUnfitForStrictProcesses?.let {
        throw Stop(
          "'${command.name}' cannot reach CMD unchanged: this JVM runs with " +
            "$STRICT_PROCESSES_PROPERTY=false, which refuses or changes the line: $it"
        )
      }
    }
    for (step in command.steps) {
      val exitCode = processes.run(commandOf(step, file.parent, arguments), file.parent)
      if (exitCode != ExitCode.SUCCESS) return exitCode
    }
    return ExitCode.SUCCESS
  }

  private fun strictProcesses() =
    System.getProperty(STRICT_PROCESSES_PROPERTY).equals("false", ignoreCase = true)

  private fun commandOf(step: CustomStep, base: Path, arguments: List<String>): List<String> =
    when (step) {
      is CustomStep.Shell -> step.started(os)
      is CustomStep.Program -> {
        val expanded = step.expanded(arguments)
        val program = expanded.first()
        val found =
          if (program.contains('/') || program.contains('\\')) {
            base.resolve(program).normalize().toString()
          } else {
            programs.find(program)?.toString() ?: throw Stop("no program named '$program' on PATH")
          }
        listOf(found) + expanded.drop(1)
      }
    }
}
