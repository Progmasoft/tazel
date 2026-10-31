/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.custom.commands

/** The file that holds the commands of `tazel custom`, in the directory they run in. */
const val CUSTOM_COMMANDS_FILE = "tazel_custom.txt"

/** One step of a custom command. */
sealed interface CustomStep {
  /** A program started directly, with these arguments and no shell in between. */
  data class Program(val arguments: List<String>) : CustomStep

  /** A whole command line for the shell of the host: CMD on Windows, `sh` elsewhere. */
  data class Shell(val line: String) : CustomStep
}

/** A named command of `tazel_custom.txt`: steps that run in order until one fails. */
data class CustomCommand(
  val name: String,
  val steps: List<CustomStep>,
)

/** What is wrong with a `tazel_custom.txt`, with where it is. */
class CustomCommandsException(message: String) : Exception(message)
