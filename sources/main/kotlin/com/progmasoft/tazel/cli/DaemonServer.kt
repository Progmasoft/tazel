/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.terminal.Terminal
import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.dsl.Phase
import com.progmasoft.tazel.dsl.ScriptCache
import com.progmasoft.tazel.dsl.StaleEnvironmentException
import com.progmasoft.tazel.dsl.Timings
import com.progmasoft.tazel.dsl.detectHost
import java.io.IOException
import java.net.StandardProtocolFamily
import java.net.UnixDomainSocketAddress
import java.nio.channels.ClosedChannelException
import java.nio.channels.FileChannel
import java.nio.channels.OverlappingFileLockException
import java.nio.channels.ServerSocketChannel
import java.nio.channels.SocketChannel
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardOpenOption
import java.security.MessageDigest
import java.time.Duration
import java.util.concurrent.Semaphore
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.concurrent.thread

/** Another daemon of the same installation and user is already running. */
internal class DaemonAlreadyRunning : Exception("a daemon of this installation is already running")

/**
 * The daemon: a long-lived JVM that runs the commands of Tazel for clients on the same machine, so
 * that the JVM, the Kotlin scripting host and the classes they load stay warm between commands.
 *
 * It listens on a Unix domain socket in the private directory of [paths]. A client must send the
 * token the daemon wrote there, which only the user can read, and the protocol and version of this
 * daemon; anything else is refused. Each command runs on its own thread with its own terminal,
 * environment, working directory and project contexts; at most [maximumConcurrentRuns] run at once.
 * The daemon stops when asked, or after [idleTimeout] without a client; a stop lets running
 * commands finish for at most [shutdownGrace] and then ends them.
 */
