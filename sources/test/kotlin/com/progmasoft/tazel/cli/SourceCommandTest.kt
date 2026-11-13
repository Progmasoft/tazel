/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.Programs
import com.progmasoft.tazel.dsl.TargetTriple
import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * Formats and lints a small module with the clang-format and clang-tidy of this machine. Without
 * them the tests return early.
 */
class SourceCommandTest {
  private val os = TargetTriple.host().os
  private val programs = Programs(os)

  private fun module(): Path {
    val root = Files.createTempDirectory("tazel-source-").toRealPath()
    fun write(path: String, text: String) {
      val file = root.resolve(path)
      Files.createDirectories(file.parent)
      Files.writeString(file, text.trimIndent() + "\n")
    }
    write("MODULE.kts", """module { name = "sample" }""")
    write(".clang-format", "BasedOnStyle: LLVM")
    write(
      "core/BUILD.kts",
      """
      isPublic = true
      c.staticlib("core") {
          sources(glob("src/*.c"))
          headers("include")
          stripIncludePrefix = "include"
          includePrefix = "sample"
      }
      """,
    )
    write("core/include/core.h", "int sample_add(int a, int b);")
    write(
      "core/src/core.c",
      "#include <sample/core.h>\nint sample_add(int a, int b) { return a + b; }",
    )
    return root
  }

  private fun tazel(root: Path, vararg arguments: String): Triple<Int, String, String> {
    val out = StringBuilder()
    val err = StringBuilder()
    val code = run(arguments.toList(), out, err, Environment(directory = root))
    return Triple(code, out.toString(), err.toString())
  }

  @Test
  fun fmtChecksThenFormats() {
    if (programs.find("clang-format") == null) return
    val root = module()
    try {
      val header = root.resolve("core/include/core.h")
      Files.writeString(header, "int   sample_add( int a,int b );\n")
      val (failed, _, _) = tazel(root, "fmt", "--check", "//...")
      assertEquals(ExitCode.FAILURE, failed)
      assertEquals("int   sample_add( int a,int b );\n", Files.readString(header))

      val (formatted, out, err) = tazel(root, "fmt", "//...")
      assertEquals(ExitCode.SUCCESS, formatted, out + err)
      assertEquals("int sample_add(int a, int b);\n", Files.readString(header))

      val (checked, checkOut, checkErr) = tazel(root, "fmt", "--check", "//...")
      assertEquals(ExitCode.SUCCESS, checked, checkOut + checkErr)
      assertContains(checkOut, "Checked 2 file(s)")
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun lintWritesTheDatabaseAndAppliesFixes() {
    if (programs.find("clang-tidy") == null) return
    if (programs.find("clang") == null && programs.find("gcc") == null) return
    val root = module()
    try {
      Files.writeString(
        root.resolve(".clang-tidy"),
        "Checks: '-*,readability-braces-around-statements'\nWarningsAsErrors: '*'\n",
      )
      val source = root.resolve("core/src/core.c")
      Files.writeString(
        source,
        "#include <sample/core.h>\n" +
          "int sample_add(int a, int b) {\n  if (a < 0)\n    return b;\n  return a + b;\n}\n",
      )
      val (failed, out, err) = tazel(root, "lint", "//...")
      assertEquals(ExitCode.FAILURE, failed, out + err)
      assertContains(out, "readability-braces-around-statements")

      val database = root.resolve(".tazel/compile_commands.json")
      assertTrue(Files.isRegularFile(database))
      val text = Files.readString(database)
      assertContains(text, source.toString().replace("\\", "\\\\"))
      assertTrue("-MF" !in text, text)

      // clang-tidy fails a run that found errors, also when it fixed them; the next run is clean.
      val (fixed, fixOut, fixErr) = tazel(root, "lint", "--fix", "//...")
      assertEquals(ExitCode.FAILURE, fixed, fixOut + fixErr)
      assertContains(fixOut, "applied 2 of 2 suggested fixes")
      assertContains(Files.readString(source), "if (a < 0) {")

      val (clean, cleanOut, cleanErr) = tazel(root, "lint", "//...")
      assertEquals(ExitCode.SUCCESS, clean, cleanOut + cleanErr)
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun eachFlagBelongsToItsCommand() {
    val root = module()
    try {
      assertEquals(ExitCode.USAGE, tazel(root, "lint", "--check").first)
      assertEquals(ExitCode.USAGE, tazel(root, "fmt", "--fix").first)
      assertEquals(ExitCode.USAGE, tazel(root, "build", "--fix").first)
    } finally {
      root.toFile().deleteRecursively()
    }
  }
}
