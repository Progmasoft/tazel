/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.driver

import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.NoSuchFileException
import java.nio.file.Path
import java.nio.file.StandardCopyOption.ATOMIC_MOVE
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.nio.file.attribute.FileTime
import java.security.MessageDigest
import java.time.Instant
import java.util.concurrent.Executors

/** What `tazel cache serve` is started with. The tokens come from the environment. */
data class CacheServerSettings(
  val directory: Path,
  val host: String,
  val port: Int,
  val maxBytes: Long,
  /** Needed for `PUT`; without one the server takes no results at all. */
  val writeToken: String? = null,
  /** Needed for `GET` when there is one. */
  val readToken: String? = null,
)

/**
 * The server of the remote build cache: `GET` and `PUT` of `/<slug>/<key>`, a file each. It is
 * deliberately plain HTTP on one address, by default of this machine; a cache for other machines
 * stands behind a proxy that speaks TLS, which is also what Tazel asks of such an address.
 *
 * Results are opaque to it. When the disk holds more than [CacheServerSettings.maxBytes], the
 * results used longest ago go first. Whether a result is genuine is decided by the signature the
 * reader checks, so that a server that was broken into cannot plant programs.
 */
class CacheServer(private val settings: CacheServerSettings) : AutoCloseable {
  private val workers = Executors.newFixedThreadPool(WORKERS)
  private val server =
    HttpServer.create(InetSocketAddress(settings.host, settings.port), 0).also {
      it.executor = workers
      it.createContext("/", ::handle)
    }
  private val evicting = Any()

  /** The port it listens on, which the system chooses for port 0. */
  val port: Int
    get() = server.address.port

  fun start() {
    Files.createDirectories(settings.directory)
    server.start()
  }

  override fun close() {
    server.stop(0)
    workers.shutdownNow()
  }

  private fun handle(exchange: HttpExchange) {
    exchange.use {
      try {
        respond(exchange)
      } catch (_: IOException) {
        // The client went away, or the disk is full; the client treats either as no result.
        runCatching { exchange.sendResponseHeaders(500, -1) }
      }
    }
  }

  private fun respond(exchange: HttpExchange) {
    val file = fileOf(exchange.requestURI.path)
    val allowed =
      when (exchange.requestMethod) {
        "GET",
        "HEAD" -> settings.readToken == null || presents(exchange, settings.readToken)
        "PUT" -> settings.writeToken != null && presents(exchange, settings.writeToken)
        else -> return exchange.sendResponseHeaders(405, -1)
      }
    when {
      !allowed -> exchange.sendResponseHeaders(if (hasToken(exchange)) 403 else 401, -1)
      file == null -> exchange.sendResponseHeaders(404, -1)
      exchange.requestMethod == "HEAD" ->
        exchange.sendResponseHeaders(if (Files.isRegularFile(file)) 200 else 404, -1)
      exchange.requestMethod == "GET" -> send(exchange, file)
      else -> receive(exchange, file)
    }
  }

  /** The file of [path], or null for anything that is not a slug and a key. */
  private fun fileOf(path: String): Path? {
    val (slug, key) = RESULT.matchEntire(path)?.destructured ?: return null
    return settings.directory.resolve(slug).resolve(key)
  }

  private fun hasToken(exchange: HttpExchange) =
    exchange.requestHeaders.getFirst("Authorization") != null

  private fun presents(
    exchange: HttpExchange,
    token: String,
  ): Boolean {
    val presented = exchange.requestHeaders.getFirst("Authorization").orEmpty()
    return MessageDigest.isEqual(presented.toByteArray(), "Bearer $token".toByteArray())
  }

  private fun send(
    exchange: HttpExchange,
    file: Path,
  ) {
    val size =
      try {
        Files.size(file)
      } catch (_: NoSuchFileException) {
        return exchange.sendResponseHeaders(404, -1)
      }
    // The results used longest ago go first, and being read is being used.
    Files.setLastModifiedTime(file, FileTime.from(Instant.now()))
    exchange.sendResponseHeaders(200, size)
    Files.copy(file, exchange.responseBody)
  }

  private fun receive(
    exchange: HttpExchange,
    file: Path,
  ) {
    val declared = exchange.requestHeaders.getFirst("Content-Length")?.toLongOrNull()
    if (declared != null && declared > MAXIMUM_ARCHIVE_BYTES) {
      return exchange.sendResponseHeaders(413, -1)
    }
    Files.createDirectories(file.parent)
    val incoming = Files.createTempFile(file.parent, ".incoming-", "")
    try {
      Files.newOutputStream(incoming).use { limited(exchange.requestBody).copyTo(it) }
      try {
        Files.move(incoming, file, ATOMIC_MOVE, REPLACE_EXISTING)
      } catch (_: AtomicMoveNotSupportedException) {
        Files.move(incoming, file, REPLACE_EXISTING)
      }
    } finally {
      Files.deleteIfExists(incoming)
    }
    exchange.sendResponseHeaders(201, -1)
    evict()
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
    const val WORKERS = 16

    /** One path segment each, so that nothing can lead out of the directory. */
    val RESULT = Regex("""/([A-Za-z0-9_-][A-Za-z0-9_.-]*)/([0-9A-Za-z_-]+(?:\.sig)?)""")
  }
}
