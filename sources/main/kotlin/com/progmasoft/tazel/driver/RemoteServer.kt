/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Host
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
import java.io.IOException
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.util.concurrent.Semaphore
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.io.path.invariantSeparatorsPathString
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.withContext

/** What `tazel remote serve` is started with. The token comes from the environment. */
data class RemoteServerSettings(
  val host: String,
  val port: Int,
  val jobs: Int,
  val token: String,
  val compiler: Path,
  val compilerVersion: String,
  val machine: Host,
  /** TLS for other machines; without it the server listens only on this one. */
  val tls: TlsSettings? = null,
)

/**
 * Compiles for other machines: `POST /compile` with the sources and the arguments in an archive,
 * answered with what the compiler said and wrote. It speaks TLS when it has a key store; without
 * one it listens only on this machine, behind a proxy that speaks TLS for the others.
 *
 * It only ever starts the compiler, on files it was sent, in a directory of their own. A request
 * has no way to name a file outside it, and a client without the token gets nothing. It answers
 * `409` when it would not build what the client would: another compiler or another kind of host.
 * The client then compiles for itself. It takes as many requests as it has compilers and [WAITING]
 * more, and answers `503` to the rest.
 */
class RemoteServer(
  private val settings: RemoteServerSettings,
  private val processes: Processes,
) : AutoCloseable {
  private val slots = Semaphore(settings.jobs, true)
  private val admitted = Semaphore(settings.jobs + WAITING)

  /**
   * The threads of the admitted requests, which block on a compiler slot and while it runs; the
   * shared IO dispatcher has a fixed number of threads, which a large `--jobs` would take up.
   */
  @OptIn(ExperimentalCoroutinesApi::class)
  private val io = Dispatchers.IO.limitedParallelism(settings.jobs + WAITING)
  private val server =
    tazelServer(settings.host, settings.port, settings.tls) {
      routing { route("{...}") { handle { serve(call) } } }
    }

  /** The port it listens on, which the system chooses for port 0. */
  val port: Int
    get() = boundPort(server)

  /** Starts listening and returns; requests are served until [close]. */
  fun start() {
    server.start(wait = false)
  }

  override fun close() = server.stop(0, 0)

  private suspend fun serve(call: ApplicationCall) {
    if (!admitted.tryAcquire()) return call.respond(HttpStatusCode.ServiceUnavailable)
    try {
      respond(call)
    } catch (_: IOException) {
      // The client went away or sent no archive; either way it compiles for itself.
      if (!call.response.isCommitted) call.respond(HttpStatusCode.BadRequest)
    } finally {
      admitted.release()
    }
  }

  private fun presents(call: ApplicationCall): Boolean {
    val presented = call.request.headers[HttpHeaders.Authorization].orEmpty()
    return MessageDigest.isEqual(presented.toByteArray(), "Bearer ${settings.token}".toByteArray())
  }

  private suspend fun respond(call: ApplicationCall) {
    val headers = call.request.headers
    val declared = headers[HttpHeaders.ContentLength]?.toLongOrNull()
    when {
      call.request.path() != "/compile" -> call.respond(HttpStatusCode.NotFound)
      call.request.httpMethod != HttpMethod.Post -> call.respond(HttpStatusCode.MethodNotAllowed)
      !presents(call) -> call.respond(HttpStatusCode.Unauthorized)
      declared != null && declared > MAXIMUM_ARCHIVE_BYTES ->
        call.respond(HttpStatusCode.PayloadTooLarge)
      headers[COMPILER_HEADER] != settings.compilerVersion ||
        headers[HOST_HEADER] != hostName(settings.machine) -> call.respond(HttpStatusCode.Conflict)
      else -> compile(call)
    }
  }

  private suspend fun compile(call: ApplicationCall) {
    val work = withContext(io) { Files.createTempDirectory("tazel-remote-") }
    val request = withContext(io) { Files.createTempFile("tazel-request-", ".zip") }
    val answer = withContext(io) { Files.createTempFile("tazel-answer-", ".zip") }
    try {
      val body = call.receiveChannel()
      withContext(io) { Files.newOutputStream(request).use { body.copyLimited(it) } }
      val packed = withContext(io) { run(work, request, answer) }
      if (!packed) return call.respond(HttpStatusCode.UnprocessableEntity)
      call.respond(fileContent(answer))
    } finally {
      withContext(io) {
        work.toFile().deleteRecursively()
        Files.deleteIfExists(request)
        Files.deleteIfExists(answer)
      }
    }
  }

  /** Unpacks [request] into [work], compiles it and packs the result into [answer]. */
  private fun run(
    work: Path,
    request: Path,
    answer: Path,
  ): Boolean {
    var arguments: List<String>? = null
    val sources = mutableListOf<String>()
    forEachEntry(request) { name, zip ->
      when {
        name == ARGUMENTS_ENTRY -> arguments = entryText(zip).split("\n")
        name.startsWith(SOURCES_PREFIX) -> {
          val source = name.removePrefix(SOURCES_PREFIX)
          if (!isPlainPath(source)) throw IOException("the archive names '$source'")
          val file = entryPath(work, source)
          file.parent?.let(Files::createDirectories)
          Files.copy(zip, file)
          sources += source
        }
      }
    }
    val command = arguments
    if (command == null || !isCompilerCommand(command, sources)) return false
    try {
      slots.acquire()
    } catch (_: InterruptedException) {
      Thread.currentThread().interrupt()
      throw IOException("the server is stopping")
    }
    val run =
      try {
        processes.runGathered(listOf(settings.compiler.toString()) + command, work)
      } finally {
        slots.release()
      }
    pack(run, work, sources.toSet(), answer)
    return true
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
      text(LOG_ENTRY, shortened(run.output))
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

/**
 * [log] as the log entry of an answer may hold it: a client reads at most
 * [MAXIMUM_TEXT_ENTRY_BYTES], so a longer log keeps its beginning, where the first diagnostics are,
 * and says how much it left out.
 */
internal fun shortened(log: String): String {
  val bytes = log.toByteArray()
  if (bytes.size <= MAXIMUM_TEXT_ENTRY_BYTES) return log
  val kept = (MAXIMUM_TEXT_ENTRY_BYTES - 1024).toInt()
  return bytes.copyOf(kept).decodeToString().trimEnd('\uFFFD') +
    "\n[tazel: ${bytes.size - kept} more bytes of the compiler's output were left out]\n"
}
