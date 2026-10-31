/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.dsl.OperatingSystem
import java.nio.file.Files
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class CustomCommandsTest {
  private fun rejection(text: String) =
    assertFailsWith<CustomCommandsException> { parseCustomCommands(text) }.message.orEmpty()

  @Test
  fun readsTheThreeKindsOfQuote() {
    assertEquals(
      listOf(
        CustomCommand(
          "build-no-cache",
          listOf(CustomStep.Program(listOf("tazel", "build", "--no-cache"))),
        )
      ),
      parseCustomCommands("""build-no-cache: ("tazel" 'build' `--no-cache`);"""),
    )
  }

  @Test
  fun aCommandIsInsensitiveToNewlinesAndComments() {
    val text =
      """
      # a comment
      <# a comment
         of two lines: (not, a, command); #>
      fetch:
          ("cargo" "fetch") # after a step
          ("tazel"
           "fetch");
      """
    assertEquals(
      listOf(
        CustomCommand(
          "fetch",
          listOf(
            CustomStep.Program(listOf("cargo", "fetch")),
            CustomStep.Program(listOf("tazel", "fetch")),
          ),
        )
      ),
      parseCustomCommands(text),
    )
  }

  @Test
  fun aShellStepIsOneString() {
    assertEquals(
      listOf(
        CustomCommand(
          "fetch",
          listOf(CustomStep.Shell("cargo fetch"), CustomStep.Program(listOf("tazel", "fetch"))),
        )
      ),
      parseCustomCommands("""fetch: s("cargo fetch") ("tazel" "fetch");"""),
    )
    assertContains(rejection("""dev: s("npm" "dev");"""), "s(...) takes one string")
    assertContains(rejection("dev: s();"), "s(...) takes one string")
  }

  @Test
  fun aStringHasNoEscapes() {
    val steps = parseCustomCommands("""say: ('echo' "it's" `a "word"` "#1");""").single().steps
    assertEquals(CustomStep.Program(listOf("echo", "it's", "a \"word\"", "#1")), steps.single())
    assertEquals(
      CustomStep.Program(listOf("echo\\")),
      parseCustomCommands("""say: ("echo\");""").single().steps.single(),
    )
    assertContains(rejection("""say: ("echo);"""), "no closing")
  }

  @Test
  fun aMistakeNamesItsPlace() {
    assertContains(rejection("first: ();"), "tazel_custom.txt:1:8:")
    assertContains(rejection("first: ();"), "at least a program")
    assertContains(rejection("first ('a');"), "':' after the name")
    assertContains(rejection("first: ('a')"), "';' at the end")
    assertContains(rejection("first: ;"), "'(' of a command")
    assertContains(rejection("a: ('b'); a: ('c');"), "defined twice")
    assertContains(rejection("<# open"), "no closing #>")
    assertContains(rejection("a! ('b');"), "has no meaning")
  }

  @Test
  fun runsTheStepsInOrderInTheDirectoryOfTheFile() = fixture {
    write(
      "tazel_custom.txt",
      """
      fetch:
          ("tools/talafmt" "fetch")
          ("tools/talalint" 'fetch');
      """,
    )
    Files.createDirectories(root.resolve("Sources"))

    assertEquals(ExitCode.SUCCESS, run("custom", "fetch", directory = "Sources"))
    assertEquals(
      listOf(listOf("fetch"), listOf("fetch")),
      callsOf("talafmt") + callsOf("talalint"),
    )
    assertEquals(root, commands.first().second.toRealPath())
  }

  @Test
  fun aFailedStepEndsTheCommandWithItsExitCode() = fixture {
    programExitCode = 3
    write("tazel_custom.txt", """all: ("bin/first") ("bin/second");""")

    assertEquals(3, run("custom", "all"))
    assertEquals(1, commands.size)
  }

  @Test
  fun aShellStepGoesToTheShellOfTheHost() = fixture {
    write("tazel_custom.txt", """dev: s("npm dev");""")
    assertEquals(ExitCode.SUCCESS, run("custom", "dev"))
    assertEquals(listOf("sh", "-c", "npm dev"), commands.single().first)
  }

  @Test
  fun anUnknownCommandSuggestsOne() = fixture {
    write("tazel_custom.txt", """npm-dev: ("npm" "dev");""")
    assertEquals(ExitCode.FAILURE, run("custom", "npm-dew"))
    assertContains(recorder.stderr(), "no command is named 'npm-dew'")
    assertContains(recorder.stderr(), "npm-dev")
  }

  @Test
  fun withoutANameItListsTheCommands() = fixture {
    write("tazel_custom.txt", """a: ("x"); b: s("y");""")
    assertEquals(ExitCode.SUCCESS, run("custom"))
    assertContains(recorder.stdout(), "a")
    assertContains(recorder.stdout(), "b")
  }

  @Test
  fun aMissingFileIsAnError() = fixture {
    assertEquals(ExitCode.FAILURE, run("custom", "dev"))
    assertContains(recorder.stderr(), "no tazel_custom.txt")
  }

  @Test
  fun aProgramThatIsNotThereIsNamed() = fixture {
    write("tazel_custom.txt", """dev: ("no-such-program");""")
    assertEquals(ExitCode.FAILURE, run("custom", "dev"))
    assertContains(recorder.stderr(), "no-such-program")
  }

  @Test
  fun windowsFindsTheScriptsAProgramIsOften() {
    val directory = Files.createTempDirectory("tazel-path")
    Files.writeString(directory.resolve("npm.cmd"), "")
    Files.writeString(directory.resolve("tool.bat"), "")
    Files.writeString(directory.resolve("both.exe"), "")
    Files.writeString(directory.resolve("both.cmd"), "")
    val toolchain = Toolchain(OperatingSystem.WINDOWS, mapOf("PATH" to directory.toString()), null)
    assertEquals(directory.resolve("npm.cmd"), toolchain.findProgram("npm"))
    assertEquals(directory.resolve("npm.cmd"), toolchain.findProgram("npm.cmd"))
    assertEquals(directory.resolve("tool.bat"), toolchain.findProgram("tool"))
    assertEquals(directory.resolve("both.exe"), toolchain.findProgram("both"))
  }
}
