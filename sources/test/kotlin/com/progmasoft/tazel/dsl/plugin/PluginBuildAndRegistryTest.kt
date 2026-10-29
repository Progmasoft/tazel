/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.api.tazel.dsl.plugin.PluginExtensions
import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.ProjectRuntime
import com.progmasoft.tazel.dsl.detectHost
import com.sun.net.httpserver.HttpServer
import java.io.ByteArrayOutputStream
import java.net.InetSocketAddress
import java.net.URI
import java.nio.file.Files
import java.nio.file.Path
import java.security.MessageDigest
import java.util.Properties
import java.util.jar.JarEntry
import java.util.jar.JarOutputStream
import kotlin.test.AfterTest
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class PluginBuildAndRegistryTest {
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
  fun buildStepsAreAddedThroughTheExtensionScope() {
    activate("native")
    PluginRuntime.declare(PluginRequest("Publisher", "Native", "1.0.0"))
    val scope = TazelExtensionHost().require("native")
    scope.buildStep("configure") {}
    scope.buildStep("compile") {}

    val steps = ProjectRuntime.current().plugins.buildSteps
    assertEquals(listOf("configure", "compile"), steps.map(PluginBuildStep::name))
    assertTrue(steps.all { it.owner == "Publisher.Native" })
    // A project whose plugins build it needs no Tala target.
    assertEquals("Publisher.Native", ProjectRuntime.build().plugins.single().coordinate)
  }

  @Test
  fun theApiReachesTazelThroughItsService() {
    activate("native")
    PluginRuntime.declare(PluginRequest("Publisher", "Native", "1.0.0"))
    PluginExtensions.require("native").buildStep("compile") {}
    assertEquals(1, ProjectRuntime.current().plugins.buildSteps.size)
  }

  @Test
  fun theExtensionScopeRefusesWhatIsNotTheProjects() {
    activate("native")
    // The plugin is resolved and active, but the project file has not declared it.
    assertFailsWith<ProjectConfigurationException> { TazelExtensionHost().require("native") }
    PluginRuntime.declare(PluginRequest("Publisher", "Native", "1.0.0"))
    assertFailsWith<ProjectConfigurationException> { TazelExtensionHost().require("cmake") }
    val scope = TazelExtensionHost().require("native")
    scope.buildStep("compile") {}
    assertFailsWith<ProjectConfigurationException> { scope.buildStep("compile") {} }
    assertFailsWith<ProjectConfigurationException> { scope.buildStep("../escape") {} }
  }

  @Test
  fun downloadsTheNewestUsableVersionFromTheRegistry() {
    withRegistry { root, server, jars ->
      jars["1.0.0"] = pluginJar("1.0.0")
      jars["1.1.0"] = pluginJar("1.1.0")
      jars["2.0.0"] = pluginJar("2.0.0", apiVersion = 99)
      server.index =
        index(
          version("1.0.0", jars.getValue("1.0.0")),
          version("1.1.0", jars.getValue("1.1.0"), yanked = true),
          version("2.0.0", jars.getValue("2.0.0"), apiVersion = 99),
        )

      val resolved =
        PluginResolver.resolve(
            root,
            listOf(PluginRequest("Publisher", "Fixture", null)),
            server.registry,
          )
          .single()

      assertEquals("1.0.0", resolved.version)
      val cached = root.resolve(".tazel/plugins/Publisher/Fixture/1.0.0/Fixture-1.0.0.jar")
      assertTrue(Files.isRegularFile(cached))
      assertEquals(
        sha256(jars.getValue("1.0.0")),
        Files.readString(cached.resolveSibling("Fixture-1.0.0.jar.sha256")).trim(),
      )
      assertEquals("A fixture plugin.", resolved.metadata.description)
      assertEquals(listOf("First Author", "Second Author"), resolved.metadata.authors)
      assertEquals("MIT", resolved.metadata.license)

      // The cache serves the next resolution; the registry is not asked again.
      server.index = null
      PluginResolver.clearCache()
      val again =
        PluginResolver.resolve(
          root,
          listOf(PluginRequest("Publisher", "Fixture", "1.0.0")),
          server.registry,
        )
      assertEquals("1.0.0", again.single().version)
    }
  }

  @Test
  fun aNamedVersionIsTakenEvenWhenYanked() {
    withRegistry { root, server, jars ->
      jars["1.1.0"] = pluginJar("1.1.0")
      server.index = index(version("1.1.0", jars.getValue("1.1.0"), yanked = true))
      val resolved =
        PluginResolver.resolve(
          root,
          listOf(PluginRequest("Publisher", "Fixture", "1.1.0")),
          server.registry,
        )
      assertEquals("1.1.0", resolved.single().version)
    }
  }

  @Test
  fun refusesADownloadWhoseDigestDiffers() {
    withRegistry { root, server, jars ->
      jars["1.0.0"] = pluginJar("1.0.0")
      server.index = index(version("1.0.0", pluginJar("1.0.0", description = "Another build.")))
      val error =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(
            root,
            listOf(PluginRequest("Publisher", "Fixture", null)),
            server.registry,
          )
        }
      assertTrue("SHA-256" in error.message.orEmpty(), error.message)
      assertFalse(
        Files.exists(root.resolve(".tazel/plugins/Publisher/Fixture/1.0.0/Fixture-1.0.0.jar"))
      )
    }
  }

  @Test
  fun aServiceEntryNamesExactlyOneClass() {
    val root = Files.createTempDirectory("tazel-plugin-service-")
    try {
      val jar = root.resolve("two.jar")
      JarOutputStream(Files.newOutputStream(jar)).use { output ->
        output.putNextEntry(JarEntry("META-INF/tazel-plugin.properties"))
        output.write(
          "publisher=Publisher\nname=Two\nversion=1.0.0\napiVersion=$PROJECT_PLUGIN_API_VERSION\n"
            .toByteArray()
        )
        output.closeEntry()
        output.putNextEntry(JarEntry("META-INF/services/${ProjectPlugin::class.java.name}"))
        output.write("com.example.First\ncom.example.Second\n".toByteArray())
        output.closeEntry()
      }
      val error =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(root, listOf(localPluginRequest("two.jar")))
        }
      assertTrue(
        "must name exactly one class; it names 2" in error.message.orEmpty(),
        error.message,
      )
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun saysWhenAPluginIsNotPublished() {
    withRegistry { root, server, _ ->
      server.index = null
      val error =
        assertFailsWith<ProjectConfigurationException> {
          PluginResolver.resolve(
            root,
            listOf(PluginRequest("Publisher", "Fixture", null)),
            server.registry,
          )
        }
      assertEquals("plugin 'Publisher.Fixture' is not published on ViGet", error.message)
    }
  }

  @Test
  fun refusesAnIndexOfAnotherPlugin() {
    assertFailsWith<ProjectConfigurationException> {
      ViGetPlugins.parseIndex(
        "Publisher",
        "Fixture",
        """{"publisher":"Publisher","name":"Other","versions":[]}""",
        URI.create("http://localhost/index.json"),
      )
    }
  }

  @Test
  fun readsTheMetadataOfTheDescriptor() {
    val properties = Properties()
    properties.load(
      """
      description=Builds C.
      author.0=One
      author.1=Two
      author.3=Skipped after the gap
      license=MPL-2.0
      homepage=https://example.com
      """
        .trimIndent()
        .reader()
    )
    val metadata = PluginMetadata.of(properties)
    assertEquals("Builds C.", metadata.description)
    assertEquals(listOf("One", "Two"), metadata.authors)
    assertEquals("MPL-2.0", metadata.license)
    assertEquals("https://example.com", metadata.homepage)
    assertEquals(null, metadata.repository)
  }

  private fun activate(extension: String) {
    val plugin =
      ResolvedPlugin(
        "Publisher",
        "Native",
        "1.0.0",
        PROJECT_PLUGIN_API_VERSION,
        "a".repeat(64),
        emptyList(),
        Path.of("Native.jar"),
      )
    val implementation = ProjectPlugin { context -> context.registerExtension(extension) }
    PluginRuntime.activate(listOf(plugin), listOf(implementation), Path.of("."), detectHost())
  }

  /** A registry on a local port that serves `index.json` and the JARs of Publisher.Fixture. */
  private class Registry(port: Int) {
    var index: String? = null
    val registry = ViGetPlugins(URI.create("http://127.0.0.1:$port"))
  }

  private fun withRegistry(block: (Path, Registry, MutableMap<String, ByteArray>) -> Unit) {
    val root = Files.createTempDirectory("tazel-plugin-registry-")
    val jars = linkedMapOf<String, ByteArray>()
    val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
    val registry = Registry(server.address.port)
    server.createContext("/dslplugins/tazel/Publisher/Fixture/") { exchange ->
      val rest = exchange.requestURI.path.removePrefix("/dslplugins/tazel/Publisher/Fixture/")
      val body =
        if (rest == "index.json") registry.index?.toByteArray()
        else
          jars[rest.substringBefore('/')]?.takeIf {
            rest == "${rest.substringBefore('/')}/Fixture-${rest.substringBefore('/')}.jar"
          }
      if (body == null) {
        exchange.sendResponseHeaders(404, -1)
      } else {
        exchange.sendResponseHeaders(200, body.size.toLong())
        exchange.responseBody.use { it.write(body) }
      }
      exchange.close()
    }
    server.start()
    try {
      block(root, registry, jars)
    } finally {
      server.stop(0)
      root.toFile().deleteRecursively()
    }
  }

  private fun index(vararg versions: String) =
    """{"publisher":"Publisher","name":"Fixture","versions":[${versions.joinToString(",")}]}"""

  private fun version(
    number: String,
    jar: ByteArray,
    apiVersion: Int = PROJECT_PLUGIN_API_VERSION,
    yanked: Boolean = false,
  ) =
    """{"version":"$number","sha256":"${sha256(jar)}","apiVersion":$apiVersion,"yanked":$yanked}"""

  private fun pluginJar(
    version: String,
    apiVersion: Int = PROJECT_PLUGIN_API_VERSION,
    description: String = "A fixture plugin.",
  ): ByteArray {
    val bytes = ByteArrayOutputStream()
    JarOutputStream(bytes).use { jar ->
      jar.putNextEntry(JarEntry("META-INF/tazel-plugin.properties"))
      jar.write(
        """
        publisher=Publisher
        name=Fixture
        version=$version
        apiVersion=$apiVersion
        description=$description
        author.0=First Author
        author.1=Second Author
        license=MIT
        """
          .trimIndent()
          .toByteArray()
      )
      jar.closeEntry()
      jar.putNextEntry(JarEntry("META-INF/services/${ProjectPlugin::class.java.name}"))
      jar.write("com.example.FixturePlugin\n".toByteArray())
      jar.closeEntry()
    }
    return bytes.toByteArray()
  }

  private fun sha256(bytes: ByteArray) =
    MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
}
