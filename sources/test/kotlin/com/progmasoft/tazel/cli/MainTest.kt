/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class MainTest {
  @Test
  fun versionPrintsTheVersion() {
    val out = StringBuilder()
    assertEquals(0, run(listOf("version"), out, StringBuilder()))
    assertTrue(out.startsWith("tazel "))
  }

  @Test
  fun anUnknownCommandIsAUsageError() {
    val err = StringBuilder()
    assertEquals(2, run(listOf("biuld"), StringBuilder(), err))
    assertTrue("'biuld' is not a command of tazel" in err)
  }
}
