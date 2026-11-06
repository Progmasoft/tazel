/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.dsl.plugin.ResolvedPlugin
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.attribute.FileTime
import java.time.Instant
import java.util.zip.CRC32
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.io.path.name
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotEquals
import kotlin.test.assertNotNull
import kotlin.test.assertNull
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

    // What a classpath entry holds decides the key: a JAR replaced with one of the same size and
    // modification time changes it, and a new modification time alone does not.
    val time = Files.getLastModifiedTime(jar)
    Files.writeString(jar, "CLASSES")
    Files.setLastModifiedTime(jar, time)
    val replaced = scriptCacheKey(base)
    assertNotEquals(key, replaced)
    Files.setLastModifiedTime(jar, FileTime.from(Instant.parse("2020-01-01T00:00:00Z")))
    assertEquals(replaced, scriptCacheKey(base))

    // A directory on the classpath is the names and contents of its files.
    val classes = Files.createDirectories(root.resolve("classes/a"))
    Files.writeString(classes.resolve("A.class"), "a")
    val withDirectory = base.copy(classpath = listOf(root.resolve("classes")))
    val directoryKey = scriptCacheKey(withDirectory)
    Files.writeString(classes.resolve("A.class"), "b")
    assertNotEquals(directoryKey, scriptCacheKey(withDirectory))
  }

  private fun jar(
    file: Path,
    entries: Map<String, String>,
    comment: String? = null,
  ): Path {
    ZipOutputStream(Files.newOutputStream(file)).use { zip ->
      comment?.let(zip::setComment)
      entries.forEach { (name, text) ->
        val bytes = text.toByteArray()
        val entry = ZipEntry(name)
        entry.method = ZipEntry.STORED
        entry.size = bytes.size.toLong()
        entry.crc = CRC32().apply { update(bytes) }.value
        zip.putNextEntry(entry)
        zip.write(bytes)
        zip.closeEntry()
      }
    }
    return file
  }

  /** A JAR is keyed by its central directory, which changes with the classes it holds. */
  @Test
  fun aJarIsKeyedByItsCentralDirectory() = inDirectory { root ->
    val library = jar(root.resolve("library.jar"), mapOf("a/A.class" to "first"), "a comment")
    assertNotNull(centralDirectory(library))
    assertNull(centralDirectory(Files.writeString(root.resolve("text.jar"), "classes")))
    val base = inputs(listOf(library))
    val key = scriptCacheKey(base)

    val time = Files.getLastModifiedTime(library)
    val size = Files.size(library)
    jar(library, mapOf("a/A.class" to "other"), "a comment")
    Files.setLastModifiedTime(library, time)
    assertEquals(size, Files.size(library))
    assertNotEquals(key, scriptCacheKey(base))
  }

  /** One loader reads each classpath entry once; the next command reads it again. */
  @Test
  fun aClasspathEntryIsReadOncePerLoader() = inDirectory { root ->
    val library = jar(root.resolve("library.jar"), mapOf("a/A.class" to "first"))
    val base = inputs(listOf(library))
    val fingerprints = ClasspathFingerprints()
    val key = scriptCacheKey(base, fingerprints)
    jar(library, mapOf("a/A.class" to "other"))
    assertEquals(key, scriptCacheKey(base, fingerprints))
    assertNotEquals(key, scriptCacheKey(base, ClasspathFingerprints()))
  }

  /** Classes behind a link inside a classpath directory are on the classpath, so they count. */
  @Test
  fun aLinkInAClasspathDirectoryIsFollowed() = inDirectory { root ->
    val target = Files.createDirectories(root.resolve("target/b"))
    Files.writeString(target.resolve("B.class"), "b")
    val classes = Files.createDirectories(root.resolve("classes"))
    try {
      Files.createSymbolicLink(classes.resolve("b"), target)
    } catch (_: IOException) {
      return@inDirectory // This system does not let the user make links.
    } catch (_: UnsupportedOperationException) {
      return@inDirectory
    }
    val withDirectory = inputs(listOf(classes))
    val key = scriptCacheKey(withDirectory)
    Files.writeString(target.resolve("B.class"), "c")
    assertNotEquals(key, scriptCacheKey(withDirectory))
  }

  /** A JAR whose end record defers to ZIP64 is read through its ZIP64 records. */
  @Test
  fun aZip64JarIsReadThroughItsZip64Records() = inDirectory { root ->
    val small = jar(root.resolve("small.jar"), mapOf("a/A.class" to "first"))
    val (_, expected) = assertNotNull(centralDirectory(small))
    val bytes = Files.readAllBytes(small)
    val end = ByteBuffer.wrap(bytes, bytes.size - 22, 22).slice().order(ByteOrder.LITTLE_ENDIAN)
    val length = end.getInt(12).toLong()
    val offset = end.getInt(16).toLong()
    val zip64At = (bytes.size - 22).toLong()
    val records = ByteBuffer.allocate(56 + 20 + 22).order(ByteOrder.LITTLE_ENDIAN)
    records.putInt(0x06064b50).putLong(44).putShort(45).putShort(45).putInt(0).putInt(0)
    records.putLong(1).putLong(1).putLong(length).putLong(offset)
    records.putInt(0x07064b50).putInt(0).putLong(zip64At).putInt(1)
    records.putInt(0x06054b50).putShort(0).putShort(0).putShort(-1).putShort(-1)
    records.putInt(-1).putInt(-1).putShort(0)
    val large = root.resolve("large.jar")
    Files.newOutputStream(large).use {
      it.write(bytes, 0, bytes.size - 22)
      it.write(records.array())
    }
    val (_, directory) = assertNotNull(centralDirectory(large))
    assertEquals(expected, directory)
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
    val compiled = ProjectLoader(cache, first).load(location)
    assertTrue(Phase.COMPILATION in first.totals())
    val entries = Files.list(cache.directory).use { stream -> stream.toList() }
    assertEquals(1, entries.count { it.name.endsWith(".jar") })

    val second = Timings(true)
    val cached = ProjectLoader(cache, second).load(location)
    assertFalse(Phase.COMPILATION in second.totals(), "the second load compiled again")
    assertTrue(Phase.CACHE_LOAD in second.totals())
    assertEquals(compiled.plan, cached.plan)

    // A changed project file is compiled again and gets its own entry.
    Files.writeString(
      location.file,
      Files.readString(location.file).replace("Cached\"", "Renamed\""),
    )
    val third = Timings(true)
    assertEquals("Renamed", ProjectLoader(cache, third).load(location).plan.identity?.name)
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
    ProjectLoader(cache).load(location)
    val entry = Files.list(cache.directory).use { stream -> stream.toList() }.single()
    Files.writeString(entry, "not a jar")

    val timings = Timings(true)
    assertEquals(
      "Broken",
      ProjectLoader(cache, timings).load(location).plan.identity?.name,
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
