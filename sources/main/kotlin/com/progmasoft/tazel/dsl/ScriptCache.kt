/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.dsl.plugin.ResolvedPlugin
import java.io.File
import java.io.IOException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.channels.FileChannel
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.nio.file.StandardOpenOption
import java.nio.file.attribute.FileTime
import java.security.MessageDigest
import java.time.Duration
import java.time.Instant
import kotlin.io.path.name
import kotlin.script.experimental.api.CompiledScript
import kotlin.script.experimental.jvm.impl.KJvmCompiledScript
import kotlin.script.experimental.jvmhost.loadScriptFromJar
import kotlin.script.experimental.jvmhost.saveToJar

/** Bumped whenever the layout of a cached script or the meaning of its key changes. */
private const val CACHE_FORMAT = 2

/** The most compiled project files the cache keeps; the least recently used go first. */
private const val DEFAULT_MAXIMUM_ENTRIES = 256

/**
 * Everything the compiled form of a project file depends on. Two runs with the same key would
 * compile the same script into the same classes, so the second one can load the first one's output.
 */
data class ScriptCacheInputs(
  val source: String,
  val scriptName: String,
  val imports: List<String>,
  val compilerOptions: List<String>,
  val jvmTarget: Int,
  /** The classpath the script is compiled against, apart from the plugins. */
  val classpath: List<Path>,
  val plugins: List<ResolvedPlugin>,
  val tazelVersion: String,
  val kotlinVersion: String = kotlinCompilerVersion(),
)

/**
 * The version of the Kotlin compiler that compiles project files. The compiler is a runtime
 * dependency of the scripting host, so it is read reflectively; the standard library, which Tazel
 * ships with the same version, stands in when the compiler cannot be found.
 */
internal fun kotlinCompilerVersion(): String =
  runCatching {
      Class.forName("org.jetbrains.kotlin.config.KotlinCompilerVersion")
        .getField("VERSION")
        .get(null) as String
    }
    .getOrElse { KotlinVersion.CURRENT.toString() }

/**
 * The cache key of a compiled project file: a SHA-256 over the script text, the Kotlin compiler
 * version, the JVM target, the compilation configuration (script name, imports, compiler options),
 * the classpath and the plugin JARs.
 *
 * A classpath entry is identified by what it holds ([contentDigest]), so a JAR that was replaced
 * keeps no compiled script of its own, whatever its size and modification time say. A plugin JAR is
 * identified by the SHA-256 it was verified against.
 */
fun scriptCacheKey(inputs: ScriptCacheInputs): String {
  val digest = MessageDigest.getInstance("SHA-256")
  fun line(text: String) {
    digest.update(text.toByteArray(Charsets.UTF_8))
    digest.update(0)
  }
  line("tazel-script-cache $CACHE_FORMAT")
  line("tazel ${inputs.tazelVersion}")
  line("kotlin ${inputs.kotlinVersion}")
  line("jvm-target ${inputs.jvmTarget}")
  line("name ${inputs.scriptName}")
  inputs.imports.forEach { line("import $it") }
  inputs.compilerOptions.forEach { line("option $it") }
  inputs.classpath.forEach { entry -> line("classpath ${fingerprint(entry)}") }
  inputs.plugins
    .sortedBy { it.coordinate }
    .forEach { line("plugin ${it.coordinate} ${it.version} ${it.apiVersion} ${it.sha256}") }
  line("source ${sha256(inputs.source)}")
  return digest.digest().toHex()
}

private fun fingerprint(entry: Path): String {
  val absolute = entry.toAbsolutePath().normalize()
  return try {
    "$absolute ${contentDigest(absolute)}"
  } catch (_: IOException) {
    "$absolute missing"
  }
}

/**
 * What a classpath entry holds: for a JAR, the SHA-256 of its size and central directory; for any
 * other file, the SHA-256 of its bytes; for a directory, the SHA-256 of the names and contents of
 * the files below it.
 *
 * The central directory of a JAR lists every entry with its name, sizes and CRC-32, so a JAR whose
 * classes changed has another one; reading it takes a few kilobytes where the whole JAR is
 * megabytes. It guards against a JAR that was replaced, not against one crafted to keep the CRCs:
 * whoever can write Tazel's installation already runs code as its user.
 */
