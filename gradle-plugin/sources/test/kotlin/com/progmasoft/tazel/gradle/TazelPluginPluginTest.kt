/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.gradle

import java.io.File
import java.nio.file.Files
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue
import org.gradle.testkit.runner.GradleRunner

/** Runs the plugin in a project of its own, as a plugin author would. */
class TazelPluginPluginTest {
  private lateinit var project: File

  @BeforeTest
  fun create() {
    project = Files.createTempDirectory("tazel-gradle-plugin-").toFile()
    project.resolve("settings.gradle.kts").writeText("rootProject.name = \"native\"\n")
  }

  @AfterTest
  fun delete() {
    project.deleteRecursively()
  }

  private fun build(
    tazelPlugin: String,
    vararg arguments: String,
  ) =
    GradleRunner.create()
      .withProjectDir(project)
      .withPluginClasspath()
      .also {
        project
          .resolve("build.gradle.kts")
          .writeText(
            """
            plugins { id("com.progmasoft.tazel.plugin") }

            // The project version is not the plugin version: tazelPlugin { version } is.
            version = "1.2.0.3.4-build5"

            tazelPlugin {
            $tazelPlugin
            }

            dependencies { tazelApi() }

            tasks.register("printApi") {
              val compileOnly = configurations.compileOnly.get().dependencies.map { "${'$'}{it.group}:${'$'}{it.name}:${'$'}{it.version}" }
              val tests = configurations.testImplementation.get().dependencies.map { "${'$'}{it.group}:${'$'}{it.name}:${'$'}{it.version}" }
              doLast { println("compileOnly=${'$'}compileOnly testImplementation=${'$'}tests") }
            }
            """
              .trimIndent()
          )
      }
      .withArguments(*arguments, "--stacktrace")

  private val identity =
    """
    group = "com.example.plugins"
    name = "native"
    version = "0.1.0"
    """

  @Test
  fun writesTheDescriptorAndTheServiceEntry() {
    build(
        """
        $identity
        pluginClass = "com.example.NativePlugin"
        imports = listOf("com.example.dsl.*")
        description = "Builds native code; with = and : in it."
        authors = listOf("First Author", "Second Author")
        license = "MIT"
        homepage = "https://example.com/native"
        """,
        "processResources",
      )
      .build()

    // The descriptor says who the plugin is, from the block and not from the project.
    val resources = project.resolve("build/resources/main/META-INF")
    assertEquals(
      listOf(
        "group=com.example.plugins",
        "name=native",
        "version=0.1.0",
        "apiVersion=${TazelPluginPlugin.API_VERSION}",
        "imports=com.example.dsl.*",
        "description=Builds native code; with \\= and \\: in it.",
        "author.0=First Author",
        "author.1=Second Author",
        "license=MIT",
        "homepage=https\\://example.com/native",
      ),
      resources.resolve("tazel-plugin.properties").readLines(),
    )
    assertEquals(
      "com.example.NativePlugin",
      resources
        .resolve("services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin")
        .readText()
        .trim(),
    )
  }

  @Test
  fun tazelApiIsACompileOnlyAndTestDependencyOfTheApiOfThisVersion() {
    val output =
      build(
          """
          $identity
          pluginClass = "com.example.NativePlugin"
          """,
          "printApi",
        )
        .build()
        .output
    val api = "com.progmasoft.tazel:tazel-api:${TazelPluginPlugin.VERSION}"
    assertTrue("compileOnly=[$api] testImplementation=[$api]" in output, output)
  }

  @Test
  fun aMissingFieldIsReportedByName() {
    val output =
      build(
          """
          $identity
          imports = listOf("com.example.dsl.*")
          """,
          "processResources",
        )
        .buildAndFail()
        .output
    assertTrue("tazelPlugin.pluginClass is required" in output, output)
  }

  @Test
  fun theIdentityIsRequiredAndChecked() {
    val cases =
      listOf(
        """name = "native"; version = "0.1.0"""" to "tazelPlugin.group is required",
        """group = "com.example"; version = "0.1.0"""" to "tazelPlugin.name is required",
        """group = "com.example"; name = "native"""" to "tazelPlugin.version is required",
        """group = "example"; name = "native"; version = "0.1.0"""" to "a reversed domain",
        """group = "com.example"; name = "-native"; version = "0.1.0"""" to "a name is letters",
        """group = "com.example"; name = "native"; version = "1.2.0.3"""" to
          "not a Semantic Version",
      )
    for ((settings, refusal) in cases) {
      val output =
        build(
            """
            $settings
            pluginClass = "com.example.NativePlugin"
            """,
            "processResources",
          )
          .buildAndFail()
          .output
      assertTrue(refusal in output, "$settings: $output")
    }
  }
}
