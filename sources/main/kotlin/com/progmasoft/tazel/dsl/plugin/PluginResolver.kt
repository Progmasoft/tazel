/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.requireModuleSegment
import com.progmasoft.tazel.dsl.requirePackageVersion
import java.math.BigInteger
import java.nio.file.Files
import java.nio.file.LinkOption
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.Properties
import java.util.concurrent.ConcurrentHashMap
import java.util.jar.JarFile
import kotlin.streams.toList
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put

object PluginResolver {
  private const val DESCRIPTOR = "META-INF/tazel-plugin.properties"
  private const val SERVICE = "META-INF/services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin"

  // What ViGet said about a version when Tazel fetched it, kept beside its JAR in the cache: a
  // plugin is who ViGet recorded when it was pushed, and neither its JAR nor its folder says so.
  // The descriptor only describes a plugin, so it may name none of the identity keys.
  private const val RECORD = "plugin.json"
  private const val PUBLISHER = "publisher"
  private const val NAME = "name"
  private const val VERSION = "version"
  private const val SHA256 = "sha256"
  private val IDENTITY_KEYS = listOf(PUBLISHER, NAME, VERSION)
  private val CLASS_NAME = Regex("[A-Za-z_$][A-Za-z0-9_$]*(?:\\.[A-Za-z_$][A-Za-z0-9_$]*)*")
  private val indexCache = ConcurrentHashMap<String, CachedIndex>()

  /**
   * The plugins [requests] name: a local plugin from the project directory, a hosted one from the
   * plugin cache of the project, `.tazel/plugins`, or, when the cache has no matching version, from
   * [registry], which adds it to the cache first.
   */
  fun resolve(
    root: Path,
    requests: List<PluginRequest>,
    registry: PluginRegistry? = null,
  ): List<ResolvedPlugin> {
    if (requests.isEmpty()) return emptyList()
    val cache = root.resolve(".tazel/plugins").toAbsolutePath().normalize()
    var candidates = cachedCandidates(cache)
    val missing = requests.filter { request ->
      !request.isLocal && candidates[request.coordinate].orEmpty().none { request.accepts(it) }
    }
    if (missing.isNotEmpty() && registry != null) {
      missing.forEach { fetch(cache, it, registry) }
      candidates = cachedCandidates(cache)
    }
    return requests.map { request ->
      if (request.isLocal) resolveLocal(root, request)
      else select(request, candidates[request.coordinate].orEmpty())
    }
  }

  private fun cachedCandidates(cache: Path): Map<String, List<ResolvedPlugin>> =
    if (Files.isDirectory(cache)) index(cache).groupBy(ResolvedPlugin::coordinate) else emptyMap()

  private fun PluginRequest.accepts(plugin: ResolvedPlugin) =
    version == null || plugin.version == version

  /** Downloads the version [request] takes to `<cache>/<Publisher>/<Name>/<Version>/`. */
  private fun fetch(
    cache: Path,
    request: PluginRequest,
    registry: PluginRegistry,
  ) {
    val publisher = request.publisher
    val name = request.name!!
    val versions = registry.versions(publisher, name)
    if (versions.isEmpty()) {
      throw ProjectConfigurationException(
        "plugin '${request.coordinate}' is not published on ViGet"
      )
    }
    val chosen =
      ViGetPlugins.choose(request.version, versions, ::compareVersions)
        ?: throw ProjectConfigurationException(
          request.version?.let { "plugin '${request.coordinate}' has no version $it on ViGet" }
            ?: "plugin '${request.coordinate}' has no version on ViGet for plugin API $PROJECT_PLUGIN_API_VERSION"
        )
    val target =
      cache
        .resolve(publisher)
        .resolve(name)
        .resolve(chosen.version)
        .resolve("$name-${chosen.version}.jar")
    registry.download(publisher, name, chosen, target)
    val record = buildJsonObject {
      put(PUBLISHER, publisher)
      put(NAME, name)
      put(VERSION, chosen.version)
      put(SHA256, chosen.sha256)
    }
    // Moved into place whole, so a scan never reads half a record, and the move replaces whatever
    // lies at that path rather than writing through a link.
    val partial = Files.createTempFile(target.parent, RECORD, ".partial")
    try {
      Files.writeString(partial, "$record\n")
      Files.move(
        partial,
        target.resolveSibling(RECORD),
        StandardCopyOption.ATOMIC_MOVE,
        StandardCopyOption.REPLACE_EXISTING,
      )
    } finally {
      Files.deleteIfExists(partial)
    }
  }

  internal fun clearCache() = indexCache.clear()

