/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

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

  private fun cache(root: Path) = BuildCache(root.resolve("cache")) { error(it) }

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

    assertEquals("a warning", cache(root).log("build-Demo", "key"))
    assertEquals(BuildCache.Restored.COPIED, cache(root).restore("build-Demo", "key", root))
    assertEquals("built", Files.readString(program))
    assertEquals(BuildCache.Restored.FRESH, cache(root).restore("build-Demo", "key", root))
  }

  @Test
  fun anUnknownResultIsNull() = inFolder { root ->
    assertNull(cache(root).log("build-Demo", "key"))
    assertNull(cache(root).restore("build-Demo", "key", root))
  }

  @Test
  fun aResultStoredAgainReplacesTheOldOne() = inFolder { root ->
    val program = Files.writeString(root.resolve("Program"), "first")
    cache(root).store("build-Demo", "key", listOf(program), "")
    Files.writeString(program, "second")
    cache(root).store("build-Demo", "key", listOf(program), "")
    Files.delete(program)

    cache(root).restore("build-Demo", "key", root)
    assertEquals("second", Files.readString(program))
  }

  @Test
  fun onlyTheNewestResultsOfASlugAreKept() = inFolder { root ->
    val slug = root.resolve("cache/build-Demo")
    for (index in 0 until BuildCache.KEPT + 3) {
      cache(root).store("build-Demo", "key$index", emptyList(), "")
      Files.setLastModifiedTime(slug.resolve("key$index"), FileTime.fromMillis(index * 1000L))
    }
    cache(root).store("build-Demo", "last", emptyList(), "")

    val left = Files.list(slug).use { paths -> paths.map { it.fileName.toString() }.toList() }
    assertEquals(BuildCache.KEPT, left.size)
    assertTrue("last" in left)
    assertTrue("key0" !in left)
  }

  @Test
  fun aCacheThatCannotBeWrittenFailsNothing() = inFolder { root ->
    Files.writeString(root.resolve("cache"), "a file where the folder should be")
    val problems = mutableListOf<String>()
    val cache = BuildCache(root.resolve("cache")) { problems += it }

    cache.store("build-Demo", "key", emptyList(), "")

    assertEquals(1, problems.size)
    assertNull(cache.log("build-Demo", "key"))
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
