/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.FilePath
import com.progmasoft.tazel.dsl.FileSelection
import com.progmasoft.tazel.dsl.Glob
import com.progmasoft.tazel.dsl.ProjectException
import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path

/**
 * Turns the paths and globs of a project file into files.
 *
 * `*` and `?` stay inside one path segment; a segment that is `**` stands for any number of
 * segments. A pattern that names a directory in an exclusion excludes everything below it. Names
 * are compared the way the host compares file names: without regard to case on Windows, exactly
 * elsewhere. Links are not followed.
 */
class FileSets(private val ignoreCase: Boolean) {
  /**
   * The files [selections] take in [directory], less those [excludes] take, in a stable order. A
   * path that names no file is an error; a glob may take none.
   */
  fun expand(
    directory: Path,
    selections: List<FileSelection>,
    excludes: List<FileSelection> = emptyList(),
  ): List<Path> {
    val base = directory.toAbsolutePath().normalize()
    val excluded = excludes.flatMap(::patternsOf).map { segments(it) }
    val found = linkedSetOf<Path>()
    for (selection in selections) {
      when (selection) {
        is FilePath -> {
          val file = base.resolve(selection.path).normalize()
          if (!Files.isRegularFile(file)) throw ProjectException("no file $file")
          if (excluded.none { matches(it, relative(base, file)) }) found.add(file)
        }
        is Glob ->
          for (pattern in selection.patterns) {
            glob(base, segments(pattern))
              .filter { file ->
                excluded.none { excludes(it, relative(base, file)) }
              }
              .forEach(found::add)
          }
      }
    }
    return found.toList()
  }

  /** True when the glob [pattern] matches [path], both relative and written with `/`. */
  fun matches(pattern: String, path: String) = matches(segments(pattern), segments(path))

  private fun patternsOf(selection: FileSelection) =
    when (selection) {
      is FilePath -> listOf(selection.path)
      is Glob -> selection.patterns
    }

  private fun glob(base: Path, pattern: List<String>): List<Path> {
    val literal = pattern.takeWhile { segment -> segment.none { it in "*?" } }
    val start = base.resolve(literal.joinToString("/").ifEmpty { "." }).normalize()
    val rest = pattern.drop(literal.size)
    if (rest.isEmpty()) return listOf(start).filter(Files::isRegularFile)
    if (!Files.isDirectory(start)) return emptyList()
    val found = mutableListOf<Path>()
    walk(start, start, rest, found)
    return found.sortedWith(
      compareBy { if (ignoreCase) it.toString().lowercase() else it.toString() }
    )
  }

  private fun walk(start: Path, directory: Path, pattern: List<String>, found: MutableList<Path>) {
    val entries =
      try {
        Files.newDirectoryStream(directory).use { it.toList() }
      } catch (error: IOException) {
        throw ProjectException("cannot list $directory: ${error.message}")
      }
    for (entry in entries) {
      if (Files.isSymbolicLink(entry)) continue
      if (Files.isDirectory(entry)) {
        walk(start, entry, pattern, found)
      } else if (matches(pattern, segments(start.relativize(entry).toString()))) {
        found.add(entry)
      }
    }
  }

  private fun relative(base: Path, file: Path) = segments(base.relativize(file).toString())

  private fun excludes(pattern: List<String>, path: List<String>) =
    (1..path.size).any { size -> matches(pattern, path.take(size)) } ||
      matches(pattern + "**", path)

  private fun matches(pattern: List<String>, path: List<String>): Boolean {
    if (pattern.isEmpty()) return path.isEmpty()
    if (pattern.first() == "**") {
      val rest = pattern.drop(1)
      return (0..path.size).any { skipped -> matches(rest, path.drop(skipped)) }
    }
    return path.isNotEmpty() &&
      matchesSegment(pattern.first(), 0, path.first(), 0) &&
      matches(pattern.drop(1), path.drop(1))
  }

  private fun matchesSegment(
    pattern: String,
    patternIndex: Int,
    text: String,
    textIndex: Int,
  ): Boolean {
    if (patternIndex == pattern.length) return textIndex == text.length
    return when (val expected = pattern[patternIndex]) {
      '*' ->
        (textIndex..text.length).any { next ->
          matchesSegment(pattern, patternIndex + 1, text, next)
        }
      '?' ->
        textIndex < text.length && matchesSegment(pattern, patternIndex + 1, text, textIndex + 1)
      else ->
        textIndex < text.length &&
          expected.equals(text[textIndex], ignoreCase) &&
          matchesSegment(pattern, patternIndex + 1, text, textIndex + 1)
    }
  }

  private fun segments(path: String) =
    path.replace('\\', '/').split('/').filter { it.isNotEmpty() && it != "." }
}
