/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl.plugin

import com.progmasoft.api.tazel.dsl.plugin.BuildMode as PluginBuildMode
import com.progmasoft.api.tazel.dsl.plugin.BuildStep
import com.progmasoft.api.tazel.dsl.plugin.ExtensionHost
import com.progmasoft.api.tazel.dsl.plugin.ExtensionScope
import com.progmasoft.api.tazel.dsl.plugin.Host as PluginHost
import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.api.tazel.dsl.plugin.ProjectPluginContext
import com.progmasoft.api.tazel.dsl.plugin.ProjectSummary
import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.Host
import com.progmasoft.tazel.dsl.ProjectConfigurationException
import com.progmasoft.tazel.dsl.ProjectPlan
import com.progmasoft.tazel.dsl.ProjectRuntime
import com.progmasoft.tazel.dsl.requireModuleSegment
import com.progmasoft.tazel.dsl.requireText
import java.nio.file.Path

/** A step a plugin added to the build of its project, with the plugin that added it. */
data class PluginBuildStep(
  val owner: String,
  val name: String,
  val step: BuildStep,
)

/** The host as the API of plugins describes it. */
internal fun Host.toPluginHost() =
  PluginHost(
    com.progmasoft.api.tazel.dsl.plugin.OperatingSystem.valueOf(os.name),
    com.progmasoft.api.tazel.dsl.plugin.Architecture.valueOf(architecture.name),
  )

internal fun BuildMode.toPluginBuildMode() = PluginBuildMode.valueOf(name)

/** The project as the API of plugins describes it. */
fun ProjectPlan.toPluginSummary(): ProjectSummary =
  object : ProjectSummary {
    override val name = identity?.name
    override val version = identity?.version
    override val buildMode = compiler.buildMode.toPluginBuildMode()
    override val outputDirectory = this@toPluginSummary.outputDirectory
  }

/**
 * The plugins of one project: those resolved for it, the ones its project file declared, and what
 * each registered, contributed, added to the build and asked to run when the plan is finished. Each
 * [com.progmasoft.tazel.dsl.ProjectContext] has its own, so plugins of two projects never see each
 * other.
 */
class PluginState internal constructor() {
  private enum class Phase {
    EMPTY,
    ACTIVATING,
    CONFIGURING,
    FINALIZING,
    FINALIZED,
  }

  private var phase = Phase.EMPTY
  private var resolved = emptyList<ResolvedPlugin>()
  private var projectRoot: Path? = null
  private var host: PluginHost? = null
  private val declared = linkedSetOf<String>()
  private val extensions = linkedMapOf<String, String>()
  private val contributions = linkedMapOf<String, LinkedHashMap<String, String>>()
  private val finalizers = mutableListOf<Pair<String, (ProjectSummary) -> Unit>>()
  private val steps = mutableListOf<PluginBuildStep>()

  /** The steps the plugins added to the build, in the order they were added. */
  val buildSteps: List<PluginBuildStep>
    get() = steps.toList()

  /**
   * Activates [plugins]; [implementations] holds the instance of each, in the same order. The
   * identity of a plugin is the one of its verified descriptor.
   */
  internal fun activate(
    plugins: List<ResolvedPlugin>,
    implementations: List<ProjectPlugin>,
    projectRoot: Path,
    host: Host,
  ) {
    checkPhase(Phase.EMPTY, "plugin runtime is already active")
    require(plugins.size == implementations.size) { "every plugin needs its instance" }
    phase = Phase.ACTIVATING
    resolved = plugins
    this.projectRoot = projectRoot
    this.host = host.toPluginHost()
    plugins.zip(implementations).forEach { (plugin, implementation) ->
      val context = Context(plugin.coordinate, projectRoot, this.host!!)
      try {
        implementation.apply(context)
      } catch (error: ProjectConfigurationException) {
        throw error
      } catch (error: Exception) {
        throw ProjectConfigurationException(
          "plugin '${plugin.coordinate}' failed during activation: ${error.message ?: error.javaClass.name}"
        )
      }
    }
    phase = Phase.CONFIGURING
  }

  internal fun declare(request: PluginRequest) {
    checkPhase(Phase.CONFIGURING, "plugins may be declared only while the project is configuring")
    val plugin =
      resolved.singleOrNull { it.requestCoordinate == request.coordinate }
        ?: throw ProjectConfigurationException(
          "plugin '${request.coordinate}' was not resolved by the host"
        )
    if (request.version != null && request.version != plugin.version) {
      throw ProjectConfigurationException(
        "plugin '${request.coordinate}' resolved to an unexpected version"
      )
    }
    if (!declared.add(request.coordinate)) {
      throw ProjectConfigurationException(
        "plugin '${request.coordinate}' is declared more than once"
      )
    }
  }

  internal fun requireExtension(
    name: String,
    publisher: String,
    plugin: String,
  ) {
    val expectedOwner = "$publisher.$plugin"
    val owner =
      extensions[name]
        ?: throw ProjectConfigurationException("plugin extension '$name' is not registered")
    if (owner != expectedOwner) {
      throw ProjectConfigurationException(
        "plugin extension '$name' belongs to '$owner', not '$expectedOwner'"
      )
    }
  }

