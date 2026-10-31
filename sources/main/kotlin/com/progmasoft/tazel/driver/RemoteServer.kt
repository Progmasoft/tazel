/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Host
import com.sun.net.httpserver.HttpExchange
import com.sun.net.httpserver.HttpServer
import java.io.IOException
import java.net.InetSocketAddress
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.util.concurrent.Executors
import java.util.concurrent.Semaphore
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.io.path.invariantSeparatorsPathString

/** What `tazel remote serve` is started with. The token comes from the environment. */
data class RemoteServerSettings(
  val host: String,
  val port: Int,
  val jobs: Int,
  val token: String,
  val compiler: Path,
  val compilerVersion: String,
  val machine: Host,
)

/**
 * Compiles for other machines: `POST /compile` with the sources and the arguments in an archive,
 * answered with what the compiler said and wrote. It is plain HTTP, by default of this machine; for
 * other machines it stands behind a proxy that speaks TLS, as Tazel asks of such an address.
 *
 * It only ever starts the compiler, on files it was sent, in a directory of their own. A request
 * has no way to name a file outside it, and a client without the token gets nothing. It answers
 * `409` when it would not build what the client would: another compiler or another kind of host.
 * The client then compiles for itself.
 */
class RemoteServer(
  private val settings: RemoteServerSettings,
  private val processes: Processes,
) : AutoCloseable {
  private val slots = Semaphore(settings.jobs, true)
  private val workers = Executors.newFixedThreadPool(settings.jobs + WAITING)
  private val server =
    HttpServer.create(InetSocketAddress(settings.host, settings.port), 0).also {
      it.executor = workers
      it.createContext("/compile", ::handle)
    }

  /** The port it listens on, which the system chooses for port 0. */
  val port: Int
    get() = server.address.port

  fun start() = server.start()

  override fun close() {
    server.stop(0)
    workers.shutdownNow()
  }

  private fun handle(exchange: HttpExchange) {
    exchange.use {
      try {
        respond(exchange)
      } catch (_: IOException) {
        // The client went away or sent no archive; either way it compiles for itself.
        runCatching { exchange.sendResponseHeaders(400, -1) }
      }
    }
  }

  private fun presents(exchange: HttpExchange): Boolean {
    val presented = exchange.requestHeaders.getFirst("Authorization").orEmpty()
    return MessageDigest.isEqual(presented.toByteArray(), "Bearer ${settings.token}".toByteArray())
  }

  private fun respond(exchange: HttpExchange) {
    val headers = exchange.requestHeaders
    val declared = headers.getFirst("Content-Length")?.toLongOrNull()
    when {
      exchange.requestMethod != "POST" -> exchange.sendResponseHeaders(405, -1)
      !presents(exchange) -> exchange.sendResponseHeaders(401, -1)
      declared != null && declared > MAXIMUM_ARCHIVE_BYTES -> exchange.sendResponseHeaders(413, -1)
      headers.getFirst(COMPILER_HEADER) != settings.compilerVersion ||
        headers.getFirst(HOST_HEADER) != hostName(settings.machine) ->
        exchange.sendResponseHeaders(409, -1)
      else -> compile(exchange)
    }
  }

  private fun compile(exchange: HttpExchange) {
    val work = Files.createTempDirectory("tazel-remote-")
    val request = Files.createTempFile("tazel-request-", ".zip")
    val answer = Files.createTempFile("tazel-answer-", ".zip")
    try {
      Files.newOutputStream(request).use { limited(exchange.requestBody).copyTo(it) }
      var arguments: List<String>? = null
      val sources = mutableListOf<String>()
      forEachEntry(request) { name, zip ->
        when {
          name == ARGUMENTS_ENTRY ->
            arguments = limited(zip).readAllBytes().decodeToString().split("\n")
          name.startsWith(SOURCES_PREFIX) -> {
            val source = name.removePrefix(SOURCES_PREFIX)
            if (!isPlainPath(source)) throw IOException("the archive names '$source'")
            val file = entryPath(work, source)
            file.parent?.let(Files::createDirectories)
            Files.copy(limited(zip), file)
            sources += source
          }
        }
      }
      val command = arguments
      if (command == null || !isCompilerCommand(command, sources)) {
        return exchange.sendResponseHeaders(422, -1)
      }
      slots.acquire()
      val run =
        try {
          processes.runGathered(listOf(settings.compiler.toString()) + command, work)
        } finally {
          slots.release()
        }
      pack(run, work, sources.toSet(), answer)
      exchange.sendResponseHeaders(200, Files.size(answer))
      Files.copy(answer, exchange.responseBody)
    } catch (_: InterruptedException) {
      Thread.currentThread().interrupt()
    } finally {
      work.toFile().deleteRecursively()
      Files.deleteIfExists(request)
      Files.deleteIfExists(answer)
    }
  }

  private fun pack(
    run: GatheredRun,
    work: Path,
    sources: Set<String>,
    archive: Path,
  ) {
    ZipOutputStream(Files.newOutputStream(archive)).use { zip ->
      fun text(
        name: String,
        value: String,
      ) {
        zip.putNextEntry(ZipEntry(name))
        zip.write(value.toByteArray())
        zip.closeEntry()
      }
      text(EXIT_CODE_ENTRY, run.exitCode.toString())
      text(LOG_ENTRY, run.output)
      Files.walk(work).use { paths ->
        paths
          .filter(Files::isRegularFile)
          .filter { work.relativize(it).invariantSeparatorsPathString !in sources }
          .forEach { file ->
            zip.putNextEntry(
              ZipEntry(OUTPUTS_PREFIX + work.relativize(file).invariantSeparatorsPathString)
            )
            Files.copy(file, zip)
            zip.closeEntry()
          }
      }
    }
  }

  private companion object {
    /** Requests that may wait for a compiler, on top of those that have one. */
    const val WAITING = 16
  }
}

/** A path with `/` between plain names, which cannot lead anywhere but below its directory. */
internal fun isPlainPath(path: String): Boolean =
  path.isNotEmpty() &&
    '\\' !in path &&
    ':' !in path &&
    path.split('/').none { it.isEmpty() || it == "." || it == ".." }

/**
 * Whether [arguments] are what Tazel asks of a compiler for [sources]: options, `--`, then exactly
 * those files. An option that names a place on the machine is refused, so that a request can make
 * the compiler read or write only what it sent.
 */
internal fun isCompilerCommand(
  arguments: List<String>,
  sources: List<String>,
): Boolean {
  val separator = arguments.indexOf("--")
  if (separator < 0) return false
  val options = arguments.subList(0, separator)
  val named = arguments.subList(separator + 1, arguments.size)
  return named == sources &&
    options.all { option ->
      option.startsWith("-") &&
        ".." !in option &&
        '/' !in option &&
        '\\' !in option &&
        REFUSED_OPTIONS.none(option::startsWith)
    }
}

private val REFUSED_OPTIONS = listOf("--sysroot", "--incremental")
