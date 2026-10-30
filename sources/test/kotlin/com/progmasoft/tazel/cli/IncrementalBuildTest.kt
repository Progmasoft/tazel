/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import java.nio.file.Files
import java.nio.file.attribute.FileTime
import kotlin.io.path.readText
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IncrementalBuildTest {
  private fun Fixture.builds(vararg arguments: String): Int {
    val before = callsOf("talac").size
    assertEquals(ExitCode.SUCCESS, run(*arguments), recorder.stderr())
    return callsOf("talac").size - before
  }

  private val Fixture.program
    get() = root.resolve("build/debug/Demo")

  @Test
  fun aBuildOfUnchangedSourcesStartsNoCompiler() = fixture {
    demo()
    assertEquals(1, builds("build"))
    assertEquals(0, builds("build"))
    assertTrue(recorder.stdout().contains("Fresh"))
  }

  @Test
  fun aChangedSourceIsCompiledAgain() = fixture {
    demo()
    builds("build")
    write("Sources/Helper.tala", "changed")
    assertEquals(1, builds("build"))
    assertEquals(0, builds("build"))
  }

  @Test
  fun aNewSourceIsCompiledAgain() = fixture {
    demo()
    builds("build")
    write("Sources/Added.tala")
    assertEquals(1, builds("build"))
  }

  @Test
  fun aFileTouchedWithoutAChangeIsNotCompiledAgain() = fixture {
    demo()
    builds("build")
    Files.setLastModifiedTime(
      root.resolve("Sources/Main.tala"),
      FileTime.fromMillis(1_000_000_000_000),
    )
    assertEquals(0, builds("build"))
  }

  @Test
  fun aChangedSettingIsCompiledAgain() = fixture {
    demo(extra = "compiler { warnings = Warnings.LOW }")
    builds("build")
    demo(extra = "compiler { warnings = Warnings.ALL }")
    assertEquals(1, builds("build"))
  }

  @Test
  fun aChosenLinkageOfTalaRtReachesTheCompilerAndCompilesAgain() = fixture {
    demo()
    builds("build")
    assertTrue(callsOf("talac").last().none { it.startsWith("--static-talart-library") })

    demo(extra = "compiler { staticTalaRTLibrary = null }")
    assertEquals(0, builds("build"))

    demo(extra = "compiler { staticTalaRTLibrary = false }")
    assertEquals(1, builds("build"))
    assertTrue("--static-talart-library=false" in callsOf("talac").last())

    demo(extra = "compiler { staticTalaRTLibrary = true }")
    assertEquals(1, builds("build"))
    assertTrue("--static-talart-library=true" in callsOf("talac").last())
  }

  @Test
  fun anotherCompilerCompilesAgain() = fixture {
    demo()
    builds("build")
    compilerVersion = "talac 0.5.3\n"
    assertEquals(1, builds("build"))
  }

  @Test
  fun aChangedBuildOfTheCompilerCompilesAgain() = fixture {
    demo()
    builds("build")
    Files.writeString(root.resolve("tools/talac"), "another build")
    assertEquals(1, builds("build"))
  }

  @Test
  fun aMissingProgramComesBackWithoutACompiler() = fixture {
    demo()
    builds("build")
    Files.delete(program)
    assertEquals(0, builds("build"))
    assertTrue(Files.isRegularFile(program))
    assertTrue(recorder.stdout().contains("Restored"))
  }

  @Test
  fun aChangedProgramIsReplacedWithoutACompiler() = fixture {
    demo()
    builds("build")
    val built = program.readText()
    Files.writeString(program, "tampered")
    assertEquals(0, builds("build"))
    assertEquals(built, program.readText())
  }

  @Test
  fun goingBackToEarlierSourcesStartsNoCompiler() = fixture {
    demo()
    write("Sources/Helper.tala", "first")
    builds("build")
    write("Sources/Helper.tala", "second")
    builds("build")
    write("Sources/Helper.tala", "first")
    assertEquals(0, builds("build"))
  }

  @Test
  fun warningsAreShownAgainByABuildThatStartsNoCompiler() = fixture {
    demo()
    compilerOutput = "Sources/Main.tala:1:1: warning: unused"
    builds("build")
    compilerOutput = ""
    val before = recorder.stderr().length
    assertEquals(0, builds("build"))
    assertTrue(recorder.stderr().substring(before).contains("warning: unused"))
  }

  @Test
  fun theLineOfTalacAboutThePlaceItLinkedToIsNotShown() = fixture {
    demo()
    compilerOutput = "talac: linked native executable 'Sources/Helper'"
    builds("build")
    builds("build")
    assertFalse(recorder.stderr().contains("linked native executable"))
  }

  @Test
  fun aRejectedBuildLeavesNoResult() = fixture {
    demo()
    compilerExitCode = 3
    assertEquals(ExitCode.FAILURE, run("build"))
    assertEquals(ExitCode.FAILURE, run("build"))
    assertEquals(2, callsOf("talac").size)
    compilerExitCode = 0
    assertEquals(1, builds("build"))
  }

  @Test
  fun noCacheCompilesAndStillLeavesAResult() = fixture {
    demo()
    builds("build")
    assertEquals(1, builds("build", "--no-cache"))
    assertEquals(0, builds("build"))
  }

  @Test
  fun aCheckAndABuildHaveResultsOfTheirOwn() = fixture {
    demo()
    assertEquals(1, builds("check"))
    assertEquals(1, builds("build"))
    assertEquals(0, builds("check"))
    assertEquals(0, builds("build"))
  }

  @Test
  fun aRunUsesTheResultOfABuild() = fixture {
    demo()
    builds("build")
    assertEquals(0, builds("run"))
    assertEquals(1, commands.count { it.first.first() == program.toString() })
  }

  @Test
  fun talaRtMovesBesideTheProgramAndComesBackWithIt() = fixture {
    demo()
    runtimeLibrary = "libtalart.so"
    builds("build")
    val library = program.resolveSibling("libtalart.so")
    assertTrue(Files.isRegularFile(library))
    assertFalse(Files.exists(root.resolve("Sources/libtalart.so")))
    Files.delete(library)
    assertEquals(0, builds("build"))
    assertTrue(Files.isRegularFile(library))
  }
}
