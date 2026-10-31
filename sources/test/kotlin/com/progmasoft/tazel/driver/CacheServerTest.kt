/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildCacheSettings
import com.progmasoft.tazel.dsl.LocalCacheSettings
import com.progmasoft.tazel.dsl.RemoteCacheSettings
import com.progmasoft.tazel.dsl.ResultKeys
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** The client of the cache against the server of the cache, signatures included. */
class CacheServerTest {
  private class Harness(
    val root: Path,
    val server: CacheServer,
  ) {
    val url
      get() = "http://127.0.0.1:${server.port}"

    fun cache(
      name: String,
      settings: RemoteCacheSettings,
      problems: MutableList<String> = mutableListOf(),
    ) =
      BuildCache(
        root.resolve(name),
        BuildCacheSettings(LocalCacheSettings(), settings),
        HttpRemoteStore(settings),
      ) {
        problems += it
      }

    fun program(text: String = "built"): Path = Files.writeString(root.resolve("Program"), text)

    fun status(
      method: String,
      path: String,
      token: String? = null,
      body: String = "",
    ): Int {
      val request =
        HttpRequest.newBuilder(URI.create(url + path))
          .method(method, HttpRequest.BodyPublishers.ofString(body))
      token?.let { request.header("Authorization", "Bearer $it") }
      return HttpClient.newHttpClient()
        .send(request.build(), HttpResponse.BodyHandlers.discarding())
        .statusCode()
    }
  }

  private fun running(
    writeToken: String? = "write",
    readToken: String? = null,
    maxBytes: Long = 1L shl 30,
    block: Harness.() -> Unit,
  ) {
    val root = Files.createTempDirectory("tazel-server-").toRealPath()
    val settings =
      CacheServerSettings(root.resolve("store"), "127.0.0.1", 0, maxBytes, writeToken, readToken)
    try {
      CacheServer(settings).use { server ->
        server.start()
        Harness(root, server).block()
      }
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun aResultGoesThroughTheServerToAnotherMachine() = running {
    val writer = RemoteCacheSettings(url, write = true, token = "write")
    cache("first", writer).store("build-Demo", "key", listOf(program()), "a warning")
    Files.delete(root.resolve("Program"))

    val hit = cache("second", RemoteCacheSettings(url)).load("build-Demo", "key", root)

    assertEquals("a warning", hit?.log)
    assertEquals("built", Files.readString(root.resolve("Program")))
  }

  @Test
  fun aSignedResultIsUsedAndTheKeysOfAnotherAreNot() = running {
    val (public, signing) = ResultKeys.generate()
    val (otherPublic, _) = ResultKeys.generate()
    val writer =
      RemoteCacheSettings(
        url,
        write = true,
        token = "write",
        publicKey = public,
        signingKey = signing,
      )
    cache("first", writer).store("build-Demo", "key", listOf(program()), "")

    val problems = mutableListOf<String>()
    assertNotNull(
      cache("second", RemoteCacheSettings(url, publicKey = public), problems)
        .load("build-Demo", "key", null)
    )
    assertEquals(emptyList(), problems)

    assertNull(
      cache("third", RemoteCacheSettings(url, publicKey = otherPublic), problems)
        .load("build-Demo", "key", null)
    )
    assertTrue("signature" in problems.single())
  }

  @Test
  fun aResultWithoutASignatureIsMissingForAReaderWithAKey() = running {
    val (public, _) = ResultKeys.generate()
    val unsigned = RemoteCacheSettings(url, write = true, token = "write")
    cache("first", unsigned).store("build-Demo", "key", listOf(program()), "")

    val problems = mutableListOf<String>()
    assertNull(
      cache("second", RemoteCacheSettings(url, publicKey = public), problems)
        .load("build-Demo", "key", null)
    )
    assertEquals(emptyList(), problems)
  }

  @Test
  fun aSignatureDoesNotCarryOverToAnotherKeyOrTarget() = running {
    val (public, signing) = ResultKeys.generate()
    val writer =
      RemoteCacheSettings(
        url,
        write = true,
        token = "write",
        publicKey = public,
        signingKey = signing,
      )
    cache("first", writer).store("build-Demo", "good", listOf(program()), "")
    val store = root.resolve("store/build-Demo")
    Files.copy(store.resolve("good"), store.resolve("evil"))
    Files.copy(store.resolve("good.sig"), store.resolve("evil.sig"))

    val problems = mutableListOf<String>()
    assertNull(
      cache("second", RemoteCacheSettings(url, publicKey = public), problems)
        .load("build-Demo", "evil", null)
    )
    assertEquals(1, problems.size)
  }

  @Test
  fun aChangedResultIsRefused() = running {
    val (public, signing) = ResultKeys.generate()
    val writer =
      RemoteCacheSettings(
        url,
        write = true,
        token = "write",
        publicKey = public,
        signingKey = signing,
      )
    cache("first", writer).store("build-Demo", "key", listOf(program()), "")
    Files.write(root.resolve("store/build-Demo/key"), byteArrayOf(1, 2, 3))

    val problems = mutableListOf<String>()
    assertNull(
      cache("second", RemoteCacheSettings(url, publicKey = public), problems)
        .load("build-Demo", "key", null)
    )
    assertEquals(1, problems.size)
  }

  @Test
  fun aReaderWithoutAKeyIsToldOnceThatItTrustsTheServer() = running {
    val writer = RemoteCacheSettings(url, write = true, token = "write")
    cache("first", writer).store("build-Demo", "one", listOf(program()), "")
    cache("first", writer).store("build-Demo", "two", listOf(program()), "")

    val problems = mutableListOf<String>()
    val reader = cache("second", RemoteCacheSettings(url), problems)
    assertNotNull(reader.load("build-Demo", "one", null))
    assertNotNull(reader.load("build-Demo", "two", null))

    assertEquals(1, problems.size)
    assertTrue("signature check" in problems.single())
  }

  @Test
  fun writingNeedsTheToken() = running {
    assertEquals(401, status("PUT", "/build-Demo/key"))
    assertEquals(403, status("PUT", "/build-Demo/key", "wrong"))
    assertEquals(201, status("PUT", "/build-Demo/key", "write", "x"))
    assertEquals(200, status("GET", "/build-Demo/key"))
    assertEquals(405, status("DELETE", "/build-Demo/key", "write"))
  }

  @Test
  fun aServerWithoutATokenTakesNothing() =
    running(writeToken = null) {
      assertEquals(401, status("PUT", "/build-Demo/key"))
    }

  @Test
  fun readingNeedsTheReadTokenWhenThereIsOne() =
    running(readToken = "read") {
      assertEquals(201, status("PUT", "/build-Demo/key", "write", "x"))
      assertEquals(401, status("GET", "/build-Demo/key"))
      assertEquals(200, status("GET", "/build-Demo/key", "read"))
    }

  @Test
  fun nothingLeadsOutOfTheDirectory() = running {
    for (path in listOf("/a", "/a/b/c", "/%2e%2e/secret", "/../secret", "/.hidden/key")) {
      assertEquals(404, status("PUT", path, "write", "x"), path)
      assertEquals(404, status("GET", path), path)
    }
    assertFalse(Files.exists(root.resolve("secret")))
  }

  @Test
  fun theResultsUsedLongestAgoGoFirst() =
    running(maxBytes = 10) {
      assertEquals(201, status("PUT", "/s/old", "write", "123456"))
      Thread.sleep(50)
      assertEquals(201, status("PUT", "/s/new", "write", "123456"))
      assertEquals(404, status("GET", "/s/old"))
      assertEquals(200, status("GET", "/s/new"))
    }
}
