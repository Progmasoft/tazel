/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.dsl.plugin.PluginResolver
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class PackageVersionTest {
  @Test
  fun aVersionHasThreeOrFourNumbers() {
    for (version in listOf("0.4.0", "0.4.0.1", "1.2.3.4-beta", "0.19.9.9")) {
      assertEquals(version, requirePackageVersion(version))
    }
    for (version in listOf("1.2", "1.2.3.4.5", "01.2.3", "1.2.3.04", "1.2.3.")) {
      val problem =
        assertFailsWith<ProjectConfigurationException> {
          requirePackageVersion(version)
        }
      assertContains(problem.message.orEmpty(), "three or four numbers")
    }
  }

  @Test
  fun aFourthNumberOrdersVersions() {
    assertTrue(PluginResolver.compareVersions("0.4.0.1", "0.4.0") > 0)
    assertTrue(PluginResolver.compareVersions("0.4.0.10", "0.4.0.9") > 0)
    assertTrue(PluginResolver.compareVersions("0.4.1.0", "0.4.0.9") > 0)
  }
}
