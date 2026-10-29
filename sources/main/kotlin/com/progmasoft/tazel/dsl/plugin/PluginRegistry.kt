/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.requirePackageVersion
import java.io.IOException
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption.ATOMIC_MOVE
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import java.time.Duration
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** One published version of a plugin, as the index of its registry lists it. */
data class RegistryVersion(
  val version: String,
  val sha256: String,
  val apiVersion: Int,
  val yanked: Boolean,
)

/** Where plugins that are not in the plugin cache of a project are fetched from. */
interface PluginRegistry {
  /** The versions of [publisher].[name], or an empty list when the registry has no such plugin. */
  fun versions(
    publisher: String,
    name: String,
  ): List<RegistryVersion>

  /**
   * Writes the JAR of [version] to [target], after checking it against [RegistryVersion.sha256].
   */
  fun download(
    publisher: String,
    name: String,
    version: RegistryVersion,
    target: Path,
  )
}

/**
 * The Tazel catalog of ViGet: `index.json` of a plugin at
 * `<base>/dslplugins/tazel/<Publisher>/<Name>/index.json`, and its JARs at
 * `<base>/dslplugins/tazel/<Publisher>/<Name>/<Version>/<Name>-<Version>.jar`.
 *
 * `TAZEL_VIGET_URL` replaces the base address, https://viget.progmasoft.com, for a mirror or a test
 * server.
 */
class ViGetPlugins(
  private val base: URI = URI.create(System.getenv("TAZEL_VIGET_URL") ?: DEFAULT_BASE),
  private val http: HttpClient =
    HttpClient.newBuilder()
      .connectTimeout(Duration.ofSeconds(15))
      .followRedirects(HttpClient.Redirect.NORMAL)
      .build(),
) : PluginRegistry {
  override fun versions(
    publisher: String,
    name: String,
  ): List<RegistryVersion> {
    val uri = pluginUri(publisher, name, "index.json")
    val response = send(uri, HttpResponse.BodyHandlers.ofString())
    if (response.statusCode() == 404) return emptyList()
    if (response.statusCode() != 200) {
      throw ProjectConfigurationException("ViGet answered $uri with HTTP ${response.statusCode()}")
    }
    return parseIndex(publisher, name, response.body(), uri)
  }

  override fun download(
    publisher: String,
    name: String,
    version: RegistryVersion,
    target: Path,
  ) {
    val uri = pluginUri(publisher, name, "${version.version}/$name-${version.version}.jar")
    Files.createDirectories(target.parent)
    val partial = Files.createTempFile(target.parent, ".download-", ".jar")
    try {
      val response = send(uri, HttpResponse.BodyHandlers.ofFile(partial, *WRITE_OPTIONS))
      if (response.statusCode() != 200) {
        throw ProjectConfigurationException(
          "ViGet answered $uri with HTTP ${response.statusCode()}"
        )
      }
      val actual = PluginResolver.sha256(partial)
      if (actual != version.sha256) {
        throw ProjectConfigurationException(
          "plugin '$publisher.$name' ${version.version} from ViGet has SHA-256 $actual; its index lists ${version.sha256}"
        )
      }
      Files.move(partial, target, REPLACE_EXISTING, ATOMIC_MOVE)
      Files.writeString(target.resolveSibling("${target.fileName}.sha256"), "${version.sha256}\n")
    } finally {
      Files.deleteIfExists(partial)
    }
  }

  private fun pluginUri(
    publisher: String,
    name: String,
    rest: String,
  ): URI = base.resolve("/dslplugins/tazel/$publisher/$name/$rest")

  private fun <T> send(
    uri: URI,
    handler: HttpResponse.BodyHandler<T>,
  ): HttpResponse<T> =
    try {
      http.send(
        HttpRequest.newBuilder(uri)
          .timeout(Duration.ofSeconds(60))
          .header("User-Agent", "Tazel")
          .GET()
          .build(),
        handler,
      )
    } catch (error: IOException) {
      throw ProjectConfigurationException("ViGet could not be reached at $uri: ${error.message}")
    } catch (_: InterruptedException) {
      Thread.currentThread().interrupt()
      throw ProjectConfigurationException("the download from $uri was interrupted")
    }

  companion object {
    const val DEFAULT_BASE = "https://viget.progmasoft.com"

    private val WRITE_OPTIONS =
      arrayOf(
        java.nio.file.StandardOpenOption.WRITE,
        java.nio.file.StandardOpenOption.TRUNCATE_EXISTING,
      )

    /**
     * Reads `index.json`: `{"publisher", "name", "versions":
     * [{"version", "sha256", "apiVersion", "yanked"}]}`.
     */
    internal fun parseIndex(
      publisher: String,
      name: String,
      text: String,
      source: URI,
    ): List<RegistryVersion> {
      val invalid = { why: String ->
        ProjectConfigurationException("the ViGet index $source is not valid: $why")
      }
      val root =
        try {
          Json.parseToJsonElement(text) as? JsonObject ?: throw invalid("it is not an object")
        } catch (error: IllegalArgumentException) {
          throw invalid(error.message ?: "it is not JSON")
        }
      if (root["publisher"]?.jsonPrimitive?.content != publisher) throw invalid("another publisher")
      if (root["name"]?.jsonPrimitive?.content != name) throw invalid("another plugin")
      val versions = root["versions"]?.jsonArray ?: throw invalid("it lists no versions")
      return versions.map { element ->
        val entry = element.jsonObject
        val version =
          entry["version"]?.jsonPrimitive?.content?.let(::requirePackageVersion)
            ?: throw invalid("a version has no number")
        val sha256 =
          entry["sha256"]?.jsonPrimitive?.content?.lowercase()?.takeIf {
            it.matches(Regex("[0-9a-f]{64}"))
          } ?: throw invalid("version $version has no SHA-256")
        RegistryVersion(
          version,
          sha256,
          entry["apiVersion"]?.jsonPrimitive?.intOrNull
            ?: throw invalid("version $version has no API version"),
          entry["yanked"]?.jsonPrimitive?.booleanOrNull ?: false,
        )
      }
    }

    /**
     * The version a request takes: the one it names, or the newest that is not yanked and was built
     * for the API of this Tazel.
     */
    internal fun choose(
      requested: String?,
      versions: List<RegistryVersion>,
      compare: (String, String) -> Int,
    ): RegistryVersion? =
      if (requested != null) versions.firstOrNull { it.version == requested }
      else
        versions
          .filter { !it.yanked && it.apiVersion == PROJECT_PLUGIN_API_VERSION }
          .maxWithOrNull { left, right -> compare(left.version, right.version) }
  }
}