internal fun contentDigest(entry: Path): String {
  if (Files.isRegularFile(entry)) {
    centralDirectory(entry)?.let { (size, directory) ->
      val digest = MessageDigest.getInstance("SHA-256")
      digest.update("zip $size".toByteArray())
      digest.update(0)
      digest.update(directory)
      return digest.digest().toHex()
    }
  }
  return wholeDigest(entry)
}

private const val END_SIGNATURE = 0x06054b50
private const val END_LENGTH = 22
private const val MAXIMUM_COMMENT = 0xFFFF
private const val ZIP64_LOCATOR_SIGNATURE = 0x07064b50
private const val ZIP64_LOCATOR_LENGTH = 20
private const val ZIP64_END_SIGNATURE = 0x06064b50
private const val ZIP64_END_LENGTH = 56
private const val CENTRAL_SIGNATURE = 0x02014b50
private const val MAXIMUM_CENTRAL_DIRECTORY = 64 shl 20

/**
 * The size and central directory of [file], or `null` when it is not a ZIP file this reads: then
 * its whole content stands for it.
 */
internal fun centralDirectory(file: Path): Pair<Long, ByteBuffer>? =
  FileChannel.open(file, StandardOpenOption.READ).use { channel ->
    val size = channel.size()
    if (size < END_LENGTH) return null
    val tailLength = minOf(size, (END_LENGTH + MAXIMUM_COMMENT).toLong()).toInt()
    val tail = read(channel, size - tailLength, tailLength) ?: return null
    // The end record is the last one whose comment reaches exactly to the end of the file.
    val end =
      (tailLength - END_LENGTH downTo 0).firstOrNull {
        tail.getInt(it) == END_SIGNATURE &&
          tail.getShort(it + 20).toInt() and 0xFFFF == tailLength - it - END_LENGTH
      } ?: return null
    var length = tail.getInt(end + 12).toLong() and 0xFFFFFFFFL
    var offset = tail.getInt(end + 16).toLong() and 0xFFFFFFFFL
    if (length == 0xFFFFFFFFL || offset == 0xFFFFFFFFL) {
      val locatorAt = size - tailLength + end - ZIP64_LOCATOR_LENGTH
      val locator = read(channel, locatorAt, ZIP64_LOCATOR_LENGTH) ?: return null
      if (locator.getInt(0) != ZIP64_LOCATOR_SIGNATURE) return null
      val zip64 = read(channel, locator.getLong(8), ZIP64_END_LENGTH) ?: return null
      if (zip64.getInt(0) != ZIP64_END_SIGNATURE) return null
      length = zip64.getLong(40)
      offset = zip64.getLong(48)
    }
    if (length !in 0..MAXIMUM_CENTRAL_DIRECTORY || offset < 0 || offset + length > size) {
      return null
    }
    val directory = read(channel, offset, length.toInt()) ?: return null
    if (length > 0 && directory.getInt(0) != CENTRAL_SIGNATURE) return null
    size to directory
  }

/** [length] bytes of [channel] from [position], little-endian, or `null` past its end. */
private fun read(
  channel: FileChannel,
  position: Long,
  length: Int,
): ByteBuffer? {
  if (position < 0) return null
  val buffer = ByteBuffer.allocate(length).order(ByteOrder.LITTLE_ENDIAN)
  while (buffer.hasRemaining()) {
    if (channel.read(buffer, position + buffer.position()) < 0) return null
  }
  return buffer.flip()
}

private fun wholeDigest(entry: Path): String {
  val digest = MessageDigest.getInstance("SHA-256")
  val buffer = ByteArray(64 * 1024)
  fun add(file: Path) =
    Files.newInputStream(file).use { stream ->
      generateSequence { stream.read(buffer) }
        .takeWhile { it >= 0 }
        .forEach { digest.update(buffer, 0, it) }
    }
  if (Files.isDirectory(entry)) {
    val files =
      Files.walk(entry)
        .use { paths -> paths.filter(Files::isRegularFile).toList() }
        .sortedBy { entry.relativize(it).toString() }
    for (file in files) {
      digest.update(entry.relativize(file).toString().replace('\\', '/').toByteArray())
      digest.update(0)
      digest.update(Files.size(file).toString().toByteArray())
      digest.update(0)
      add(file)
    }
  } else {
    add(entry)
  }
  return digest.digest().toHex()
}

internal fun sha256(text: String): String =
  MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8)).toHex()

private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }

