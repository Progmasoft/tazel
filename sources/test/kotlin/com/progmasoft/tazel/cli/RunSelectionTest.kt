/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertIs

class RunSelectionTest {
  private fun Fixture.twoPrograms(project: String = "") {
    write(
      "BUILD.kts",
      """
      project {
        name = "App"
        $project
      }
      sources {
        executable {
          name = "Server"
          entry = "App.Server"
        }
        executable {
          name = "Client"
          entry = "App.Client"
        }
      }
      """,
    )
    write("Sources/Main.tala")
  }

  private fun Fixture.ran() = commands.map { it.first.first() }.filterNot { it.endsWith("talac") }

  @Test
  fun theOnlyExecutableRunsWithoutBeingNamed() = fixture {
    demo()
    assertEquals(ExitCode.SUCCESS, run("run"))
    assertEquals(listOf(root.resolve("build/debug/Demo").toString()), ran())
    // The program reads what the user types; the compiler that built it did not.
    assertEquals(listOf(listOf(root.resolve("build/debug/Demo").toString())), interactive)
  }

  @Test
  fun severalExecutablesNeedANameOrADefault() = fixture {
    twoPrograms()
    assertEquals(ExitCode.FAILURE, run("run"))
    assertContains(recorder.stderr(), "several executables (App/Server, App/Client)")
    assertEquals(emptyList(), ran())

    assertEquals(ExitCode.SUCCESS, run("run", "-e", "Client"))
    assertEquals(listOf(root.resolve("build/debug/Client").toString()), ran())
  }

  @Test
  fun defaultRunChoosesAndTheCommandLineOverridesIt() = fixture {
    twoPrograms("""defaultRun = "Server"""")
    assertEquals(ExitCode.SUCCESS, run("run"))
    assertEquals(listOf(root.resolve("build/debug/Server").toString()), ran())

    assertEquals(ExitCode.SUCCESS, run("run", "--executable", "Client"))
    assertEquals(root.resolve("build/debug/Client").toString(), ran().last())
  }

  @Test
  fun theChosenProgramIsTheOneBuiltWhenMembersShareTheirNames() = fixture {
    write("BUILD.kts", "project { name = \"Root\" }\nworkspace { members(\"First\", \"Second\") }")
    for ((member, project) in listOf("First" to "", "Second" to """defaultRun = "Server"""")) {
      write(
        "$member/BUILD.kts",
        """
        project {
          name = "App"
          $project
        }
        sources {
          executable {
            name = "Server"
            entry = "App.Server"
          }
          executable {
            name = "Client"
            entry = "App.Client"
          }
        }
        """,
      )
      write("$member/Sources/Main.tala")
    }
    assertEquals(ExitCode.SUCCESS, run("run"))
    assertEquals(listOf(root.resolve("Second/build/debug/Server").toString()), ran())
  }

  @Test
  fun defaultRunMustNameAnExecutable() = fixture {
    twoPrograms("""defaultRun = "Missing"""")
    assertEquals(ExitCode.FAILURE, run("run"))
    assertContains(recorder.stderr(), "project.defaultRun is 'Missing'")
  }

  @Test
  fun runTakesOneViPkgOneExecutableAndNoLibrary() {
    for ((arguments, message) in
      listOf(
        listOf("run", "-l", "Utils") to "--library selects targets of tazel build",
        listOf("run", "-p", "App", "-p", "Tools") to "one --vipkg, not 2",
        listOf("run", "-e", "Server", "-e", "Client") to "one --executable, not 2",
      )) {
      val result = assertIs<ParseResult.Rejected>(parseCommandLine(arguments))
      assertContains(result.message, message)
    }
    assertIs<ParseResult.Parsed>(
      parseCommandLine(listOf("build", "-p", "App", "-p", "Tools", "-l", "Utils"))
    )
  }
}
