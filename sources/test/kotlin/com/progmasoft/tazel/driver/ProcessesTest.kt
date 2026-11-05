/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertEquals

class ProcessesTest {
  /** A compiler or a build step reads nothing: its input ends before it starts. */
  @Test
  fun aProgramTazelRunsForItselfReadsNothing() {
    val root = Files.createTempDirectory("tazel-processes-")
    try {
      val program = root.resolve("Reads.java")
      Files.writeString(
        program,
        """
        class Reads {
          public static void main(String[] arguments) throws Exception {
            System.exit(System.in.read() == -1 ? 7 : 8);
          }
        }
        """
          .trimIndent(),
      )
      val java = Path.of(System.getProperty("java.home"), "bin", "java").toString()
      assertEquals(7, SystemProcesses.run(listOf(java, program.toString()), root))
      assertEquals(7, SystemProcesses.runWith(listOf(java, program.toString()), root, emptyMap()))
    } finally {
      root.toFile().deleteRecursively()
    }
  }
}
