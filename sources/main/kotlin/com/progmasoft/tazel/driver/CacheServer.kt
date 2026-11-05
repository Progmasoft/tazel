/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import io.ktor.http.HttpHeaders
import io.ktor.http.HttpMethod
import io.ktor.http.HttpStatusCode
import io.ktor.server.application.ApplicationCall
import io.ktor.server.request.httpMethod
import io.ktor.server.request.path
import io.ktor.server.request.receiveChannel
import io.ktor.server.response.respond
import io.ktor.server.routing.route
import io.ktor.server.routing.routing
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.NoSuchFileException
import java.nio.file.Path
import java.nio.file.StandardCopyOption.ATOMIC_MOVE
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.nio.file.attribute.FileTime
import java.security.MessageDigest
import java.time.Instant
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** What `tazel cache serve` is started with. The tokens come from the environment. */
data class CacheServerSettings(
  val directory: Path,
  val host: String,
  val port: Int,
  val maxBytes: Long,
  /** Needed for `GET` and `HEAD`: nothing is handed out to whoever asks. */
  val readToken: String,
  /** Needed for `PUT`; without one the server takes no results at all. */
  val writeToken: String? = null,
  /** TLS for other machines; without it the server listens only on this one. */
  val tls: TlsSettings? = null,
)

/**
 * The server of the remote build cache: `GET` and `PUT` of `/<slug>/<key>`, a file each. It speaks
 * TLS when it has a key store; without one it listens only on this machine, behind a proxy that
 * speaks TLS for the others. Reading needs the read token or the write token, and writing the write
 * token, so a client that reads and writes needs only one.
 *
 * Results are opaque to it. When the disk holds more than [CacheServerSettings.maxBytes], the
 * results used longest ago go first. Whether a result is genuine is decided by the signature the
 * reader checks, so that a server that was broken into cannot plant programs.
 */
class CacheServer(private val settings: CacheServerSettings) : AutoCloseable {
  private val server =
    tazelServer(settings.host, settings.port, settings.tls) {
      routing { route("{...}") { handle { respond(call) } } }
    }
  private val evicting = Any()

  /** The port it listens on, which the system chooses for port 0. */
  val port: Int
    get() = boundPort(server)

  /** Creates the directory, starts listening and returns; requests are served until [close]. */
  fun start() {
    Files.createDirectories(settings.directory)
    server.start(wait = false)
  }

  override fun close() = server.stop(0, 0)

  private suspend fun respond(call: ApplicationCall) {
    val method = call.request.httpMethod
    val file = fileOf(call.request.path())
    val allowed =
      when (method) {
        HttpMethod.Get,
        HttpMethod.Head ->
          presents(call, settings.readToken) ||
            settings.writeToken != null && presents(call, settings.writeToken)
        HttpMethod.Put -> settings.writeToken != null && presents(call, settings.writeToken)
        else -> return call.respond(HttpStatusCode.MethodNotAllowed)
      }
    when {
      !allowed ->
        call.respond(
          if (call.request.headers[HttpHeaders.Authorization] != null) HttpStatusCode.Forbidden
          else HttpStatusCode.Unauthorized
        )
      file == null -> call.respond(HttpStatusCode.NotFound)
      method == HttpMethod.Head ->
        call.respond(
          if (withContext(Dispatchers.IO) { Files.isRegularFile(file) }) HttpStatusCode.OK
          else HttpStatusCode.NotFound
        )
      method == HttpMethod.Get -> send(call, file)
      else -> receive(call, file)
    }
  }

  /** The file of [path], or null for anything that is not a slug and a key. */
  private fun fileOf(path: String): Path? {
    val (slug, key) = RESULT.matchEntire(path)?.destructured ?: return null
    return settings.directory.resolve(slug).resolve(key)
  }

  private fun presents(
    call: ApplicationCall,
    token: String,
  ): Boolean {
    val presented = call.request.headers[HttpHeaders.Authorization].orEmpty()
    return MessageDigest.isEqual(presented.toByteArray(), "Bearer $token".toByteArray())
  }

  private suspend fun send(
    call: ApplicationCall,
    file: Path,
  ) {
    val found =
      withContext(Dispatchers.IO) {
        try {
          // The results used longest ago go first, and being read is being used.
          Files.setLastModifiedTime(file, FileTime.from(Instant.now()))
          true
        } catch (_: NoSuchFileException) {
          false
        }
      }
    if (!found) return call.respond(HttpStatusCode.NotFound)
    call.respond(fileContent(file))
  }

  private suspend fun receive(
    call: ApplicationCall,
    file: Path,
  ) {
    val declared = call.request.headers[HttpHeaders.ContentLength]?.toLongOrNull()
    if (declared != null && declared > MAXIMUM_ARCHIVE_BYTES) {
      return call.respond(HttpStatusCode.PayloadTooLarge)
    }
    val body = call.receiveChannel()
    withContext(Dispatchers.IO) {
      Files.createDirectories(file.parent)
      val incoming = Files.createTempFile(file.parent, ".incoming-", "")
      try {
        Files.newOutputStream(incoming).use { body.copyLimited(it) }
        try {
          Files.move(incoming, file, ATOMIC_MOVE, REPLACE_EXISTING)
        } catch (_: AtomicMoveNotSupportedException) {
          Files.move(incoming, file, REPLACE_EXISTING)
        }
      } finally {
        Files.deleteIfExists(incoming)
      }
    }
    call.respond(HttpStatusCode.Created)
    withContext(Dispatchers.IO) { evict() }
  }

  private fun evict() {
    synchronized(evicting) {
      val files =
        Files.walk(settings.directory).use { paths ->
          paths
            .filter { Files.isRegularFile(it) && !it.fileName.toString().startsWith(".") }
            .toList()
        }
      var size = files.sumOf(Files::size)
      for (file in files.sortedBy(Files::getLastModifiedTime)) {
        if (size <= settings.maxBytes) break
        size -= Files.size(file)
        Files.deleteIfExists(file)
      }
    }
  }

  private companion object {
    /** One path segment each, so that nothing can lead out of the directory. */
    val RESULT = Regex("""/([A-Za-z0-9_-][A-Za-z0-9_.-]*)/([0-9A-Za-z_-]+(?:\.sig)?)""")
  }
}
