/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

/** The last line of a test runner of Talac: how many tests ran and how they ended. */
data class TestTotals(
  val passed: Int,
  val failed: Int,
) {
  val tests: Int
    get() = passed + failed

  operator fun plus(other: TestTotals) = TestTotals(passed + other.passed, failed + other.failed)

  override fun toString() = "$tests ${testsWord(tests)}, $passed passed, $failed failed"

  companion object {
    private val line = Regex("""(\d+) tests?, (\d+) passed, (\d+) failed""")

    /** The totals [text] states, or null when it is any other line a runner writes. */
    fun parse(text: String): TestTotals? {
      val (tests, passed, failed) = line.matchEntire(text.trim())?.destructured ?: return null
      val totals =
        TestTotals(passed.toIntOrNull() ?: return null, failed.toIntOrNull() ?: return null)
      return totals.takeIf { it.tests.toString() == tests }
    }
  }
}

private fun testsWord(count: Int) = if (count == 1) "test" else "tests"
