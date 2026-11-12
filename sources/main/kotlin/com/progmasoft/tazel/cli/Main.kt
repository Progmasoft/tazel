/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import kotlin.system.exitProcess

/** The version the JAR of Tazel was built as; a run from loose classes has none. */
internal fun tazelVersion(): String =
  object {}.javaClass.`package`?.implementationVersion ?: "development"

/** The help of the commands this build of Tazel has. */
internal val HELP =
  """
  Tazel builds C, C++, Objective-C, Objective-C++ and Zig with Kotlin project files.

  Usage: tazel <command>

  Commands:
    version   prints the version of Tazel
    help      prints this help
  """
    .trimIndent()

/**
 * Runs one command line and returns its exit code: 0 for success, 2 for a command line that is
 * wrong.
 */
internal fun run(arguments: List<String>, out: Appendable, err: Appendable): Int =
  when (arguments) {
    listOf("version"),
    listOf("--version"),
    listOf("-v") -> {
      out.appendLine("tazel ${tazelVersion()}")
      0
    }
    emptyList<String>(),
    listOf("help"),
    listOf("--help"),
    listOf("-h") -> {
      out.appendLine(HELP)
      0
    }
    else -> {
      err.appendLine("error: '${arguments.first()}' is not a command of tazel; write tazel help")
      2
    }
  }

fun main(arguments: Array<String>) {
  exitProcess(run(arguments.toList(), System.out, System.err))
}
