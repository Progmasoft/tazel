/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import java.io.File
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.lang.management.ManagementFactory
import java.net.StandardProtocolFamily
import java.net.UnixDomainSocketAddress
import java.nio.channels.FileChannel
import java.nio.channels.OverlappingFileLockException
import java.nio.channels.SocketChannel
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption
import java.time.Duration
import kotlin.concurrent.thread

/** Whether a command goes to the daemon: `TAZEL_DAEMON`. */
internal enum class DaemonMode {
  /** Every command runs in the process that read it. */
  OFF,

  /** A command goes to the daemon when one is running, and runs directly otherwise; the default. */
  AUTO,

  /** A command goes to the daemon, which is started first when none is running. */
  START;

  companion object {
    fun of(environment: (String) -> String?): DaemonMode =
      when (environment("TAZEL_DAEMON")?.trim()?.lowercase()) {
        "0",
        "false",
        "off",
        "never" -> OFF
        "start",
        "always" -> START
        else -> AUTO
      }
  }
}

/** A daemon answered the handshake: the connection is ready for one request. */
internal class DaemonConnection(
  private val channel: SocketChannel,
  val reader: FrameReader,
  val writer: FrameWriter,
) : AutoCloseable {
  /**
   * Closes the connection after [timeout] unless [block] returned first, so a hung daemon is
   * noticed.
   */
  fun <T> within(
    timeout: Duration,
    block: () -> T,
  ): T {
    val watchdog =
      thread(isDaemon = true, name = "tazel-daemon-watchdog") {
        try {
          Thread.sleep(timeout.toMillis())
          close()
        } catch (_: InterruptedException) {
          // Answered in time.
        }
      }
    try {
      return block()
    } finally {
      watchdog.interrupt()
    }
  }

  override fun close() {
    runCatching { channel.close() }
  }
}

/** Why a client could not use a daemon. */
internal sealed interface Unreachable {
  /** No daemon is running for this installation. */
  data object Absent : Unreachable

  /** A daemon holds the lock but does not answer: it hangs, or is still starting. */
  data object Silent : Unreachable

  /** A daemon answered and would not serve this client. */
  data class Refused(val reason: String) : Unreachable
}

/** What a client got from a daemon: a connection, or why there is none. */
internal sealed interface Reached {
  data class Ready(val connection: DaemonConnection) : Reached

  data class Failed(val why: Unreachable) : Reached
}

/**
 * The side of the daemon in the process a user starts: finds the daemon of this installation, talks
 * to it, starts and stops it.
 */
