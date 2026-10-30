/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class TestCommandTest {
  /** A project with [declared] in its `sources` block and [extra] below that block. */
  private fun Fixture.project(
    declared: String,
    extra: String = "",
  ) {
    write(
      "BUILD.kts",
      """
      project { name = "Demo" }
      sources {
        executable { entry = "Demo.Program" }
        $declared
      }
      $extra
      """,
    )
    write("Sources/Main.tala")
    write("Sources/Helper.tala")
  }

  private fun Fixture.suites(vararg names: String) {
    project(names.joinToString(System.lineSeparator()) { "test(\"$it\") {}" })
    names.forEach { write("Tests/$it/Case.tala") }
    names.forEach { programOutput[it] = listOf("PASS Demo.Adds", "1 test, 1 passed, 0 failed") }
  }

  private fun Fixture.runnerRuns(name: String) = commands.count {
    Path.of(it.first.first()).endsWith(".tazel/tests/$name/$name")
  }

  @Test
  fun aSuiteIsCompiledWithTheSourcesOfTheProjectAndRunFromItsRoot() = fixture {
    suites("Unit")

    assertEquals(ExitCode.SUCCESS, run("test"))

    val call = callsOf("talac").single()
    assertTrue("--test" in call)
    assertTrue(call.none { it.startsWith("--entry") })
    assertEquals(
      listOf("Sources/Helper.tala", "Sources/Main.tala", "Tests/Unit/Case.tala"),
      call.drop(call.indexOf("--") + 1),
    )
    val runner = commands.last()
    assertEquals(root.resolve(".tazel/tests/Unit/Unit").toString(), runner.first.single())
    assertEquals(root, runner.second)
    val printed = recorder.stdout()
    assertTrue(printed.contains("Demo.Adds"))
    assertTrue(printed.contains("1 suite, 1 test, 1 passed, 0 failed"))
    assertFalse(Files.exists(root.resolve("Sources/Helper")))
  }

  @Test
  fun aFailingSuiteFailsTheCommandAndTheNextSuiteStillRuns() = fixture {
    suites("Integration", "Unit")
    programOutput["Integration"] =
      listOf("FAIL Demo.Adds (1 failed expectation)", "1 test, 0 passed, 1 failed")
    programExitCode = 1

    assertEquals(ExitCode.FAILURE, run("test"))

    assertEquals(1, runnerRuns("Integration"))
    assertEquals(1, runnerRuns("Unit"))
    assertTrue(recorder.stderr().contains("2 suites, 2 tests, 1 passed, 1 failed"))
    assertTrue(recorder.stderr().contains("'Integration'"))
  }

  @Test
  fun aRunnerThatStopsBeforeItsTotalsFails() = fixture {
    suites("Unit")
    programOutput["Unit"] = listOf("PASS Demo.Adds")

    assertEquals(ExitCode.FAILURE, run("test"))

    assertTrue(recorder.stderr().contains("test suite 'Unit' stopped before it finished"))
  }

  @Test
  fun aSuiteThatDoesNotCompileEndsTheCommand() = fixture {
    suites("Unit")
    compilerExitCode = 2

    assertEquals(ExitCode.FAILURE, run("test"))

    assertEquals(0, runnerRuns("Unit"))
    assertTrue(recorder.stderr().contains("talac rejected test suite 'Unit'"))
  }

  @Test
  fun aSuiteWithoutSourcesIsAnError() = fixture {
    suites("Unit")
    Files.delete(root.resolve("Tests/Unit/Case.tala"))

    assertEquals(ExitCode.FAILURE, run("test"))

    assertTrue(recorder.stderr().contains("test suite 'Unit' has no .tala source"))
  }

  @Test
  fun aSuiteDoesNotSeeTheTestsOfAnother() = fixture {
    project(
      """
      test("Unit") { testDir = "Sources/Unit" }
      test("Other") { testDir = "Sources/Other" }
      """
    )
    write("Sources/Unit/Case.tala")
    write("Sources/Other/Case.tala")
    programOutput["Unit"] = listOf("1 test, 1 passed, 0 failed")
    programOutput["Other"] = listOf("1 test, 1 passed, 0 failed")

    assertEquals(ExitCode.SUCCESS, run("test"))

    val unit = callsOf("talac").first { "Sources/Unit/Case.tala" in it }
    assertFalse("Sources/Other/Case.tala" in unit)
  }

  @Test
  fun aSuiteThatPassedIsNotRunAgainUntilSomethingChanges() = fixture {
    suites("Unit")
    assertEquals(ExitCode.SUCCESS, run("test"))
    assertEquals(ExitCode.SUCCESS, run("test"))
    assertEquals(1, runnerRuns("Unit"))
    assertEquals(1, callsOf("talac").size)
    assertTrue(recorder.stdout().contains("Cached"))
    assertTrue(recorder.stdout().contains("1 suite, 1 test, 1 passed, 0 failed"))

    write("Sources/Helper.tala", "changed")
    assertEquals(ExitCode.SUCCESS, run("test"))
    assertEquals(2, runnerRuns("Unit"))
  }

  @Test
  fun aChangedDataFileRunsTheSuiteWithoutCompilingIt() = fixture {
    suites("Unit")
    write("Tests/Unit/expected.json", "{}")
    run("test")

    write("Tests/Unit/expected.json", "[]")
    assertEquals(ExitCode.SUCCESS, run("test"))

    assertEquals(2, runnerRuns("Unit"))
    assertEquals(1, callsOf("talac").size)
  }

  @Test
  fun aSuiteThatFailedRunsAgain() = fixture {
    suites("Unit")
    programOutput["Unit"] = listOf("1 test, 0 passed, 1 failed")
    programExitCode = 1
    run("test")
    run("test")
    assertEquals(2, runnerRuns("Unit"))
  }

  @Test
  fun whatFailedLastRunsFirst() = fixture {
    suites("First", "Second")
    programOutput["Second"] = listOf("1 test, 0 passed, 1 failed")
    run("test")
    assertEquals(
      listOf("First", "Second"),
      commands.map { Path.of(it.first.first()).fileName.toString() }.filter { it in programOutput },
    )

    commands.clear()
    programOutput["Second"] = listOf("1 test, 1 passed, 0 failed")
    write("Sources/Helper.tala", "changed")
    run("test")
    assertEquals(
      listOf("Second", "First"),
      commands.map { Path.of(it.first.first()).fileName.toString() }.filter { it in programOutput },
    )
  }

  @Test
  fun noCacheRunsASuiteThatPassed() = fixture {
    suites("Unit")
    run("test")
    assertEquals(ExitCode.SUCCESS, run("test", "--no-cache"))
    assertEquals(2, runnerRuns("Unit"))
  }

  @Test
  fun aTestDependencyIsRefusedLikeABuildDependency() = fixture {
    suites("Unit")
    project(
      "test(\"Unit\") {}",
      "dependencies { localTestDependency { path = \"Fixtures.vipkg\" } }",
    )
    write("Fixtures.vipkg")

    assertEquals(ExitCode.FAILURE, run("test"))

    assertTrue(recorder.stderr().contains("Fixtures.vipkg"))
  }
}
