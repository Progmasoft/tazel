/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.driver.SystemProcesses
import com.progmasoft.tazel.dsl.TargetTriple
import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * Runs `tazel test` on C tests built with the compiler of this machine. Without Clang or GCC on
 * PATH there is nothing to build with, and the tests return early.
 */
class TestCommandTest {
  private val os = TargetTriple.host().os
  private val programs = Programs(os)
  private val compiler = programs.find("clang") ?: programs.find("gcc")

  private fun module(): Path {
    val root = Files.createTempDirectory("tazel-test-").toRealPath()
    fun write(path: String, text: String) {
      val file = root.resolve(path)
      Files.createDirectories(file.parent)
      Files.writeString(file, text.trimIndent() + "\n")
    }
    write("MODULE.kts", """module { name = "sample" }""")
    write(
      "tests/BUILD.kts",
      """
      c.test("passes") {
          sources("passes.c")
          data("fixture.txt")
          args("--expected", "fixture")
          env("SAMPLE_GREETING" to "hi")
      }
      c.test("fails") { sources("fails.c") }
      c.test("flaky") {
          sources("flaky.c")
          flaky = true
      }
      c.test("sharded") {
          sources("sharded.c")
          shards = 2
      }
      c.test("hangs") {
          sources("hangs.c")
          timeout = 2.seconds
          tags(TestTag.MANUAL)
      }
      """,
    )
    write("tests/fixture.txt", "fixture")
    write(
      "tests/passes.c",
      """
      #include <stdio.h>
      #include <stdlib.h>
      #include <string.h>
      int main(int argc, char **argv) {
          char text[32] = {0};
          FILE *file = fopen("fixture.txt", "r");
          if (!file || !fgets(text, sizeof text, file)) return 2;
          text[strcspn(text, "\n")] = 0;
          const char *greeting = getenv("SAMPLE_GREETING");
          if (argc != 3 || strcmp(argv[2], text) != 0 || !greeting || strcmp(greeting, "hi") != 0) return 3;
          puts("passed");
          return 0;
      }
      """,
    )
    write(
      "tests/fails.c",
      """
      #include <stdio.h>
      int main(void) { puts("expected 2, got 3"); return 1; }
      """,
    )
    write(
      "tests/flaky.c",
      """
      #include <stdio.h>
      int main(void) {
          int runs = 0;
          FILE *file = fopen("flaky.count", "r");
          if (file) { if (fscanf(file, "%d", &runs) != 1) runs = 0; fclose(file); }
          file = fopen("flaky.count", "w");
          fprintf(file, "%d", runs + 1);
          fclose(file);
          return runs == 0 ? 1 : 0;
      }
      """,
    )
    write(
      "tests/sharded.c",
      """
      #include <stdio.h>
      #include <stdlib.h>
      int main(void) {
          printf("shard %s of %s\n", getenv("TEST_SHARD_INDEX"), getenv("TEST_TOTAL_SHARDS"));
          return 0;
      }
      """,
    )
    write("tests/hangs.c", "int main(void) { for (;;) {} }")
    return root
  }

  private fun tazel(root: Path, vararg arguments: String): Pair<Int, Recorder> {
    val recorder = Recorder()
    val code =
      run(
        arguments.toList(),
        recorder.out,
        recorder.err,
        Environment(root, os, programs, SystemProcesses),
      )
    return code to recorder
  }

  @Test
  fun testsPassFailRetryShardAndAreCached() {
    if (compiler == null) return
    val root = module()

    val (code, output) = tazel(root, "test", "//tests/...")
    val text = output.stdout()
    assertEquals(ExitCode.FAILURE, code, text + output.stderr())
    assertContains(text, "PASSED //tests/:passes")
    assertContains(text, "FAILED //tests/:fails")
    assertContains(text, "expected 2, got 3")
    assertContains(text, "FLAKY //tests/:flaky")
    assertContains(text, "PASSED //tests/:sharded")
    assertContains(text, "4 test(s): 3 passed, 1 failed")
    assertFalse("hangs" in text, "a MANUAL test runs only when it is named")

    val out = root.resolve(".tazel/out").resolve(TargetTriple.host().text).resolve("debug/tests")
    assertEquals(
      "shard 0 of 2",
      Files.readString(out.resolve("sharded/test.shard-1-of-2.log")).trim(),
    )
    assertEquals(
      "shard 1 of 2",
      Files.readString(out.resolve("sharded/test.shard-2-of-2.log")).trim(),
    )

    // Nothing changed: the tests that passed are not run again.
    val (_, again) = tazel(root, "test", "//tests/:passes", "//tests/:sharded")
    assertContains(again.stdout(), "CACHED //tests/:passes (cached)")
    assertContains(again.stdout(), "CACHED //tests/:sharded (cached)")

    // Other arguments are another run.
    val (_, other) = tazel(root, "test", "//tests/:passes", "--", "--extra")
    assertTrue("CACHED" !in other.stdout(), other.stdout())
  }

  @Test
  fun aTestThatRunsTooLongIsStopped() {
    if (compiler == null) return
    val root = module()
    val (code, output) = tazel(root, "test", "//tests/:hangs")
    assertEquals(ExitCode.FAILURE, code)
    assertContains(output.stdout(), "TIMEOUT //tests/:hangs")
  }

  @Test
  fun onlyTestTargetsAreTested() {
    val root = module()
    Files.writeString(root.resolve("BUILD.kts"), """c.bin("app") { sources("app.c") }""")
    val (code, output) = tazel(root, "test", "//:app")
    assertEquals(ExitCode.FAILURE, code)
    assertContains(output.stderr(), "//:app is not a test target")
  }
}
