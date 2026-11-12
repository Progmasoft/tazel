/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.project

import com.progmasoft.tazel.dsl.BuildFile
import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.FilePath
import com.progmasoft.tazel.dsl.FileSelection
import com.progmasoft.tazel.dsl.Glob
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.ModuleFile
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetDeclaration
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.TargetSettings
import com.progmasoft.tazel.dsl.TargetTriple
import com.progmasoft.tazel.label.LabelException
import com.progmasoft.tazel.label.Labels
import com.progmasoft.tazel.label.PackageId
import com.progmasoft.tazel.label.PackagePattern
import com.progmasoft.tazel.label.Selection
import com.progmasoft.tazel.label.TargetLabel
import java.nio.file.Path

/** One `BUILD.kts` of a module: the package [id] in [directory]. */
data class Package(val id: PackageId, val directory: Path, val file: BuildFile) {
  /** How messages name the file, such as `core/BUILD.kts`. */
  val shownName: String
    get() = if (id.path.isEmpty()) "BUILD.kts" else "${id.path}/BUILD.kts"
}

/** A dependency of a configured target. */
sealed interface Dependency {
  val label: TargetLabel
}

/** A library of this module. */
data class ModuleDependency(override val label: TargetLabel, val kind: TargetKind) : Dependency

/** A target of a dependency of the module, `@name//...`; it is read when that module is. */
data class ExternalDependency(override val label: TargetLabel) : Dependency

/** A target with what its block set for one triple and build mode, its labels resolved. */
data class ConfiguredTarget(
  val label: TargetLabel,
  val language: Language,
  val kind: TargetKind,
  val triple: TargetTriple,
  val mode: BuildMode,
  val settings: TargetSettings,
  val dependencies: List<Dependency>,
)

/** A module after its `MODULE.kts` and every `BUILD.kts` ran. */
class Module(val root: Path, val file: ModuleFile, packages: List<Package>) {
  val packages: Map<PackageId, Package> = packages.associateBy(Package::id)

  /** The package of [directory]: its own `BUILD.kts`, or the nearest one above it in the module. */
  fun packageOf(directory: Path): Package? {
    var current: Path? = directory.toAbsolutePath().normalize()
    while (current != null && current.startsWith(root)) {
      packages[PackageId(null, packagePath(current))]?.let {
        return it
      }
      current = current.parent
    }
    return null
  }

  /** The targets [selection] names; a label of a target that does not exist is an error. */
  fun select(selection: Selection): List<TargetLabel> =
    when (selection) {
      is TargetLabel -> {
        declaration(selection)
        listOf(selection)
      }
      is PackagePattern -> {
        if (selection.packageId.repository != null) {
          throw ProjectException("$selection: only targets of this module can be selected")
        }
        val labels =
          packages.values
            .filter { selection.covers(it.id) }
            .sortedBy { it.id.path }
            .flatMap { pkg -> pkg.file.targets.map { TargetLabel(pkg.id, it.name) } }
        if (labels.isEmpty()) throw ProjectException("$selection has no targets")
        labels
      }
    }

  /** The package and declaration of [label], a target of this module. */
  fun declaration(label: TargetLabel): Pair<Package, TargetDeclaration> {
    if (label.packageId.repository != null) {
      throw ProjectException("$label is a target of a dependency, not of this module")
    }
    val pkg =
      packages[label.packageId]
        ?: throw ProjectException(
          "$label: there is no ${label.packageId.path.ifEmpty { "." }}/BUILD.kts in the module"
        )
    val declaration =
      pkg.file.targets.firstOrNull { it.name == label.name }
        ?: throw ProjectException("$label: ${pkg.shownName} has no target ${label.name}")
    return pkg to declaration
  }

  /** The triples [label] is built for in [mode]: the ones it names, or [host]. */
  fun triplesOf(label: TargetLabel, mode: BuildMode, host: TargetTriple): List<TargetTriple> {
    val (pkg, declaration) = declaration(label)
    val named = run(pkg, label) { declaration.configure(host, mode) }.triples
    return named.map(TargetTriple::parse).ifEmpty { listOf(host) }
  }

