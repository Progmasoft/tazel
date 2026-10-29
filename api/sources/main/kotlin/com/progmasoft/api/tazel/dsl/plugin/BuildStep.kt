/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.api.tazel.dsl.plugin

import java.nio.file.Path

/** Work a plugin adds to the build of a project; see [ExtensionScope.buildStep]. */
fun interface BuildStep {
  fun run(context: BuildContext)
}

/** What a [BuildStep] runs with. */
interface BuildContext {
  val projectRoot: Path

  /** The absolute directory the build mode of the project writes artifacts to. */
  val outputDirectory: Path

  val buildMode: BuildMode
  val host: Host

  /**
   * A directory of this step for intermediate files, created before the step runs: the same one in
   * every build, below `.tazel/build` of the project.
   */
  val workDirectory: Path

  /** Prints a status line, such as `Configuring cmake (Release)`. */
  fun status(
    verb: String,
    message: String,
  )

  fun warn(message: String)

  /**
   * Runs a program with the variables of [environment] added to those of Tazel and returns its exit
   * code. What it prints goes to the output of Tazel.
   */
  fun run(
    command: List<String>,
    directory: Path = projectRoot,
    environment: Map<String, String> = emptyMap(),
  ): Int

  /** The program [name] found on `PATH`, with the executable extension of the host, or `null`. */
  fun findProgram(name: String): Path?

  /** Stops the build with [message]. */
  fun fail(message: String): Nothing
}

/** Thrown by [BuildContext.fail]; Tazel reports its message and stops the build. */
class BuildStepException(message: String) : RuntimeException(message)
