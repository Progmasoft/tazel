/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Host
import com.progmasoft.tazel.dsl.RemoteExecutionSettings
import io.ktor.client.plugins.timeout
import io.ktor.client.request.header
import io.ktor.client.request.preparePost
import io.ktor.client.request.setBody
import io.ktor.client.statement.bodyAsChannel
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.time.Duration
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

internal const val COMPILER_HEADER = "X-Tala-Compiler"
internal const val HOST_HEADER = "X-Tazel-Host"
internal const val ARGUMENTS_ENTRY = "arguments"
internal const val EXIT_CODE_ENTRY = "exit-code"
internal const val LOG_ENTRY = "log"
internal const val SOURCES_PREFIX = "sources/"
internal const val OUTPUTS_PREFIX = "outputs/"

/** The answer to a compilation: what the compiler said, and an archive of what it wrote. */
class RemoteCompilation(
  val exitCode: Int,
  val log: String,
  private val archive: Path,
) : AutoCloseable {
  /** Writes what the compiler wrote into [root], where a local compiler would have left it. */
  fun unpack(root: Path) {
    forEachEntry(archive) { name, zip ->
      if (name.startsWith(OUTPUTS_PREFIX)) {
        val file = entryPath(root, name.removePrefix(OUTPUTS_PREFIX))
        file.parent?.let(Files::createDirectories)
        Files.copy(zip, file, REPLACE_EXISTING)
      }
    }
  }

  override fun close() = Files.deleteIfExists(archive).let {}
}

/** Compiles on another machine. Every failure is an [IOException]. */
interface RemoteCompiler {
  /**
   * Compiles with [arguments] the files [inputs] of [root] name. The answer is null when that
   * machine cannot do it the way this one would: another compiler, or another kind of host.
   */
  fun compile(
    root: Path,
    arguments: List<String>,
    inputs: List<String>,
    compilerVersion: String,
    host: Host,
  ): RemoteCompilation?
}

/** The name a host goes by in a request; a program only suits its own kind. */
internal fun hostName(host: Host) = "${host.os} ${host.architecture}"

/** The file [name] of an archive inside [root]; one that would lead out of it is refused. */
internal fun entryPath(
  root: Path,
  name: String,
): Path {
  val base = root.toAbsolutePath().normalize()
  val file = base.resolve(name).normalize()
  if (!file.startsWith(base) || file == base) throw IOException("the archive names '$name'")
  return file
}

/**
 * Calls [entry] with the name and the contents of each file of [archive], which came from another
 * machine. The contents of all files together share one limit of [limits], and the archive holds at
 * most as many files as it allows: a small archive of well compressed files would otherwise unpack
 * to far more than it is. What [entry] leaves unread of a file is read through the same limit, so a
 * file it ignores counts as well.
 */
internal inline fun forEachEntry(
  archive: Path,
  limits: ArchiveLimits = ArchiveLimits(),
  entry: (String, InputStream) -> Unit,
) {
  ZipInputStream(Files.newInputStream(archive)).use { zip ->
    val contents = limited(zip, limits.bytes)
    generateSequence { zip.nextEntry }
      .forEachIndexed { index, it ->
        if (index >= limits.entries) {
          throw IOException(
            "an archive from another machine holds more than ${limits.entries} entries"
          )
        }
        if (!it.isDirectory) {
          entry(it.name, contents)
          contents.transferTo(OutputStream.nullOutputStream())
        }
      }
  }
}

/** The text of an entry of an archive, which may hold at most [MAXIMUM_TEXT_ENTRY_BYTES]. */
internal fun entryText(contents: InputStream) =
  limited(contents, MAXIMUM_TEXT_ENTRY_BYTES).readAllBytes().decodeToString()

/** Sends the sources to `<url>/compile`, which answers with an archive; redirects are refused. */
class HttpRemoteCompiler(private val settings: RemoteExecutionSettings) : RemoteCompiler {
  private fun pack(
    root: Path,
    arguments: List<String>,
    inputs: List<String>,
    archive: Path,
  ) {
    ZipOutputStream(Files.newOutputStream(archive)).use { zip ->
      zip.putNextEntry(ZipEntry(ARGUMENTS_ENTRY))
      zip.write(arguments.joinToString("\n").toByteArray())
      zip.closeEntry()
      inputs.forEach { input ->
        zip.putNextEntry(ZipEntry(SOURCES_PREFIX + input))
        Files.copy(root.resolve(input), zip)
        zip.closeEntry()
      }
    }
  }

  override fun compile(
    root: Path,
    arguments: List<String>,
    inputs: List<String>,
    compilerVersion: String,
    host: Host,
  ): RemoteCompilation? {
    val request = Files.createTempFile("tazel-compile-", ".zip")
    val answer = Files.createTempFile("tazel-compiled-", ".zip")
    var kept = false
    try {
      pack(root, arguments, inputs, request)
      val status = blocking {
        tazelClient
          .preparePost("${settings.url}/compile") {
            timeout {
              connectTimeoutMillis = CONNECT_TIMEOUT.toMillis()
              socketTimeoutMillis = COMPILE_TIMEOUT.toMillis()
            }
            header(COMPILER_HEADER, compilerVersion)
            header(HOST_HEADER, hostName(host))
            settings.token?.let { header(HttpHeaders.Authorization, "Bearer $it") }
            setBody(fileContent(request))
          }
          .execute { response ->
            if (response.status == HttpStatusCode.OK) {
              val body = response.bodyAsChannel()
              withContext(Dispatchers.IO) {
                Files.newOutputStream(answer).use { body.copyLimited(it) }
              }
            }
            response.status
          }
      }
      when (status) {
        HttpStatusCode.OK -> Unit
        HttpStatusCode.Conflict -> return null
        else -> throw IOException("${settings.url} answered ${status.value}")
      }
      var exitCode: Int? = null
      var log = ""
      forEachEntry(answer) { name, zip ->
        when (name) {
          EXIT_CODE_ENTRY -> exitCode = entryText(zip).toIntOrNull()
          LOG_ENTRY -> log = entryText(zip)
        }
      }
      val code = exitCode ?: throw IOException("the answer of ${settings.url} has no exit code")
      kept = true
      return RemoteCompilation(code, log, answer)
    } finally {
      Files.deleteIfExists(request)
      if (!kept) Files.deleteIfExists(answer)
    }
  }

  private companion object {
    val CONNECT_TIMEOUT: Duration = Duration.ofSeconds(5)
    /** How long the server may send nothing, which it does while it compiles. */
    val COMPILE_TIMEOUT: Duration = Duration.ofMinutes(30)
  }
}
