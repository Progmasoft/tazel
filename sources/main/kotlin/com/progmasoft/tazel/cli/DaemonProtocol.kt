/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream

/** Bumped whenever a frame changes; a client and a daemon of different protocols do not talk. */
internal const val DAEMON_PROTOCOL = 1

/**
 * The largest frame either side accepts, so a confused peer cannot make the other allocate
 * gigabytes.
 */
private const val MAXIMUM_FRAME = 16 * 1024 * 1024

/**
 * The kinds of frame. A frame is one byte of kind, four bytes of length and the payload.
 *
 * A client opens with [HELLO] and then sends [RUN], [PING] or [STOP]; during a run it may send
 * [STDIN] and [STDIN_END]. The daemon answers [WELCOME] or [REFUSED], then [STDOUT] and [STDERR]
 * while a command runs, and [EXIT] at its end; a ping is answered with [PONG] and a stop with
 * [STOPPING].
 */
internal enum class FrameKind(val code: Int) {
  HELLO(1),
  RUN(2),
  STDIN(3),
  STDIN_END(4),
  PING(5),
  STOP(6),
  WELCOME(16),
  REFUSED(17),
  STDOUT(18),
  STDERR(19),
  EXIT(20),
  PONG(21),
  STOPPING(22);

  companion object {
    private val byCode = entries.associateBy(FrameKind::code)

    fun of(code: Int): FrameKind = byCode[code] ?: throw IOException("unknown frame kind $code")
  }
}

internal class Frame(
  val kind: FrameKind,
  val payload: ByteArray = ByteArray(0),
)

/**
 * Writes frames; safe to use from several threads, since the output of a run is written by several.
 */
internal class FrameWriter(output: OutputStream) {
  private val stream = DataOutputStream(output.buffered())

  @Synchronized
  fun write(frame: Frame) {
    stream.writeByte(frame.kind.code)
    stream.writeInt(frame.payload.size)
    stream.write(frame.payload)
    stream.flush()
  }

  fun write(
    kind: FrameKind,
    build: DataOutputStream.() -> Unit = {},
  ) = write(Frame(kind, encode(build)))
}

internal class FrameReader(input: InputStream) {
  private val stream = DataInputStream(input.buffered())

  /** The next frame, or null when the peer closed the connection between frames. */
  fun read(): Frame? {
    val code =
      try {
        stream.readUnsignedByte()
      } catch (_: EOFException) {
        return null
      }
    val size = stream.readInt()
    if (size !in 0..MAXIMUM_FRAME) throw IOException("frame of $size bytes refused")
    val payload = ByteArray(size)
    stream.readFully(payload)
    return Frame(FrameKind.of(code), payload)
  }
}

internal fun encode(build: DataOutputStream.() -> Unit): ByteArray =
  ByteArrayOutputStream().also { bytes -> DataOutputStream(bytes).use(build) }.toByteArray()

internal fun <T> Frame.decode(read: DataInputStream.() -> T): T =
  DataInputStream(ByteArrayInputStream(payload)).use(read)

internal fun DataOutputStream.writeText(text: String) {
  val bytes = text.toByteArray(Charsets.UTF_8)
  writeInt(bytes.size)
  write(bytes)
}

internal fun DataInputStream.readText(): String {
  val size = readInt()
  if (size !in 0..MAXIMUM_FRAME) throw IOException("text of $size bytes refused")
  return String(readNBytes(size).also { if (it.size != size) throw EOFException() }, Charsets.UTF_8)
}

/** The first frame of a client: who it is and which daemon it expects. */
internal data class Hello(
  val token: String,
  val protocol: Int,
  val tazelVersion: String,
) {
  fun frame() =
    Frame(
      FrameKind.HELLO,
      encode {
        writeText(token)
        writeInt(protocol)
        writeText(tazelVersion)
      },
    )

  companion object {
    fun of(frame: Frame) = frame.decode { Hello(readText(), readInt(), readText()) }
  }
}

/** What a terminal of the client can show, so the daemon renders for it. */
internal data class TerminalSettings(
  val ansiLevel: String,
  val hyperlinks: Boolean,
  val outputInteractive: Boolean,
  val inputInteractive: Boolean,
  val width: Int,
  val height: Int,
)

/** A command line of the client, with everything the run depends on outside the arguments. */
internal data class RunRequest(
  val arguments: List<String>,
  val directory: String,
  val environment: Map<String, String>,
  val terminal: TerminalSettings,
) {
  fun frame() =
    Frame(
      FrameKind.RUN,
      encode {
        writeInt(arguments.size)
        arguments.forEach(::writeText)
        writeText(directory)
        writeInt(environment.size)
        environment.forEach { (name, value) ->
          writeText(name)
          writeText(value)
        }
        writeText(terminal.ansiLevel)
        writeBoolean(terminal.hyperlinks)
        writeBoolean(terminal.outputInteractive)
        writeBoolean(terminal.inputInteractive)
        writeInt(terminal.width)
        writeInt(terminal.height)
      },
    )

  companion object {
    fun of(frame: Frame) = frame.decode {
      val arguments = List(readInt()) { readText() }
      val directory = readText()
      val environment = buildMap { repeat(readInt()) { put(readText(), readText()) } }
      val terminal =
        TerminalSettings(
          readText(),
          readBoolean(),
          readBoolean(),
          readBoolean(),
          readInt(),
          readInt(),
        )
      RunRequest(arguments, directory, environment, terminal)
    }
  }
}

/**
 * The end of a run: its exit code and, when timings were asked for, the durations of its phases.
 */
internal data class RunExit(
  val exitCode: Int,
  val phases: Map<String, Long>,
) {
  fun frame() =
    Frame(
      FrameKind.EXIT,
      encode {
        writeInt(exitCode)
        writeInt(phases.size)
        phases.forEach { (phase, nanos) ->
          writeText(phase)
          writeLong(nanos)
        }
      },
    )

  companion object {
    fun of(frame: Frame) = frame.decode {
      val code = readInt()
      RunExit(code, buildMap { repeat(readInt()) { put(readText(), readLong()) } })
    }
  }
}

/** What a daemon says about itself in answer to a ping. */
internal data class DaemonStatus(
  val pid: Long,
  val tazelVersion: String,
  val runningCommands: Int,
) {
  fun frame() =
    Frame(
      FrameKind.PONG,
      encode {
        writeLong(pid)
        writeText(tazelVersion)
        writeInt(runningCommands)
      },
    )

  companion object {
    fun of(frame: Frame) = frame.decode { DaemonStatus(readLong(), readText(), readInt()) }
  }
}
