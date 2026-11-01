/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands

/** The file that holds the commands of `tazel custom`, in the directory they run in. */
const val CUSTOM_COMMANDS_FILE = "tazel_custom.txt"

/** The most words `tazel custom` takes, those of the name of the command included. */
const val MAX_CUSTOM_ARGUMENTS = 191

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
  data class Shell(val line: String) : CustomStep
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

  /** Whether `<*>` takes any number of arguments. */
  val takesAll: Boolean
    get() = CustomArgument.All in placeholders
}

/**
 * The command that [words] name, with the arguments that follow its name. Names may start with
 * other names, so the longest one that fits wins.
 */
fun List<CustomCommand>.matching(words: List<String>): Pair<CustomCommand, List<String>>? =
  filter { words.take(it.words.size) == it.words }
    .maxByOrNull { it.words.size }
    ?.let { it to words.drop(it.words.size) }

/** What is wrong with a `tazel_custom.txt`, with where it is. */
class CustomCommandsException(message: String) : Exception(message)
