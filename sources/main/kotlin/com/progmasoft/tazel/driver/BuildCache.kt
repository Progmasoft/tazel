/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildCacheSettings
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
 * The compiler as a key sees it: its version and the contents of the files that make it up, because
 * a build of the compiler can change without its version changing. Contents rather than times, so
 * that two machines with the same compiler share results. Reading them takes a while, so [memo]
 * keeps the digest for as long as the sizes and times of the files stay the same.
 */
fun toolIdentity(
  compiler: Path,
  version: String,
  memo: Path,
): String {
  val names = (listOf(compiler.fileName.toString()) + COMPILER_FILES).distinct()
  val files = names.map(compiler::resolveSibling).filter(Files::isRegularFile)
  val state =
    files.joinToString("\n") { "${it.fileName} ${Files.size(it)} ${Files.getLastModifiedTime(it)}" }
  val known =
    try {
      Files.readString(memo).takeIf { it.startsWith("$state\n$MEMO_END") }?.substringAfterLast('\n')
    } catch (_: IOException) {
      null
    }
  val digest = known ?: contentsDigest(files, compiler.fileName.toString())
  if (known == null) {
    try {
      Files.createDirectories(memo.parent)
      Files.writeString(memo, "$state\n$MEMO_END\n$digest")
    } catch (_: IOException) {
      // Without the memo the next command reads the files again.
    }
  }
  return "$version\n$digest"
}

/** The program of the compiler counts under one name, which differs between hosts. */
private fun contentsDigest(
  files: List<Path>,
  programName: String,
): String {
  val digest = MessageDigest.getInstance("SHA-256")
  val buffer = ByteArray(BUFFER_SIZE)
  for (file in files) {
    val name = file.fileName.toString()
    digest.update((if (name == programName) "compiler" else name).toByteArray())
    Files.newInputStream(file).use { stream ->
      generateSequence { stream.read(buffer) }
        .takeWhile { it >= 0 }
        .forEach { digest.update(buffer, 0, it) }
    }
  }
  return digest.digest().joinToString("") { "%02x".format(it) }
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
 * Results of earlier runs, kept in [directory], `.tazel/cache` of a project, and in [remote] when
 * the project names one.
 *
 * Every local result lives in `<slug>/<key>`, a directory that appears whole or not at all. A
 * result stays after the sources move on, so going back to an earlier branch or switching the build
 * mode back brings its files back without a compiler run. The newest `maxEntries` of each slug are
 * kept.
 *
 * The local cache is asked first. What comes from the remote one is kept locally, so it is fetched
 * once.
 *
 * A cache must never fail a build: when a file cannot be read or written the result counts as
 * missing, and [failure] is told why. A remote cache that fails once is left alone for the rest of
 * the command, so that a server that is down costs one timeout and not one for each target.
 */
class BuildCache(
  private val directory: Path,
  private val settings: BuildCacheSettings,
  private val remote: RemoteStore?,
  private val failure: (String) -> Unit,
) {
  /** What became of the files of a result. */
  enum class Restored {
    /** The files in place were already those of the result. */
    FRESH,

    /** At least one file was missing or different and came from the cache. */
    COPIED,
  }

  /** A result that was found: what its run wrote and, when it was asked for, its files. */
  class Hit(
    val log: String,
    val restored: Restored?,
  )

  private var remoteFailed = false

  private val readsRemote
    get() = remote != null && settings.remote?.read == true && !remoteFailed

  private val writesRemote
    get() = remote != null && settings.remote?.write == true && !remoteFailed

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

  /** Like [guarded] for a call of the remote cache, which is then left alone. */
  private inline fun <T> remotely(
    what: String,
    block: () -> T,
  ): T? =
    try {
      block()
    } catch (problem: IOException) {
      remoteFailed = true
      failure("the remote build cache could not $what, and is not asked again: $problem")
      null
    }

  /**
   * The result of [key], with its files put into [target] over any that differ; null when there is
   * no such result or it could not be used, and the run has to happen. Without a [target] only its
   * log is read.
   */
  fun load(
    slug: String,
    key: String,
    target: Path?,
  ): Hit? {
    val local = entry(slug, key)
    if (settings.local.enabled && Files.isRegularFile(local.resolve(LOG_FILE))) {
      return guarded("restore a result") { open(local, target) }
    }
    if (!readsRemote) return null
    return guarded("keep a result of the remote cache") {
      val parent = Files.createDirectories(directory.resolve(slug))
      val fetched = Files.createTempDirectory(parent, ".fetched-")
      val archive = Files.createTempFile(parent, ".fetching-", ".zip")
      try {
        val found =
          remotely("read a result") {
            remote!!.fetch(slug, key, archive) && unpacked(archive, fetched)
          } == true
        when {
          !found -> null
          settings.local.enabled -> {
            moveIntoPlace(fetched, local)
            prune(parent)
            open(local, target)
          }
          else -> open(fetched, target)
        }
      } finally {
        fetched.toFile().deleteRecursively()
        Files.deleteIfExists(archive)
      }
    }
  }

  private fun unpacked(
    archive: Path,
    fetched: Path,
  ): Boolean {
    unpackResult(archive, fetched)
    if (!Files.isRegularFile(fetched.resolve(LOG_FILE))) {
      throw IOException("a result of the remote cache has no log")
    }
    return true
  }

  private fun open(
    entry: Path,
    target: Path?,
  ): Hit {
    val log = Files.readString(entry.resolve(LOG_FILE))
    if (target == null) return Hit(log, null)
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
    return Hit(log, restored)
  }

  /** Records [files] and the [log] of a run that succeeded. */
  fun store(
    slug: String,
    key: String,
    files: List<Path>,
    log: String,
  ) {
    if (!settings.local.enabled && !writesRemote) return
    guarded("store a result") {
      val parent = Files.createDirectories(directory.resolve(slug))
      val staged = Files.createTempDirectory(parent, ".staged-")
      try {
        files.forEach { Files.copy(it, staged.resolve(it.fileName.toString())) }
        Files.writeString(staged.resolve(LOG_FILE), log)
        if (writesRemote) publish(slug, key, staged, parent)
        if (settings.local.enabled) {
          moveIntoPlace(staged, entry(slug, key))
          prune(parent)
        }
      } finally {
        staged.toFile().deleteRecursively()
      }
    }
  }

  private fun publish(
    slug: String,
    key: String,
    staged: Path,
    parent: Path,
  ) {
    val archive = Files.createTempFile(parent, ".publishing-", ".zip")
    try {
      packResult(staged, archive)
      remotely("store a result") { remote!!.publish(slug, key, archive) }
    } finally {
      Files.deleteIfExists(archive)
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
      .drop(settings.local.maxEntries)
      .forEach { it.toFile().deleteRecursively() }
  }

  companion object {
    /** Where a project keeps its results, relative to its root. */
    const val DIRECTORY = ".tazel/cache"

    /** The file of [toolIdentity] in [DIRECTORY]. */
    const val COMPILER_MEMO = ".compiler"

    private const val LOG_FILE = ".log"
  }
}

private const val BUFFER_SIZE = 64 * 1024
private const val MEMO_END = "digest"
