/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl.plugin

import java.nio.file.Path
import java.util.Properties

// A hosted publisher is an identifier, so this spelling can never name one.
internal const val LOCAL_PLUGIN_PUBLISHER = "<local>"

// A local plugin has no version of its own: its JAR names none, and its digest tells it apart.
internal const val LOCAL_PLUGIN_VERSION = "local"

/**
 * The folder below `.tazel/build` that holds the steps of the plugin [coordinate] names: the
 * coordinate itself, with `local:` and the separators of a path turned into characters a file name
 * may hold.
 */
internal fun pluginFolder(coordinate: String) =
  coordinate
    .replace(Regex("^local:"), "local.")
    .replace('/', '_')
    .replace('\\', '_')
    .replace(':', '_')

/** `Publisher.Name` of a plugin of ViGet, `local:<path>` of a local one. */
internal fun pluginCoordinate(
  publisher: String,
  name: String,
) = if (publisher == LOCAL_PLUGIN_PUBLISHER) "local:$name" else "$publisher.$name"

data class PluginRequest(
  val publisher: String,
  val name: String?,
  val version: String?,
  val path: String? = null,
) {
  val coordinate: String
    get() = if (isLocal) "local:$path" else "$publisher.$name"

  val isLocal: Boolean
    get() = publisher == LOCAL_PLUGIN_PUBLISHER
}

/**
 * What the descriptor of a plugin says about it besides its identity, for the page ViGet shows:
 * `description`, `author.0`, `author.1`, ..., `license` (an SPDX expression), `homepage` and
 * `repository`. Tazel itself does not need any of them.
 */
data class PluginMetadata(
  val description: String? = null,
  val authors: List<String> = emptyList(),
  val license: String? = null,
  val homepage: String? = null,
  val repository: String? = null,
) {
  companion object {
    fun of(properties: Properties): PluginMetadata {
      fun text(key: String) = properties.getProperty(key)?.trim()?.takeIf(String::isNotEmpty)
      val authors =
        generateSequence(0) { it + 1 }.map { text("author.$it") }.takeWhile { it != null }
      return PluginMetadata(
        text("description"),
        authors.filterNotNull().toList(),
        text("license"),
        text("homepage"),
        text("repository"),
      )
    }
  }
}

data class ResolvedPlugin(
  val publisher: String,
  val name: String,
  val version: String,
  val apiVersion: Int,
  val sha256: String,
  val imports: List<String>,
  val artifact: Path,
  val requestCoordinate: String = pluginCoordinate(publisher, name),
  val metadata: PluginMetadata = PluginMetadata(),
  /** The class the service entry of the JAR names, which implements `ProjectPlugin`. */
  val pluginClass: String = "",
) {
  val coordinate: String
    get() = pluginCoordinate(publisher, name)
}

data class PluginPlanEntry(
  val publisher: String,
  val name: String,
  val version: String,
  val apiVersion: Int,
  val sha256: String,
  val extensions: List<String>,
  val contributions: Map<String, String>,
) {
  val coordinate: String
    get() = pluginCoordinate(publisher, name)
}
