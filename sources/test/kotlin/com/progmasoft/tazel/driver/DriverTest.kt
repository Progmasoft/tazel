/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.FilePath
import com.progmasoft.tazel.dsl.Glob
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetTriple
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.FileTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class DriverTest {
  @Test
  fun depfilesAreReadWithTheirEscapes() {
    val text = "obj/a.c.o: src/a.c include/a\\ b.h \\\n  include/c.h \\\r\n  /usr/include/stdio.h\n"
    assertEquals(
      listOf("src/a.c", "include/a b.h", "include/c.h", "/usr/include/stdio.h"),
      depfileInputs(text),
    )
    assertEquals(listOf("C:\\src\\a.c"), depfileInputs("C:\\out\\a.obj: C:\\src\\a.c\n"))
  }

  @Test
  fun anOutputIsCurrentUntilItsCommandOrAnInputChanges() {
    val directory = Files.createTempDirectory("tazel-up-to-date")
    val input = Files.writeString(directory.resolve("a.c"), "int a;")
    val output = Files.writeString(directory.resolve("a.o"), "object")
    Files.setLastModifiedTime(input, FileTime.fromMillis(1_000_000))
    Files.setLastModifiedTime(output, FileTime.fromMillis(2_000_000))
    val command = listOf("cc", "-c", "a.c")
    assertFalse(UpToDate.check(output, command, listOf(input)), "never recorded")
    UpToDate.record(output, command)
    assertTrue(UpToDate.check(output, command, listOf(input)))
    assertFalse(UpToDate.check(output, command + "-O2", listOf(input)), "another command")
    Files.setLastModifiedTime(input, FileTime.fromMillis(3_000_000))
    assertFalse(UpToDate.check(output, command, listOf(input)), "a newer input")
    Files.setLastModifiedTime(input, FileTime.fromMillis(1_000_000))
    assertFalse(
      UpToDate.check(output, command, listOf(directory.resolve("gone.h"))),
      "a missing input",
    )
    UpToDate.forget(output)
    assertFalse(UpToDate.check(output, command, listOf(input)), "forgotten")
  }

  @Test
  fun globsTakeFilesBelowThePackageAndExcludesRemoveThem() {
    val root = Files.createTempDirectory("tazel-globs")
    for (file in
      listOf("src/a.c", "src/deep/b.c", "src/experimental/x.c", "src/a.h", "other/c.c", "top.c")) {
      Files.createDirectories(root.resolve(file).parent)
      Files.writeString(root.resolve(file), "")
    }
    val sets = FileSets(ignoreCase = false)
    fun names(files: List<Path>) = files.map { root.relativize(it).toString().replace('\\', '/') }
    assertEquals(
      listOf("src/a.c", "src/deep/b.c", "src/experimental/x.c"),
      names(sets.expand(root, listOf(Glob(listOf("src/**/*.c"))))),
    )
    assertEquals(
      listOf("src/a.c", "src/deep/b.c"),
      names(
        sets.expand(
          root,
          listOf(Glob(listOf("src/**/*.c"))),
          listOf(Glob(listOf("src/experimental"))),
        )
      ),
    )
    assertEquals(listOf("src/a.c"), names(sets.expand(root, listOf(Glob(listOf("src/*.c"))))))
    assertEquals(
      listOf("top.c", "other/c.c"),
      names(sets.expand(root, listOf(FilePath("top.c"), Glob(listOf("other/?.c"))))),
    )
    val sub = root.resolve("src/deep")
    assertEquals(listOf("other/c.c"), names(sets.expand(sub, listOf(FilePath("../../other/c.c")))))
    assertFailsWith<ProjectException> { sets.expand(root, listOf(FilePath("missing.c"))) }
    assertEquals(emptyList(), sets.expand(root, listOf(Glob(listOf("nowhere/*.c")))))
  }

  @Test
  fun zigSpellsTriplesItsOwnWay() {
    fun zig(text: String) = zigTriple(TargetTriple.parse(text))
    assertEquals("x86_64-linux-gnu", zig("x86_64-unknown-linux-gnu"))
    assertEquals("aarch64-macos", zig("aarch64-apple-darwin"))
    assertEquals("aarch64-macos", zig("arm64-apple-macosx14.0"))
    assertEquals("x86_64-windows-msvc", zig("x86_64-pc-windows-msvc"))
    assertEquals("x86_64-freebsd", zig("x86_64-unknown-freebsd14"))
  }

  @Test
  fun outputNamesFollowTheSystemOfTheTriple() {
    val linux = TargetTriple.parse("x86_64-unknown-linux-gnu").os
    val windows = TargetTriple.parse("x86_64-pc-windows-msvc").os
    val mac = TargetTriple.parse("aarch64-apple-darwin").os
    assertEquals("libcore.a", OutputNames.staticLibrary("core", linux))
    assertEquals("core.lib", OutputNames.staticLibrary("core", windows))
    assertEquals("libcore.so.1", OutputNames.sharedLibrary("core", linux, "1"))
    assertEquals("libcore.dylib", OutputNames.sharedLibrary("core", mac, null))
    assertEquals("core.dll", OutputNames.sharedLibrary("core", windows, "1"))
    assertEquals("app.exe", OutputNames.executable("app", windows))
    assertEquals("app", OutputNames.executable("app", mac))
  }
}