  /**
   * Runs the block of [label] for [triple] in [mode] and resolves its dependencies. A target that
   * names triples is configured only for one of them.
   */
  fun configure(label: TargetLabel, triple: TargetTriple, mode: BuildMode): ConfiguredTarget {
    val (pkg, declaration) = declaration(label)
    val settings = run(pkg, label) { declaration.configure(triple, mode) }
    if (settings.triples.isNotEmpty() && triple.text !in settings.triples) {
      throw ProjectException("$label is not built for $triple; it names ${settings.triples}")
    }
    checkFiles(pkg, label, settings)
    checkKind(label, declaration, settings)
    val dependencies = settings.depends.map { resolve(pkg, label, declaration.kind, it) }
    dependencies
      .groupBy { it.label }
      .filterValues { it.size > 1 }
      .keys
      .firstOrNull()
      ?.let { throw ProjectException("$label depends on $it twice") }
    return ConfiguredTarget(
      label,
      declaration.language,
      declaration.kind,
      triple,
      mode,
      settings,
      dependencies,
    )
  }

  private fun resolve(pkg: Package, from: TargetLabel, kind: TargetKind, text: String): Dependency {
    val label =
      try {
        Labels.target(text, pkg.id)
      } catch (error: LabelException) {
        throw ProjectException("$from: ${error.message}")
      }
    if (label == from) throw ProjectException("$from depends on itself")
    val repository = label.packageId.repository
    if (repository != null) {
      val testOnly =
        file.dependencyNames[repository]
          ?: throw ProjectException("$from: @$repository is not a dependency in MODULE.kts")
      if (testOnly && kind != TargetKind.TEST) {
        throw ProjectException(
          "$from: @$repository is a test dependency; only test targets may depend on it"
        )
      }
      return ExternalDependency(label)
    }
    val (target, declaration) =
      try {
        declaration(label)
      } catch (error: ProjectException) {
        throw ProjectException("$from: ${error.message}")
      }
    if (target.id != pkg.id && !target.file.isPublic) {
      throw ProjectException(
        "$from: $label is not visible; ${target.shownName} has isPublic = false"
      )
    }
    if (!declaration.kind.isLibrary) {
      throw ProjectException(
        "$from: $label is a ${declaration.kind.keyword}; a target depends only on libraries"
      )
    }
    return ModuleDependency(label, declaration.kind)
  }

  private fun checkKind(
    label: TargetLabel,
    declaration: TargetDeclaration,
    settings: TargetSettings,
  ) {
    if (declaration.language == Language.ZIG && settings.sources.singleOrNull() !is FilePath) {
      throw ProjectException("$label: a Zig target names its one root file in sources(\"...\")")
    }
  }

  private fun checkFiles(pkg: Package, label: TargetLabel, settings: TargetSettings) {
    val selections =
      settings.sources +
        settings.excludes +
        settings.textualHeaders +
        settings.data +
        (settings.bundle?.resources ?: emptyList())
    val paths =
      selections.flatMap(::literalParts) +
        settings.headers +
        settings.privateHeaders +
        listOfNotNull(
          settings.precompiledHeader,
          settings.defFile,
          settings.linkerScript,
          settings.bundle?.infoPlist,
        ) +
        settings.resources
    for (path in paths) {
      val resolved = pkg.directory.resolve(path).normalize()
      if (!resolved.startsWith(root)) {
        throw ProjectException("$label: '$path' is outside the module")
      }
    }
  }

  private fun <T> run(pkg: Package, label: TargetLabel, block: () -> T): T =
    try {
      block()
    } catch (error: ProjectException) {
      throw ProjectException("${pkg.shownName}: $label: ${error.message}")
    } catch (error: LabelException) {
      throw ProjectException("${pkg.shownName}: $label: ${error.message}")
    }

  internal fun packagePath(directory: Path): String =
    root.relativize(directory).joinToString("/") { it.toString() }
}

/** The part of a path or glob pattern before its first wildcard. */
private fun literalParts(selection: FileSelection): List<String> =
  when (selection) {
    is FilePath -> listOf(selection.path)
    is Glob ->
      selection.patterns.map { pattern ->
        pattern.split('/').takeWhile { segment -> segment.none { it in "*?[{" } }.joinToString("/")
      }
  }
