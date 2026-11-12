/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.label

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class LabelTest {
  private val app = PackageId(null, "app")

  @Test
  fun theAcceptedFormsAreRead() {
    assertEquals(TargetLabel(PackageId.ROOT, "x"), Labels.target("//:x", app))
    assertEquals(TargetLabel(PackageId(null, "core"), "core"), Labels.target("//core/:core", app))
    assertEquals(TargetLabel(PackageId(null, "a/b"), "c"), Labels.target("//a/b/:c", app))
    assertEquals(TargetLabel(app, "bridge"), Labels.target(":bridge", app))
    assertEquals(TargetLabel(PackageId("toml", ""), "toml"), Labels.target("@toml//:toml", app))
    assertEquals(
      TargetLabel(PackageId("json-lib", "src"), "json"),
      Labels.target("@json-lib//src/:json", app),
    )
  }

  @Test
  fun labelsPrintAsTheyAreWritten() {
    for (text in listOf("//:x", "//core/:core", "//a/b/:c", "@toml//:toml", "@dep//d/:t")) {
      assertEquals(text, Labels.target(text, app).toString())
    }
    assertEquals("//tests/...", Labels.selection("//tests/...", app).toString())
    assertEquals("//...", Labels.selection("//...", app).toString())
  }

  @Test
  fun patternsCoverTheirPackageAndThoseBelow() {
    val tests = Labels.selection("//tests/...", app) as PackagePattern
    assertTrue(tests.covers(PackageId(null, "tests")))
    assertTrue(tests.covers(PackageId(null, "tests/unit")))
    assertFalse(tests.covers(PackageId(null, "tests-extra")))
    assertFalse(tests.covers(PackageId.ROOT))
    val all = Labels.selection("//...", app) as PackagePattern
    assertTrue(all.covers(PackageId.ROOT))
    assertTrue(all.covers(PackageId(null, "a/b")))
    assertFalse(all.covers(PackageId("dep", "")))
  }

  @Test
  fun theShorthandAndOtherFormsAreRefused() {
    val wrong =
      listOf(
        "//core",
        "//core:core",
        "core/:core",
        "//core/",
        "//:",
        ":",
        "@toml",
        "@//:x",
        "//../:x",
        "//a//:x",
        "//a/./:x",
        "//a b/:x",
        "//:x/y",
        "",
      )
    for (text in wrong) {
      assertFailsWith<LabelException>(text) { Labels.target(text, app) }
    }
    assertFailsWith<LabelException> { Labels.target("//tests/...", app) }
  }

  @Test
  fun theShorthandGetsAHint() {
    val error = assertFailsWith<LabelException> { Labels.target("//core", app) }
    assertTrue("//core/:core" in error.message!!, error.message)
  }
}
