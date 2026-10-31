/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Host
import com.progmasoft.tazel.dsl.RemoteExecutionSettings
import java.io.IOException
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.time.Duration
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

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
        Files.copy(limited(zip), file, REPLACE_EXISTING)
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

internal inline fun forEachEntry(
  archive: Path,
  entry: (String, ZipInputStream) -> Unit,
) {
  ZipInputStream(Files.newInputStream(archive)).use { zip ->
    generateSequence { zip.nextEntry }
      .filterNot(ZipEntry::isDirectory)
      .forEach { entry(it.name, zip) }
  }
}

/** Sends the sources to `<url>/compile`, which answers with an archive; redirects are refused. */
class HttpRemoteCompiler(private val settings: RemoteExecutionSettings) : RemoteCompiler {
  private val client =
    HttpClient.newBuilder()
      .connectTimeout(CONNECT_TIMEOUT)
      .followRedirects(HttpClient.Redirect.NEVER)
      .build()

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
      val builder =
        HttpRequest.newBuilder(URI.create("${settings.url}/compile"))
          .timeout(COMPILE_TIMEOUT)
          .header(COMPILER_HEADER, compilerVersion)
          .header(HOST_HEADER, hostName(host))
          .POST(HttpRequest.BodyPublishers.ofFile(request))
      settings.token?.let { builder.header("Authorization", "Bearer $it") }
      val response =
        try {
          client.send(builder.build(), HttpResponse.BodyHandlers.ofFile(answer))
        } catch (_: InterruptedException) {
          Thread.currentThread().interrupt()
          throw IOException("the request was interrupted")
        }
      when (response.statusCode()) {
        200 -> Unit
        409 -> return null
        else -> throw IOException("${settings.url} answered ${response.statusCode()}")
      }
      if (Files.size(answer) > MAXIMUM_ARCHIVE_BYTES) throw IOException("the answer is too large")
      var exitCode: Int? = null
      var log = ""
      forEachEntry(answer) { name, zip ->
        when (name) {
          EXIT_CODE_ENTRY -> exitCode = limited(zip).readAllBytes().decodeToString().toIntOrNull()
          LOG_ENTRY -> log = limited(zip).readAllBytes().decodeToString()
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
    val COMPILE_TIMEOUT: Duration = Duration.ofMinutes(30)
  }
}
