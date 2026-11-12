/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Os
import java.io.File
import java.nio.file.Files
import java.nio.file.Path

/** Finds the programs Tazel starts on `PATH`. */
class Programs(
  private val os: Os,
  private val environment: Map<String, String> = System.getenv(),
) {
  /**
   * The program [name] on `PATH`. On Windows a program may also be a script, like `npm.cmd`, and a
   * name without an extension finds any of them.
   */
  fun find(name: String): Path? {
    val extensions = if (os == Os.WINDOWS) WINDOWS_PROGRAMS else emptyList()
    val fileNames =
      if (extensions.isEmpty() || extensions.any { name.endsWith(it, ignoreCase = true) }) {
        listOf(name)
      } else {
        extensions.map { name + it }
      }
    return variable("PATH")
      .orEmpty()
      .split(File.pathSeparatorChar)
      .filter(String::isNotEmpty)
      .flatMap { directory -> fileNames.map { Path.of(directory).resolve(it) } }
      .firstOrNull(Files::isRegularFile)
  }

  private companion object {
    /** What Windows runs by itself, in the order a directory is searched. */
    val WINDOWS_PROGRAMS = listOf(".exe", ".cmd", ".bat", ".com")
  }

  // Windows spells the same variable "Path" or "PATH".
  private fun variable(name: String) =
    environment.entries
      .firstOrNull { it.key.equals(name, ignoreCase = os == Os.WINDOWS) }
      ?.value
      ?.takeIf(String::isNotBlank)
}
