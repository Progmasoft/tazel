/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.api.tazel.dsl.plugin

import java.nio.file.Path

/** The version of this API; a plugin built for another one is not loaded. */
const val PROJECT_PLUGIN_API_VERSION = 2

/**
 * A plugin of the Tazel project file.
 *
 * Tazel finds a plugin as the [java.util.ServiceLoader] service of its JAR, checks it against the
 * descriptor of the JAR, `META-INF/tazel-plugin.properties`, and calls [apply] once for every
 * project that declares it, before the project file runs. The project file then uses the DSL the
 * plugin defines, such as a `cmake { }` block; see [PluginExtensions].
 *
 * A plugin runs with the same JVM permissions as Tazel. The integrity and the compatibility of its
 * JAR are checked before it is activated; its code is not sandboxed.
 */
interface ProjectPlugin {
  /** The Progmasoft Account the plugin is published under on ViGet. */
  val publisher: String
  val name: String
  val version: String
  val apiVersion: Int
    get() = PROJECT_PLUGIN_API_VERSION

  fun apply(context: ProjectPluginContext)
}

/** What a plugin can do while it is activated for one project. */
interface ProjectPluginContext {
  val projectRoot: Path

  /** The machine Tazel runs on. */
  val host: Host

  /**
   * Claims [name] as an extension of this plugin: a block of the project file that the DSL of the
   * plugin defines, such as `cmake`. Two plugins of one project cannot claim the same name.
   */
  fun registerExtension(name: String)

  /** Records [value] under [key] in the plan of the project; each key is written once. */
  fun contribute(
    key: String,
    value: String,
  )

  /** Runs [action] once the project file has run and the plan of the project is finished. */
  fun onFinalize(action: (ProjectSummary) -> Unit)
}

/** The project as a plugin sees it once its project file has run. */
interface ProjectSummary {
  val name: String?
  val version: String?
  val buildMode: BuildMode

  /** The directory the build mode writes artifacts to, relative to the project root. */
  val outputDirectory: String
}
