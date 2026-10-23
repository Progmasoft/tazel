/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.terminal.Terminal
import com.github.ajalt.mordant.terminal.TerminalRecorder
import com.progmasoft.tazel.dsl.Phase
import com.progmasoft.tazel.dsl.Timings
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.PrintStream
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertIs
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class DaemonTest {
  @Test
  fun parsesTheDaemonCommands() {
    fun parsed(vararg arguments: String) =
      assertIs<ParseResult.Parsed>(parseCommandLine(arguments.toList())).command

    assertEquals(Command.Daemon(DaemonAction.START), parsed("jvm", "start-daemon"))
    assertEquals(Command.Daemon(DaemonAction.RESTART), parsed("jvm", "restart-daemon"))
    assertEquals(Command.Daemon(DaemonAction.STOP), parsed("jvm", "stop-daemon"))
    val misspelt = assertIs<ParseResult.Rejected>(parseCommandLine(listOf("jvm", "start-deamon")))
    assertContains(misspelt.hint.orEmpty(), "did you mean tazel jvm start-daemon?")
    assertIs<ParseResult.Rejected>(parseCommandLine(listOf("jvm", "stop-daemon", "now")))
  }

  @Test
  fun framesSurviveTheRoundTrip() {
    val run =
      RunRequest(
        listOf("lock", "graph", "ü"),
        "/work",
        mapOf("PATH" to "/bin", "EMPTY" to ""),
        TerminalSettings("TRUECOLOR", true, true, false, 120, 40),
      )
    assertEquals(run, RunRequest.of(run.frame()))
    val hello = Hello("a".repeat(64), DAEMON_PROTOCOL, "1.2.3")
    assertEquals(hello, Hello.of(hello.frame()))
    val exit = RunExit(3, mapOf("COMMAND" to 42L))
    assertEquals(exit, RunExit.of(exit.frame()))
    val status = DaemonStatus(7, "1.2.3", 2)
    assertEquals(status, DaemonStatus.of(status.frame()))

    val bytes = ByteArrayOutputStream()
    FrameWriter(bytes).write(run.frame())
    FrameWriter(bytes).write(Frame(FrameKind.STDIN_END))
    val reader = FrameReader(ByteArrayInputStream(bytes.toByteArray()))
    assertEquals(run, RunRequest.of(assertNotNull(reader.read())))
    assertEquals(FrameKind.STDIN_END, reader.read()?.kind)
    assertNull(reader.read())
  }

  @Test
  fun refusesAnOversizedFrame() {
    val bytes = byteArrayOf(FrameKind.STDOUT.code.toByte(), 0x7f, 0, 0, 0)
    assertFailsWith<java.io.IOException> { FrameReader(ByteArrayInputStream(bytes)).read() }
  }

  @Test
  fun readsTheModeAndIdleTimeout() {
    assertEquals(DaemonMode.AUTO, DaemonMode.of { null })
    assertEquals(DaemonMode.OFF, DaemonMode.of { "off" })
    assertEquals(DaemonMode.START, DaemonMode.of { " Start " })
    assertEquals(Duration.ofMinutes(180), DaemonServer.idleTimeout { null })
    assertEquals(Duration.ofMinutes(5), DaemonServer.idleTimeout { "5" })
    assertNull(DaemonServer.idleTimeout { "0" })
  }

  @Test
  fun keysTheDaemonByInstallation() {
    val classpath = listOf(Path.of("tazel.jar"))
    val key = DaemonPaths.installationKey("1.2.3", "/jdk", classpath)
    assertEquals(16, key.length)
    assertEquals(key, DaemonPaths.installationKey("1.2.3", "/jdk", classpath))
    assertNotEquals(key, DaemonPaths.installationKey("1.2.4", "/jdk", classpath))
    assertNotEquals(key, DaemonPaths.installationKey("1.2.3", "/other", classpath))
    val env = mapOf("TAZEL_DAEMON_DIR" to "/state", "LOCALAPPDATA" to "C:/Local")
    assertEquals(Path.of("/state"), DaemonPaths.stateDirectory(env::get, "Linux", "/home/u"))
    assertEquals(
      Path.of("C:/Local", "Progmasoft", "Tazel"),
      DaemonPaths.stateDirectory(mapOf("LOCALAPPDATA" to "C:/Local")::get, "Windows 11", "C:/u"),
    )
    assertEquals(
      Path.of("/home/u/.local/state", "progmasoft", "tazel"),
      DaemonPaths.stateDirectory({ null }, "Linux", "/home/u"),
    )
  }

  @Test
  fun routesPrintsOfARunToItsClient() {
    val frames = ByteArrayOutputStream()
    val fallback = ByteArrayOutputStream()
    val routed = PrintStream(RoutedStream(PrintStream(fallback), stderr = false), true)
    val sink = RequestSink(FrameWriter(frames))
    RoutedStream.routedTo(sink) {
      routed.print("in the run")
      thread { routed.print(", and on its thread") }.join()
    }
    routed.print("outside")
    val reader = FrameReader(ByteArrayInputStream(frames.toByteArray()))
    val text = generateSequence { reader.read() }.joinToString("") { String(it.payload) }
    assertEquals("in the run, and on its thread", text)
    assertEquals("outside", fallback.toString())
  }

  private class Running(root: Path) : AutoCloseable {
    val paths = DaemonPaths(root.resolve("daemon"), "test")
    val log = StringBuffer()
    val server = DaemonServer(paths, "1.2.3", idleTimeout = null, log = { log.appendLine(it) })
    private val serving = thread(name = "test-daemon") { server.serve() }
    val client = DaemonClient(paths, "1.2.3")

    init {
      val deadline = System.nanoTime() + Duration.ofSeconds(20).toNanos()
      while (client.status().first == null) {
        check(System.nanoTime() < deadline) { "the daemon did not come up: $log" }
        Thread.sleep(20)
      }
    }

    /**
     * Waits until [DaemonServer.serve] returned, which is when a stopped daemon has released its
     * files.
     */
    fun awaitStopped() {
      serving.join(Duration.ofSeconds(90))
      check(!serving.isAlive) { "the daemon did not stop" }
    }

    override fun close() {
      server.stop()
      awaitStopped()
    }
  }

  private fun daemon(block: Running.(Path) -> Unit) {
    // Short: a socket path is limited to about a hundred bytes, and macOS temporary paths are long.
    val root = Files.createTempDirectory("tzd")
    try {
      Running(root).use { it.block(root) }
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  private class Result(
    val exitCode: Int?,
    val stdout: String,
    val stderr: String,
    val timings: Timings,
  )

  private fun Running.forward(
    directory: Path,
    environment: Map<String, String>,
    vararg arguments: String,
    version: String = "1.2.3",
  ): Result {
    val output = ByteArrayOutputStream()
    val errors = ByteArrayOutputStream()
    val timings = Timings(true)
    val recorder = TerminalRecorder()
    val code =
      forwardToDaemon(
        Terminal(terminalInterface = recorder),
        arguments.toList(),
        DaemonClient(paths, version),
        DaemonMode.AUTO,
        environment + ("TAZEL_TIMINGS" to "1"),
        timings,
        ByteArrayInputStream(ByteArray(0)),
        output,
        errors,
        directory,
      )
    return Result(
      code,
      output.toString(Charsets.UTF_8),
      errors.toString(Charsets.UTF_8) + recorder.stderr(),
      timings,
    )
  }

  @Test
  fun runsCommandsInTheDaemon() = daemon { root ->
    val status = assertNotNull(client.status().first)
    assertEquals(ProcessHandle.current().pid(), status.pid)
    assertEquals("1.2.3", status.tazelVersion)

    val version = forward(root, emptyMap(), "version")
    assertEquals(ExitCode.SUCCESS, version.exitCode)
    assertContains(version.stdout, "1.2.3")
    assertTrue(Phase.IPC in version.timings.totals())
    assertTrue(Phase.COMMAND in version.timings.totals())

    val project = root.resolve("Demo")
    Files.createDirectories(project)
    Files.writeString(
      project.resolve("BUILD.kts"),
      "project { name = \"Demo\" }\nsources { executable { entry = \"Demo.Program\" } }\n",
    )
    val environment = mapOf("TAZEL_CACHE_DIR" to root.resolve("cache").toString())
    repeat(2) {
      val graph = forward(project, environment, "lock", "graph")
      assertEquals(ExitCode.SUCCESS, graph.exitCode, graph.stderr)
      assertContains(graph.stdout, "Demo")
    }
    assertTrue(Files.exists(project.resolve("LOCK.sqlite3")))

    val rejected = forward(root, emptyMap(), "biuld")
    assertEquals(ExitCode.USAGE, rejected.exitCode)
    assertContains(rejected.stderr, "did you mean tazel build?")
  }

  @Test
  fun refusesStrangersAndOtherVersions() = daemon { root ->
    val other = forward(root, emptyMap(), "version", version = "9.9.9")
    assertNull(other.exitCode)
    assertContains(other.stderr, "this daemon runs tazel 1.2.3")

    Files.writeString(paths.token, "0".repeat(64))
    val stranger = assertIs<Reached.Failed>(client.connect()).why
    assertEquals(Unreachable.Refused("the token does not match this daemon"), stranger)
  }

  @Test
  fun keepsOneDaemonAndStops() = daemon { root ->
    val second = DaemonServer(paths, "1.2.3", idleTimeout = null, log = {})
    assertFailsWith<DaemonAlreadyRunning> { second.serve() }
    assertIs<DaemonStart.AlreadyRunning>(client.start(emptyMap()))

    assertTrue(client.stop())
    awaitStopped()
    assertFalse(Files.exists(paths.socket))
    assertEquals(Unreachable.Absent, client.status().second)
    assertNull(forward(root, emptyMap(), "version").exitCode)
  }
}
