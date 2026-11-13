/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path

/**
 * The inputs a Make-style dependency file names, as compilers write it with `-MD -MF`: the target,
 * a colon, then the inputs, with `\ ` for a space and a backslash at the end of a line to go on.
 */
fun depfileInputs(text: String): List<String> {
  val body = text.substringAfter(": ", text.substringAfter(":\n", ""))
  val inputs = mutableListOf<String>()
  val current = StringBuilder()
  var index = 0
  fun flush() {
    if (current.isNotEmpty()) inputs += current.toString()
    current.clear()
  }
  while (index < body.length) {
    val c = body[index]
    when {
      c == '\\' && index + 1 < body.length && body[index + 1] == ' ' -> {
        current.append(' ')
        index++
      }
      c == '\\' && index + 1 < body.length && body[index + 1] == '\n' -> {
        flush()
        index++
      }
      c == '\\' &&
        index + 2 < body.length &&
        body[index + 1] == '\r' &&
        body[index + 2] == '\n' -> {
        flush()
        index += 2
      }
      c == '$' && index + 1 < body.length && body[index + 1] == '$' -> {
        current.append('$')
        index++
      }
      c.isWhitespace() -> flush()
      else -> current.append(c)
    }
    index++
  }
  flush()
  return inputs
}

/**
 * The SHA-256 of files, kept while a file keeps its size and modification time, so a build reads a
 * file once and a file that is touched but not changed costs one more read.
 */
class Digests {
  private data class Stamp(val size: Long, val modified: Long)

  private val known = java.util.concurrent.ConcurrentHashMap<Path, Pair<Stamp, String>>()

  /** The digest of [file] as hex, or `null` when it is not a regular file. */
  fun of(file: Path): String? {
    val attributes =
      try {
        Files.readAttributes(file, java.nio.file.attribute.BasicFileAttributes::class.java)
      } catch (_: IOException) {
        return null
      }
    if (!attributes.isRegularFile) return null
    val stamp = Stamp(attributes.size(), attributes.lastModifiedTime().toMillis())
    known[file]?.let { (seen, digest) -> if (seen == stamp) return digest }
    val digest =
      try {
        sha256(Files.newInputStream(file).use { it.readAllBytes() })
      } catch (_: IOException) {
        return null
      }
    known[file] = stamp to digest
    return digest
  }

  companion object {
    fun sha256(bytes: ByteArray): String =
      java.security.MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") {
        "%02x".format(it)
      }
  }
}

/**
 * One action of a build: a command line with its environment, the files it reads and the files it
 * writes. Inputs the command finds by itself, such as headers, come from [discovered] after it ran.
 */
class Action(
  val command: List<String>,
  val directory: Path,
  val inputs: List<Path>,
  val outputs: List<Path>,
  val environment: Map<String, String> = emptyMap(),
  val discovered: () -> List<Path> = { emptyList() },
) {
  /**
   * The digest of what the action is apart from its files: the command, directory and environment.
   */
  val key: String
    get() =
      Digests.sha256(
        (command +
            "\u0001" +
            directory.toString() +
            "\u0001" +
            environment.toSortedMap().map { (name, value) -> "$name=$value" })
          .joinToString("\u0000")
          .toByteArray()
      )
}

/**
 * Action-level incremental builds. Each action keeps a record beside its first output: its key, and
 * the digest of every input it read and every output it wrote. An action is current while its key
 * is the same, every input has the digest it had, and every output is still what it wrote. Inputs
 * are compared by content, so touching a file reruns nothing, and an output rebuilt to the same
 * bytes reruns none of the actions that read it.
 */
class ActionCache(private val digests: Digests = Digests()) {
  /** Whether [action] would write what it wrote last time. */
  fun current(action: Action): Boolean {
    val record = read(recordOf(action)) ?: return false
    if (record.key != action.key) return false
    return record.files.all { (file, digest) -> digests.of(file) == digest } &&
      action.outputs.all { it in record.files.keys }
  }

  /** Records [action] after it succeeded. */
  fun record(action: Action) {
    val files = linkedMapOf<Path, String>()
    for (file in
      (action.inputs + action.discovered() + action.outputs).map {
        it.toAbsolutePath().normalize()
      }) {
      files[file] = digests.of(file) ?: throw IOException("$file was not written or cannot be read")
    }
    val text = buildString {
      appendLine("key ${action.key}")
      files.forEach { (file, digest) -> appendLine("file $digest $file") }
    }
    Files.writeString(recordOf(action), text)
  }

  /** Forgets [action], so that a failed or stopped run is redone. */
  fun forget(action: Action) {
    Files.deleteIfExists(recordOf(action))
  }

  private class Record(val key: String, val files: Map<Path, String>)

  private fun read(file: Path): Record? {
    val lines =
      try {
        Files.readAllLines(file)
      } catch (_: IOException) {
        return null
      }
    val key =
      lines.firstOrNull()?.removePrefix("key ")?.takeIf { lines.first().startsWith("key ") }
        ?: return null
    val files = linkedMapOf<Path, String>()
    for (line in lines.drop(1)) {
      if (!line.startsWith("file ")) return null
      val digest = line.substring(5, minOf(line.length, 5 + 64))
      files[Path.of(line.substring(5 + 64 + 1))] = digest
    }
    return Record(key, files)
  }

  private fun recordOf(action: Action): Path {
    val first = action.outputs.first()
    return first.resolveSibling(first.fileName.toString() + ".action")
  }
}
