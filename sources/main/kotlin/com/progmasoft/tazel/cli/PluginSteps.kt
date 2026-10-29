/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.progmasoft.api.tazel.dsl.plugin.BuildContext
import com.progmasoft.api.tazel.dsl.plugin.BuildMode
import com.progmasoft.api.tazel.dsl.plugin.BuildStepException
import com.progmasoft.api.tazel.dsl.plugin.CapturedRun
import com.progmasoft.api.tazel.dsl.plugin.Host
import com.progmasoft.api.tazel.dsl.plugin.ProjectSummary
import java.nio.file.Path

/** What a build step reaches of Tazel: its output and the programs it starts. */
internal interface StepServices {
  fun status(
    verb: String,
    message: String,
  )

  fun warn(message: String)

  fun run(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): Int

  fun capture(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): CapturedRun

  fun findProgram(name: String): Path?
}

/** The [BuildContext] one build step of a plugin runs with. */
internal class PluginStepContext(
  override val projectRoot: Path,
  override val project: ProjectSummary,
  override val outputDirectory: Path,
  override val buildMode: BuildMode,
  override val host: Host,
  override val workDirectory: Path,
  private val services: StepServices,
) : BuildContext {
  override fun status(
    verb: String,
    message: String,
  ) = services.status(verb, message)

  override fun warn(message: String) = services.warn(message)

  override fun run(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): Int {
    if (command.isEmpty()) fail("a build step asked to run an empty command")
    return services.run(command, directory, environment)
  }

  override fun capture(
    command: List<String>,
    directory: Path,
    environment: Map<String, String>,
  ): CapturedRun {
    if (command.isEmpty()) fail("a build step asked to run an empty command")
    return services.capture(command, directory, environment)
  }

  override fun findProgram(name: String): Path? = services.findProgram(name)

  override fun fail(message: String): Nothing = throw BuildStepException(message)
}
