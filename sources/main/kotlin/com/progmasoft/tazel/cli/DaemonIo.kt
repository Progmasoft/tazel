/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.input.InputEvent
import com.github.ajalt.mordant.input.MouseTracking
import com.github.ajalt.mordant.rendering.AnsiLevel
import com.github.ajalt.mordant.rendering.Size
import com.github.ajalt.mordant.terminal.PrintRequest
import com.github.ajalt.mordant.terminal.TerminalInfo
import com.github.ajalt.mordant.terminal.TerminalInterface
import com.progmasoft.tazel.driver.GatheredRun
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.io.PrintStream
import java.nio.ByteBuffer
import java.nio.channels.SocketChannel
import java.nio.file.Path
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.time.TimeMark

/**
 * Reads a socket channel. `Channels.newInputStream` and `newOutputStream` of one channel share a
 * lock, so a read waiting for the peer would block every write; these streams use the channel
 * directly, and a socket channel reads and writes on two threads at once.
 */
internal class ChannelInput(private val channel: SocketChannel) : InputStream() {
  override fun read(): Int {
    val one = ByteArray(1)
    return if (read(one, 0, 1) < 0) -1 else one[0].toInt() and 0xff
  }

  override fun read(
    bytes: ByteArray,
    offset: Int,
    length: Int,
  ): Int {
    if (length == 0) return 0
    return channel.read(ByteBuffer.wrap(bytes, offset, length))
  }
}

internal class ChannelOutput(private val channel: SocketChannel) : OutputStream() {
  override fun write(byte: Int) = write(byteArrayOf(byte.toByte()), 0, 1)

  override fun write(
    bytes: ByteArray,
    offset: Int,
    length: Int,
  ) {
    val buffer = ByteBuffer.wrap(bytes, offset, length)
    while (buffer.hasRemaining()) channel.write(buffer)
  }
}

/**
 * Where the output of one run goes: frames to its client. Once the client is gone every write is
 * dropped and the actions registered with [onDisconnect] run, so programs the run started are
 * stopped.
 */
internal class RequestSink(private val writer: FrameWriter) {
  @Volatile
  var connected = true
    private set

  private val disconnectActions = CopyOnWriteArrayList<() -> Unit>()

  fun write(
    bytes: ByteArray,
    stderr: Boolean,
  ) {
    if (!connected || bytes.isEmpty()) return
    try {
      writer.write(Frame(if (stderr) FrameKind.STDERR else FrameKind.STDOUT, bytes))
    } catch (_: IOException) {
      disconnect()
    }
  }

  fun text(
    text: String,
    stderr: Boolean,
  ) = write(text.toByteArray(Charsets.UTF_8), stderr)

  fun onDisconnect(action: () -> Unit) {
    disconnectActions += action
    if (!connected) action()
  }

  fun forget(action: () -> Unit) {
    disconnectActions -= action
  }

  fun disconnect() {
    if (!connected) return
    connected = false
    disconnectActions.forEach { runCatching(it) }
  }
}

/** What the client types while a run is going on, for the programs the run starts. */
internal class RequestInput {
  private val chunks = LinkedBlockingQueue<ByteArray>()

  @Volatile
  var ended = false
    private set

  fun feed(bytes: ByteArray) {
    if (bytes.isNotEmpty()) chunks += bytes
  }

  fun end() {
    ended = true
    chunks += ByteArray(0)
  }

  /** The next chunk, an empty array at the end of the input, or null when nothing came in time. */
  fun next(
    timeout: Long,
    unit: TimeUnit,
  ): ByteArray? = chunks.poll(timeout, unit)
}

/**
 * `System.out` and `System.err` of the daemon. A project file can print, and several runs are
 * served at once, so each write goes to the client of the run on the current thread, and to the log
 * outside runs.
 */
internal class RoutedStream(
  private val fallback: PrintStream,
  private val stderr: Boolean,
) : OutputStream() {
  override fun write(byte: Int) = write(byteArrayOf(byte.toByte()), 0, 1)

  override fun write(
    bytes: ByteArray,
    offset: Int,
    length: Int,
  ) {
    val sink = currentSink.get()
    if (sink == null) {
      fallback.write(bytes, offset, length)
    } else {
      sink.write(bytes.copyOfRange(offset, offset + length), stderr)
    }
  }

  override fun flush() {
    if (currentSink.get() == null) fallback.flush()
  }

  companion object {
    private val currentSink = InheritableThreadLocal<RequestSink?>()

    /** Replaces the standard streams of the process with routed ones. */
    fun install() {
      val out = System.out
      val err = System.err
      System.setOut(PrintStream(RoutedStream(out, stderr = false), true, Charsets.UTF_8))
      System.setErr(PrintStream(RoutedStream(err, stderr = true), true, Charsets.UTF_8))
    }

    /**
     * Runs [block] with the standard streams of this thread, and of threads it starts, sent to
     * [sink].
     */
    fun <T> routedTo(
      sink: RequestSink,
      block: () -> T,
    ): T {
      val previous = currentSink.get()
      currentSink.set(sink)
      try {
        return block()
      } finally {
        System.out.flush()
        System.err.flush()
        currentSink.set(previous)
      }
    }
  }
}

/**
 * The terminal of a client as Mordant sees it in the daemon: what it can show comes from the
 * client, and what is printed goes to it.
 */
