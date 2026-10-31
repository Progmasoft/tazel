/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.GatheredRun
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.RemoteServer
import com.progmasoft.tazel.driver.RemoteServerSettings
import com.progmasoft.tazel.dsl.detectHost
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.CyclicBarrier
import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class ExecutionTest {
  private fun parsed(vararg arguments: String) =
    (parseCommandLine(arguments.toList()) as ParseResult.Parsed).command

  private fun Fixture.twoExecutables(extra: String = "") {
    write(
      "BUILD.kts",
      """
      project { name = "Pair" }
      sources {
        executable {
          name = "First"
          entry = "Pair.First"
          srcDir = "First"
        }
        executable {
          name = "Second"
          entry = "Pair.Second"
          srcDir = "Second"
        }
      }
      $extra
      """,
    )
    write("First/Main.tala")
    write("Second/Main.tala")
  }

  @Test
  fun readsTheFlagsOfEveryCommandThatBuilds() {
    val options =
      ExecutionOptions(
        jobs = 4,
        cpus = 6,
        memory = 8L shl 30,
        memoryPerJob = 512L shl 20,
        keepGoing = true,
        incremental = false,
        profile = "trace.json",
        remoteExecutor = "https://build.example.com",
      )
    val line =
      arrayOf(
        "-j4",
        "--cpus=6",
        "--memory",
        "8g",
        "--memory-per-job=512m",
        "-k",
        "--no-incremental",
        "--profile",
        "trace.json",
        "--remote-executor",
        "https://build.example.com",
      )
    assertEquals(options, (parsed("build", *line) as Command.Build).execution)
    assertEquals(options, (parsed("check", *line) as Command.Check).execution)
    assertEquals(options, (parsed("test", *line) as Command.Test).execution)
    assertEquals(options, (parsed("run", *line, "--", "x") as Command.Run).execution)
    assertEquals(true, (parsed("build", "--incremental") as Command.Build).execution.incremental)
    assertTrue(
      (parsed("build", "--no-remote-executor") as Command.Build).execution.noRemoteExecutor
    )
  }

  @Test
  fun refusesFlagsItCannotUse() {
    fun message(vararg arguments: String) =
      (parseCommandLine(arguments.toList()) as ParseResult.Rejected).message
    assertContains(message("build", "--jobs", "0"), "whole number from 1")
    assertContains(message("build", "--memory", "lots"), "looks like")
    assertContains(message("build", "-j2", "--jobs=3"), "written twice")
    assertContains(message("build", "--keep-going=yes"), "takes no value")
    assertContains(message("build", "--incremental", "--no-incremental"), "written twice")
  }

  @Test
  fun aJobPerProcessorRunsAtOnce() = fixture {
    twoExecutables()
    val meeting = CyclicBarrier(2)
    val processes = meetingProcesses(meeting)
    processors = 2
    assertEquals(ExitCode.SUCCESS, runWith(processes, "build"))
    assertEquals(0, meeting.numberWaiting)
    assertEquals(2, processes.compilations)
  }

  @Test
  fun theCommandLineGoesBeforeTheProjectFile() = fixture {
    twoExecutables("execution { jobs = 8 }")
    val order = mutableListOf<String>()
    val processes = recordingProcesses(order)
    assertEquals(ExitCode.SUCCESS, runWith(processes, "build", "--jobs", "1"))
    assertEquals(listOf("start", "end", "start", "end"), order)
  }

  @Test
  fun memoryLimitsTheJobs() = fixture {
    twoExecutables("""execution { memory = "1g"; memoryPerJob = "1g" }""")
    processors = 8
    val order = mutableListOf<String>()
    assertEquals(ExitCode.SUCCESS, runWith(recordingProcesses(order), "build"))
    assertEquals(listOf("start", "end", "start", "end"), order)
  }

  @Test
  fun aFailureStopsTheJobsThatDidNotStart() = fixture {
    twoExecutables()
    compilerExitCode = 1
    assertEquals(ExitCode.FAILURE, run("build", "--jobs", "1"))
    assertEquals(1, callsOf("talac").size)
  }

  @Test
  fun keepGoingFinishesTheOtherJobsAndReportsEveryFailure() = fixture {
    twoExecutables("execution { keepGoing = true }")
    compilerExitCode = 1
    assertEquals(ExitCode.FAILURE, run("build", "--jobs", "1"))
    assertEquals(2, callsOf("talac").size)
    assertContains(recorder.stderr(), "executable 'First'")
    assertContains(recorder.stderr(), "executable 'Second'")
  }

  @Test
  fun aProfileHoldsEveryJob() = fixture {
    twoExecutables()
    assertEquals(ExitCode.SUCCESS, run("build", "--profile", "trace.json"))
    val trace = Files.readString(root.resolve("trace.json"))
    assertContains(trace, "\"name\":\"First\"")
    assertContains(trace, "\"name\":\"Second\"")
  }

  @Test
  fun aDebugBuildKeepsObjectsWhenTheCompilerCan() = fixture {
    demo()
    compilerVersion = "--incremental=<directory>\n"
    assertEquals(ExitCode.SUCCESS, run("build"))
    assertTrue(callsOf("talac").last().any { it.startsWith("--incremental=.tazel/incremental/") })
  }

  @Test
  fun aCompilerWithoutTheOptionIsNotAskedForIt() = fixture {
    demo()
    assertEquals(ExitCode.SUCCESS, run("build"))
    assertFalse(callsOf("talac").last().any { it.startsWith("--incremental") })
  }

  @Test
  fun noIncrementalBuildsWhole() = fixture {
    demo()
    compilerVersion = "--incremental=<directory>\n"
    assertEquals(ExitCode.SUCCESS, run("build", "--no-incremental"))
    assertFalse(callsOf("talac").last().any { it.startsWith("--incremental") })
  }

  private fun Fixture.serverOf(
    processes: Processes,
    version: String = "0.5.2",
  ) =
    RemoteServer(
      RemoteServerSettings("127.0.0.1", 0, 2, "secret", Path.of("talac"), version, detectHost()),
      processes,
    )

  private class RemoteCompilerStandIn : Processes {
    var runs = 0

    override fun run(
      command: List<String>,
      directory: Path,
    ) = 0

    override fun capture(command: List<String>) = null

    override fun runGathered(
      command: List<String>,
      directory: Path,
    ): GatheredRun {
      runs++
      val first = command[command.indexOf("--") + 1].removeSuffix(".tala")
      Files.writeString(directory.resolve(first), "built remotely")
      return GatheredRun(0, "a remote warning\n")
    }
  }

  @Test
  fun aRemoteExecutorCompilesAndThisMachineGetsTheResult() = fixture {
    demo()
    val remote = RemoteCompilerStandIn()
    serverOf(remote).use { server ->
      server.start()
      environment["TAZEL_REMOTE_TOKEN"] = "secret"
      val address = "http://127.0.0.1:${server.port}"
      assertEquals(ExitCode.SUCCESS, run("build", "--remote-executor", address))
    }
    assertEquals(1, remote.runs)
    assertEquals(emptyList(), callsOf("talac"))
    assertEquals("built remotely", Files.readString(root.resolve("build/debug/Demo")))
    assertContains(recorder.stderr(), "a remote warning")
  }

  @Test
  fun aWrongTokenLeavesTheWorkToThisMachine() = fixture {
    demo()
    val remote = RemoteCompilerStandIn()
    serverOf(remote).use { server ->
      server.start()
      environment["TAZEL_REMOTE_TOKEN"] = "wrong"
      val address = "http://127.0.0.1:${server.port}"
      assertEquals(ExitCode.SUCCESS, run("build", "--remote-executor", address))
    }
    assertEquals(0, remote.runs)
    assertEquals(1, callsOf("talac").size)
    assertContains(recorder.stderr(), "compiling here")
  }

  @Test
  fun anotherCompilerLeavesTheWorkToThisMachine() = fixture {
    demo()
    val remote = RemoteCompilerStandIn()
    serverOf(remote, "9.9.9").use { server ->
      server.start()
      environment["TAZEL_REMOTE_TOKEN"] = "secret"
      val address = "http://127.0.0.1:${server.port}"
      assertEquals(ExitCode.SUCCESS, run("build", "--remote-executor", address))
    }
    assertEquals(0, remote.runs)
    assertContains(recorder.stderr(), "another compiler")
  }

  @Test
  fun aServerNeedsAToken() = fixture {
    assertEquals(ExitCode.FAILURE, run("remote", "serve"))
    assertContains(recorder.stderr(), "TAZEL_REMOTE_TOKEN")
  }

  @Test
  fun aRequestCannotNameAnythingOutsideItsDirectory() {
    assertTrue(isCommand(listOf("--entry=A.B", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--", "B.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--sysroot=C", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--incremental=o", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--x=../y", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--x=/etc/y", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("Plain", "--", "A.tala"), listOf("A.tala")))
    assertFalse(isCommand(listOf("--entry=A.B"), listOf("A.tala")))
    assertFalse(com.progmasoft.tazel.driver.isPlainPath("../A.tala"))
    assertFalse(com.progmasoft.tazel.driver.isPlainPath("C:/A.tala"))
    assertFalse(com.progmasoft.tazel.driver.isPlainPath("/A.tala"))
    assertTrue(com.progmasoft.tazel.driver.isPlainPath("Sources/A.tala"))
  }

  private fun isCommand(
    arguments: List<String>,
    sources: List<String>,
  ) = com.progmasoft.tazel.driver.isCompilerCommand(arguments, sources)

  /** Compilations wait for each other, so a test that finishes shows that they overlapped. */
  private class MeetingProcesses(
    private val fixture: Fixture,
    private val meeting: CyclicBarrier,
  ) : Processes by fixture {
    @Volatile var compilations = 0

    override fun runGathered(
      command: List<String>,
      directory: Path,
    ): GatheredRun {
      meeting.await(30, TimeUnit.SECONDS)
      synchronized(this) { compilations++ }
      return fixture.runGathered(command, directory)
    }
  }

  private fun Fixture.meetingProcesses(meeting: CyclicBarrier) = MeetingProcesses(this, meeting)

  private fun Fixture.recordingProcesses(order: MutableList<String>) =
    object : Processes by this {
      override fun runGathered(
        command: List<String>,
        directory: Path,
      ): GatheredRun {
        synchronized(order) { order += "start" }
        Thread.sleep(50)
        return this@recordingProcesses.runGathered(command, directory).also {
          synchronized(order) { order += "end" }
        }
      }
    }
}
