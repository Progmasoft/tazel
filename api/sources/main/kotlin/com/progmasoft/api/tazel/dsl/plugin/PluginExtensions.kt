/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.api.tazel.dsl.plugin

import java.nio.file.Path
import java.util.ServiceLoader

/**
 * The extensions of the project whose file is running.
 *
 * The DSL function of a plugin calls [require] with an extension its plugin registered and adds the
 * work of its block through the [ExtensionScope] it receives:
 * ```
 * fun cmake(block: CMakeScope.() -> Unit) {
 *   val settings = CMakeScope().apply(block)
 *   PluginExtensions.require("cmake").buildStep("configure") { context -> ... }
 * }
 * ```
 */
object PluginExtensions {
  private val host: ExtensionHost by lazy {
    ServiceLoader.load(ExtensionHost::class.java, ExtensionHost::class.java.classLoader)
      .firstOrNull()
      ?: throw IllegalStateException("plugin extensions are available only inside Tazel")
  }

  /**
   * The scope of [extension] in the project whose file is running. It fails when no plugin of the
   * project registered [extension], or when the project file did not declare that plugin.
   */
  fun require(extension: String): ExtensionScope = host.require(extension)
}

/** What the block of an extension can add to its project. */
interface ExtensionScope {
  val projectRoot: Path
  val host: Host

  /**
   * Adds [step] to the build of the project. `tazel build` and `tazel run` run the steps of a
   * project in the order they were added, before its Tala targets compile; a failed step stops the
   * build. [name] names the step in the output and is one identifier, unique within the plugin.
   */
  fun buildStep(
    name: String,
    step: BuildStep,
  )
}

/** Provided by Tazel, which [PluginExtensions] finds as a service. Plugins do not implement it. */
interface ExtensionHost {
  fun require(extension: String): ExtensionScope
}