internal class DaemonClient(
  private val paths: DaemonPaths,
  private val tazelVersion: String,
  private val handshakeTimeout: Duration = Duration.ofSeconds(5),
) {
  /** Connects and introduces this client to the daemon. */
  fun connect(): Reached {
    val token = paths.readToken() ?: return Reached.Failed(absentOrSilent())
    val channel =
      try {
        SocketChannel.open(StandardProtocolFamily.UNIX).also {
          it.connect(UnixDomainSocketAddress.of(paths.socket))
        }
      } catch (_: IOException) {
        return Reached.Failed(absentOrSilent())
      }
    val connection =
      DaemonConnection(
        channel,
        FrameReader(ChannelInput(channel)),
        FrameWriter(ChannelOutput(channel)),
      )
    val answer =
      try {
        connection.within(handshakeTimeout) {
          connection.writer.write(Hello(token, DAEMON_PROTOCOL, tazelVersion).frame())
          connection.reader.read()
        }
      } catch (_: IOException) {
        null
      }
    return when (answer?.kind) {
      FrameKind.WELCOME -> Reached.Ready(connection)
      FrameKind.REFUSED -> {
        connection.close()
        Reached.Failed(Unreachable.Refused(answer.decode { readText() }))
      }
      else -> {
        connection.close()
        Reached.Failed(absentOrSilent())
      }
    }
  }

  /**
   * [Unreachable.Absent] when no process holds the lock of the daemon, after removing what a daemon
   * that died left behind; [Unreachable.Silent] when one does.
   */
  private fun absentOrSilent(): Unreachable {
    if (!Files.exists(paths.lock)) return Unreachable.Absent
    return try {
      FileChannel.open(paths.lock, StandardOpenOption.WRITE).use { channel ->
        val lock = channel.tryLock() ?: return Unreachable.Silent
        lock.release()
        paths.clean()
        Unreachable.Absent
      }
    } catch (_: OverlappingFileLockException) {
      // This process runs the daemon itself, as tests do.
      Unreachable.Silent
    } catch (_: IOException) {
      Unreachable.Absent
    }
  }

  /** What the daemon says about itself, or why it says nothing. */
  fun status(): Pair<DaemonStatus?, Unreachable?> =
    when (val reached = connect()) {
      is Reached.Failed -> null to reached.why
      is Reached.Ready ->
        reached.connection.use { connection ->
          try {
            connection.within(handshakeTimeout) {
              connection.writer.write(Frame(FrameKind.PING))
              val pong = connection.reader.read()?.takeIf { it.kind == FrameKind.PONG }
              if (pong == null) null to Unreachable.Silent else DaemonStatus.of(pong) to null
            }
          } catch (_: IOException) {
            null to Unreachable.Silent
          }
        }
    }

  /**
   * Runs [request] in the daemon on [connection]: hands it what arrives on [input] and copies what
   * it prints to [output] and [errors]. Null when the daemon went away before the command ended.
   */
  fun run(
    connection: DaemonConnection,
    request: RunRequest,
    input: InputStream,
    output: OutputStream,
    errors: OutputStream,
  ): RunExit? = connection.use {
    try {
      connection.writer.write(request.frame())
      thread(isDaemon = true, name = "tazel-client-stdin") { forward(input, connection.writer) }
      copyUntilExit(connection.reader, output, errors)
    } catch (_: IOException) {
      null
    }
  }

  private fun copyUntilExit(
    reader: FrameReader,
    output: OutputStream,
    errors: OutputStream,
  ): RunExit? {
    while (true) {
      val frame = reader.read() ?: return null
      when (frame.kind) {
        FrameKind.STDOUT -> output.write(frame.payload).also { output.flush() }
        FrameKind.STDERR -> errors.write(frame.payload).also { errors.flush() }
        FrameKind.EXIT -> return RunExit.of(frame)
        FrameKind.STALE -> return RunExit(ExitCode.SUCCESS, emptyMap(), stale = true)
        else -> return null
      }
    }
  }

  private fun forward(
    input: InputStream,
    writer: FrameWriter,
  ) {
    val buffer = ByteArray(8192)
    try {
      while (true) {
        val count = input.read(buffer)
        if (count < 0) break
        if (count > 0) writer.write(Frame(FrameKind.STDIN, buffer.copyOf(count)))
      }
      writer.write(Frame(FrameKind.STDIN_END))
    } catch (_: IOException) {
      // The run is over, or the daemon went away.
    }
  }

  /** Asks the daemon to stop and waits until its process is gone; false when none was running. */
  fun stop(wait: Duration = Duration.ofSeconds(75)): Boolean {
    val pid = runningPid()
    val reached = connect()
    if (reached is Reached.Ready) {
      reached.connection.use { connection ->
        runCatching {
          connection.within(handshakeTimeout) {
            connection.writer.write(Frame(FrameKind.STOP))
            connection.reader.read()
          }
        }
      }
    } else if (pid == null) {
      return false
    }
    val process = pid?.let { ProcessHandle.of(it).orElse(null) }
    if (process != null && process.pid() != ProcessHandle.current().pid()) {
      // A daemon that does not answer is ended; one that answered gets time to finish its commands.
      val patience = if (reached is Reached.Ready) wait else Duration.ofSeconds(2)
      if (!waitFor(process, patience)) {
        process.destroy()
        if (!waitFor(process, Duration.ofSeconds(10))) process.destroyForcibly()
        waitFor(process, Duration.ofSeconds(10))
      }
    }
    if (absentOrSilent() == Unreachable.Absent) paths.clean()
    return true
  }

  private fun runningPid(): Long? =
    runCatching { Files.readString(paths.pid).trim().toLong() }
      .getOrNull()
      ?.takeIf { pid -> ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false) }

  private fun waitFor(
    process: ProcessHandle,
    timeout: Duration,
  ): Boolean {
    val deadline = System.nanoTime() + timeout.toNanos()
    while (process.isAlive && System.nanoTime() < deadline) Thread.sleep(100)
    return !process.isAlive
  }

  /**
   * Starts a daemon unless one already answers, and waits until it does. The daemon runs on the
   * Java runtime and classpath of this process, detached from its terminal, with its output in the
   * log.
   */
  fun start(
    environment: Map<String, String>,
    wait: Duration = Duration.ofSeconds(30),
  ): DaemonStart {
    val (running, why) = status()
    if (running != null) return DaemonStart.AlreadyRunning(running)
    if (why == Unreachable.Silent) return DaemonStart.Failed(SILENT)
    if (why is Unreachable.Refused) return DaemonStart.Failed(why.reason)
    paths.prepare()
    val process =
      try {
        ProcessBuilder(launchCommand(environment))
          .redirectErrorStream(true)
          .redirectOutput(paths.log.toFile())
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .also { builder ->
            builder.environment().clear()
            builder.environment().putAll(environment)
          }
          .start()
      } catch (error: IOException) {
        return DaemonStart.Failed("the daemon could not be started: ${error.message}")
      }
    val deadline = System.nanoTime() + wait.toNanos()
    while (System.nanoTime() < deadline) {
      status().first?.let {
        return DaemonStart.Started(it)
      }
      if (!process.isAlive) break
      Thread.sleep(100)
    }
    if (process.isAlive) process.destroyForcibly()
    return DaemonStart.Failed("the daemon did not come up; see ${paths.log}")
  }

  private fun launchCommand(environment: Map<String, String>): List<String> {
    val windows = System.getProperty("os.name").startsWith("Windows")
    val bin = Path.of(System.getProperty("java.home"), "bin")
    // javaw has no console, so the daemon outlives the console window the client ran in.
    val java = bin.resolve(if (windows) "javaw.exe" else "java").toString()
    val inherited =
      ManagementFactory.getRuntimeMXBean().inputArguments.filterNot { argument ->
        argument.startsWith("-agentlib:jdwp") || argument.startsWith("-Xmx")
      }
    val heap = environment["TAZEL_DAEMON_MAX_HEAP"]?.trim()?.takeIf(String::isNotEmpty) ?: "2g"
    val command =
      listOf(java) +
        inherited +
        listOf(
          "-Xmx$heap",
          // Class loaders of project files that leak fill the metaspace; the daemon then exits
          // rather
          // than growing without bound, and the next command starts a fresh one or runs directly.
          "-XX:MaxMetaspaceSize=1g",
          "-XX:+ExitOnOutOfMemoryError",
          "-cp",
          System.getProperty("java.class.path"),
          "com.progmasoft.tazel.cli.DaemonMainKt",
        )
    if (windows) return command
    // Without nohup the daemon would end with the terminal it was started from.
    val nohup = listOf("/usr/bin/nohup", "/bin/nohup").firstOrNull { File(it).canExecute() }
    return listOfNotNull(nohup) + command
  }

  private fun nullDevice() =
    File(if (System.getProperty("os.name").startsWith("Windows")) "NUL" else "/dev/null")

  companion object {
    const val SILENT =
      "a daemon of this installation holds its lock but does not answer; " +
        "tazel jvm restart-daemon ends it and starts a new one"
  }
}

/** How [DaemonClient.start] went. */
internal sealed interface DaemonStart {
  data class Started(val status: DaemonStatus) : DaemonStart

  data class AlreadyRunning(val status: DaemonStatus) : DaemonStart

  data class Failed(val reason: String) : DaemonStart
}
