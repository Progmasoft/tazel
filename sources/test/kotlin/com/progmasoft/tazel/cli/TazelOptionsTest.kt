/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.dsl.options.Allocation
import com.progmasoft.tazel.dsl.options.TazelOptions
import com.progmasoft.tazel.dsl.options.TazelOptionsException
import com.progmasoft.tazel.dsl.options.effectiveTazelOptions
import com.progmasoft.tazel.dsl.options.parseTazelOptions
import java.nio.file.Files
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNotNull

class TazelOptionsTest {
  private fun rejection(text: String) =
    assertFailsWith<TazelOptionsException> { parseTazelOptions(text) }.message.orEmpty()

  @Test
  fun readsTheOptionsAndTheirComments() {
    val text =
      """
      # comment
      <# comment #>

      sandboxing-with-microvm: true; # a comment after an option
      microvm-network: true;
      microvm-memory: 2048;
      microvm-cpus: 1
      """
    assertEquals(
      TazelOptions(true, true, Allocation.Fixed(2048), Allocation.Fixed(1)),
      parseTazelOptions(text),
    )
  }

  @Test
  fun whatIsNotWrittenKeepsItsDefault() {
    assertEquals(TazelOptions(), parseTazelOptions("# nothing\n"))
    assertEquals(false, TazelOptions().sandboxingWithMicrovm)
    assertEquals(false, TazelOptions().microvmNetwork)
    assertEquals(Allocation.Auto, TazelOptions().microvmMemory)
    assertEquals(Allocation.Auto, TazelOptions().microvmCpus)
  }

  @Test
  fun autoIsAStringInAnyQuote() {
    for (quote in listOf("\"", "'", "`")) {
      val options =
        parseTazelOptions("microvm-memory: ${quote}auto$quote; microvm-cpus: ${quote}auto$quote;")
      assertEquals(Allocation.Auto, options.microvmMemory)
      assertEquals(Allocation.Auto, options.microvmCpus)
    }
    assertContains(rejection("microvm-cpus: auto;"), "expected a number")
    assertContains(rejection("microvm-cpus: \"two\";"), "only string here is \"auto\"")
  }

  @Test
  fun mistakesNameTheirPlace() {
    assertContains(rejection("sandbox: true;"), "'sandbox' is not an option")
    assertContains(rejection("microvm-network: true; microvm-network: false;"), "set twice")
    assertContains(rejection("microvm-network: yes;"), "expected true or false")
    assertContains(rejection("microvm-network: \"true\";"), "expected true or false")
    assertContains(rejection("microvm-network true;"), "expected ':'")
    assertContains(rejection("microvm-network: true microvm-cpus: 1"), "expected ';'")
    assertContains(rejection("microvm-cpus: 0;"), "expected a number")
    assertContains(rejection("microvm-cpus: 257;"), "outside 1 to 256")
    assertContains(rejection("microvm-memory: 100;"), "outside 256 to 1048576")
    assertContains(rejection("microvm-memory:"), "expected a value")
    assertContains(
      rejection("microvm-memory: \"auto"),
      "tazel_options.txt:1:17: this string has no closing",
    )
  }

  @Test
  fun aSandboxThatCannotStartStopsTheCommand() = fixture {
    write("tazel_custom.txt", """dev: ("bin/tool");""")
    write("tazel_options.txt", "sandboxing-with-microvm: true;")
    assertEquals(ExitCode.FAILURE, run("custom", "dev"))
    assertContains(recorder.stderr(), "has no MicroVM to start, so nothing ran")
    assertEquals(0, commands.size)
    assertEquals(ExitCode.SUCCESS, run("version"))
  }

  @Test
  fun withoutTheSandboxCommandsRunAsBefore() = fixture {
    write("tazel_custom.txt", """dev: ("bin/tool");""")
    write("tazel_options.txt", "sandboxing-with-microvm: false; microvm-cpus: 2;")
    assertEquals(ExitCode.SUCCESS, run("custom", "dev"))
    assertEquals(1, commands.size)
  }

  @Test
  fun aCloserFileCannotTurnOffASandboxAskedForAbove() = fixture {
    write("tazel_options.txt", "sandboxing-with-microvm: true; microvm-cpus: 4;")
    write("project/tazel_custom.txt", """dev: ("../bin/tool");""")
    write("project/tazel_options.txt", "sandboxing-with-microvm: false; microvm-cpus: 2;")
    assertEquals(ExitCode.FAILURE, run("custom", "dev", directory = "project"))
    assertContains(recorder.stderr(), "has no MicroVM to start, so nothing ran")
    assertEquals(0, commands.size)
    val (options, asking) = assertNotNull(effectiveTazelOptions(root.resolve("project")))
    assertEquals(true, options.sandboxingWithMicrovm)
    assertEquals(Allocation.Fixed(2), options.microvmCpus)
    assertEquals(root.resolve("tazel_options.txt").toAbsolutePath(), asking)
  }

  @Test
  fun anUnreadableOptionsFileStopsTheCommand() = fixture {
    write("tazel_custom.txt", """dev: ("bin/tool");""")
    Files.write(root.resolve("tazel_options.txt"), byteArrayOf(0xFF.toByte(), 0xFE.toByte()))
    assertEquals(ExitCode.FAILURE, run("custom", "dev"))
    assertContains(recorder.stderr(), "could not be read")
    assertEquals(0, commands.size)
  }

  @Test
  fun aBrokenOptionsFileStopsTheCommand() = fixture {
    write("tazel_custom.txt", """dev: ("bin/tool");""")
    write("tazel_options.txt", "sandboxing-with-microvm: maybe;")
    assertEquals(ExitCode.FAILURE, run("custom", "dev"))
    assertContains(recorder.stderr(), "tazel_options.txt:1:26: expected true or false")
    assertEquals(0, commands.size)
  }
}
