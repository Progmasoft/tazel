/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.project

import com.progmasoft.tazel.dsl.BuildFileScope
import com.progmasoft.tazel.dsl.ModuleFileScope
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.label.Labels
import com.progmasoft.tazel.label.PackageId
import java.io.IOException
import java.nio.charset.MalformedInputException
import java.nio.file.Files
import java.nio.file.LinkOption
import java.nio.file.Path

const val MODULE_FILE = "MODULE.kts"
const val BUILD_FILE = "BUILD.kts"

/** Finds a module on disk and runs its project files. */
class ModuleLoader(private val scripts: ScriptRunner = ScriptRunner()) {
  /** The root of the module [start] is in: the nearest directory with a `MODULE.kts`. */
  fun findRoot(start: Path): Path {
    var directory: Path? = start.toAbsolutePath().normalize()
    while (directory != null) {
      if (MODULE_FILE in fileNames(directory)) return directory
      directory = directory.parent
    }
    throw ProjectException(
      "no $MODULE_FILE in ${start.toAbsolutePath().normalize()} or a directory above it"
    )
  }

  /**
   * Runs the `MODULE.kts` of [root] and every `BUILD.kts` below it. Directories whose name starts
   * with a dot, and directories with their own `MODULE.kts` (other modules), are not packages.
   */
  fun load(root: Path): Module {
    val moduleRoot = root.toAbsolutePath().normalize()
    val moduleScope = ModuleFileScope()
    run(moduleRoot.resolve(MODULE_FILE), MODULE_FILE, moduleScope)
    val moduleFile =
      try {
        moduleScope.build()
      } catch (error: ProjectException) {
        throw ProjectException("$MODULE_FILE: ${error.message}")
      }
    val packages =
      packageDirectories(moduleRoot).map { directory ->
        val path = moduleRoot.relativize(directory).joinToString("/") { it.toString() }
        path.split('/').filter(String::isNotEmpty).forEach { Labels.requireName(it, "package") }
        val shown = if (path.isEmpty()) BUILD_FILE else "$path/$BUILD_FILE"
        val scope = BuildFileScope()
        run(directory.resolve(BUILD_FILE), shown, scope)
        Package(PackageId(null, path), directory, scope.build())
      }
    return Module(moduleRoot, moduleFile, packages).also(::checkPushTargets)
  }

  private fun checkPushTargets(module: Module) {
    for (text in module.file.viget.pushTargets) {
      val label =
        try {
          Labels.target(text, PackageId.ROOT)
        } catch (error: IllegalArgumentException) {
          throw ProjectException("$MODULE_FILE: pushTarget: ${error.message}")
        }
      val (pkg, _) =
        try {
          module.declaration(label)
        } catch (error: ProjectException) {
          throw ProjectException("$MODULE_FILE: pushTarget: ${error.message}")
        }
      if (!pkg.file.isPublic) {
        throw ProjectException(
          "$MODULE_FILE: pushTarget($label): ${pkg.shownName} needs isPublic = true"
        )
      }
    }
  }

  private inline fun <reified T : Any> run(file: Path, shown: String, receiver: T) {
    val source =
      try {
        Files.readString(file)
      } catch (_: MalformedInputException) {
        throw ProjectException("$shown is not UTF-8 text")
      } catch (error: IOException) {
        throw ProjectException("$shown could not be read: ${error.message}")
      }
    scripts.run(file, shown, source, receiver, T::class)
  }

  private fun packageDirectories(root: Path): List<Path> {
    val found = mutableListOf<Path>()
    fun visit(directory: Path) {
      val names = fileNames(directory)
      if (directory != root && MODULE_FILE in names) return
      if (BUILD_FILE in names) found.add(directory)
      val children =
        try {
          Files.list(directory).use { entries ->
            entries
              .filter { Files.isDirectory(it, LinkOption.NOFOLLOW_LINKS) }
              .filter { !it.fileName.toString().startsWith(".") }
              .sorted()
              .toList()
          }
        } catch (error: IOException) {
          throw ProjectException("$directory could not be listed: ${error.message}")
        }
      children.forEach(::visit)
    }
    visit(root)
    return found
  }
}

/**
 * The regular files of [directory] as they are spelled: Windows and macOS would find `build.kts`
 * for `BUILD.kts`, and the names are case-sensitive.
 */
private fun fileNames(directory: Path): Set<String> =
  try {
    Files.list(directory).use { entries ->
      entries
        .filter { Files.isRegularFile(it, LinkOption.NOFOLLOW_LINKS) }
        .map { it.fileName.toString() }
        .toList()
        .toSet()
    }
  } catch (_: IOException) {
    emptySet()
  }
