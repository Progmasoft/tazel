/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.dsl.plugin.ResolvedPlugin
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.FileTime
import java.time.Instant
import kotlin.io.path.name
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertTrue

class ScriptCacheTest {
  private fun inDirectory(block: (Path) -> Unit) {
    val root = Files.createTempDirectory("tazel-cache-").toRealPath()
    try {
      block(root)
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  private val plugin =
    ResolvedPlugin(
      publisher = "Publisher",
      name = "Plugin",
      version = "1.0.0",
      apiVersion = 1,
      sha256 = "a".repeat(64),
      imports = emptyList(),
      artifact = Path.of("plugin.jar"),
    )

  private fun inputs(classpath: List<Path> = emptyList()) =
    ScriptCacheInputs(
      source = "project { name = \"Cached\" }",
      scriptName = "BUILD.kts",
      imports = listOf("com.progmasoft.tazel.dsl.*"),
      compilerOptions = listOf("-jvm-target", "25"),
      jvmTarget = 25,
      classpath = classpath,
      plugins = listOf(plugin),
      tazelVersion = "0.1.0",
      kotlinVersion = "2.4.0",
    )

  @Test
  fun theKeyFollowsEveryInputOfTheCompilation() = inDirectory { root ->
    val jar = Files.writeString(root.resolve("library.jar"), "classes")
    val base = inputs(listOf(jar))
    val key = scriptCacheKey(base)
    assertEquals(64, key.length)
    assertEquals(key, scriptCacheKey(base.copy()))
    val changed =
      listOf(
        base.copy(source = base.source + "\n"),
        base.copy(scriptName = "BUILD.tzl.kts"),
        base.copy(imports = base.imports + "other.*"),
        base.copy(compilerOptions = listOf("-jvm-target", "26")),
        base.copy(jvmTarget = 26),
        base.copy(kotlinVersion = "2.4.1"),
        base.copy(tazelVersion = "0.2.0"),
        base.copy(plugins = listOf(plugin.copy(sha256 = "b".repeat(64)))),
        base.copy(plugins = emptyList()),
        base.copy(classpath = listOf(jar, root.resolve("other.jar"))),
      )
    changed.forEach { assertNotEquals(key, scriptCacheKey(it), it.toString()) }

    // A classpath entry that changes on disk changes the key.
    Files.setLastModifiedTime(jar, FileTime.from(Instant.parse("2020-01-01T00:00:00Z")))
    assertNotEquals(key, scriptCacheKey(base))
  }

  @Test
  fun aSecondLoadUsesTheCompiledScriptOfTheFirst() = inDirectory { root ->
    val project = Files.createDirectories(root.resolve("Project"))
    Files.writeString(
      project.resolve("BUILD.kts"),
      """
      project { name = "Cached" }
      sources { executable { entry = "Cached.Program" } }
      """
        .trimIndent(),
    )
    val cache = ScriptCache(root.resolve("cache"))
    val location = projectFileIn(project)!!

    val first = Timings(true)
    val compiled = ProjectLoader("1.0.0", cache, first).load(location)
    assertTrue(Phase.COMPILATION in first.totals())
    val entries = Files.list(cache.directory).use { stream -> stream.toList() }
    assertEquals(1, entries.count { it.name.endsWith(".jar") })

    val second = Timings(true)
    val cached = ProjectLoader("1.0.0", cache, second).load(location)
    assertFalse(Phase.COMPILATION in second.totals(), "the second load compiled again")
    assertTrue(Phase.CACHE_LOAD in second.totals())
    assertEquals(compiled.plan, cached.plan)

    // A changed project file is compiled again and gets its own entry.
    Files.writeString(
      location.file,
      Files.readString(location.file).replace("Cached\"", "Renamed\""),
    )
    val third = Timings(true)
    assertEquals("Renamed", ProjectLoader("1.0.0", cache, third).load(location).plan.identity?.name)
    assertTrue(Phase.COMPILATION in third.totals())
  }

  @Test
  fun anEntryThatCannotBeReadIsCompiledAgain() = inDirectory { root ->
    val project = Files.createDirectories(root.resolve("Project"))
    Files.writeString(
      project.resolve("BUILD.kts"),
      "project { name = \"Broken\" }\nsources { executable { entry = \"Broken.Program\" } }",
    )
    val cache = ScriptCache(root.resolve("cache"))
    val location = projectFileIn(project)!!
    ProjectLoader("1.0.0", cache).load(location)
    val entry = Files.list(cache.directory).use { stream -> stream.toList() }.single()
    Files.writeString(entry, "not a jar")

    val timings = Timings(true)
    assertEquals(
      "Broken",
      ProjectLoader("1.0.0", cache, timings).load(location).plan.identity?.name,
    )
    assertTrue(Phase.COMPILATION in timings.totals())
  }

  @Test
  fun theCacheKeepsOnlyTheMostRecentlyUsedEntries() = inDirectory { root ->
    val cache = ScriptCache(root, maximumEntries = 2)
    val now = Instant.now()
    listOf("a", "b", "c").forEachIndexed { index, name ->
      val file = Files.writeString(root.resolve("$name.jar"), name)
      Files.setLastModifiedTime(file, FileTime.from(now.minusSeconds(60L * (3 - index))))
    }
    val abandoned = Files.writeString(root.resolve("d-1.tmp"), "")
    Files.setLastModifiedTime(abandoned, FileTime.from(now.minusSeconds(7200)))

    cache.prune()

    val left = Files.list(root).use { stream -> stream.map { it.name }.toList() }.sorted()
    assertEquals(listOf("b.jar", "c.jar"), left)
  }

  @Test
  fun eachSystemKeepsTheCacheWhereItsUsersCachesLive() {
    val none: (String) -> String? = { null }
    assertEquals(
      Path.of("C:/Users/u/AppData/Local", "Progmasoft", "Tazel", "Cache"),
      ScriptCache.userCacheDirectory(
        { if (it == "LOCALAPPDATA") "C:/Users/u/AppData/Local" else null },
        "Windows 11",
        "C:/Users/u",
      ),
    )
    assertEquals(
      Path.of("/Users/u", "Library", "Caches", "Progmasoft", "Tazel"),
      ScriptCache.userCacheDirectory(none, "Mac OS X", "/Users/u"),
    )
    assertEquals(
      Path.of("/home/u/.cache", "progmasoft", "tazel"),
      ScriptCache.userCacheDirectory(none, "Linux", "/home/u"),
    )
    assertEquals(
      Path.of("/tmp/tazel-cache"),
      ScriptCache.userCacheDirectory(
        { if (it == "TAZEL_CACHE_DIR") "/tmp/tazel-cache" else null },
        "Linux",
        "/home/u",
      ),
    )
  }

  @Test
  fun timingsAreReportedOnlyWhenAskedFor() {
    assertFalse(Timings.fromEnvironment { null }.enabled)
    assertTrue(Timings.fromEnvironment { if (it == "TAZEL_TIMINGS") "1" else null }.enabled)
    val timings = Timings(true)
    timings.measure(Phase.EVALUATION) {}
    assertEquals(listOf(Phase.EVALUATION), timings.totals().keys.toList())
    assertTrue(timings.report().single().startsWith("script evaluation"))
  }
}
