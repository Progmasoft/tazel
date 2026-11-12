/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.gradle

import java.net.URI
import java.util.Properties
import javax.inject.Inject
import org.gradle.api.Plugin
import org.gradle.api.Project
import org.gradle.api.plugins.JavaPluginExtension
import org.gradle.api.provider.ListProperty
import org.gradle.api.provider.Property

/**
 * `id("com.progmasoft.tazel.plugin")`: builds a plugin of the Tazel project file.
 *
 * It adds the repository of the Tazel API, https://maven.progmasoft.com/tazel, the `tazelApi()`
 * dependency and the `tazelPlugin { }` block, from which it writes the descriptor and the service
 * entry of the plugin into its JAR. The version of this Gradle plugin is the version of Tazel, and
 * `tazelApi()` takes the API of that version.
 */
class TazelPluginPlugin : Plugin<Project> {
  override fun apply(project: Project) {
    project.pluginManager.apply("java-library")
    project.repositories.maven { repository ->
      repository.name = "ProgmasoftTazel"
      repository.url = URI.create(REPOSITORY)
      // Only the API comes from it; everything else keeps its own repositories.
      repository.content { it.includeGroup(GROUP) }
    }
    val settings = project.extensions.create("tazelPlugin", TazelPluginExtension::class.java)
    project.extensions.create("tazelApi", TazelApiDependency::class.java, project)

    val descriptor =
      project.tasks.register("tazelPluginDescriptor", TazelPluginDescriptorTask::class.java) { task
        ->
        task.group = "build"
        task.description = "Writes the descriptor and the service entry of the Tazel plugin."
        task.pluginGroup.set(settings.group)
        task.pluginName.set(settings.name)
        task.pluginVersion.set(settings.version)
        task.pluginClass.set(settings.pluginClass)
        task.imports.set(settings.imports)
        task.description_.set(settings.description)
        task.authors.set(settings.authors)
        task.license.set(settings.license)
        task.homepage.set(settings.homepage)
        task.repository.set(settings.repository)
        task.apiVersion.set(API_VERSION)
        task.outputDirectory.set(project.layout.buildDirectory.dir("generated/tazel-plugin"))
      }
    project.extensions.getByType(JavaPluginExtension::class.java).sourceSets.named("main") {
      it.resources.srcDir(descriptor)
    }
  }

  companion object {
    const val REPOSITORY = "https://maven.progmasoft.com/tazel"
    const val GROUP = "com.progmasoft.tazel"

    /** The plugin API of the Tazel this Gradle plugin belongs to. */
    const val API_VERSION = 1

    /** The version of this Gradle plugin, which is the version of Tazel and of its API. */
    val VERSION: String by lazy {
      val properties = Properties()
      TazelPluginPlugin::class.java.getResourceAsStream("/tazel-gradle-plugin.properties").use {
        stream ->
        requireNotNull(stream) { "the Tazel Gradle plugin has no version resource" }
        properties.load(stream)
      }
      properties.getProperty("version")
    }
  }
}

/**
 * The `tazelPlugin { }` block: who the plugin is, its class, and what Tazel Portal shows about it.
 * `tazel portal plugins push plugin.jar` publishes the JAR under its group, name and version.
 */
abstract class TazelPluginExtension {
  /**
   * The namespace of the plugin, such as `com.example`; Tazel Portal checks it by the Sonatype
   * rules.
   */
  abstract val group: Property<String>

  /** The name of the plugin within its group, such as `cmake`. */
  abstract val name: Property<String>

  /** A Semantic Version, such as `0.1.0`. */
  abstract val version: Property<String>

  /** The class that implements `ProjectPlugin`. */
  abstract val pluginClass: Property<String>

  /** Packages or classes the project file imports by default, such as `com.example.cmake.*`. */
  abstract val imports: ListProperty<String>

  abstract val description: Property<String>
  abstract val authors: ListProperty<String>

  /** An SPDX license expression, such as `MIT`. */
  abstract val license: Property<String>

  abstract val homepage: Property<String>
  abstract val repository: Property<String>
}

/**
 * `tazelApi()` in the `dependencies` block: the API of Tazel, which the plugin compiles against and
 * its tests use. It is not bundled; Tazel provides it at run time.
 */
abstract class TazelApiDependency @Inject constructor(private val project: Project) {
  operator fun invoke() {
    val coordinate = "${TazelPluginPlugin.GROUP}:tazel-api:${TazelPluginPlugin.VERSION}"
    project.dependencies.add("compileOnly", coordinate)
    project.dependencies.add("testImplementation", coordinate)
  }
}