/**
 * Compiled project files on disk, one JAR per key.
 *
 * The cache is only an optimization: anything it cannot read or write is treated as a miss and the
 * script is compiled again. An entry is written to a temporary file and moved into place, so a
 * concurrent run never loads half a JAR, and the directory never holds more than [maximumEntries]
 * entries.
 */
class ScriptCache(
  val directory: Path,
  private val maximumEntries: Int = DEFAULT_MAXIMUM_ENTRIES,
) {
  init {
    require(maximumEntries > 0) { "maximumEntries must be positive" }
  }

  private fun entry(key: String): Path = directory.resolve("$key.jar")

  /** The compiled script of [key], or `null` when there is none or it cannot be used. */
  fun load(key: String): CompiledScript? {
    val file = entry(key)
    if (!Files.isRegularFile(file)) return null
    return try {
      // A dependency of the script that is gone makes the entry unusable rather than a failure
      // later.
      val script = file.toFile().loadScriptFromJar(checkMissingDependencies = true)
      if (script == null) {
        Files.deleteIfExists(file)
      } else {
        Files.setLastModifiedTime(file, FileTime.from(Instant.now()))
      }
      script
    } catch (_: Exception) {
      runCatching { Files.deleteIfExists(file) }
      null
    }
  }

  /** Keeps [script] under [key]. A script that cannot be saved is simply not cached. */
  fun store(
    key: String,
    script: CompiledScript,
  ) {
    if (script !is KJvmCompiledScript) return
    try {
      Files.createDirectories(directory)
      val temporary = Files.createTempFile(directory, "$key-", ".tmp")
      try {
        script.saveToJar(temporary.toFile())
        moveIntoPlace(temporary, entry(key))
      } finally {
        Files.deleteIfExists(temporary)
      }
      prune()
    } catch (_: IOException) {
      // The next run compiles again.
    } catch (_: RuntimeException) {
      // saveToJar reports some failures unchecked; the cache stays optional.
    }
  }

  private fun moveIntoPlace(
    temporary: Path,
    target: Path,
  ) {
    try {
      Files.move(
        temporary,
        target,
        StandardCopyOption.ATOMIC_MOVE,
        StandardCopyOption.REPLACE_EXISTING,
      )
    } catch (_: AtomicMoveNotSupportedException) {
      Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING)
    }
  }

  /**
   * Removes the least recently used entries beyond the limit, and temporary files a crash left
   * behind.
   */
  internal fun prune() {
    val files =
      try {
        Files.list(directory).use { it.toList() }
      } catch (_: IOException) {
        return
      }
    val staleBefore = Instant.now().minus(Duration.ofHours(1))
    files
      .filter { it.name.endsWith(".tmp") }
      .filter { lastModified(it).isBefore(staleBefore) }
      .forEach { runCatching { Files.deleteIfExists(it) } }
    files
      .filter { it.name.endsWith(".jar") }
      .sortedByDescending(::lastModified)
      .drop(maximumEntries)
      .forEach { runCatching { Files.deleteIfExists(it) } }
  }

  private fun lastModified(file: Path): Instant =
    try {
      Files.getLastModifiedTime(file).toInstant()
    } catch (_: IOException) {
      Instant.EPOCH
    }

  companion object {
    /**
     * The cache of the user running Tazel: `TAZEL_CACHE_DIR` when it is set, otherwise the cache
     * directory the operating system gives a user.
     */
    fun forUser(environment: (String) -> String? = System::getenv): ScriptCache =
      ScriptCache(userCacheDirectory(environment).resolve("scripts"))

    internal fun userCacheDirectory(
      environment: (String) -> String?,
      osName: String = System.getProperty("os.name"),
      home: String = System.getProperty("user.home"),
    ): Path {
      environment("TAZEL_CACHE_DIR")?.takeIf(String::isNotBlank)?.let {
        return Path.of(it)
      }
      val os = osName.lowercase()
      return when {
        os.startsWith("windows") ->
          Path.of(
            environment("LOCALAPPDATA")?.takeIf(String::isNotBlank)
              ?: File(home, "AppData/Local").path,
            "Progmasoft",
            "Tazel",
            "Cache",
          )
        os.startsWith("mac") -> Path.of(home, "Library", "Caches", "Progmasoft", "Tazel")
        else ->
          Path.of(
            environment("XDG_CACHE_HOME")?.takeIf(String::isNotBlank) ?: File(home, ".cache").path,
            "progmasoft",
            "tazel",
          )
      }
    }
  }
}
