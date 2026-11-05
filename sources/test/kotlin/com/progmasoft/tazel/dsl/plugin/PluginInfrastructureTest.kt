/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.api.tazel.dsl.plugin.ProjectPluginContext
import com.progmasoft.tazel.dsl.DependencyManifest
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.ProjectRuntime
import com.progmasoft.tazel.dsl.detectHost
import com.progmasoft.tazel.dsl.project
import com.progmasoft.tazel.dsl.sources
import com.progmasoft.tazel.lock.LockedPlugin
import com.progmasoft.tazel.lock.LockedProject
import com.progmasoft.tazel.lock.ProjectLockFile
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.sql.DriverManager
import java.util.jar.JarEntry
import java.util.jar.JarOutputStream
import kotlin.io.path.outputStream
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

class PluginInfrastructureTest {
  private val fixture = PluginRequest("Publisher", "Fixture", "1.0.0")

  @BeforeTest
  fun reset() {
    ProjectRuntime.reset()
    PluginResolver.clearCache()
  }

  @AfterTest
  fun cleanup() {
    ProjectRuntime.reset()
  }

  @Test
  fun parsesPluginPreambleWithoutExecutingProjectCode() {
    val requests =
      PluginPreamble.parse(
        """
        // plugins { plugin("Ignored") { name = "Comment" } }
        val raw = ${"\"\"\""}plugins { plugin("Ignored") { name = "RawString" } }${"\"\"\""}
        val character = '}'
        plugins {
          plugin("Publisher") {
            name = "CMake"
            version = "1.2.3"
          }
        }
        panic("must not execute")
        """
          .trimIndent()
      )

    assertEquals(
      PluginRequest("Publisher", "CMake", "1.2.3"),
      requests.single(),
    )
  }

