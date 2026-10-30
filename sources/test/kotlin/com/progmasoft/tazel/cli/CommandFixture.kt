/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.terminal.Terminal
import com.github.ajalt.mordant.terminal.TerminalRecorder
import com.progmasoft.tazel.driver.GatheredRun
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.dsl.OperatingSystem
import java.nio.file.Files
import java.nio.file.Path

/**
 * Runs whole commands against projects on disk. The tools are stand-ins: they record the command
 * line they were started with and leave the file the real tool would leave.
 */
internal class Fixture(
  val root: Path,
  val recorder: TerminalRecorder = TerminalRecorder(),
) : Processes {
  val commands = mutableListOf<Pair<List<String>, Path>>()
  var compilerExitCode = 0
  var programExitCode = 0

  /** What the compiler writes when it runs, warnings for instance. */
  var compilerOutput = ""

  /** What a program writes, by the name of the program; a test runner's lines, for instance. */
  val programOutput = mutableMapOf<String, List<String>>()

  /** The version the compiler reports. */
  var compilerVersion = "talac 0.5.2\n"

  /** The file the compiler leaves beside its output, like TalaRT. */
  var runtimeLibrary: String? = null

  init {
    listOf("talac", "talafmt", "talalint").forEach { tool ->
      Files.createDirectories(root.resolve("tools"))
      Files.writeString(root.resolve("tools/$tool"), "")
    }
  }

  fun write(
    file: String,
    text: String = "",
  ) {
    val path = root.resolve(file)
    Files.createDirectories(path.parent)
    Files.writeString(path, text.trimIndent())
  }

  fun tazel(directory: String = "."): Tazel =
    Tazel(
      Terminal(terminalInterface = recorder),
      root.resolve(directory).normalize(),
      OperatingSystem.LINUX,
      Toolchain(OperatingSystem.LINUX, mapOf("PATH" to root.resolve("tools").toString()), null),
      this,
      "1.2.3",
    )

  fun run(
    vararg arguments: String,
    directory: String = ".",
  ): Int {
    val parsed = parseCommandLine(arguments.toList())
    check(parsed is ParseResult.Parsed) { "not a command line: ${arguments.toList()}" }
    return tazel(directory).run(parsed.command)
  }

  /** The recorded command lines of one tool, without the path of the tool. */
  fun callsOf(tool: String) =
    commands.map { it.first }.filter { it.first().endsWith(tool) }.map { it.drop(1) }

  override fun run(
    command: List<String>,
    directory: Path,
  ): Int {
    commands += command to directory
    if (!command.first().endsWith("talac")) {
      return if (command.first().contains("tools")) 0 else programExitCode
    }
    if (compilerExitCode != 0) return compilerExitCode
    val first = command[command.indexOf("--") + 1].removeSuffix(".tala")
    val extension = if ("--emit=metadata" in command) ".talameta" else ""
    val written = directory.resolve(first + extension)
    Files.writeString(written, "artifact of $first")
    runtimeLibrary?.let { Files.writeString(written.resolveSibling(it), "runtime") }
    return 0
  }

  override fun runGathered(
    command: List<String>,
    directory: Path,
  ) = GatheredRun(run(command, directory), compilerOutput)

  override fun runLines(
    command: List<String>,
    directory: Path,
    line: (String) -> Unit,
  ): Int {
    val exitCode = run(command, directory)
    programOutput[Path.of(command.first()).fileName.toString()]?.forEach(line)
    return exitCode
  }

  override fun capture(command: List<String>) = compilerVersion
}

internal fun fixture(
  recorder: TerminalRecorder = TerminalRecorder(),
  block: Fixture.() -> Unit,
) {
  val root = Files.createTempDirectory("tazel-command-").toRealPath()
  try {
    Fixture(root, recorder).block()
  } finally {
    root.toFile().deleteRecursively()
  }
}

internal fun Fixture.demo(directory: String = ".", name: String = "Demo", extra: String = "") {
  write(
    "$directory/BUILD.kts",
    """
    project { name = "$name" }
    sources {
      executable {
        entry = "$name.Program"
        exclude("Sources/Generated")
      }
    }
    $extra
    """,
  )
  write("$directory/Sources/Main.tala")
  write("$directory/Sources/Helper.tala")
  write("$directory/Sources/Generated/Skipped.tala")
}
