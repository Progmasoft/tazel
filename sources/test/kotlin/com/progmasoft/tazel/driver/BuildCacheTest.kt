/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildCacheSettings
import com.progmasoft.tazel.dsl.LocalCacheSettings
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.FileTime
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BuildCacheTest {
  private fun inFolder(block: (Path) -> Unit) {
    val root = Files.createTempDirectory("tazel-cache-").toRealPath()
    try {
      block(root)
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  private fun cache(
    root: Path,
    settings: BuildCacheSettings = BuildCacheSettings(),
  ) = BuildCache(root.resolve("cache"), settings, null) { error(it) }

  @Test
  fun aKeyFollowsContentsNamesAndArguments() = inFolder { root ->
    Files.writeString(root.resolve("a.tala"), "one")
    val key = buildKey("talac", listOf("--opt"), root, listOf("a.tala"))

    assertEquals(key, buildKey("talac", listOf("--opt"), root, listOf("a.tala")))
    assertNotEquals(key, buildKey("talac", listOf("--other"), root, listOf("a.tala")))
    assertNotEquals(key, buildKey("other", listOf("--opt"), root, listOf("a.tala")))
    Files.writeString(root.resolve("a.tala"), "two")
    assertNotEquals(key, buildKey("talac", listOf("--opt"), root, listOf("a.tala")))
  }

  @Test
  fun aKeyTellsWhereOneArgumentEndsAndTheNextBegins() = inFolder { root ->
    assertNotEquals(
      buildKey("talac", listOf("ab", "c"), root, emptyList()),
      buildKey("talac", listOf("a", "bc"), root, emptyList()),
    )
  }

  @Test
  fun aResultComesBackWithItsLog() = inFolder { root ->
    val program = Files.writeString(root.resolve("Program"), "built")
    cache(root).store("build-Demo", "key", listOf(program), "a warning")
    Files.delete(program)

    assertEquals("a warning", cache(root).load("build-Demo", "key", null)?.log)
    assertEquals(BuildCache.Restored.COPIED, cache(root).load("build-Demo", "key", root)?.restored)
    assertEquals("built", Files.readString(program))
    assertEquals(BuildCache.Restored.FRESH, cache(root).load("build-Demo", "key", root)?.restored)
  }

  @Test
  fun anUnknownResultIsNull() = inFolder { root ->
    assertNull(cache(root).load("build-Demo", "key", root))
  }

  @Test
  fun aResultStoredAgainReplacesTheOldOne() = inFolder { root ->
    val program = Files.writeString(root.resolve("Program"), "first")
    cache(root).store("build-Demo", "key", listOf(program), "")
    Files.writeString(program, "second")
    cache(root).store("build-Demo", "key", listOf(program), "")
    Files.delete(program)

    cache(root).load("build-Demo", "key", root)
    assertEquals("second", Files.readString(program))
  }

  @Test
  fun onlyTheNewestResultsOfASlugAreKept() = inFolder { root ->
    val slug = root.resolve("cache/build-Demo")
    val kept = BuildCacheSettings(LocalCacheSettings(maxEntries = 3))
    for (index in 0 until 6) {
      cache(root, kept).store("build-Demo", "key$index", emptyList(), "")
      Files.setLastModifiedTime(slug.resolve("key$index"), FileTime.fromMillis(index * 1000L))
    }
    cache(root, kept).store("build-Demo", "last", emptyList(), "")

    val left = Files.list(slug).use { paths -> paths.map { it.fileName.toString() }.toList() }
    assertEquals(3, left.size)
    assertTrue("last" in left)
    assertTrue("key0" !in left)
  }

  @Test
  fun aCacheThatCannotBeWrittenFailsNothing() = inFolder { root ->
    Files.writeString(root.resolve("cache"), "a file where the folder should be")
    val problems = mutableListOf<String>()
    val cache = BuildCache(root.resolve("cache"), BuildCacheSettings(), null) { problems += it }

    cache.store("build-Demo", "key", emptyList(), "")

    assertEquals(1, problems.size)
    assertNull(cache.load("build-Demo", "key", null))
  }

  @Test
  fun aDisabledLocalCacheKeepsNothing() = inFolder { root ->
    val off = BuildCacheSettings(LocalCacheSettings(enabled = false))
    cache(root, off).store("build-Demo", "key", emptyList(), "")
    assertNull(cache(root, off).load("build-Demo", "key", null))
    assertTrue(Files.notExists(root.resolve("cache")))
  }

  @Test
  fun aCompilerIsKnownByItsContentsAndItsDigestIsRemembered() = inFolder { root ->
    val one = Files.createDirectories(root.resolve("one"))
    val other = Files.createDirectories(root.resolve("other"))
    val compiler = Files.writeString(one.resolve("talac"), "compiler")
    Files.writeString(other.resolve("talac.exe"), "compiler")
    Files.writeString(one.resolve("talart.dll"), "runtime")
    Files.writeString(other.resolve("talart.dll"), "runtime")
    val memo = root.resolve("cache/.compiler")

    val first = toolIdentity(compiler, "1.0", memo)
    assertEquals(first, toolIdentity(other.resolve("talac.exe"), "1.0", root.resolve("elsewhere")))
    assertEquals(first, toolIdentity(compiler, "1.0", memo))

    // The memo answers while the sizes and the times stay; a change of them reads the files.
    val time = Files.getLastModifiedTime(compiler)
    Files.writeString(compiler, "COMPILER")
    Files.setLastModifiedTime(compiler, time)
    assertEquals(first, toolIdentity(compiler, "1.0", memo))
    Files.setLastModifiedTime(compiler, FileTime.fromMillis(1_000))
    assertNotEquals(first, toolIdentity(compiler, "1.0", memo))
    assertNotEquals(first, toolIdentity(compiler, "2.0", memo))
  }

  @Test
  fun totalsAreReadFromTheLastLineOfARunner() {
    assertEquals(TestTotals(1, 0), TestTotals.parse("1 test, 1 passed, 0 failed"))
    assertEquals(TestTotals(9, 3), TestTotals.parse("12 tests, 9 passed, 3 failed"))
    assertNull(TestTotals.parse("PASS Demo.Adds"))
    assertNull(TestTotals.parse("3 tests, 1 passed, 1 failed"))
    assertEquals("12 tests, 9 passed, 3 failed", TestTotals(9, 3).toString())
  }
}
