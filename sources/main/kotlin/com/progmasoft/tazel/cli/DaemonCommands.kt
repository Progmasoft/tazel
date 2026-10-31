/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.rendering.TextColors.brightGreen
import com.github.ajalt.mordant.rendering.TextStyles.bold
import com.github.ajalt.mordant.terminal.Terminal
import com.progmasoft.tazel.dsl.Phase
import com.progmasoft.tazel.dsl.Timings
import java.io.InputStream
import java.io.OutputStream
import java.nio.file.Path
import java.time.Duration

/** Runs `tazel jvm start-daemon`, `restart-daemon` or `stop-daemon`. */
internal fun runDaemonCommand(
  terminal: Terminal,
  action: DaemonAction,
  client: DaemonClient,
  environment: Map<String, String>,
): Int {
  fun done(message: String) = terminal.println((bold + brightGreen)("daemon") + ": " + message)
  fun start(): Int =
    when (val started = client.start(environment)) {
      is DaemonStart.Started -> {
        done("started, process ${started.status.pid}")
        ExitCode.SUCCESS
      }
      is DaemonStart.AlreadyRunning -> {
        done("already running, process ${started.status.pid}")
        ExitCode.SUCCESS
      }
      is DaemonStart.Failed -> {
        reportError(terminal, started.reason)
        ExitCode.FAILURE
      }
    }
  return when (action) {
    DaemonAction.START -> start()
    DaemonAction.STOP -> {
      done(if (client.stop()) "stopped" else "none was running")
      ExitCode.SUCCESS
    }
    DaemonAction.RESTART -> {
      client.stop()
      start()
    }
  }
}

/**
 * Hands a command line to the daemon as [mode] allows, and returns its exit code; null when the
 * command is to run in this process instead. The time between sending the command and its end that
 * the daemon did not spend on the command is recorded as IPC.
 */
internal fun forwardToDaemon(
  terminal: Terminal,
  arguments: List<String>,
  client: DaemonClient,
  mode: DaemonMode,
  environment: Map<String, String>,
  timings: Timings,
  input: InputStream = System.`in`,
  output: OutputStream = System.out,
  errors: OutputStream = System.err,
  directory: Path = Path.of("").toAbsolutePath(),
): Int? {
  if (mode == DaemonMode.OFF) return null
  val info = terminal.terminalInfo
  val request =
    RunRequest(
      arguments,
      directory.toString(),
      environment,
      TerminalSettings(
        info.ansiLevel.name,
        info.ansiHyperLinks,
        info.outputInteractive,
        info.inputInteractive,
        terminal.size.width,
        terminal.size.height,
      ),
    )
  // Detecting the terminal and starting a daemon are not the cost of talking to one.
  var started = System.nanoTime()
  var reached = client.connect()
  if (reached is Reached.Failed && reached.why == Unreachable.Absent && mode == DaemonMode.START) {
    when (val start = client.start(environment)) {
      is DaemonStart.Failed -> {
        reportError(terminal, start.reason, "running the command without the daemon")
        return null
      }
      else -> {
        started = System.nanoTime()
        reached = client.connect()
      }
    }
  }
  val connection =
    when (reached) {
      is Reached.Ready -> reached.connection
      is Reached.Failed -> {
        when (val why = reached.why) {
          Unreachable.Absent -> Unit
          Unreachable.Silent ->
            reportError(terminal, DaemonClient.SILENT, "running the command without the daemon")
          is Unreachable.Refused ->
            reportError(terminal, why.reason, "running the command without the daemon")
        }
        return null
      }
    }
  val exit = client.run(connection, request, input, output, errors)
  if (exit == null) {
    reportError(terminal, "the daemon went away before the command ended; see its log")
    return ExitCode.FAILURE
  }
  if (exit.stale) {
    // The project file reads a variable that changed since the daemon started. The command runs
    // here, and the next one starts a daemon that sees the new value.
    client.stop()
    return null
  }
  val total = Duration.ofNanos(System.nanoTime() - started)
  var command = Duration.ZERO
  exit.phases.forEach { (name, nanos) ->
    val phase = Phase.entries.firstOrNull { it.name == name } ?: return@forEach
    val duration = Duration.ofNanos(nanos)
    if (phase == Phase.COMMAND) command = duration
    timings.record(phase, duration)
  }
  timings.record(Phase.IPC, (total - command).coerceAtLeast(Duration.ZERO))
  return exit.exitCode
}