  @Test
  fun rejectsDuplicateAndUnsafeDeclarations() {
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse(
        "plugins { plugin(\"Progmasoft\") { name=\"CMake\" }; plugin(\"Progmasoft\") { name=\"CMake\" } }"
      )
    }
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse("plugins { plugin(\"../bad\") { name=\"CMake\" } }")
    }
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse(
        "plugins { plugin(\"Progmasoft\") { name=\"CMake\"; stability=Stability.STABLE } }"
      )
    }
  }

  @Test
  fun resolvesExplicitLocalPluginJarByItsVerifiedDescriptor() {
    val root = Files.createTempDirectory("tala-local-plugin-")
    try {
      val artifact = createPluginJar(root)
      val request = localPluginRequest(artifact.fileName.toString())
      val resolved = PluginResolver.resolve(root, listOf(request)).single()

      // A local plugin is the file the project names; its JAR says nothing about who it is.
      assertEquals("local:${artifact.fileName}", resolved.coordinate)
      assertEquals("local:${artifact.fileName}", resolved.requestCoordinate)
      assertEquals("local", resolved.version)
      assertEquals(
        request,
        PluginPreamble.parse("plugins { localPlugin { path = \"${artifact.fileName}\" } }")
          .single(),
      )
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun rejectsUnsafeOrMismatchedLocalPluginPaths() {
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse("plugins { localPlugin { path = \"../plugin.jar\" } }")
    }
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse("plugins { localPlugin { path = \"plugin.vipkg\" } }")
    }
    assertFailsWith<ProjectConfigurationException> {
      PluginPreamble.parse(
        "plugins { plugin(\"Progmasoft\") { name = \"CMake\"; path = \"plugin.jar\" } }"
      )
    }
  }

  @Test
  fun resolvesDescriptorAndVerifiesOptionalChecksum() {
    val root = Files.createTempDirectory("tala-plugin-resolver-")
    try {
      val artifact = cachePluginJar(root.resolve(".tazel/plugins"), version = "1.2.3")
      Files.writeString(artifact.resolveSibling("${artifact.fileName}.sha256"), sha256(artifact))

      val resolved =
        PluginResolver.resolve(
          root,
          listOf(PluginRequest("Publisher", "Fixture", "1.2.3")),
        )

      assertEquals("Publisher.Fixture", resolved.single().coordinate)
      assertEquals(listOf("com.progmasoft.tazel.dsl.*"), resolved.single().imports)
      assertEquals(64, resolved.single().sha256.length)
      assertSame(
        resolved.single(),
        PluginResolver.resolve(
            root,
            listOf(PluginRequest("Publisher", "Fixture", "1.2.3")),
          )
          .single(),
      )
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun rejectsChecksumMismatchAndApiMismatch() {
    val root = Files.createTempDirectory("tala-plugin-integrity-")
    try {
      val cache = root.resolve(".tazel/plugins")
      val artifact = cachePluginJar(cache)
      Files.writeString(artifact.resolveSibling("${artifact.fileName}.sha256"), "0".repeat(64))
      assertFailsWith<ProjectConfigurationException> {
        PluginResolver.resolve(root, listOf(PluginRequest("Publisher", "Fixture", null)))
      }
      Files.delete(artifact.resolveSibling("${artifact.fileName}.sha256"))
      cachePluginJar(cache, apiVersion = 99)
      PluginResolver.clearCache()
      assertFailsWith<ProjectConfigurationException> {
        PluginResolver.resolve(root, listOf(PluginRequest("Publisher", "Fixture", null)))
      }
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  /** A cached plugin is who its `plugin.json` says; a JAR that names itself is refused. */
  @Test
  fun takesWhoAPluginIsFromViGetAndNeverFromItsJar() {
    val root = Files.createTempDirectory("tala-plugin-identity-")
    try {
      val cache = root.resolve(".tazel/plugins")
      // A JAR that names itself is refused, whoever recorded it.
      cachePluginJar(cache, identity = true)
      val named =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(root, listOf(fixture))
        }
      assertTrue("names publisher, name, version" in named.message.orEmpty(), named.message)

      // The record decides who a cached JAR is, not the folders it lies in.
      root.resolve(".tazel").toFile().deleteRecursively()
      PluginResolver.clearCache()
      val artifact = cachePluginJar(cache)
      val moved = cache.resolve("Other/Fixture/1.0.0/Fixture-1.0.0.jar")
      Files.createDirectories(moved.parent)
      Files.copy(artifact, moved)
      val resolved = PluginResolver.resolve(root, listOf(fixture))
      assertEquals("Publisher.Fixture", resolved.single().coordinate)
      assertFailsWith<ProjectConfigurationException> {
        PluginResolver.resolve(root, listOf(PluginRequest("Other", "Fixture", "1.0.0")))
      }

      // A record that lists another digest than its JAR has is refused.
      PluginResolver.clearCache()
      val record = artifact.resolveSibling("plugin.json")
      Files.writeString(
        record,
        Files.readString(record).replace(sha256(artifact), "0".repeat(64)),
      )
      val changed =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(root, listOf(fixture))
        }
      assertTrue("another SHA-256" in changed.message.orEmpty(), changed.message)

      // A field that is not a JSON string is a diagnostic of the record, not a crash.
      PluginResolver.clearCache()
      Files.writeString(
        record,
        """{"publisher":{"a":1},"name":"Fixture","version":"1.0.0","sha256":"${sha256(artifact)}"}""",
      )
      val malformed =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(root, listOf(fixture))
        }
      assertTrue("has no string publisher" in malformed.message.orEmpty(), malformed.message)
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun twoPluginsNeverShareAStepFolder() {
    assertEquals("Publisher.Native", pluginFolder("Publisher.Native"))
    assertEquals("local_3Aa_2Fb.jar", pluginFolder("local:a/b.jar"))
    assertEquals("local_3Aa_5Fb.jar", pluginFolder("local:a_b.jar"))
    assertEquals("local_3Aa_5Cb.jar", pluginFolder("local:a\\b.jar"))
    assertEquals("local_3A_C3_BC.jar", pluginFolder("local:ü.jar"))
  }

  @Test
  fun activatesLifecycleAndRejectsExtensionCollisions() {
    project { name = "Demo" }
    val root = Files.createTempDirectory("tala-plugin-runtime-")
    try {
      val first = resolved(root, "First")
      val second = resolved(root, "Second")
      val firstPlugin = fixturePlugin { context ->
        context.registerExtension("native")
        context.contribute("enabled", "true")
      }
      val secondPlugin = fixturePlugin { context -> context.registerExtension("native") }

      ProjectRuntime.reset()
      assertFailsWith<ProjectConfigurationException> {
        PluginRuntime.activate(
          listOf(first, second),
          listOf(firstPlugin, secondPlugin),
          root,
          detectHost(),
        )
      }

      ProjectRuntime.reset()
      project { name = "Demo" }
      PluginRuntime.activate(listOf(first), listOf(firstPlugin), root, detectHost())
      PluginRuntime.declare(PluginRequest("Publisher", "First", "1.0.0"))
      sources {
        executable {
          name = "Demo"
          entry = "Demo.Main"
        }
      }
      val plan = ProjectRuntime.build()
      assertEquals(listOf("native"), plan.plugins.single().extensions)
      assertEquals("true", plan.plugins.single().contributions["enabled"])
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun lockfilePersistsResolvedPluginIdentityAndDigest() {
    val root = Files.createTempDirectory("tala-plugin-lock-")
    try {
      val plugin =
        LockedPlugin("Publisher", "CMake", "1.0.0", PROJECT_PLUGIN_API_VERSION, "a".repeat(64))
      ProjectLockFile.write(root, LockedProject(DependencyManifest(emptyList()), listOf(plugin)))
      val restored = ProjectLockFile.read(root.resolve(ProjectLockFile.FILE_NAME))
      assertEquals(listOf(plugin), restored.plugins)
      DriverManager.getConnection("jdbc:sqlite:${root.resolve(ProjectLockFile.FILE_NAME)}").use {
        connection ->
        connection.createStatement().use { statement ->
          statement.executeQuery("PRAGMA table_info(plugins)").use { rows ->
            val columns = buildList {
              while (rows.next()) add(rows.getString("name"))
            }
            assertFalse("stability" in columns)
          }
        }
      }
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  private fun createPluginJar(
    directory: Path,
    version: String = "1.0.0",
    apiVersion: Int = PROJECT_PLUGIN_API_VERSION,
    fileName: String = "fixture-$version.jar",
    identity: Boolean = false,
  ): Path {
    val path = directory.resolve(fileName)
    val names = if (identity) "publisher=Publisher\nname=Fixture\nversion=$version\n" else ""
    JarOutputStream(path.outputStream()).use { jar ->
      jar.putNextEntry(JarEntry("META-INF/tazel-plugin.properties"))
      jar.write(
        (names + "apiVersion=$apiVersion\nimports=com.progmasoft.tazel.dsl.*\n").toByteArray()
      )
      jar.closeEntry()
      jar.putNextEntry(JarEntry("META-INF/services/${ProjectPlugin::class.java.name}"))
      jar.write("# The class of the plugin.\ncom.example.FixturePlugin\n".toByteArray())
      jar.closeEntry()
    }
    return path
  }

  private fun cachePluginJar(
    cache: Path,
    version: String = "1.0.0",
    apiVersion: Int = PROJECT_PLUGIN_API_VERSION,
    identity: Boolean = false,
  ): Path {
    val directory = Files.createDirectories(cache.resolve("Publisher/Fixture/$version"))
    val artifact = createPluginJar(directory, version, apiVersion, "Fixture-$version.jar", identity)
    Files.writeString(
      directory.resolve("plugin.json"),
      """{"publisher":"Publisher","name":"Fixture","version":"$version","sha256":"${sha256(artifact)}"}""",
    )
    return artifact
  }

  private fun resolved(
    root: Path,
    name: String,
  ) =
    ResolvedPlugin(
      "Publisher",
      name,
      "1.0.0",
      PROJECT_PLUGIN_API_VERSION,
      "a".repeat(64),
      emptyList(),
      root.resolve("$name.jar"),
    )

  private fun fixturePlugin(action: (ProjectPluginContext) -> Unit) = ProjectPlugin { context ->
    action(context)
  }

  private fun sha256(path: Path): String {
    val digest = MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(path))
    return digest.joinToString("") { "%02x".format(it) }
  }
}
