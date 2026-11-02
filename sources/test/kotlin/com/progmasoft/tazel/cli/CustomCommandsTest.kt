/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.custom.commands.CustomArgument
import com.progmasoft.tazel.custom.commands.CustomCommand
import com.progmasoft.tazel.custom.commands.CustomCommandsException
import com.progmasoft.tazel.custom.commands.CustomStep
import com.progmasoft.tazel.custom.commands.parser.parseCustomCommands
import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.dsl.OperatingSystem
import java.nio.file.Files
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class CustomCommandsTest {
  private fun program(vararg arguments: String) =
    CustomStep.Program(arguments.map(CustomArgument::Text))

  private fun rejection(text: String) =
    assertFailsWith<CustomCommandsException> { parseCustomCommands(text) }.message.orEmpty()

  @Test
  fun readsTheThreeKindsOfQuote() {
    assertEquals(
      listOf(
        CustomCommand(
          "build-no-cache",
          listOf(program("tazel", "build", "--no-cache")),
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
            program("cargo", "fetch"),
            program("tazel", "fetch"),
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
          listOf(CustomStep.Shell("cargo fetch"), program("tazel", "fetch")),
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
    assertEquals(program("echo", "it's", "a \"word\"", "#1"), steps.single())
    assertEquals(
      program("echo\\"),
      parseCustomCommands("""say: ("echo\");""").single().steps.single(),
    )
    assertContains(rejection("""say: ("echo);"""), "no closing")
  }

  @Test
  fun aNameMayHoldSeveralWords() {
    val command =
      parseCustomCommands("""build no-cache: ("tazel" "build" "--no-cache");""").single()
    assertEquals("build no-cache", command.name)
    assertEquals(listOf("build", "no-cache"), command.words)
    assertContains(rejection("a b: ('c'); a  b: ('d');"), "defined twice")
    assertContains(rejection(List(192) { "w" }.joinToString(" ") + ": ('c');"), "at most 191")
  }

  @Test
  fun placeholdersAreReadInPrograms() {
    val step = parseCustomCommands("""with: ("tazel" <1> "x" <190> <*>);""").single().steps.single()
    assertEquals(
      CustomStep.Program(
        listOf(
          CustomArgument.Text("tazel"),
          CustomArgument.Position(1),
          CustomArgument.Text("x"),
          CustomArgument.Position(190),
          CustomArgument.All,
        )
      ),
      step,
    )
    assertContains(rejection("""a: ("b" <0>);"""), "runs from 1")
    assertContains(rejection("""a: ("b" <192>);"""), "runs from 1")
    assertContains(rejection("""a: ("b" <191>);"""), "leaves room for 190")
    assertContains(rejection("""a b: ("c" <190>);"""), "leaves room for 189")
    assertContains(rejection("""a: ("b" <01>);"""), "runs from 1")
    assertContains(rejection("""a: ("b" <x>);"""), "runs from 1")
    assertContains(rejection("""a: ("b" <1);"""), "expected <N>")
    assertContains(rejection("""a: (<1> "b");"""), "name of its program")
    assertContains(rejection("""a: s(<1>);"""), "s(...) takes no arguments")
    assertContains(rejection("""a: s("b" <*>);"""), "s(...) takes no arguments")
    // In a string it is text, and `<#` still opens a comment.
    assertEquals(
      program("b", "<1>"),
      parseCustomCommands("""a: ("b" "<1>");""").single().steps.single(),
    )
    assertEquals(
      program("b"),
      parseCustomCommands("""a: ("b" <# not an argument #>);""").single().steps.single(),
    )
  }

  @Test
  fun theArgumentsGoWhereTheCommandSays() = fixture {
    write("tazel_custom.txt", """build with: ("bin/tool" "build" <2> <1>);""")
    assertEquals(ExitCode.SUCCESS, run("custom", "build", "with", "-e", "MyApp"))
    assertEquals(listOf(listOf("build", "MyApp", "-e")), callsOf("tool"))
  }

  @Test
  fun allTheArgumentsGoTogether() = fixture {
    write("tazel_custom.txt", """pass: ("bin/tool" <*>) ("bin/other" "first" <*> "last");""")
    assertEquals(ExitCode.SUCCESS, run("custom", "pass", "--flag", "a b"))
    assertEquals(listOf(listOf("--flag", "a b")), callsOf("tool"))
    assertEquals(listOf(listOf("first", "--flag", "a b", "last")), callsOf("other"))
    assertEquals(ExitCode.SUCCESS, run("custom", "pass"))
    assertEquals(listOf("--flag", "a b"), callsOf("tool").first())
    assertEquals(emptyList(), callsOf("tool").last())
  }

  @Test
  fun theLongestNameWins() = fixture {
    write(
      "tazel_custom.txt",
      """
      build: ("bin/short" <*>);
      build no-cache: ("bin/long" <*>);
      """,
    )
    assertEquals(ExitCode.SUCCESS, run("custom", "build", "no-cache", "x"))
    assertEquals(listOf(listOf("x")), callsOf("long"))
    assertEquals(ExitCode.SUCCESS, run("custom", "build", "other"))
    assertEquals(listOf(listOf("other")), callsOf("short"))
  }

  @Test
  fun theSeparatorEndsTheName() = fixture {
    write(
      "tazel_custom.txt",
      """
      first: ("bin/short" <*>);
      first second: ("bin/long" <*>);
      """,
    )
    assertEquals(ExitCode.SUCCESS, run("custom", "first", "second"))
    assertEquals(listOf(emptyList<String>()), callsOf("long"))
    assertEquals(ExitCode.SUCCESS, run("custom", "first", "--", "second"))
    assertEquals(listOf(listOf("second")), callsOf("short"))
    assertEquals(ExitCode.SUCCESS, run("custom", "first", "a", "--", "second", "--"))
    assertEquals(listOf("a", "second", "--"), callsOf("short").last())
  }

  @Test
  fun aNameCannotHoldTheSeparator() {
    assertContains(rejection("""a -- b: ("c");"""), "cannot hold it")
  }

  @Test
  fun aShellLineIsNotChanged() {
    val line = """"a b" \c\"""
    assertEquals(listOf("sh", "-c", line), CustomStep.Shell(line).started(OperatingSystem.LINUX))
    assertEquals(
      listOf("cmd.exe", "/d", "/s", "/c", "\"$line\""),
      CustomStep.Shell(line).started(OperatingSystem.WINDOWS),
    )
  }

  @Test
  fun strictProcessesAreRefusedOnlyForLinesTheyChange() {
    val command = { line: String -> CustomCommand("a", listOf(CustomStep.Shell(line))) }
    assertEquals(null, command("echo a b").lineUnfitForStrictProcesses)
    assertEquals("echo \"a\"", command("echo \"a\"").lineUnfitForStrictProcesses)
    assertEquals("dir a\\", command("dir a\\").lineUnfitForStrictProcesses)
  }

  @Test
  fun cmdReceivesTheLineAsWritten() {
    if (!System.getProperty("os.name").startsWith("Windows")) return
    val started = CustomStep.Shell("echo trailing\\").started(OperatingSystem.WINDOWS)
    val process = ProcessBuilder(started).redirectErrorStream(true).start()
    assertEquals("trailing\\", process.inputStream.bufferedReader().readText().trim())
  }

  @Test
  fun theNumberOfArgumentsIsChecked() = fixture {
    write(
      "tazel_custom.txt",
      """
      two: ("bin/tool" <1> <2>);
      none: ("bin/tool");
      """,
    )
    assertEquals(ExitCode.FAILURE, run("custom", "two", "a"))
    assertContains(recorder.stderr(), "needs 2 argument(s), not 1")
    assertEquals(ExitCode.FAILURE, run("custom", "two", "a", "b", "c"))
    assertContains(recorder.stderr(), "takes 2 argument(s), not 3")
    assertEquals(ExitCode.FAILURE, run("custom", "none", "a"))
    assertContains(recorder.stderr(), "takes 0 argument(s), not 1")
    assertEquals(0, commands.size)
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
