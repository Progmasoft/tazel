/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
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

private fun sha256(text: String) =
  MessageDigest.getInstance("SHA-256").digest(text.toByteArray()).toHex()

private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

/**
 * Everything that decides what one compiler run writes.
 *
 * Talac compiles all sources of a target as one program, so a target is the smallest thing that can
 * be skipped. [arguments] are a digest of the settings and the names of the inputs, and [files] the
 * digests of the contents by path. Times do not count, so a checkout that touches a file and
 * changes nothing still finds its results. [tool] is a digest of what identifies the compiler.
 *
 * The parts stay apart so that a new run can say which of them differ from the last one.
 */
data class BuildInputs(
  val tool: String,
  val arguments: String,
  val files: Map<String, String>,
) {
  val text: String
    get() =
      (listOf(tool, arguments) + files.map { (file, digest) -> "$digest $file" }).joinToString("\n")

  /** The key of a result in the cache. */
  val key: String
    get() = sha256("tazel build key 2\n$text")

  /**
   * Why a run with these inputs cannot use the result of one with [previous]: the first change that
   * matters to the person waiting for it.
   */
  fun changeFrom(previous: BuildInputs): String? {
    if (tool != previous.tool) return "the compiler changed"
    val changes =
      files.keys
        .filter { it in previous.files && files[it] != previous.files[it] }
        .map { "$it changed" } +
        (files.keys - previous.files.keys).map { "$it added" } +
        (previous.files.keys - files.keys).map { "$it removed" }
    return when {
      changes.size == 1 -> changes.single()
      changes.size > 1 -> "${changes.first()}, and ${changes.size - 1} more"
      arguments != previous.arguments -> "the settings changed"
      else -> null
    }
  }

  companion object {
    /** The inputs of a run of [tool] with [arguments] on [files], paths relative to [root]. */
    fun of(
      tool: String,
      arguments: List<String>,
      root: Path,
      files: List<String>,
    ) =
      BuildInputs(
        sha256(tool),
        sha256(arguments.joinToString("") { "${it.length}:$it" }),
        files.associateWith { contentsDigest(root.resolve(it)) },
      )

    /** The inputs [text] stands for; null when it is not what [BuildInputs.text] writes. */
    fun parse(text: String): BuildInputs? {
      val lines = text.lines()
      if (lines.size < 2) return null
      val files = lines.drop(2).map { it.substringAfter(' ') to it.substringBefore(' ') }
      return BuildInputs(lines[0], lines[1], files.toMap())
    }
  }
}

/** The key of a compiler run; see [BuildInputs]. */
fun buildKey(
  tool: String,
  arguments: List<String>,
  root: Path,
  files: List<String>,
): String = BuildInputs.of(tool, arguments, root, files).key

private fun contentsDigest(file: Path): String {
  val digest = MessageDigest.getInstance("SHA-256")
  updateWith(digest, file)
  return digest.digest().toHex()
}

