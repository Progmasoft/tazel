/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.io.IOException
import java.io.OutputStream
import java.nio.file.Files
import java.nio.file.Path
import java.util.zip.Deflater
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.test.AfterTest
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class ArchiveLimitsTest {
  private val root = Files.createTempDirectory("tazel-archive-limits-")

  @AfterTest
  fun cleanup() {
    root.toFile().deleteRecursively()
  }

  @Test
  fun theFilesOfAnArchiveShareOneLimit() {
    // Two files, each below the limit and together above it.
    val archive = archive("a" to SMALL.bytes / 2 + 1, "b" to SMALL.bytes / 2 + 1)
    val error =
      assertFailsWith<IOException> {
        forEachEntry(archive, SMALL) { _, contents ->
          contents.copyTo(OutputStream.nullOutputStream())
        }
      }
    assertContains(error.message.orEmpty(), "more than ${SMALL.bytes} bytes")
    forEachEntry(archive("a" to SMALL.bytes / 2, "b" to SMALL.bytes / 2), SMALL) { _, contents ->
      contents.copyTo(OutputStream.nullOutputStream())
    }
  }

  @Test
  fun anArchiveHoldsALimitedNumberOfFiles() {
    val archive = archive(*Array(SMALL.entries + 1) { "file$it" to 0L })
    val error = assertFailsWith<IOException> { forEachEntry(archive, SMALL) { _, _ -> } }
    assertContains(error.message.orEmpty(), "more than ${SMALL.entries} entries")
  }

  @Test
  fun anEntryOfTextIsSmall() {
    val archive = archive("arguments" to MAXIMUM_TEXT_ENTRY_BYTES + 1)
    assertFailsWith<IOException> { forEachEntry(archive) { _, contents -> entryText(contents) } }
    var text = ""
    forEachEntry(archive("arguments" to 3)) { _, contents -> text = entryText(contents) }
    assertEquals("\u0000\u0000\u0000", text)
  }

  @Test
  fun aResultOfTheCacheHoldsALimitedNumberOfFiles() {
    val archive = archive(*Array(SMALL.entries + 1) { "file$it" to 0L })
    val target = Files.createDirectories(root.resolve("unpacked"))
    val error = assertFailsWith<IOException> { unpackResult(archive, target, SMALL) }
    assertContains(error.message.orEmpty(), "more than ${SMALL.entries} files")
  }

  private companion object {
    /** Limits small enough for a test to reach; the defaults are 1 GiB and 65,536 files. */
    val SMALL = ArchiveLimits(bytes = 4L shl 20, entries = 16)
  }

  /** An archive of files of zeros, each of the given size. */
  private fun archive(vararg files: Pair<String, Long>): Path {
    val archive = Files.createTempFile(root, "archive-", ".zip")
    val zeros = ByteArray(1 shl 20)
    ZipOutputStream(Files.newOutputStream(archive)).use { zip ->
      zip.setLevel(Deflater.BEST_SPEED)
      for ((name, size) in files) {
        zip.putNextEntry(ZipEntry(name))
        var left = size
        while (left > 0) {
          val count = minOf(left, zeros.size.toLong()).toInt()
          zip.write(zeros, 0, count)
          left -= count
        }
        zip.closeEntry()
      }
    }
    return archive
  }
}
