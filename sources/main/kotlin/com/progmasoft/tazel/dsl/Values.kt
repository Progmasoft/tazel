/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

/** A project file that Tazel cannot use, with the reason. */
class ProjectException(message: String) : RuntimeException(message)

/** Files a target names: one path, or the files that glob patterns take. */
sealed interface FileSelection

/** One path, relative to the directory of the `BUILD.kts` or `MODULE.kts`. */
data class FilePath(val path: String) : FileSelection

/** The files that match any of [patterns]; `*` stays in one directory, `**` crosses them. */
data class Glob(val patterns: List<String>) : FileSelection

// The documentation below spells no pattern: a slash before a star would open a nested comment.
/** The files matching [patterns], for example every C file below `src`. */
fun glob(vararg patterns: String): Glob {
  if (patterns.isEmpty()) throw ProjectException("glob needs at least one pattern")
  patterns.forEach { requireRelativePath(it, "glob pattern") }
  return Glob(patterns.toList())
}

/** The value of the environment variable [name] where Tazel runs, `null` when it is not set. */
fun env(name: String): String? = System.getenv(name)

/** A number of bytes, such as `16.gigabytes`. */
@JvmInline
value class ByteSize(val bytes: Long) {
  override fun toString(): String = "$bytes bytes"
}

val Int.kilobytes: ByteSize
  get() = ByteSize(this.toLong() * 1024)

val Int.megabytes: ByteSize
  get() = ByteSize(this.toLong() * 1024 * 1024)

val Int.gigabytes: ByteSize
  get() = ByteSize(this.toLong() * 1024 * 1024 * 1024)

/** Refuses an empty or absolute path; `..` is checked against the module when it is loaded. */
internal fun requireRelativePath(path: String, what: String): String {
  if (path.isBlank()) throw ProjectException("a $what is empty")
  if (path.startsWith("/") || path.startsWith("\\") || Regex("^[A-Za-z]:").containsMatchIn(path)) {
    throw ProjectException("the $what '$path' is absolute; write it relative to its project file")
  }
  if ('\\' in path) throw ProjectException("the $what '$path' uses \\; write paths with /")
  return path
}

internal fun requireText(value: String, what: String): String {
  if (value.isBlank()) throw ProjectException("$what is empty")
  return value
}

private val SEMVER =
  Regex(
    """(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)""" +
      """(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?"""
  )

/** Checks that [version] is a Semantic Version such as `1.2.0` or `1.2.0-rc.1`. */
internal fun requireSemVer(version: String, what: String): String {
  if (!SEMVER.matches(version)) {
    throw ProjectException("$what '$version' is not a Semantic Version such as 1.2.0")
  }
  return version
}