  private fun resolveLocal(root: Path, request: PluginRequest): ResolvedPlugin {
    val projectRoot = root.toAbsolutePath().normalize()
    val artifact = projectRoot.resolve(request.path!!).normalize()
    if (!artifact.startsWith(projectRoot)) {
      throw ProjectConfigurationException(
        "local plugin path escapes the project root: ${request.path}"
      )
    }
    if (
      !Files.isRegularFile(artifact, LinkOption.NOFOLLOW_LINKS) || Files.isSymbolicLink(artifact)
    ) {
      throw ProjectConfigurationException("local plugin JAR does not exist: ${request.path}")
    }
    val realRoot = projectRoot.toRealPath()
    val realArtifact = artifact.toRealPath()
    if (!realArtifact.startsWith(realRoot)) {
      throw ProjectConfigurationException(
        "local plugin path escapes the project root: ${request.path}"
      )
    }
    return inspect(
        realRoot,
        realArtifact,
        LOCAL_PLUGIN_PUBLISHER,
        request.path,
        LOCAL_PLUGIN_VERSION,
      )
      .copy(requestCoordinate = request.coordinate)
  }

  private fun index(root: Path): List<ResolvedPlugin> {
    val fingerprint = directoryFingerprint(root)
    val key = root.toString()
    indexCache[key]
      ?.takeIf { it.fingerprint == fingerprint }
      ?.let {
        return it.plugins
      }
    // A JAR without a record was not fetched by this Tazel and is not a candidate.
    val plugins =
      Files.walk(root)
        .use { paths ->
          paths
            .filter { path -> path.fileName.toString() == RECORD }
            .filter { path -> Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS) }
            .map { record -> pluginOfRecord(root, record) }
            .toList()
        }
        .sortedWith(compareBy(ResolvedPlugin::coordinate, ResolvedPlugin::version))
    indexCache[key] = CachedIndex(fingerprint, plugins)
    return plugins
  }

  private fun directoryFingerprint(root: Path): Long =
    Files.walk(root).use { paths ->
      paths
        .filter { Files.isRegularFile(it, LinkOption.NOFOLLOW_LINKS) }
        .mapToLong { path ->
          Files.size(path) xor Files.getLastModifiedTime(path, LinkOption.NOFOLLOW_LINKS).toMillis()
        }
        .reduce(0L) { left, right -> left xor right }
    }

  private fun pluginOfRecord(
    root: Path,
    record: Path,
  ): ResolvedPlugin {
    val invalid = { why: String ->
      ProjectConfigurationException("the plugin cache record $record $why; delete its folder")
    }
    val fields =
      try {
        Json.parseToJsonElement(Files.readString(record)) as? JsonObject
      } catch (error: IllegalArgumentException) {
        null
      } ?: throw invalid("is not a JSON object")
    // Each field is a JSON string; the record is checked against the JAR beside it.
    fun text(key: String) =
      (fields[key] as? JsonPrimitive)?.takeIf(JsonPrimitive::isString)?.content
        ?: throw invalid("has no string $key")
    val publisher = requireModuleSegment(text(PUBLISHER), "plugin publisher")
    val name = requireModuleSegment(text(NAME), "plugin name")
    val version = requirePackageVersion(text(VERSION))
    val artifact = record.resolveSibling("$name-$version.jar")
    if (!Files.isRegularFile(artifact, LinkOption.NOFOLLOW_LINKS)) throw invalid("has no JAR")
    val plugin = inspect(root, artifact, publisher, name, version)
    if (plugin.sha256 != text(SHA256)) {
      throw invalid("lists another SHA-256 than its JAR has")
    }
    return plugin
  }

  private fun inspect(
    root: Path,
    artifact: Path,
    publisher: String,
    name: String,
    version: String,
  ): ResolvedPlugin {
    val normalized = artifact.toAbsolutePath().normalize()
    if (!normalized.startsWith(root) || Files.isSymbolicLink(normalized)) {
      throw ProjectConfigurationException("plugin artifact escapes its configured root: $artifact")
    }
    val properties = Properties()
    val classes =
      JarFile(normalized.toFile(), true).use { jar ->
        val entry =
          jar.getJarEntry(DESCRIPTOR)
            ?: throw ProjectConfigurationException("plugin artifact has no $DESCRIPTOR: $artifact")
        jar.getInputStream(entry).use(properties::load)
        val service =
          jar.getJarEntry(SERVICE)
            ?: throw ProjectConfigurationException("plugin artifact has no $SERVICE: $artifact")
        jar.getInputStream(service).bufferedReader().readLines()
      }
    // The service entry names one class; lines may carry `#` comments, as for ServiceLoader.
    val named = classes.map { it.substringBefore('#').trim() }.filter(String::isNotEmpty)
    val pluginClass =
      named.singleOrNull()?.takeIf { it.matches(CLASS_NAME) }
        ?: throw ProjectConfigurationException(
          "the service entry of $artifact must name exactly one class; it names ${named.size}"
        )
    val coordinate = pluginCoordinate(publisher, name)
    val identityKeys = IDENTITY_KEYS.filter(properties::containsKey)
    if (identityKeys.isNotEmpty()) {
      throw ProjectConfigurationException(
        "the descriptor of plugin '$coordinate' names ${identityKeys.joinToString()}; " +
          "Tazel takes who a plugin is from ViGet or from localPlugin, so its JAR names none"
      )
    }
    val apiVersion =
      properties.required("apiVersion", artifact).toIntOrNull()
        ?: throw ProjectConfigurationException("plugin '$coordinate' has invalid API version")
    if (apiVersion != PROJECT_PLUGIN_API_VERSION) {
      throw ProjectConfigurationException(
        "plugin '$coordinate' requires API $apiVersion; runtime provides $PROJECT_PLUGIN_API_VERSION"
      )
    }
    val imports =
      properties
        .getProperty("imports", "")
        .split(',')
        .map(String::trim)
        .filter(String::isNotEmpty)
        .onEach(::requireSafeImport)
        .distinct()
    val digest = sha256(normalized)
    verifySidecar(normalized, digest)
    return ResolvedPlugin(
      publisher,
      name,
      version,
      apiVersion,
      digest,
      imports,
      normalized,
      metadata = PluginMetadata.of(properties),
      pluginClass = pluginClass,
    )
  }

  private fun select(
    request: PluginRequest,
    candidates: List<ResolvedPlugin>,
  ): ResolvedPlugin {
    val matching = candidates.filter { plugin ->
      request.version == null || plugin.version == request.version
    }
    if (matching.isEmpty()) {
      val requestedVersion = request.version?.let { " version $it" }.orEmpty()
      throw ProjectConfigurationException(
        "plugin '${request.coordinate}'$requestedVersion is not installed in the project plugin cache"
      )
    }
    val selected =
      matching.maxWithOrNull { left, right ->
        val versionOrder = compareVersions(left.version, right.version)
        if (versionOrder != 0) versionOrder else left.sha256.compareTo(right.sha256)
      }!!
    return if (selected.requestCoordinate == request.coordinate) selected
    else selected.copy(requestCoordinate = request.coordinate)
  }

  /** Orders by the numbers, then a version without metadata above the same one with it. */
  internal fun compareVersions(
    left: String,
    right: String,
  ): Int {
    val leftParts = numbersOf(left)
    val rightParts = numbersOf(right)
    repeat(maxOf(leftParts.size, rightParts.size)) { index ->
      val comparison =
        leftParts
          .getOrElse(index) { BigInteger.ZERO }
          .compareTo(rightParts.getOrElse(index) { BigInteger.ZERO })
      if (comparison != 0) return comparison
    }
    val leftPlain = '-' !in left
    val rightPlain = '-' !in right
    return if (leftPlain != rightPlain) leftPlain.compareTo(rightPlain) else left.compareTo(right)
  }

  private fun numbersOf(version: String) =
    version.substringBefore('-').split('.').map { it.toBigIntegerOrNull() ?: BigInteger.ZERO }

  private fun requireSafeImport(value: String) {
    if (!value.matches(Regex("[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*(?:\\.\\*)?"))) {
      throw ProjectConfigurationException("plugin descriptor contains unsafe import '$value'")
    }
  }

  private fun verifySidecar(
    artifact: Path,
    actual: String,
  ) {
    val sidecar = artifact.resolveSibling("${artifact.fileName}.sha256")
    if (!Files.exists(sidecar)) return
    if (!Files.isRegularFile(sidecar, LinkOption.NOFOLLOW_LINKS) || Files.isSymbolicLink(sidecar)) {
      throw ProjectConfigurationException("plugin checksum is not a regular file: $sidecar")
    }
    val expected = Files.readString(sidecar).trim().substringBefore(' ').lowercase()
    if (!expected.matches(Regex("[0-9a-f]{64}")) || expected != actual) {
      throw ProjectConfigurationException("plugin checksum mismatch: $artifact")
    }
  }

  internal fun sha256(path: Path): String {
    val digest = MessageDigest.getInstance("SHA-256")
    Files.newInputStream(path).use { input ->
      val buffer = ByteArray(64 * 1024)
      while (true) {
        val count = input.read(buffer)
        if (count < 0) break
        digest.update(buffer, 0, count)
      }
    }
    return digest.digest().joinToString("") { byte -> "%02x".format(byte) }
  }

  private fun Properties.required(
    name: String,
    artifact: Path,
  ): String =
    getProperty(name)?.trim()?.takeIf(String::isNotEmpty)
      ?: throw ProjectConfigurationException("plugin descriptor '$artifact' requires '$name'")

  private data class CachedIndex(
    val fingerprint: Long,
    val plugins: List<ResolvedPlugin>,
  )
}
