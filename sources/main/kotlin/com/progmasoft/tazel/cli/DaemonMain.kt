/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import java.time.Instant
import kotlin.system.exitProcess

/** The process `tazel jvm start-daemon` starts. Its output is the log of the daemon. */
fun main() {
  // The log keeps the stream of the process; once routed, what a command prints goes to its client.
  val log = System.out
  RoutedStream.install()
  val version = tazelVersion()
  val server =
    DaemonServer(
      DaemonPaths.forInstallation(version),
      version,
      DaemonServer.idleTimeout(System::getenv),
      log = { log.println("${Instant.now()} $it") },
    )
  try {
    server.serve()
  } catch (error: DaemonAlreadyRunning) {
    log.println("${Instant.now()} ${error.message}")
    exitProcess(ExitCode.FAILURE)
  }
  exitProcess(ExitCode.SUCCESS)
}
