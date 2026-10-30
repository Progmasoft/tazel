/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class BuildCacheDslTest {
  private fun settings(block: BuildCacheScope.() -> Unit) = BuildCacheScope().apply(block).build()

  private fun remoteAt(address: String) = settings { remote { url = address } }.remote

  @Test
  fun aProjectWithoutTheBlockHasALocalCacheOnly() {
    assertEquals(BuildCacheSettings(LocalCacheSettings(true, 8), null), settings {})
  }

  @Test
  fun everySettingIsRead() {
    val cache = settings {
      local {
        enabled = false
        maxEntries = 3
      }
      remote {
        url = "https://cache.example.com/tala/"
        read = false
        write = true
        token = "secret"
      }
    }
    assertEquals(LocalCacheSettings(false, 3), cache.local)
    assertEquals(
      RemoteCacheSettings("https://cache.example.com/tala", false, true, "secret"),
      cache.remote,
    )
  }

  @Test
  fun aRemoteCacheReadsAndDoesNotWriteUnlessToldTo() {
    val remote = remoteAt("https://cache.example.com")
    assertEquals(true, remote?.read)
    assertEquals(false, remote?.write)
    assertNull(remote?.token)
  }

  @Test
  fun anEmptyTokenCountsAsNone() {
    assertNull(
      settings {
          remote {
            url = "https://a.example"
            token = ""
          }
        }
        .remote
        ?.token
    )
  }

  @Test
  fun aTokenIsNotPrinted() {
    val text = RemoteCacheSettings("https://a.example", token = "secret").toString()
    assertFalse("secret" in text)
    assertTrue("token=set" in text)
  }

  @Test
  fun anAddressNeedsHttpsOrThisMachine() {
    assertEquals("http://localhost:8080", remoteAt("http://localhost:8080")?.url)
    assertEquals("http://127.0.0.1:8080/c", remoteAt("http://127.0.0.1:8080/c/")?.url)
    for (address in
      listOf(
        "http://cache.example.com",
        "ftp://cache.example.com",
        "cache.example.com",
        "https://",
        "https://user:pass@cache.example.com",
        "https://cache.example.com/?key=1",
        "https://cache.example.com/#part",
        "not an address",
      )) {
      assertFailsWith<ProjectConfigurationException>(address) { remoteAt(address) }
    }
  }

  @Test
  fun whatCannotWorkIsRefused() {
    assertFailsWith<ProjectConfigurationException> { settings { remote {} } }
    assertFailsWith<ProjectConfigurationException> { settings { local { maxEntries = 0 } } }
    assertFailsWith<ProjectConfigurationException> {
      settings {
        remote {
          url = "https://a.example"
          token = "a\nb"
        }
      }
    }
    assertFailsWith<ProjectConfigurationException> {
      settings {
        local {}
        local {}
      }
    }
  }
}
