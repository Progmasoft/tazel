/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class RequestInputTest {
  /** Typing nobody reads waits like a pipe, and a program that reads makes room again. */
  @Test
  fun inputNobodyReadsWaitsForRoom() {
    val input = RequestInput(capacity = 4)
    input.feed(byteArrayOf(1, 2, 3))
    val feeding = thread { input.feed(byteArrayOf(4, 5, 6)) }
    feeding.join(300)
    assertTrue(feeding.isAlive, "the input took more than it holds")

    assertContentEquals(byteArrayOf(1, 2, 3), input.next(1, TimeUnit.SECONDS))
    feeding.join(5_000)
    assertFalse(feeding.isAlive)
    val rest =
      generateSequence { input.next(100, TimeUnit.MILLISECONDS) }.flatMap { it.asSequence() }
    assertEquals(listOf<Byte>(4, 5, 6), rest.toList())
  }

  /** When the run ends, a client still piping is no longer waited for. */
  @Test
  fun aClosedInputStopsWaiting() {
    val input = RequestInput(capacity = 2)
    input.feed(byteArrayOf(1, 2))
    val feeding = thread { input.feed(byteArrayOf(3)) }
    feeding.join(300)
    assertTrue(feeding.isAlive)
    input.close()
    feeding.join(5_000)
    assertFalse(feeding.isAlive)
    assertTrue(input.ended)
  }
}
