/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands

import com.progmasoft.tazel.dsl.OperatingSystem

/** The file that holds the commands of `tazel custom`, in the directory they run in. */
const val CUSTOM_COMMANDS_FILE = "tazel_custom.txt"

/** The most words `tazel custom` takes, those of the name of the command included. */
const val MAX_CUSTOM_ARGUMENTS = 191

/** Ends the name of a custom command on the command line; what follows it is arguments. */
const val CUSTOM_NAME_END = "--"

/** With the value `false`, the JVM quotes the arguments of processes on Windows more strictly. */
const val STRICT_PROCESSES_PROPERTY = "jdk.lang.Process.allowAmbiguousCommands"

/** One argument of a program in a custom command. */
sealed interface CustomArgument {
  /** A string of the file. */
  data class Text(val value: String) : CustomArgument

  /** `<N>`: the Nth argument of the command line, from 1. */
  data class Position(val number: Int) : CustomArgument

  /** `<*>`: all the arguments of the command line. */
  data object All : CustomArgument
}

/** One step of a custom command. */
sealed interface CustomStep {
  /**
   * A program started directly, with these arguments and no shell in between. The first is the
   * program, and is a string.
   */
  data class Program(val arguments: List<CustomArgument>) : CustomStep {
    /** What to start, given the arguments of the command line. */
    fun expanded(given: List<String>): List<String> = arguments.flatMap {
      when (it) {
        is CustomArgument.Text -> listOf(it.value)
        is CustomArgument.Position -> listOf(given[it.number - 1])
        CustomArgument.All -> given
      }
    }
  }

  /** A whole command line for the shell of the host: CMD on Windows, `sh` elsewhere. */
  data class Shell(val line: String) : CustomStep {
    /**
     * What to start on [os]. The line reaches the shell unchanged: `/s` makes CMD drop the quotes
     * around it, and Java leaves an argument that is already quoted alone, where it would otherwise
     * double a trailing backslash and treat a line that starts and ends with a quote as quoted.
     */
    fun started(os: OperatingSystem): List<String> =
      if (os == OperatingSystem.WINDOWS) listOf("cmd.exe", "/d", "/s", "/c", "\"$line\"")
      else listOf("sh", "-c", line)

    /**
     * Whether [started] still holds under `jdk.lang.Process.allowAmbiguousCommands=false`. In that
     * mode Java refuses a quoted argument with a quote inside and doubles the backslash before a
     * closing quote, and it has no other way to give CMD a line as it is.
     */
    val fitsStrictProcesses: Boolean
      get() = '"' !in line && !line.endsWith('\\')
  }
}

/** A named command of `tazel_custom.txt`: steps that run in order until one fails. */
data class CustomCommand(
  val name: String,
  val steps: List<CustomStep>,
) {
  private val placeholders
    get() = steps.filterIsInstance<CustomStep.Program>().flatMap { it.arguments }

  /** The words of [name]. */
  val words: List<String>
    get() = name.split(' ')

  /** The highest `<N>`: the arguments the command cannot do without. */
  val required: Int
    get() = placeholders.filterIsInstance<CustomArgument.Position>().maxOfOrNull { it.number } ?: 0

  /** The first line of a shell step that Java's strict process mode would refuse or change. */
  val lineUnfitForStrictProcesses: String?
    get() = steps.filterIsInstance<CustomStep.Shell>().firstOrNull { !it.fitsStrictProcesses }?.line

  /** Whether `<*>` takes any number of arguments. */
  val takesAll: Boolean
    get() = CustomArgument.All in placeholders
}

/**
 * The command that [words] name, with the arguments that follow its name. Names may start with
 * other names, so the longest one that fits wins. The first [CUSTOM_NAME_END] stops the name, and
 * everything after it is an argument, so that an argument can be a word that a longer name holds.
 */
fun List<CustomCommand>.matching(words: List<String>): Pair<CustomCommand, List<String>>? {
  val end = words.indexOf(CUSTOM_NAME_END)
  val named = if (end < 0) words else words.take(end)
  val after = if (end < 0) emptyList() else words.drop(end + 1)
  return filter { named.take(it.words.size) == it.words }
    .maxByOrNull { it.words.size }
    ?.let { it to named.drop(it.words.size) + after }
}

/** What is wrong with a `tazel_custom.txt`, with where it is. */
class CustomCommandsException(message: String) : Exception(message)
