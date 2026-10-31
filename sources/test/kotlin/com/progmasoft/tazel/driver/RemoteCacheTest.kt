/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildCacheSettings
import com.progmasoft.tazel.dsl.LocalCacheSettings
import com.progmasoft.tazel.dsl.RemoteCacheSettings
import com.sun.net.httpserver.HttpServer
import java.io.ByteArrayOutputStream
import java.net.InetSocketAddress
import java.nio.file.Files
import java.nio.file.Path
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** Runs the cache against a small HTTP server that keeps what it is sent. */
class RemoteCacheTest {
  private class Server(
    val root: Path,
    private val server: HttpServer,
  ) {
    val stored = mutableMapOf<String, ByteArray>()
    val requests = mutableListOf<String>()
    val tokens = mutableListOf<String?>()
    var status: Int? = null

    val url
      get() = "http://127.0.0.1:${server.address.port}/cache"

    init {
      server.createContext("/cache") { exchange ->
        val path = exchange.requestURI.path.removePrefix("/cache/")
        requests += "${exchange.requestMethod} $path"
        tokens += exchange.requestHeaders.getFirst("Authorization")
        val forced = status
        val body = exchange.requestBody.readBytes()
        when {
          forced != null -> exchange.sendResponseHeaders(forced, -1)
          exchange.requestMethod == "PUT" -> {
            stored[path] = body
            exchange.sendResponseHeaders(201, -1)
          }
          path in stored -> {
            exchange.sendResponseHeaders(200, stored.getValue(path).size.toLong())
            exchange.responseBody.write(stored.getValue(path))
          }
          else -> exchange.sendResponseHeaders(404, -1)
        }
        exchange.close()
      }
      server.start()
    }

    fun cache(
      name: String,
      settings: RemoteCacheSettings,
      local: LocalCacheSettings = LocalCacheSettings(),
      problems: MutableList<String> = mutableListOf(),
    ) =
      BuildCache(
        root.resolve(name),
        BuildCacheSettings(local, settings),
        HttpRemoteStore(settings),
      ) {
        problems += it
      }

    fun program(text: String = "built"): Path = Files.writeString(root.resolve("Program"), text)

    fun stop() = server.stop(0)
  }

  private fun withServer(block: Server.() -> Unit) {
    val root = Files.createTempDirectory("tazel-remote-").toRealPath()
    val server = Server(root, HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0))
    try {
      server.block()
    } finally {
      server.stop()
      root.toFile().deleteRecursively()
    }
  }

  private val Server.writes
    get() = RemoteCacheSettings(url, read = true, write = true, token = "secret")

  @Test
  fun aResultStoredOnOneMachineComesBackOnAnother() = withServer {
    cache("first", writes).store("build-Demo", "key", listOf(program()), "a warning")
    Files.delete(root.resolve("Program"))

    val hit = cache("second", RemoteCacheSettings(url)).load("build-Demo", "key", root)

    assertEquals("a warning", hit?.log)
    assertEquals(BuildCache.Restored.COPIED, hit?.restored)
    assertEquals("built", Files.readString(root.resolve("Program")))
    assertEquals(listOf("PUT build-Demo/key", "GET build-Demo/key"), requests)
    assertEquals(listOf("Bearer secret", null), tokens)
  }

  @Test
  fun theLocalCacheIsAskedFirstAndKeepsWhatWasFetched() = withServer {
    cache("first", writes).store("build-Demo", "key", listOf(program()), "")
    val reader = RemoteCacheSettings(url)

    assertNotNull(cache("second", reader).load("build-Demo", "key", null))
    assertNotNull(cache("second", reader).load("build-Demo", "key", null))
    assertNotNull(cache("first", reader).load("build-Demo", "key", null))

    assertEquals(1, requests.count { it.startsWith("GET") })
  }

  @Test
  fun aDisabledLocalCacheStillUsesTheRemoteOne() = withServer {
    cache("first", writes).store("build-Demo", "key", listOf(program()), "")
    val off = LocalCacheSettings(enabled = false)

    assertNotNull(cache("second", RemoteCacheSettings(url), off).load("build-Demo", "key", root))
    assertNotNull(cache("second", RemoteCacheSettings(url), off).load("build-Demo", "key", root))

    assertEquals(2, requests.count { it.startsWith("GET") })
    assertFalse(Files.exists(root.resolve("second/build-Demo/key")))
  }

  @Test
  fun aCacheThatMayNotWriteStoresNothingAndOneThatMayNotReadAsksNothing() = withServer {
    val silent = RemoteCacheSettings(url, read = false, write = false)
    cache("first", silent).store("build-Demo", "key", listOf(program()), "")
    assertNull(cache("second", silent).load("build-Demo", "key", null))
    assertEquals(emptyList(), requests)
  }

  @Test
  fun aResultTheServerDoesNotHaveIsAMiss() = withServer {
    val problems = mutableListOf<String>()
    assertNull(
      cache("first", RemoteCacheSettings(url), problems = problems).load("build-Demo", "key", null)
    )
    assertEquals(emptyList(), problems)
  }

  @Test
  fun aServerThatFailsIsAWarningOnceAndThenLeftAlone() = withServer {
    status = 500
    val problems = mutableListOf<String>()
    val cache = cache("first", writes, problems = problems)

    assertNull(cache.load("build-Demo", "one", null))
    assertNull(cache.load("build-Demo", "two", null))
    cache.store("build-Demo", "three", listOf(program()), "")

    assertEquals(1, requests.size)
    assertEquals(1, problems.size)
    assertTrue("500" in problems.single())
    assertFalse("secret" in problems.single())
    // The local cache goes on.
    assertNotNull(cache.load("build-Demo", "three", null))
  }

  @Test
  fun anArchiveThatLeadsOutOfItsDirectoryIsRefused() = withServer {
    for (name in listOf("../escaped", "..", "a/b", "a\\b", "C:escaped")) {
      val bytes = ByteArrayOutputStream()
      ZipOutputStream(bytes).use { zip ->
        zip.putNextEntry(ZipEntry(".log"))
        zip.closeEntry()
        zip.putNextEntry(ZipEntry(name))
        zip.write("bad".toByteArray())
        zip.closeEntry()
      }
      stored["build-Demo/key"] = bytes.toByteArray()
      val problems = mutableListOf<String>()

      val cache = cache("first", RemoteCacheSettings(url), problems = problems)

      assertNull(cache.load("build-Demo", "key", root), name)
      assertTrue(problems.any { "names the file" in it }, name)
      assertFalse(Files.exists(root.resolve("escaped")), name)
    }
  }

  @Test
  fun anArchiveWithoutALogIsRefused() = withServer {
    val bytes = ByteArrayOutputStream()
    ZipOutputStream(bytes).use { zip -> zip.putNextEntry(ZipEntry("Program")) }
    stored["build-Demo/key"] = bytes.toByteArray()
    assertNull(
      cache("first", RemoteCacheSettings(url), problems = mutableListOf())
        .load("build-Demo", "key", null)
    )
  }
}
