/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.TargetTriple
import com.progmasoft.tazel.label.TargetLabel
import com.progmasoft.tazel.project.ConfiguredTarget
import com.progmasoft.tazel.project.ExternalDependency
import com.progmasoft.tazel.project.Module
import com.progmasoft.tazel.project.ModuleDependency
import java.nio.file.Files
import java.nio.file.Path

/** A configured target with where its outputs go. */
class PlannedTarget(
  val target: ConfiguredTarget,
  /** The directory of its `BUILD.kts`, which its paths are relative to. */
  val packageDirectory: Path,
  /** The directory of its outputs: objects, staged headers and the product. */
  val outputDirectory: Path,
  /** The program or library it makes. */
  val product: Path,
  /** Its libraries, the nearest first, each once: what it links, in link order. */
  val libraries: List<PlannedTarget>,
) {
  val label: TargetLabel
    get() = target.label

  val kind: TargetKind
    get() = target.kind

  val language: Language
    get() = target.language

  /** The libraries it depends on directly. */
  val direct: List<PlannedTarget>
    get() = libraries.filter { library -> target.dependencies.any { it.label == library.label } }
}

/**
 * Configures what a command builds: the selected targets and every library they depend on, for one
 * triple and build mode. A target is configured once, however many depend on it.
 */
class BuildPlan(val module: Module, val triple: TargetTriple, val mode: BuildMode) {
  private val planned = linkedMapOf<TargetLabel, PlannedTarget>()
  private val visiting = linkedSetOf<TargetLabel>()

  /** The targets of the plan, each after the libraries it depends on. */
  val targets: List<PlannedTarget>
    get() = planned.values.toList()

  /** The output directory of a triple and build mode, below `.tazel/out` of the module. */
  val outputRoot: Path =
    module.root.resolve(".tazel/out").resolve(triple.text).resolve(mode.name.lowercase())

  /** Adds [label] and what it depends on. */
  fun add(label: TargetLabel): PlannedTarget {
    planned[label]?.let {
      return it
    }
    if (!visiting.add(label)) {
      throw ProjectException(
        "the targets depend on each other: ${(visiting.dropWhile { it != label } + label).joinToString(" -> ")}"
      )
    }
    val target = module.configure(label, triple, mode)
    val direct =
      target.dependencies.map { dependency ->
        when (dependency) {
          is ModuleDependency -> add(dependency.label)
          is ExternalDependency ->
            throw ProjectException(
              "$label depends on ${dependency.label}: dependencies from ViGet and local .vipkg " +
                "files are not built by this Tazel yet"
            )
        }
      }
    // Nearest first, each library once, a library after everything that depends on it.
    val order = mutableListOf<PlannedTarget>()
    fun visit(library: PlannedTarget) {
      order.remove(library)
      order += library
      library.direct.forEach(::visit)
    }
    direct.forEach(::visit)
    val (pkg, _) = module.declaration(label)
    val directory = outputRoot.resolve(label.packageId.path.ifEmpty { "_root" }).resolve(label.name)
    val name = target.settings.outputName ?: label.name
    val os = triple.os
    val product =
      directory.resolve(
        when (target.kind) {
          TargetKind.BIN,
          TargetKind.TEST -> OutputNames.executable(name, os)
          TargetKind.STATICLIB -> OutputNames.staticLibrary(name, os)
          TargetKind.SHAREDLIB -> OutputNames.sharedLibrary(name, os, target.settings.soVersion)
        }
      )
    visiting.remove(label)
    return PlannedTarget(target, pkg.directory, directory, product, order.toList()).also {
      planned[label] = it
    }
  }

  /** Whether a shared library links [library], so it is compiled as position-independent code. */
  fun linkedIntoSharedLibrary(library: PlannedTarget): Boolean =
    planned.values.any { it.kind == TargetKind.SHAREDLIB && library in it.libraries }

  /** Creates the output directory of [target]. */
  fun prepare(target: PlannedTarget) {
    Files.createDirectories(target.outputDirectory)
  }
}
