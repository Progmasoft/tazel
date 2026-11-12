/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path

/**
 * The inputs a Make-style dependency file names, as compilers write it with `-MD -MF`: the target,
 * a colon, then the inputs, with `\ ` for a space and a backslash at the end of a line to go on.
 */
fun depfileInputs(text: String): List<String> {
  val body = text.substringAfter(": ", text.substringAfter(":\n", ""))
  val inputs = mutableListOf<String>()
  val current = StringBuilder()
  var index = 0
  fun flush() {
    if (current.isNotEmpty()) inputs += current.toString()
    current.clear()
  }
  while (index < body.length) {
    val c = body[index]
    when {
      c == '\\' && index + 1 < body.length && body[index + 1] == ' ' -> {
        current.append(' ')
        index++
      }
      c == '\\' && index + 1 < body.length && body[index + 1] == '\n' -> {
        flush()
        index++
      }
      c == '\\' &&
        index + 2 < body.length &&
        body[index + 1] == '\r' &&
        body[index + 2] == '\n' -> {
        flush()
        index += 2
      }
      c == '$' && index + 1 < body.length && body[index + 1] == '$' -> {
        current.append('$')
        index++
      }
      c.isWhitespace() -> flush()
      else -> current.append(c)
    }
    index++
  }
  flush()
  return inputs
}

/**
 * Decides whether an output is still what its command would make. The command line is kept beside
 * the output in a `.cmd` file; the output is current when that line is the same and no input is
 * newer than the output.
 */
object UpToDate {
  /** Whether [output] made by [command] from [inputs] is current. */
  fun check(output: Path, command: List<String>, inputs: List<Path>): Boolean {
    if (!Files.isRegularFile(output)) return false
    val recorded =
      try {
        Files.readString(commandFile(output))
      } catch (_: IOException) {
        return false
      }
    if (recorded != commandText(command)) return false
    val made = Files.getLastModifiedTime(output).toMillis()
    return inputs.all { input ->
      try {
        Files.getLastModifiedTime(input).toMillis() <= made
      } catch (_: IOException) {
        false
      }
    }
  }

  /** Whether an object made by [command] is current, with its inputs from the depfile [depfile]. */
  fun checkObject(objectFile: Path, depfile: Path, command: List<String>, base: Path): Boolean {
    val text =
      try {
        Files.readString(depfile)
      } catch (_: IOException) {
        return false
      }
    val inputs = depfileInputs(text).map { base.resolve(it).normalize() }
    return inputs.isNotEmpty() && check(objectFile, command, inputs)
  }

  /** Records that [output] was made by [command]. */
  fun record(output: Path, command: List<String>) {
    Files.writeString(commandFile(output), commandText(command))
  }

  /** Forgets how [output] was made, so a failed or stopped run is redone. */
  fun forget(output: Path) {
    Files.deleteIfExists(commandFile(output))
  }

  private fun commandFile(output: Path) = output.resolveSibling(output.fileName.toString() + ".cmd")

  private fun commandText(command: List<String>) = command.joinToString("\u0000")
}
