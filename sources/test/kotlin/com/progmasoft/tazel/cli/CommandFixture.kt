/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.dsl.Os
import java.nio.file.Files
import java.nio.file.Path

/** What a command wrote. */
internal class Recorder {
  val out = StringBuilder()
  val err = StringBuilder()

  fun stdout() = out.toString()

  fun stderr() = err.toString()
}

/**
 * Runs whole commands in a directory on disk. Programs are stand-ins: they record the command line
 * they were started with; one under `tools/` succeeds, any other exits with [programExitCode].
 */
internal class Fixture(val root: Path) : Processes {
  val recorder = Recorder()
  val commands: MutableList<Pair<List<String>, Path>> = mutableListOf()
  var programExitCode = 0

  init {
    Files.createDirectories(root.resolve("tools"))
  }

  fun write(file: String, text: String = "") {
    val path = root.resolve(file)
    Files.createDirectories(path.parent)
    Files.writeString(path, text.trimIndent())
  }

  fun run(vararg arguments: String, directory: String = "."): Int =
    run(
      arguments.toList(),
      recorder.out,
      recorder.err,
      Environment(
        directory = root.resolve(directory).normalize(),
        os = Os.LINUX,
        programs = Programs(Os.LINUX, mapOf("PATH" to root.resolve("tools").toString())),
        processes = this,
      ),
    )

  /** The recorded command lines of one program, without the path of the program. */
  fun callsOf(program: String) =
    commands.map { it.first }.filter { it.first().endsWith(program) }.map { it.drop(1) }

  override fun run(command: List<String>, directory: Path): Int {
    commands += command to directory
    return if (command.first().contains("tools")) 0 else programExitCode
  }

  override fun capture(command: List<String>): String? = null
}

internal fun fixture(block: Fixture.() -> Unit) {
  val root = Files.createTempDirectory("tazel-command-").toRealPath()
  try {
    Fixture(root).block()
  } finally {
    root.toFile().deleteRecursively()
  }
}
