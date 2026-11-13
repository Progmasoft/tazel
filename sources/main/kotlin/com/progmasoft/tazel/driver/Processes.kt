/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.ProjectException
import java.nio.file.Path
import java.util.concurrent.TimeUnit
import kotlin.time.Duration.Companion.nanoseconds

/** Starts the programs Tazel runs. A test replaces it to see the commands without running them. */
interface Processes {
  /** Runs [command] in [directory] on the streams of Tazel and returns its exit code. */
  fun run(
    command: List<String>,
    directory: Path,
  ): Int

  /**
   * Runs [command] like [run] with the variables of [environment] added to those of Tazel. Only the
   * build steps of plugins pass variables; a stand-in that starts no program may ignore them.
   */
  fun runWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): Int = run(command, directory)

  /** Runs [command] like [runGathered] with the variables of [environment] added. */
  fun runGatheredWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): GatheredRun = runGathered(command, directory)

  /**
   * Runs [command] in [directory] with [environment] added, for at most [timeout], writing its
   * output and errors to [log]; a program still running then is stopped with its children. A
   * stand-in may run it without a limit.
   */
  fun runLogged(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
    log: Path,
    timeout: kotlin.time.Duration,
  ): TimedRun {
    val started = System.nanoTime()
    val result = runGatheredWith(command, directory, environment)
    java.nio.file.Files.writeString(log, result.output)
    return TimedRun(result.exitCode, (System.nanoTime() - started).nanoseconds)
  }

  /** Runs [command] and returns what it wrote to its output, or null when it failed. */
  fun capture(command: List<String>): String?

  /**
   * Runs [command] in [directory] with its output and errors gathered instead of shown, so the
   * caller can print them where it wants, above a progress bar for instance.
   */
  fun runGathered(
    command: List<String>,
    directory: Path,
  ): GatheredRun = GatheredRun(run(command, directory), "")

  /**
   * Runs [command] in [directory] and hands each line it writes, output and errors in the order
   * written, to [line] as soon as the line is complete.
   */
  fun runLines(
    command: List<String>,
    directory: Path,
    line: (String) -> Unit,
  ): Int = runGathered(command, directory).also { it.output.lines().forEach(line) }.exitCode
}

/** How a program with a time limit ended: its exit code, or `null` when it ran out of time. */
data class TimedRun(
  val exitCode: Int?,
  val elapsed: kotlin.time.Duration,
)

/** The exit code of a program and everything it wrote, output and errors in the order written. */
data class GatheredRun(
  val exitCode: Int,
  val output: String,
)

object SystemProcesses : Processes {
  override fun run(
    command: List<String>,
    directory: Path,
  ): Int = runWith(command, directory, emptyMap())

  override fun runWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): Int =
    try {
      ProcessBuilder(command)
        .directory(directory.toFile())
        .inheritIO()
        .also { it.environment().putAll(environment) }
        .start()
        .waitFor()
    } catch (error: java.io.IOException) {
      throw ProjectException("${command.first()} could not be started: ${error.message}")
    }

  override fun runGathered(
    command: List<String>,
    directory: Path,
  ): GatheredRun = runGatheredWith(command, directory, emptyMap())

  override fun runGatheredWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): GatheredRun =
    try {
      val process =
        ProcessBuilder(command)
          .directory(directory.toFile())
          .also { it.environment().putAll(environment) }
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .start()
      val output = process.inputStream.bufferedReader().readText()
      GatheredRun(process.waitFor(), output)
    } catch (error: java.io.IOException) {
      throw ProjectException("${command.first()} could not be started: ${error.message}")
    }

  override fun runLines(
    command: List<String>,
    directory: Path,
    line: (String) -> Unit,
  ): Int =
    try {
      val process =
        ProcessBuilder(command)
          .directory(directory.toFile())
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .start()
      process.inputStream.bufferedReader().forEachLine(line)
      process.waitFor()
    } catch (error: java.io.IOException) {
      throw ProjectException("${command.first()} could not be started: ${error.message}")
    }

  override fun runLogged(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
    log: Path,
    timeout: kotlin.time.Duration,
  ): TimedRun {
    val started = System.nanoTime()
    val process =
      try {
        ProcessBuilder(command)
          .directory(directory.toFile())
          .also { it.environment().putAll(environment) }
          .redirectErrorStream(true)
          .redirectOutput(log.toFile())
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .start()
      } catch (error: java.io.IOException) {
        throw ProjectException("${command.first()} could not be started: ${error.message}")
      }
    val finished = process.waitFor(timeout.inWholeMilliseconds, TimeUnit.MILLISECONDS)
    if (!finished) {
      process.descendants().forEach { it.destroyForcibly() }
      process.destroyForcibly()
      process.waitFor()
    }
    val elapsed = (System.nanoTime() - started).nanoseconds
    return TimedRun(if (finished) process.exitValue() else null, elapsed)
  }

  override fun capture(command: List<String>): String? =
    try {
      val process =
        ProcessBuilder(command)
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .start()
      val output = process.inputStream.bufferedReader().readText()
      if (process.waitFor(30, TimeUnit.SECONDS) && process.exitValue() == 0) output else null
    } catch (_: java.io.IOException) {
      null
    }

  private fun nullDevice() =
    java.io.File(if (System.getProperty("os.name").startsWith("Windows")) "NUL" else "/dev/null")
}
