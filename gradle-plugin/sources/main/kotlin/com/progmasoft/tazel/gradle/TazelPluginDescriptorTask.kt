/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.gradle

import java.io.StringWriter
import java.util.Properties
import org.gradle.api.DefaultTask
import org.gradle.api.GradleException
import org.gradle.api.file.DirectoryProperty
import org.gradle.api.provider.ListProperty
import org.gradle.api.provider.Property
import org.gradle.api.tasks.CacheableTask
import org.gradle.api.tasks.Input
import org.gradle.api.tasks.Optional
import org.gradle.api.tasks.OutputDirectory
import org.gradle.api.tasks.TaskAction

/**
 * Writes `META-INF/tazel-plugin.properties` and the `ProjectPlugin` service entry of a Tazel
 * plugin, which Tazel reads before it loads the plugin and when it pushes it to Tazel Portal. The
 * descriptor says who the plugin is: its group, name and version.
 */
@CacheableTask
abstract class TazelPluginDescriptorTask : DefaultTask() {
  // A required field is optional to Gradle, so the task reports it as a tazelPlugin setting.
  @get:Input @get:Optional abstract val pluginGroup: Property<String>

  @get:Input @get:Optional abstract val pluginName: Property<String>

  @get:Input @get:Optional abstract val pluginVersion: Property<String>

  @get:Input @get:Optional abstract val pluginClass: Property<String>

  @get:Input abstract val imports: ListProperty<String>

  // `description` is a property of every task already.
  @get:Input @get:Optional abstract val description_: Property<String>

  @get:Input abstract val authors: ListProperty<String>

  @get:Input @get:Optional abstract val license: Property<String>

  @get:Input @get:Optional abstract val homepage: Property<String>

  @get:Input @get:Optional abstract val repository: Property<String>

  @get:Input abstract val apiVersion: Property<Int>

  @get:OutputDirectory abstract val outputDirectory: DirectoryProperty

  @TaskAction
  fun write() {
    val group = required(pluginGroup, "group")
    if (!group.matches(GROUP)) {
      throw GradleException(
        "tazelPlugin.group is '$group'; a group is a reversed domain such as com.example, " +
          "of at least two parts of letters, digits, - and _"
      )
    }
    val name = required(pluginName, "name")
    if (!name.matches(NAME)) {
      throw GradleException(
        "tazelPlugin.name is '$name'; a name is letters, digits and _ . -, " +
          "starting with a letter or digit"
      )
    }
    val version = required(pluginVersion, "version")
    if (!version.matches(SEMVER)) {
      throw GradleException(
        "tazelPlugin.version is '$version', which is not a Semantic Version such as 0.1.0"
      )
    }
    val pluginClass = required(pluginClass, "pluginClass")
    val imports = imports.get()
    imports.forEach { import ->
      if (!import.matches(IMPORT)) {
        throw GradleException("tazelPlugin.imports has '$import', which is not a dotted name")
      }
    }

    val entries =
      linkedMapOf(
        "group" to group,
        "name" to name,
        "version" to version,
        "apiVersion" to apiVersion.get().toString(),
      )
    if (imports.isNotEmpty()) entries["imports"] = imports.joinToString(",")
    description_.orNull?.let { entries["description"] = it }
    authors.get().forEachIndexed { index, author -> entries["author.$index"] = author }
    license.orNull?.let { entries["license"] = it }
    homepage.orNull?.let { entries["homepage"] = it }
    repository.orNull?.let { entries["repository"] = it }

    val metaInf = outputDirectory.get().asFile.resolve("META-INF")
    metaInf.resolve("services").mkdirs()
    metaInf.resolve("tazel-plugin.properties").writeText(properties(entries))
    metaInf
      .resolve("services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin")
      .writeText("$pluginClass\n")
  }

  private fun required(
    property: Property<String>,
    name: String,
  ): String =
    property.orNull?.trim()?.takeIf(String::isNotEmpty)
      ?: throw GradleException("tazelPlugin.$name is required")

  /**
   * The entries in the format of [Properties], escaped by it, without the date line it writes, so
   * the same settings always give the same bytes.
   */
  private fun properties(entries: Map<String, String>): String {
    val text = StringWriter()
    Properties()
      .apply { entries.forEach { (key, value) -> setProperty(key, value) } }
      .store(text, null)
    val lines = text.toString().lines().filter { it.isNotEmpty() && !it.startsWith("#") }
    val order = entries.keys.toList()
    return lines
      .sortedBy { line -> order.indexOf(line.substringBefore('=').replace("\\", "")) }
      .joinToString("\n", postfix = "\n")
  }

  private companion object {
    val GROUP = Regex("[A-Za-z0-9][A-Za-z0-9_-]*(?:\\.[A-Za-z0-9][A-Za-z0-9_-]*)+")
    val NAME = Regex("[A-Za-z0-9][A-Za-z0-9_.-]*")
    val SEMVER =
      Regex(
        """(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)""" +
          """(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?"""
      )
    val IMPORT = Regex("[A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*(?:\\.\\*)?")
  }
}
