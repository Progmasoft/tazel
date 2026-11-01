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
  fun aVersionHasThreeToFiveNumbers() {
    val versions =
      listOf("0.4.0", "0.4.0.1", "1.2.3.4-beta", "0.19.9.9", "1.2.3.4.5", "1.2.3.4.5-build.7")
    for (version in versions) {
      assertEquals(version, requirePackageVersion(version))
    }
    for (version in listOf("1.2", "1.2.3.4.5.6", "01.2.3", "1.2.3.04", "1.2.3.", "1.2.3.4.05")) {
      val problem =
        assertFailsWith<ProjectConfigurationException> {
          requirePackageVersion(version)
        }
      assertContains(problem.message.orEmpty(), "three to five numbers")
    }
  }

  @Test
  fun aFourthNumberOrdersVersions() {
    assertTrue(PluginResolver.compareVersions("0.4.0.1", "0.4.0") > 0)
    assertTrue(PluginResolver.compareVersions("0.4.0.10", "0.4.0.9") > 0)
    assertTrue(PluginResolver.compareVersions("0.4.1.0", "0.4.0.9") > 0)
  }

  @Test
  fun aFifthNumberOrdersVersions() {
    assertTrue(PluginResolver.compareVersions("1.2.3.4.1", "1.2.3.4") > 0)
    assertTrue(PluginResolver.compareVersions("1.2.3.4.10", "1.2.3.4.9") > 0)
  }

  @Test
  fun numbersOfAnySizeOrderVersions() {
    assertTrue(PluginResolver.compareVersions("1.2.3.3000000000", "1.2.3.2999999999") > 0)
    assertTrue(PluginResolver.compareVersions("1.2.3.99999999999999999999", "1.2.3.4") > 0)
  }

  @Test
  fun metadataRanksBelowTheSameVersionWithoutIt() {
    assertTrue(PluginResolver.compareVersions("1.2.3.4", "1.2.3.4-build") > 0)
    assertTrue(PluginResolver.compareVersions("1.2.3.4-build", "1.2.3.4") < 0)
    assertTrue(PluginResolver.compareVersions("1.2.3.5-build", "1.2.3.4") > 0)
    assertTrue(PluginResolver.compareVersions("1.2.3.4-b", "1.2.3.4-a") > 0)
  }
}
