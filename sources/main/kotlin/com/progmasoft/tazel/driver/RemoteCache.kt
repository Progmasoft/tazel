/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.RemoteCacheSettings
import com.progmasoft.tazel.dsl.ResultKeys
import io.ktor.client.plugins.timeout
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.head
import io.ktor.client.request.header
import io.ktor.client.request.prepareGet
import io.ktor.client.request.put
import io.ktor.client.request.setBody
import io.ktor.client.request.url
import io.ktor.client.statement.bodyAsChannel
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import java.io.IOException
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.security.Signature
import java.security.SignatureException
import java.time.Duration
import java.util.zip.ZipEntry
import java.util.zip.ZipInputStream
import java.util.zip.ZipOutputStream
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** The most a result of a remote cache may hold, packed or unpacked. */
internal const val MAXIMUM_ARCHIVE_BYTES = 1L shl 30

/** The most files an archive from another machine may hold. */
internal const val MAXIMUM_ARCHIVE_ENTRIES = 65_536

/** The most an entry of text in an archive, its arguments, exit code or log, may hold. */
internal const val MAXIMUM_TEXT_ENTRY_BYTES = 16L shl 20

/** How much an archive from another machine may unpack to: [bytes] in all, in [entries] files. */
internal data class ArchiveLimits(
  val bytes: Long = MAXIMUM_ARCHIVE_BYTES,
  val entries: Int = MAXIMUM_ARCHIVE_ENTRIES,
)

/** Where results are shared between machines. Every failure is an [IOException]. */
interface RemoteStore {
  /** Whether the store has the result, without fetching it. */
  fun contains(
    slug: String,
    key: String,
  ): Boolean

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
 * for a `PUT`, for success and `404` for a result that is not there. A signature is a result of its
 * own, `<key>.sig`. Redirects are not followed, so the token goes only to the address the project
 * names.
 */
class HttpRemoteStore(private val settings: RemoteCacheSettings) : RemoteStore {
  private fun HttpRequestBuilder.result(
    slug: String,
    key: String,
  ) {
    url("${settings.url}/$slug/$key")
    timeout {
      connectTimeoutMillis = CONNECT_TIMEOUT.toMillis()
      requestTimeoutMillis = REQUEST_TIMEOUT.toMillis()
    }
    settings.token?.let { header(HttpHeaders.Authorization, "Bearer $it") }
  }

  override fun contains(
    slug: String,
    key: String,
  ): Boolean = blocking {
    val status = tazelClient.head { result(slug, key) }.status
    when (status) {
      HttpStatusCode.OK -> true
      HttpStatusCode.NotFound -> false
      else -> throw IOException("${settings.url} answered ${status.value}")
    }
  }

  override fun fetch(
    slug: String,
    key: String,
    archive: Path,
  ): Boolean = blocking {
    tazelClient
      .prepareGet { result(slug, key) }
      .execute { response ->
        when (response.status) {
          HttpStatusCode.OK -> Unit
          HttpStatusCode.NotFound -> return@execute false
          else -> throw IOException("${settings.url} answered ${response.status.value}")
        }
        val body = response.bodyAsChannel()
        withContext(Dispatchers.IO) { Files.newOutputStream(archive).use { body.copyLimited(it) } }
        true
      }
  }

  override fun publish(
    slug: String,
    key: String,
    archive: Path,
  ) = blocking {
    val status =
      tazelClient
        .put {
          result(slug, key)
          setBody(fileContent(archive))
        }
        .status
    if (status.value !in setOf(200, 201, 204)) {
      throw IOException("${settings.url} answered ${status.value}")
    }
  }

  private companion object {
    val CONNECT_TIMEOUT: Duration = Duration.ofSeconds(5)
    val REQUEST_TIMEOUT: Duration = Duration.ofMinutes(5)
  }
}

/** [stream], failing once it has given more than [maximum] bytes. */
internal fun limited(
  stream: InputStream,
  maximum: Long = MAXIMUM_ARCHIVE_BYTES,
) =
  object : InputStream() {
    private var left = maximum

    private fun counted(count: Int): Int {
      if (count > 0) left -= count
      if (left < 0)
        throw IOException("an archive from another machine holds more than $maximum bytes")
      return count
    }

    override fun read(): Int = stream.read().also { if (it >= 0) counted(1) }

    override fun read(
      buffer: ByteArray,
      offset: Int,
      length: Int,
    ): Int = counted(stream.read(buffer, offset, length))
  }

/**
 * What a signature of a result covers: the archive and the place it belongs, so that a signed
 * result cannot be served under the key of another one.
 */
private fun signedBytes(
  slug: String,
  key: String,
  archive: Path,
): ByteArray {
  val digest = MessageDigest.getInstance("SHA-256")
  Files.newInputStream(archive).use { stream ->
    val buffer = ByteArray(64 * 1024)
    generateSequence { stream.read(buffer) }
      .takeWhile { it >= 0 }
      .forEach { digest.update(buffer, 0, it) }
  }
  val contents = digest.digest().joinToString("") { "%02x".format(it) }
  return "tazel result 1\n$slug\n$key\n$contents".toByteArray()
}

/** The signature of a result as the text a store keeps beside it. */
internal fun signResult(
  signingKey: String,
  slug: String,
  key: String,
  archive: Path,
): ByteArray {
  val signature = Signature.getInstance("Ed25519")
  signature.initSign(ResultKeys.signingOf(signingKey))
  signature.update(signedBytes(slug, key, archive))
  return signature.sign()
}

/** Whether [signature] was made by the key behind [publicKey] for this very result. */
internal fun isSignedResult(
  publicKey: String,
  slug: String,
  key: String,
  archive: Path,
  signature: ByteArray,
): Boolean {
  val verifier = Signature.getInstance("Ed25519")
  verifier.initVerify(ResultKeys.publicOf(publicKey))
  verifier.update(signedBytes(slug, key, archive))
  return try {
    verifier.verify(signature)
  } catch (_: SignatureException) {
    false
  }
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
  limits: ArchiveLimits = ArchiveLimits(),
) {
  val base = directory.toAbsolutePath().normalize()
  ZipInputStream(Files.newInputStream(archive)).use { zip ->
    // One limit for the whole archive: many small files are as large as one big one.
    val contents = limited(zip, limits.bytes)
    generateSequence { zip.nextEntry }
      .forEachIndexed { index, entry ->
        if (index >= limits.entries) {
          throw IOException("a result of the remote cache holds more than ${limits.entries} files")
        }
        val name = entry.name
        val file = base.resolve(name).normalize()
        if (name.any { it in "/\\:" } || file.parent != base || !file.startsWith(base)) {
          throw IOException("a result of the remote cache names the file '$name'")
        }
        Files.newOutputStream(file).use { contents.copyTo(it) }
        file.toFile().setExecutable(true)
      }
  }
}