private fun updateWith(
  digest: MessageDigest,
  file: Path,
) {
  val buffer = ByteArray(BUFFER_SIZE)
  Files.newInputStream(file).use { stream ->
    generateSequence { stream.read(buffer) }
      .takeWhile { it >= 0 }
      .forEach { digest.update(buffer, 0, it) }
  }
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
  for (file in files) {
    val name = file.fileName.toString()
    digest.update((if (name == programName) "compiler" else name).toByteArray())
    updateWith(digest, file)
  }
  return digest.digest().toHex()
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

  /** One result in the local cache. */
  class Stored(
    val slug: String,
    val key: String,
    val bytes: Long,
  )

  /** What [push] did: results sent, results the server had, and whether it stopped on a failure. */
  class Pushed(
    val sent: Int,
    val present: Int,
    val failed: Boolean,
  )

  /** A result that was found: what its run wrote and, when it was asked for, its files. */
  class Hit(
    val log: String,
    val restored: Restored?,
  )

  private var remoteFailed = false
  private var warnedUnsigned = false

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
            remote!!.fetch(slug, key, archive) &&
              genuine(slug, key, archive) &&
              unpacked(archive, fetched)
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

  private fun directoriesIn(parent: Path): List<Path> =
    if (!Files.isDirectory(parent)) emptyList()
    else
      Files.newDirectoryStream(parent).use { stream ->
        stream
          .filter { Files.isDirectory(it) && !it.fileName.toString().startsWith(".") }
          .sortedBy { it.fileName.toString() }
      }

  private fun sizeOf(directory: Path): Long =
    Files.walk(directory).use { paths ->
      paths.filter(Files::isRegularFile).mapToLong(Files::size).sum()
    }

  /** The results the local cache holds. */
  fun results(): List<Stored> =
    guarded("read its results") {
        directoriesIn(directory).flatMap { slug ->
          directoriesIn(slug).map {
            Stored(slug.fileName.toString(), it.fileName.toString(), sizeOf(it))
          }
        }
      }
      .orEmpty()

  /** Removes the local cache and returns how many bytes that freed. */
  fun clean(): Long {
    if (!Files.isDirectory(directory)) return 0
    return guarded("clean") {
      val bytes = sizeOf(directory)
      if (!directory.toFile().deleteRecursively()) throw IOException("$directory stayed")
      bytes
    } ?: 0
  }

  /** Sends what the local cache holds and the server does not. */
  fun push(): Pushed {
    var sent = 0
    var present = 0
    for (result in results()) {
      val there = remotely("say what it holds") { remote!!.contains(result.slug, result.key) }
      when {
        there == null -> return Pushed(sent, present, true)
        there -> present++
        else -> {
          guarded("send a result") {
            val parent = directory.resolve(result.slug)
            publish(result.slug, result.key, parent.resolve(result.key), parent)
          }
          if (remoteFailed) return Pushed(sent, present, true)
          sent++
        }
      }
    }
    return Pushed(sent, present, false)
  }

  /** Whether the server answers; the reason when it does not. */
  fun remoteProblem(): String? =
    try {
      remote?.contains("status", "status")
      null
    } catch (problem: IOException) {
      problem.message ?: problem.javaClass.name
    }

  /** The inputs of the last run of [slug] that left a result, to tell what is new. */
  fun latest(slug: String): BuildInputs? =
    try {
      BuildInputs.parse(Files.readString(directory.resolve(slug).resolve(LATEST_FILE)))
    } catch (_: IOException) {
      null
    }

  /** Records [inputs] as the last run of [slug]; losing that loses only an explanation. */
  fun remember(
    slug: String,
    inputs: BuildInputs,
  ) {
    if (!settings.local.enabled) return
    try {
      Files.createDirectories(directory.resolve(slug))
      Files.writeString(directory.resolve(slug).resolve(LATEST_FILE), inputs.text)
    } catch (_: IOException) {
      // The next run says less about why it runs.
    }
  }

  /** The test suites that failed in the last run. */
  fun failedSuites(): Set<String> =
    try {
      Files.readAllLines(directory.resolve(FAILED_SUITES_FILE)).toSet()
    } catch (_: IOException) {
      emptySet()
    }

  /** Records [suites] for [failedSuites]; losing that loses only their turn at the front. */
  fun rememberFailedSuites(suites: List<String>) {
    if (!settings.local.enabled) return
    try {
      Files.createDirectories(directory)
      Files.write(directory.resolve(FAILED_SUITES_FILE), suites)
    } catch (_: IOException) {
      // The next run keeps the declared order.
    }
  }

  /**
   * Whether the result in [archive] may be used. With a public key it needs its signature, and a
   * result without one is simply not there yet, or never was; a wrong one is an attack or a
   * mistake, and the server is not trusted further. Without a key the project has chosen to trust
   * the server, which it is told once.
   */
  private fun genuine(
    slug: String,
    key: String,
    archive: Path,
  ): Boolean {
    val configured = settings.remote ?: return false
    val publicKey = configured.publicKey
    if (publicKey == null) {
      if (!warnedUnsigned) {
        warnedUnsigned = true
        failure(
          "results of ${configured.url} are used without a signature check; " +
            "tazel cache keygen makes the buildCache.remote.publicKey that turns it on"
        )
      }
      return true
    }
    val signature = Files.createTempFile(archive.parent, ".signature-", "")
    try {
      if (!remote!!.fetch(slug, "$key$SIGNATURE_SUFFIX", signature)) return false
      if (!isSignedResult(publicKey, slug, key, archive, Files.readAllBytes(signature))) {
        throw IOException("the signature of a result is wrong")
      }
      return true
    } finally {
      Files.deleteIfExists(signature)
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
      remotely("store a result") {
        remote!!.publish(slug, key, archive)
        settings.remote?.signingKey?.let { signingKey ->
          // The signature goes second: a reader that comes between finds no result, not a bad one.
          val signature = Files.createTempFile(parent, ".signature-", "")
          try {
            Files.write(signature, signResult(signingKey, slug, key, archive))
            remote.publish(slug, "$key$SIGNATURE_SUFFIX", signature)
          } finally {
            Files.deleteIfExists(signature)
          }
        }
      }
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
    private const val SIGNATURE_SUFFIX = ".sig"
    private const val LATEST_FILE = ".latest"
    private const val FAILED_SUITES_FILE = ".failed-suites"
  }
}

private const val BUFFER_SIZE = 64 * 1024
private const val MEMO_END = "digest"
