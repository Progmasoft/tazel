/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.tazel.driver.blocking
import com.progmasoft.tazel.driver.copyLimited
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.requirePackageVersion
import io.ktor.client.HttpClient
import io.ktor.client.engine.cio.CIO
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.timeout
import io.ktor.client.request.header
import io.ktor.client.request.prepareGet
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsChannel
import io.ktor.http.HttpHeaders
import io.ktor.http.HttpStatusCode
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.URI
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption.ATOMIC_MOVE
import java.nio.file.StandardCopyOption.REPLACE_EXISTING
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
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
  private val http: HttpClient = followingClient,
) : PluginRegistry {
  override fun versions(
    publisher: String,
    name: String,
  ): List<RegistryVersion> {
    val uri = pluginUri(publisher, name, "index.json")
    val (status, body) =
      send(uri) { response ->
        response.status to
          if (response.status == HttpStatusCode.OK) {
            ByteArrayOutputStream()
              .also { response.bodyAsChannel().copyLimited(it, MAXIMUM_INDEX_BYTES) }
              .toString(Charsets.UTF_8)
          } else ""
      }
    if (status == HttpStatusCode.NotFound) return emptyList()
    if (status != HttpStatusCode.OK) {
      throw ProjectConfigurationException("ViGet answered $uri with HTTP ${status.value}")
    }
    return parseIndex(publisher, name, body, uri)
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
      val status =
        send(uri) { response ->
          if (response.status == HttpStatusCode.OK) {
            val body = response.bodyAsChannel()
            withContext(Dispatchers.IO) {
              Files.newOutputStream(partial).use { body.copyLimited(it, MAXIMUM_JAR_BYTES) }
            }
          }
          response.status
        }
      if (status != HttpStatusCode.OK) {
        throw ProjectConfigurationException("ViGet answered $uri with HTTP ${status.value}")
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
    read: suspend (HttpResponse) -> T,
  ): T =
    try {
      blocking {
        http
          .prepareGet(uri.toString()) {
            timeout {
              connectTimeoutMillis = 15_000
              socketTimeoutMillis = 60_000
            }
            header(HttpHeaders.UserAgent, "Tazel")
          }
          .execute(read)
      }
    } catch (error: IOException) {
      throw ProjectConfigurationException("ViGet could not be reached at $uri: ${error.message}")
    }

  companion object {
    const val DEFAULT_BASE = "https://viget.progmasoft.com"

    /** The largest `index.json` and plugin JAR Tazel takes; ViGet takes JARs of up to 64 MiB. */
    private const val MAXIMUM_INDEX_BYTES = 4L shl 20
    private const val MAXIMUM_JAR_BYTES = 64L shl 20

    /** Follows the redirects of ViGet, which may move a file, but never from https to http. */
    private val followingClient: HttpClient by lazy {
      HttpClient(CIO) {
        expectSuccess = false
        followRedirects = true
        install(HttpTimeout)
      }
    }

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