internal class ForwardingTerminal(
  private val settings: TerminalSettings,
  private val sink: RequestSink,
) : TerminalInterface {
  override fun info(
    ansiLevel: AnsiLevel?,
    hyperlinks: Boolean?,
    outputInteractive: Boolean?,
    inputInteractive: Boolean?,
  ): TerminalInfo {
    val output = outputInteractive ?: settings.outputInteractive
    return TerminalInfo(
      ansiLevel =
        ansiLevel
          ?: runCatching { AnsiLevel.valueOf(settings.ansiLevel) }.getOrDefault(AnsiLevel.NONE),
      ansiHyperLinks = hyperlinks ?: settings.hyperlinks,
      outputInteractive = output,
      inputInteractive = inputInteractive ?: settings.inputInteractive,
      supportsAnsiCursor = output,
    )
  }

  override fun completePrintRequest(request: PrintRequest) =
    sink.text(if (request.trailingLinebreak) request.text + "\n" else request.text, request.stderr)

  override fun readLineOrNull(hideInput: Boolean): String? = null

  override fun getTerminalSize(): Size = Size(settings.width, settings.height)

  override fun readInputEvent(
    timeout: TimeMark,
    mouseTracking: MouseTracking,
  ): InputEvent = throw UnsupportedOperationException("the daemon reads no terminal input")

  override fun enterRawMode(mouseTracking: MouseTracking): AutoCloseable =
    throw UnsupportedOperationException("the daemon cannot change the mode of a client terminal")

  override fun shouldAutoUpdateSize(): Boolean = false
}

/**
 * The programs a run in the daemon starts. They get the environment of the client, write to the
 * client, read what the client types, and are stopped when the client goes away.
 */
internal class ForwardingProcesses(
  private val environment: Map<String, String>,
  private val sink: RequestSink,
  private val input: RequestInput,
) : Processes {
  private fun builder(
    command: List<String>,
    directory: Path?,
    added: Map<String, String> = emptyMap(),
  ) =
    ProcessBuilder(command).also { builder ->
      directory?.let { builder.directory(it.toFile()) }
      builder.environment().clear()
      builder.environment().putAll(environment)
      builder.environment().putAll(added)
    }

  private fun started(
    command: List<String>,
    builder: ProcessBuilder,
  ): Process =
    try {
      builder.start()
    } catch (error: IOException) {
      throw ProjectConfigurationException(
        "${command.first()} could not be started: ${error.message}"
      )
    }

  override fun run(
    command: List<String>,
    directory: Path,
  ): Int = runWith(command, directory, emptyMap())

  override fun runWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): Int {
    val process = started(command, builder(command, directory, environment))
    val stop = {
      process.destroyForcibly()
      Unit
    }
    sink.onDisconnect(stop)
    try {
      val output = pump(process.inputStream, stderr = false)
      val errors = pump(process.errorStream, stderr = true)
      val typing = thread(isDaemon = true, name = "tazel-daemon-stdin") { feed(process) }
      val code = process.waitFor()
      output.join()
      errors.join()
      typing.interrupt()
      return code
    } finally {
      sink.forget(stop)
    }
  }

  private fun pump(
    stream: InputStream,
    stderr: Boolean,
  ) =
    thread(isDaemon = true, name = "tazel-daemon-output") {
      val buffer = ByteArray(8192)
      stream.use {
        while (true) {
          val count =
            try {
              it.read(buffer)
            } catch (_: IOException) {
              -1
            }
          if (count < 0) break
          sink.write(buffer.copyOf(count), stderr)
        }
      }
    }

  private fun feed(process: Process) {
    val stdin = process.outputStream
    try {
      while (process.isAlive) {
        val chunk = input.next(100, TimeUnit.MILLISECONDS) ?: continue
        if (chunk.isEmpty()) break
        stdin.write(chunk)
        stdin.flush()
      }
    } catch (_: IOException) {
      // The program closed its input.
    } catch (_: InterruptedException) {
      // The program ended.
    } finally {
      runCatching { stdin.close() }
    }
  }

  override fun runGathered(
    command: List<String>,
    directory: Path,
  ): GatheredRun = runGatheredWith(command, directory, emptyMap())

  override fun runGatheredWith(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): GatheredRun {
    val process =
      started(
        command,
        builder(command, directory, environment)
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice())),
      )
    val stop = {
      process.destroyForcibly()
      Unit
    }
    sink.onDisconnect(stop)
    try {
      val output = process.inputStream.bufferedReader().readText()
      return GatheredRun(process.waitFor(), output)
    } finally {
      sink.forget(stop)
    }
  }

  override fun runLines(
    command: List<String>,
    directory: Path,
    line: (String) -> Unit,
  ): Int {
    val process =
      started(
        command,
        builder(command, directory)
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice())),
      )
    val stop = {
      process.destroyForcibly()
      Unit
    }
    sink.onDisconnect(stop)
    try {
      process.inputStream.bufferedReader().forEachLine(line)
      return process.waitFor()
    } finally {
      sink.forget(stop)
    }
  }

  override fun capture(command: List<String>): String? =
    try {
      val process =
        builder(command, null)
          .redirectErrorStream(true)
          .redirectInput(ProcessBuilder.Redirect.from(nullDevice()))
          .start()
      val output = process.inputStream.bufferedReader().readText()
      if (process.waitFor(30, TimeUnit.SECONDS) && process.exitValue() == 0) output else null
    } catch (_: IOException) {
      null
    }

  private fun nullDevice() =
    java.io.File(if (System.getProperty("os.name").startsWith("Windows")) "NUL" else "/dev/null")
}