  /** The scope the block of [extension] adds its work through. */
  internal fun extensionScope(extension: String): ExtensionScope {
    checkPhase(
      Phase.CONFIGURING,
      "plugin extension '$extension' can be used only while the project file runs",
    )
    val owner =
      extensions[extension]
        ?: throw ProjectConfigurationException(
          "plugin extension '$extension' is not registered by any plugin of the project"
        )
    val plugin = resolved.first { it.coordinate == owner }
    if (plugin.requestCoordinate !in declared) {
      throw ProjectConfigurationException(
        "plugin extension '$extension' is used before its plugin '$owner' is declared in plugins { }"
      )
    }
    return Scope(owner, projectRoot!!, host!!)
  }

  internal fun finish(plan: ProjectPlan): ProjectPlan {
    if (phase == Phase.EMPTY) return plan
    checkPhase(Phase.CONFIGURING, "plugin runtime cannot finalize from phase $phase")
    val missing = resolved.map(ResolvedPlugin::requestCoordinate).filterNot(declared::contains)
    if (missing.isNotEmpty()) {
      throw ProjectConfigurationException(
        "resolved plugins were not declared by the script: ${missing.joinToString()}"
      )
    }
    phase = Phase.FINALIZING
    val summary = plan.toPluginSummary()
    finalizers.forEach { (owner, action) ->
      try {
        action(summary)
      } catch (error: Exception) {
        throw ProjectConfigurationException(
          "plugin '$owner' failed while finalizing the project: ${error.message ?: error.javaClass.name}"
        )
      }
    }
    val entries = resolved.map { plugin ->
      PluginPlanEntry(
        plugin.publisher,
        plugin.name,
        plugin.version,
        plugin.apiVersion,
        plugin.sha256,
        extensions.filterValues { it == plugin.coordinate }.keys.sorted(),
        contributions[plugin.coordinate]?.toSortedMap().orEmpty(),
      )
    }
    phase = Phase.FINALIZED
    return plan.copy(plugins = entries)
  }

  private fun checkPhase(
    expected: Phase,
    message: String,
  ) {
    if (phase != expected) throw ProjectConfigurationException(message)
  }

  private inner class Scope(
    private val owner: String,
    override val projectRoot: Path,
    override val host: PluginHost,
  ) : ExtensionScope {
    override fun buildStep(
      name: String,
      step: BuildStep,
    ) {
      val normalized = requireModuleSegment(name, "build step name")
      if (steps.any { it.owner == owner && it.name == normalized }) {
        throw ProjectConfigurationException(
          "plugin '$owner' added build step '$normalized' more than once"
        )
      }
      steps += PluginBuildStep(owner, normalized, step)
    }
  }

  private inner class Context(
    private val owner: String,
    override val projectRoot: Path,
    override val host: PluginHost,
  ) : ProjectPluginContext {
    override fun registerExtension(name: String) {
      val normalized = requireModuleSegment(name, "plugin extension")
      val previous = extensions.putIfAbsent(normalized, owner)
      if (previous != null) {
        throw ProjectConfigurationException(
          "plugin extension '$normalized' is registered by both '$previous' and '$owner'"
        )
      }
    }

    override fun contribute(
      key: String,
      value: String,
    ) {
      val normalizedKey = requireText(key, "plugin contribution key")
      if (!normalizedKey.matches(Regex("[A-Za-z_][A-Za-z0-9_.-]{0,127}"))) {
        throw ProjectConfigurationException("invalid plugin contribution key '$normalizedKey'")
      }
      val normalizedValue = requireText(value, "plugin contribution value")
      val values = contributions.getOrPut(owner, ::linkedMapOf)
      if (values.putIfAbsent(normalizedKey, normalizedValue) != null) {
        throw ProjectConfigurationException(
          "plugin '$owner' contributed '$normalizedKey' more than once"
        )
      }
    }

    override fun onFinalize(action: (ProjectSummary) -> Unit) {
      finalizers += owner to action
    }
  }
}

/**
 * The plugins of the project being evaluated on this thread. A project file declares its plugins
 * through this object, and the extensions of plugins check their registration with
 * [requireExtension].
 */
object PluginRuntime {
  private val state
    get() = ProjectRuntime.current().plugins

  fun declare(request: PluginRequest) = state.declare(request)

  fun requireExtension(
    name: String,
    publisher: String,
    plugin: String,
  ) = state.requireExtension(name, publisher, plugin)

  internal fun activate(
    plugins: List<ResolvedPlugin>,
    implementations: List<ProjectPlugin>,
    projectRoot: Path,
    host: Host,
  ) = state.activate(plugins, implementations, projectRoot, host)
}

/**
 * The [ExtensionHost] of the API of plugins: [com.progmasoft.api.tazel.dsl.plugin.PluginExtensions]
 * finds it as a service and reaches the project being evaluated on its thread through it.
 */
class TazelExtensionHost : ExtensionHost {
  override fun require(extension: String): ExtensionScope =
    ProjectRuntime.current().plugins.extensionScope(extension)
}
