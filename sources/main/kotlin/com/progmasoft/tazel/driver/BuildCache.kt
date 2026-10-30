/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

import java.io.IOException
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption.ATOMIC_MOVE
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.nio.file.attribute.FileTime
import java.security.MessageDigest
import java.time.Instant

/** The names TalaRT may have beside a program; each is moved and kept together with the program. */
val RUNTIME_LIBRARIES = listOf("talart.dll", "libtalart.so", "libtalart.dylib")

/**
 * The key of one compiler run: everything that decides what it writes.
 *
 * Talac compiles all sources of a target as one program, so a target is the smallest thing that can
 * be skipped. [arguments] hold the settings and the names of the inputs; [files], paths relative to
 * [root], add their contents. Times do not count, so a checkout that touches a file and changes
 * nothing still finds its results. [tool] identifies the compiler.
 */
fun buildKey(
  tool: String,
  arguments: List<String>,
  root: Path,
  files: List<String>,
): String {
  val digest = MessageDigest.getInstance("SHA-256")
  fun line(text: String) {
    val bytes = text.toByteArray()
    digest.update("${bytes.size}:".toByteArray())
    digest.update(bytes)
  }
  line("tazel build key 1")
  line(tool)
  arguments.forEach(::line)
  files.forEach { file ->
    line(file)
    Files.newInputStream(root.resolve(file)).use { stream ->
      val buffer = ByteArray(BUFFER_SIZE)
      generateSequence { stream.read(buffer) }
        .takeWhile { it >= 0 }
        .forEach { digest.update(buffer, 0, it) }
    }
    line("end of file")
  }
  return digest.digest().joinToString("") { "%02x".format(it) }
}

/**
 * The compiler as a key sees it: its version and, because a build of the compiler can change
 * without its version changing, the size and time of the files that make it up.
 */
fun toolIdentity(
  compiler: Path,
  version: String,
): String {
  val names = listOf(compiler.fileName.toString()) + COMPILER_FILES
  val files =
    names.distinct().mapNotNull { name ->
      val file = compiler.resolveSibling(name)
      if (Files.isRegularFile(file)) {
        "$name ${Files.size(file)} ${Files.getLastModifiedTime(file).toMillis()}"
      } else null
    }
  return (listOf(version) + files).joinToString("\n")
}

/** The other files that take part in a compiler run, beside the program of the compiler. */
private val COMPILER_FILES =
  RUNTIME_LIBRARIES +
    listOf(
      "talart.lib",
      "libtalart.a",
      "tala-frontend.dll",
      "libtala-frontend.so",
      "libtala-frontend.dylib",
    )

/**
 * Results of earlier runs, kept in [directory], `.tazel/cache` of a project.
 *
 * Every result lives in `<slug>/<key>`, a directory that appears whole or not at all. A result
 * stays after the sources move on, so going back to an earlier branch or switching the build mode
 * back brings its files back without a compiler run. The newest [KEPT] results of each slug are
 * kept.
 *
 * A cache must never fail a build: when a file cannot be read or written the result counts as
 * missing, and [failure] is told why.
 */
class BuildCache(
  private val directory: Path,
  private val failure: (String) -> Unit,
) {
  /** What [restore] did with the files of a result. */
  enum class Restored {
    /** The files in place were already those of the result. */
    FRESH,

    /** At least one file was missing or different and came from the cache. */
    COPIED,
  }

  private fun entry(
    slug: String,
    key: String,
  ) = directory.resolve(slug).resolve(key)

  private inline fun <T> guarded(
    what: String,
    block: () -> T,
  ): T? =
    try {
      block()
    } catch (problem: IOException) {
      failure("the build cache could not $what: $problem")
      null
    }

  /** What the run that made the result wrote, or null when there is no such result. */
  fun log(
    slug: String,
    key: String,
  ): String? {
    val file = entry(slug, key).resolve(LOG_FILE)
    if (!Files.isRegularFile(file)) return null
    return guarded("read a result") { Files.readString(file) }
  }

  /**
   * Puts the files of a result into [target], over any that differ. Null when there is no such
   * result or it could not be used, and the run has to happen.
   */
  fun restore(
    slug: String,
    key: String,
    target: Path,
  ): Restored? {
    val entry = entry(slug, key)
    if (!Files.isDirectory(entry)) return null
    return guarded("restore a result") {
      var restored = Restored.FRESH
      Files.createDirectories(target)
      Files.newDirectoryStream(entry).use { files ->
        for (file in files) {
          if (file.fileName.toString() == LOG_FILE) continue
          val place = target.resolve(file.fileName.toString())
          if (!Files.exists(place) || Files.mismatch(file, place) != -1L) {
            Files.copy(file, place, REPLACE_EXISTING)
            restored = Restored.COPIED
          }
        }
      }
      // The newest results are the ones kept, and using one makes it new.
      Files.setLastModifiedTime(entry, FileTime.from(Instant.now()))
      restored
    }
  }

  /** Records [files] and the [log] of a run that succeeded. */
  fun store(
    slug: String,
    key: String,
    files: List<Path>,
    log: String,
  ) {
    guarded("store a result") {
      val parent = directory.resolve(slug)
      Files.createDirectories(parent)
      val staged = Files.createTempDirectory(parent, ".staged-")
      try {
        files.forEach { Files.copy(it, staged.resolve(it.fileName.toString())) }
        Files.writeString(staged.resolve(LOG_FILE), log)
        moveIntoPlace(staged, entry(slug, key))
      } finally {
        staged.toFile().deleteRecursively()
      }
      prune(parent)
    }
  }

  private fun moveIntoPlace(
    staged: Path,
    entry: Path,
  ) {
    entry.toFile().deleteRecursively()
    try {
      Files.move(staged, entry, ATOMIC_MOVE)
    } catch (_: AtomicMoveNotSupportedException) {
      Files.move(staged, entry)
    } catch (_: FileAlreadyExistsException) {
      // Another run of Tazel stored the same result first.
    }
  }

  private fun prune(parent: Path) {
    val entries =
      Files.newDirectoryStream(parent).use { stream ->
        stream.filter { !it.fileName.toString().startsWith(".") }
      }
    entries
      .sortedByDescending { Files.getLastModifiedTime(it) }
      .drop(KEPT)
      .forEach { it.toFile().deleteRecursively() }
  }

  companion object {
    /** How many results of one slug are kept. */
    const val KEPT = 8

    /** Where a project keeps its results, relative to its root. */
    const val DIRECTORY = ".tazel/cache"

    private const val LOG_FILE = ".log"
  }
}

private const val BUFFER_SIZE = 64 * 1024