internal class DaemonServer(
  private val paths: DaemonPaths,
  private val tazelVersion: String,
  private val idleTimeout: Duration?,
  private val maximumConcurrentRuns: Int = 8,
  private val shutdownGrace: Duration = Duration.ofSeconds(60),
  private val log: (String) -> Unit = { println(it) },
) {
  private val runs = AtomicInteger()
  private val slots = Semaphore(maximumConcurrentRuns)
  private val lastActivity = AtomicLong(System.nanoTime())

  @Volatile private var stopping = false
  @Volatile private var server: ServerSocketChannel? = null
  private lateinit var token: String

  /**
   * Serves clients until the daemon is stopped; throws [DaemonAlreadyRunning] when another serves.
   */
  fun serve() {
    paths.prepare()
    FileChannel.open(paths.lock, StandardOpenOption.CREATE, StandardOpenOption.WRITE).use { lockFile
      ->
      val lock =
        try {
          lockFile.tryLock()
        } catch (_: OverlappingFileLockException) {
          null
        } ?: throw DaemonAlreadyRunning()
      try {
        Files.deleteIfExists(paths.socket)
        val channel =
          ServerSocketChannel.open(StandardProtocolFamily.UNIX)
            .bind(UnixDomainSocketAddress.of(paths.socket))
        server = channel
        token = paths.newToken()
        Files.writeString(paths.pid, ProcessHandle.current().pid().toString())
        log("tazel daemon $tazelVersion listening on ${paths.socket}")
        idleTimeout?.let(::watchIdleness)
        accept(channel)
        awaitRuns()
      } finally {
        server?.let { runCatching { it.close() } }
        paths.clean()
        lock.release()
        log("tazel daemon stopped")
      }
    }
  }

  /** Stops accepting clients; [serve] returns once the running commands are over. */
  fun stop() {
    if (stopping) return
    stopping = true
    log("tazel daemon stopping")
    server?.let { runCatching { it.close() } }
  }

  private fun accept(channel: ServerSocketChannel) {
    while (!stopping) {
      val client =
        try {
          channel.accept()
        } catch (_: ClosedChannelException) {
          break
        } catch (error: IOException) {
          if (stopping) break
          log("accept failed: ${error.message}")
          continue
        }
      thread(isDaemon = true, name = "tazel-daemon-client") { serve(client) }
    }
  }

  private fun awaitRuns() {
    val deadline = System.nanoTime() + shutdownGrace.toNanos()
    while (runs.get() > 0 && System.nanoTime() < deadline) Thread.sleep(50)
    if (runs.get() > 0)
      log("${runs.get()} command(s) still running after ${shutdownGrace.seconds} s; ending them")
  }

  private fun watchIdleness(timeout: Duration) {
    thread(isDaemon = true, name = "tazel-daemon-idle") {
      while (!stopping) {
        Thread.sleep(1_000)
        val idle = Duration.ofNanos(System.nanoTime() - lastActivity.get())
        if (runs.get() == 0 && idle >= timeout) {
          log("idle for ${idle.toMinutes()} min")
          stop()
        }
      }
    }
  }

  private fun touch() = lastActivity.set(System.nanoTime())

  private fun serve(client: SocketChannel) {
    touch()
    client.use {
      try {
        val reader = FrameReader(ChannelInput(client))
        val writer = FrameWriter(ChannelOutput(client))
        val hello = reader.read()?.takeIf { it.kind == FrameKind.HELLO }?.let(Hello::of) ?: return
        refusal(hello)?.let { reason ->
          writer.write(FrameKind.REFUSED) { writeText(reason) }
          return
        }
        writer.write(FrameKind.WELCOME)
        val request = reader.read() ?: return
        when (request.kind) {
          FrameKind.PING ->
            writer.write(
              DaemonStatus(ProcessHandle.current().pid(), tazelVersion, runs.get()).frame()
            )
          FrameKind.STOP -> {
            writer.write(FrameKind.STOPPING)
            stop()
          }
          FrameKind.RUN -> run(RunRequest.of(request), reader, writer)
          else -> return
        }
      } catch (error: IOException) {
        log("client failed: ${error.message}")
      } finally {
        touch()
      }
    }
  }

  private fun refusal(hello: Hello): String? {
    val expected = token.toByteArray(Charsets.UTF_8)
    if (!MessageDigest.isEqual(hello.token.toByteArray(Charsets.UTF_8), expected)) {
      return "the token does not match this daemon"
    }
    if (hello.protocol != DAEMON_PROTOCOL || hello.tazelVersion != tazelVersion) {
      return "this daemon runs tazel $tazelVersion with protocol $DAEMON_PROTOCOL; " +
        "the client is tazel ${hello.tazelVersion} with protocol ${hello.protocol}"
    }
    if (stopping) return "the daemon is stopping"
    return null
  }

  private fun run(
    request: RunRequest,
    reader: FrameReader,
    writer: FrameWriter,
  ) {
    slots.acquire()
    runs.incrementAndGet()
    try {
      val sink = RequestSink(writer)
      val input = RequestInput()
      thread(isDaemon = true, name = "tazel-daemon-input") { listen(reader, sink, input) }
      val timings = Timings.fromEnvironment(request.environment::get)
      val code =
        try {
          RoutedStream.routedTo(sink) { execute(request, sink, input, timings) }
        } catch (_: StaleEnvironmentException) {
          // Nothing has happened yet: the first thing a command does is to load the project.
          if (sink.connected) writer.write(Frame(FrameKind.STALE))
          return
        }
      if (sink.connected) {
        val phases = timings.totals().map { (phase, duration) -> phase.name to duration.toNanos() }
        writer.write(RunExit(code, phases.toMap()).frame())
      }
    } finally {
      runs.decrementAndGet()
      slots.release()
    }
  }

  /** Takes what the client sends during a run: its typing, and the end of the connection. */
  private fun listen(
    reader: FrameReader,
    sink: RequestSink,
    input: RequestInput,
  ) {
    try {
      while (true) {
        val frame = reader.read() ?: break
        when (frame.kind) {
          FrameKind.STDIN -> input.feed(frame.payload)
          FrameKind.STDIN_END -> input.end()
          else -> break
        }
      }
    } catch (_: IOException) {
      // The client went away.
    }
    input.end()
    sink.disconnect()
  }

  private fun execute(
    request: RunRequest,
    sink: RequestSink,
    input: RequestInput,
    timings: Timings,
  ): Int {
    val parsed = parseCommandLine(request.arguments)
    val output = (parsed as? ParseResult.Parsed)?.output ?: OutputOptions()
    val terminal =
      outputTerminal(output) { level, interactive ->
        Terminal(
          ansiLevel = level,
          interactive = interactive,
          terminalInterface = ForwardingTerminal(request.terminal, sink),
        )
      }
    return when (parsed) {
      is ParseResult.Rejected -> {
        reportError(terminal, parsed.message, parsed.hint)
        ExitCode.USAGE
      }
      is ParseResult.Parsed -> {
        val host = detectHost()
        val tazel =
          Tazel(
            terminal,
            Path.of(request.directory),
            host.os,
            Toolchain(host.os, request.environment),
            ForwardingProcesses(request.environment, sink, input),
            tazelVersion,
            ScriptCache.forUser(request.environment::get),
            timings,
            parsed.output,
            request.environment,
          )
        timings.measure(Phase.COMMAND) { tazel.run(parsed.command) }
      }
    }
  }

  companion object {
    /**
     * How long the daemon waits for a client before it stops: `TAZEL_DAEMON_IDLE_MINUTES`, 0 never.
     */
    fun idleTimeout(environment: (String) -> String?): Duration? {
      val minutes = environment("TAZEL_DAEMON_IDLE_MINUTES")?.trim()?.toLongOrNull() ?: 180
      return if (minutes <= 0) null else Duration.ofMinutes(minutes)
    }
  }
}
