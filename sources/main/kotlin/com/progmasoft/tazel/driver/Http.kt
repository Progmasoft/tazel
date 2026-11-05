/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import io.ktor.client.HttpClient
import io.ktor.client.engine.cio.CIO
import io.ktor.client.plugins.HttpTimeout
import io.ktor.http.ContentType
import io.ktor.http.content.OutgoingContent
import io.ktor.server.application.Application
import io.ktor.server.engine.EmbeddedServer
import io.ktor.server.engine.connector
import io.ktor.server.engine.embeddedServer
import io.ktor.server.engine.sslConnector
import io.ktor.server.netty.Netty
import io.ktor.server.netty.NettyApplicationEngine
import io.ktor.util.cio.readChannel
import io.ktor.utils.io.ByteReadChannel
import io.ktor.utils.io.readAvailable
import java.io.IOException
import java.io.OutputStream
import java.net.InetAddress
import java.net.UnknownHostException
import java.nio.file.Files
import java.nio.file.Path
import java.security.GeneralSecurityException
import java.security.KeyStore
import kotlin.coroutines.EmptyCoroutineContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.runBlocking

/**
 * The certificate chain and private key a server of Tazel presents: a PKCS #12 key store and its
 * password, which comes from `TAZEL_TLS_PASSWORD` and is never written on a command line.
 */
class TlsSettings(
  val keyStore: Path,
  private val password: CharArray,
) {
  internal fun password(): CharArray = password.copyOf()

  /** The key store and the alias of its private key; one that cannot be read is refused. */
  internal fun load(): Pair<KeyStore, String> {
    val store = KeyStore.getInstance("PKCS12")
    try {
      Files.newInputStream(keyStore).use { store.load(it, password) }
    } catch (error: GeneralSecurityException) {
      throw IOException("$keyStore cannot be read with TAZEL_TLS_PASSWORD: ${error.message}")
    }
    val alias =
      store.aliases().toList().firstOrNull(store::isKeyEntry)
        ?: throw IOException("$keyStore holds no private key")
    return store to alias
  }

  override fun toString() = "TlsSettings(keyStore=$keyStore)"
}

/** Whether every address [host] names is of this machine. */
internal fun isLoopback(host: String): Boolean =
  try {
    InetAddress.getAllByName(host).all(InetAddress::isLoopbackAddress)
  } catch (_: UnknownHostException) {
    false
  }

/**
 * A server on [host] and [port] that speaks TLS with [tls]. Without TLS it may listen only on this
 * machine, behind a proxy that speaks TLS for the others, so that no token and no source crosses a
 * network in the clear.
 */
internal fun tazelServer(
  host: String,
  port: Int,
  tls: TlsSettings?,
  module: Application.() -> Unit,
): EmbeddedServer<NettyApplicationEngine, NettyApplicationEngine.Configuration> {
  if (tls == null && !isLoopback(host)) {
    throw IOException("$host is not of this machine; a server for other machines needs TLS")
  }
  val keys = tls?.load()
  return embeddedServer(
    Netty,
    configure = {
      if (tls == null || keys == null) {
        connector {
          this.host = host
          this.port = port
        }
      } else {
        sslConnector(keys.first, keys.second, tls::password, tls::password) {
          this.host = host
          this.port = port
        }
      }
    },
    module = module,
  )
}

/** The port [server] listens on, which the system chooses for port 0. */
internal fun boundPort(server: EmbeddedServer<*, *>): Int = runBlocking {
  server.engine.resolvedConnectors().first().port
}

/**
 * The client of every request Tazel sends. It follows no redirect, so a token goes only to the
 * address it was given; a request sets its own time limit.
 */
internal val tazelClient: HttpClient by lazy {
  HttpClient(CIO) {
    expectSuccess = false
    followRedirects = false
    install(HttpTimeout)
  }
}

/** Runs [block] on this thread; an interrupt of the thread ends it with an [IOException]. */
internal fun <T> blocking(block: suspend CoroutineScope.() -> T): T =
  try {
    runBlocking(EmptyCoroutineContext, block)
  } catch (_: InterruptedException) {
    Thread.currentThread().interrupt()
    throw IOException("the request was interrupted")
  }

/** The bytes of [file] as the body of a request or a response, with its length. */
internal fun fileContent(file: Path): OutgoingContent.ReadChannelContent {
  val length = Files.size(file)
  return object : OutgoingContent.ReadChannelContent() {
    override val contentLength = length
    override val contentType = ContentType.Application.OctetStream

    override fun readFrom(): ByteReadChannel = file.toFile().readChannel()
  }
}

/** Copies this channel to [target], failing once it has given more than [maximum] bytes. */
internal suspend fun ByteReadChannel.copyLimited(
  target: OutputStream,
  maximum: Long = MAXIMUM_ARCHIVE_BYTES,
) {
  val buffer = ByteArray(64 * 1024)
  var total = 0L
  while (true) {
    val count = readAvailable(buffer, 0, buffer.size)
    if (count < 0) break
    total += count
    if (total > maximum) throw IOException("a body holds more than $maximum bytes")
    target.write(buffer, 0, count)
  }
}
