/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.GatheredRun
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.driver.SystemProcesses
import com.progmasoft.tazel.dsl.TargetTriple
import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Builds and runs a small module with the C compiler of this machine. Without Clang or GCC on PATH
 * there is nothing to build with, and the tests return early.
 */
class BuildCommandTest {
  private val os = TargetTriple.host().os
  private val programs = Programs(os)
  private val compiler = programs.find("clang") ?: programs.find("gcc")

  /** Real processes; what a program `tazel run` starts prints is kept instead of shown. */
  private class Recording : Processes by SystemProcesses {
    var printed = ""

    override fun runWith(
      command: List<String>,
      directory: Path,
      environment: Map<String, String>,
    ): Int {
      val result: GatheredRun = SystemProcesses.runGatheredWith(command, directory, environment)
      printed += result.output
      return result.exitCode
    }
  }

  private fun module(): Path {
    val root = Files.createTempDirectory("tazel-build-").toRealPath()
    fun write(path: String, text: String) {
      val file = root.resolve(path)
      Files.createDirectories(file.parent)
      Files.writeString(file, text.trimIndent() + "\n")
    }
    write("MODULE.kts", """module { name = "sample" }""")
    write(
      "core/BUILD.kts",
      """
      isPublic = true
      c.staticlib("core") {
          sources(glob("src/*.c"))
          headers("include")
          stripIncludePrefix = "include"
          includePrefix = "sample"
          defines("SAMPLE_OFFSET=1")
      }
      """,
    )
    write("core/include/core.h", "int sample_add(int a, int b);")
    write(
      "core/src/core.c",
      "#include <sample/core.h>\nint sample_add(int a, int b) { return a + b + SAMPLE_OFFSET - 1; }",
    )
    write(
      "app/BUILD.kts",
      """
      c.bin("app") {
          sources("main.c")
          depends("//core/:core")
          args("--given")
          env("SAMPLE_GREETING" to "hi")
      }
      """,
    )
    write(
      "app/main.c",
      """
      #include <stdio.h>
      #include <stdlib.h>
      #include <sample/core.h>
      int main(int argc, char **argv) {
          printf("%s %d", getenv("SAMPLE_GREETING"), sample_add(2, 3));
          for (int i = 1; i < argc; ++i) printf(" %s", argv[i]);
          printf("\n");
          return 0;
      }
      """,
    )
    return root
  }

  private fun tazel(
    root: Path,
    directory: String,
    processes: Processes,
    vararg arguments: String,
  ): Pair<Int, Recorder> {
    val recorder = Recorder()
    val code =
      run(
        arguments.toList(),
        recorder.out,
        recorder.err,
        Environment(root.resolve(directory), os, programs, processes),
      )
    return code to recorder
  }

  @Test
  fun buildsRunsAndRebuildsOnlyWhatChanged() {
    if (compiler == null) return
    val root = module()
    val processes = Recording()

    val (first, firstOutput) = tazel(root, ".", processes, "build", "//...")
    assertEquals(ExitCode.SUCCESS, first, firstOutput.stderr() + firstOutput.stdout())
    assertContains(firstOutput.stdout(), "4 command(s) run")

    val (again, againOutput) = tazel(root, ".", processes, "build", "//...")
    assertEquals(ExitCode.SUCCESS, again, againOutput.stderr())
    assertContains(againOutput.stdout(), "0 command(s) run")

    // A header change recompiles what includes it, and relinks.
    val header = root.resolve("core/include/core.h")
    Files.writeString(header, "int sample_add(int a, int b); /* changed */\n")
    Files.setLastModifiedTime(
      header,
      java.nio.file.attribute.FileTime.fromMillis(System.currentTimeMillis() + 5_000),
    )
    val (changed, changedOutput) = tazel(root, ".", processes, "build", "//...")
    assertEquals(ExitCode.SUCCESS, changed, changedOutput.stderr())
    assertContains(changedOutput.stdout(), "4 command(s) run")

    val (ran, ranOutput) = tazel(root, "app", processes, "run", "--", "extra")
    assertEquals(ExitCode.SUCCESS, ran, ranOutput.stderr())
    assertEquals("hi 5 --given extra", processes.printed.trim())
  }

  @Test
  fun aCompilerErrorFailsTheBuildWithItsMessage() {
    if (compiler == null) return
    val root = module()
    Files.writeString(root.resolve("app/main.c"), "int main(void) { return undeclared; }\n")
    val (code, output) = tazel(root, ".", Recording(), "build", "//app/:app")
    assertEquals(ExitCode.FAILURE, code)
    assertContains(output.stdout(), "undeclared")
    assertContains(output.stderr(), "//app/:app")
  }

  @Test
  fun commandLineMistakesAreUsageErrors() {
    val root = module()
    assertEquals(ExitCode.USAGE, tazel(root, ".", Recording(), "build", "--fast").first)
    assertEquals(ExitCode.USAGE, tazel(root, ".", Recording(), "build", "//core").first)
    val (code, output) = tazel(root, ".", Recording(), "run", "//core/:core")
    assertEquals(ExitCode.FAILURE, code)
    assertTrue("no bin target" in output.stderr(), output.stderr())
  }
}
