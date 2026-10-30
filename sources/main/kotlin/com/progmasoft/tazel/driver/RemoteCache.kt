/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.RemoteCacheSettings
import java.io.IOException
import java.io.InputStream
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import java.time.Duration
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream

/** The most a result of a remote cache may hold, packed or unpacked. */
private const val MAXIMUM_ARCHIVE_BYTES = 1L shl 30

/** Where results are shared between machines. Every failure is an [IOException]. */
interface RemoteStore {
  /** Writes the archive of a result to [archive]; false when the store does not have it. */
  fun fetch(
    slug: String,
    key: String,
    archive: Path,
  ): Boolean

  fun publish(
    slug: String,
    key: String,
    archive: Path,
  )
}

/**
 * A remote cache over HTTP: `GET` and `PUT` of `<url>/<slug>/<key>`, with `200`, or `201` and `204`
 * for a `PUT`, for success and `404` for a result that is not there. Redirects are not followed, so
 * the token goes only to the address the project names.
 */
class HttpRemoteStore(private val settings: RemoteCacheSettings) : RemoteStore {
  private val client =
    HttpClient.newBuilder()
      .connectTimeout(CONNECT_TIMEOUT)
      .followRedirects(HttpClient.Redirect.NEVER)
      .build()

  private fun request(
    slug: String,
    key: String,
  ): HttpRequest.Builder {
    val builder =
      HttpRequest.newBuilder(URI.create("${settings.url}/$slug/$key")).timeout(REQUEST_TIMEOUT)
    settings.token?.let { builder.header("Authorization", "Bearer $it") }
    return builder
  }

  private fun <T> send(
    request: HttpRequest,
    body: HttpResponse.BodyHandler<T>,
  ): HttpResponse<T> =
    try {
      client.send(request, body)
    } catch (_: InterruptedException) {
      Thread.currentThread().interrupt()
      throw IOException("the request was interrupted")
    }

  override fun fetch(
    slug: String,
    key: String,
    archive: Path,
  ): Boolean {
    val response = send(request(slug, key).GET().build(), HttpResponse.BodyHandlers.ofInputStream())
    response.body().use { body ->
      when (response.statusCode()) {
        200 -> Unit
        404 -> return false
        else -> throw IOException("${settings.url} answered ${response.statusCode()}")
      }
      Files.newOutputStream(archive).use { limited(body).copyTo(it) }
    }
    return true
  }

  override fun publish(
    slug: String,
    key: String,
    archive: Path,
  ) {
    val body = HttpRequest.BodyPublishers.ofFile(archive)
    val response =
      send(request(slug, key).PUT(body).build(), HttpResponse.BodyHandlers.discarding())
    if (response.statusCode() !in setOf(200, 201, 204)) {
      throw IOException("${settings.url} answered ${response.statusCode()}")
    }
  }

  private companion object {
    val CONNECT_TIMEOUT: Duration = Duration.ofSeconds(5)
    val REQUEST_TIMEOUT: Duration = Duration.ofMinutes(5)
  }
}

/** [stream], failing once it has given more than a result may hold. */
private fun limited(stream: InputStream) =
  object : InputStream() {
    private var left = MAXIMUM_ARCHIVE_BYTES

    private fun counted(count: Int): Int {
      if (count > 0) left -= count
      if (left < 0) throw IOException("a result of the remote cache is too large")
      return count
    }

    override fun read(): Int = stream.read().also { if (it >= 0) counted(1) }

    override fun read(
      buffer: ByteArray,
      offset: Int,
      length: Int,
    ): Int = counted(stream.read(buffer, offset, length))
  }

/** Packs the files directly in [directory] into [archive]. */
internal fun packResult(
  directory: Path,
  archive: Path,
) {
  ZipOutputStream(Files.newOutputStream(archive)).use { zip ->
    Files.newDirectoryStream(directory).use { files ->
      for (file in files) {
        zip.putNextEntry(ZipEntry(file.fileName.toString()))
        Files.copy(file, zip)
        zip.closeEntry()
      }
    }
  }
}

/**
 * Unpacks [archive] into [directory]. An archive comes from another machine, so a name has to be a
 * plain file name: nothing in it may lead out of [directory]. What it holds becomes executable,
 * because that is what a compiler run leaves and an archive does not record it.
 */
internal fun unpackResult(
  archive: Path,
  directory: Path,
) {
  ZipInputStream(Files.newInputStream(archive)).use { zip ->
    generateSequence { zip.nextEntry }
      .forEach { entry ->
        val name = entry.name
        if (name.isEmpty() || name == ".." || name.any { it in "/\\:" }) {
          throw IOException("a result of the remote cache names the file '$name'")
        }
        val file = directory.resolve(name)
        Files.newOutputStream(file).use { limited(zip).copyTo(it) }
        file.toFile().setExecutable(true)
      }
  }
}
