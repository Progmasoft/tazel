/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.CacheServer
import com.progmasoft.tazel.driver.CacheServerSettings
import java.nio.file.Files
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** `tazel cache` against projects on disk and a real server of the cache. */
class CacheCommandsTest {
  private fun Fixture.served(block: Fixture.(url: String) -> Unit) {
    val settings = CacheServerSettings(root.resolve("store"), "127.0.0.1", 0, 1L shl 30, "write")
    CacheServer(settings).use { server ->
      server.start()
      block("http://127.0.0.1:${server.port}")
    }
  }

  private fun Fixture.remote(
    url: String,
    write: Boolean = true,
  ) =
    demo(
      extra =
        """
      buildCache {
        remote {
          url = "$url"
          write = $write
          token = "write"
        }
      }
      """
    )

  private val Fixture.cache
    get() = root.resolve(".tazel/cache")

  @Test
  fun statusShowsWhatTheCacheHolds() = fixture {
    demo()
    run("build")
    assertEquals(ExitCode.SUCCESS, run("cache", "status"))
    val printed = recorder.stdout()
    assertTrue(printed.contains("1 results"))
    assertTrue(printed.contains("executable-Demo: 1"))
    assertTrue(printed.contains("remote  none"))
  }

  @Test
  fun statusSaysHowARemoteCacheStands() = fixture {
    served { url ->
      remote(url)
      assertEquals(ExitCode.SUCCESS, run("cache", "status"))
      assertTrue(recorder.stdout().contains("read and write, results not verified, reachable"))
    }
    assertEquals(ExitCode.SUCCESS, run("cache", "status"))
    assertTrue(recorder.stdout().contains("not reachable"))
  }

  @Test
  fun cleanRemovesTheLocalCacheAndTheNextBuildCompiles() = fixture {
    demo()
    run("build")
    assertEquals(ExitCode.SUCCESS, run("cache", "clean"))
    assertFalse(Files.exists(cache))
    run("build")
    assertEquals(2, callsOf("talac").size)
  }

  @Test
  fun pushSendsWhatTheServerDoesNotHave() = fixture {
    demo()
    run("build")
    served { url ->
      remote(url)
      assertEquals(ExitCode.SUCCESS, run("cache", "push"))
      assertTrue(recorder.stdout().contains("1 sent, 0 already there"), recorder.stdout())

      assertEquals(ExitCode.SUCCESS, run("cache", "push"))
      assertTrue(recorder.stdout().contains("0 sent, 1 already there"), recorder.stdout())
    }
  }

  @Test
  fun pushNeedsARemoteCacheItMayWriteTo() = fixture {
    demo()
    assertEquals(ExitCode.FAILURE, run("cache", "push"))
    assertTrue(recorder.stderr().contains("no buildCache.remote"))
    served { url ->
      remote(url, write = false)
      assertEquals(ExitCode.FAILURE, run("cache", "push"))
      assertTrue(recorder.stderr().contains("write = false"))
    }
  }

  @Test
  fun pushFailsWhenTheServerDoes() = fixture {
    var address = ""
    served { address = it }
    demo()
    run("build")
    remote(address)
    assertEquals(ExitCode.FAILURE, run("cache", "push"))
  }

  @Test
  fun pullBringsTheResultsOfTheCurrentSources() = fixture {
    served { url ->
      remote(url)
      run("build")
      run("cache", "clean")

      assertEquals(ExitCode.SUCCESS, run("cache", "pull"))
      assertTrue(recorder.stdout().contains("1 of 2 results"), recorder.stdout())
      assertTrue(Files.isDirectory(cache.resolve("executable-Demo")))
    }
    // The server is gone, and the build needs no compiler.
    run("build")
    assertEquals(1, callsOf("talac").size)
  }

  @Test
  fun pullNeedsARemoteCache() = fixture {
    demo()
    assertEquals(ExitCode.FAILURE, run("cache", "pull"))
    assertTrue(recorder.stderr().contains("no buildCache.remote"))
  }
}
