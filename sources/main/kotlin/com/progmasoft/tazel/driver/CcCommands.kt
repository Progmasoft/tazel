/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.CStandard
import com.progmasoft.tazel.dsl.CppStandard
import com.progmasoft.tazel.dsl.DebugInfo
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.Lto
import com.progmasoft.tazel.dsl.Optimize
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.Subsystem
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.Warnings
import java.nio.file.Files
import java.nio.file.Path

/** The extensions each C-family language compiles; headers among the sources are not compiled. */
private val EXTENSIONS =
  mapOf(
    Language.C to setOf("c"),
    Language.CPP to setOf("cpp", "cc", "cxx", "c++"),
    Language.OBJC to setOf("m"),
    Language.OBJCPP to setOf("mm"),
  )

private val HEADERS = setOf("h", "hh", "hpp", "hxx", "h++", "inc", "inl", "ipp", "tpp")

/** The compile and link command lines of C, C++, Objective-C and Objective-C++ targets. */
class CcCommands(private val plan: BuildPlan, private val compiler: CcCompiler) {
  private val os = plan.triple.os
  private val mode = plan.mode

  /** The files of [target] to compile; a file of another language is an error. */
  fun translationUnits(target: PlannedTarget, files: List<Path>): List<Path> =
    files.filter { file ->
      val extension = file.fileName.toString().substringAfterLast('.', "").lowercase()
      when {
        extension in HEADERS -> false
        extension in EXTENSIONS.getValue(target.language) -> true
        else ->
          throw ProjectException(
            "${target.label}: ${target.language.keyword} targets compile " +
              "${EXTENSIONS.getValue(target.language).joinToString { ".$it" }} files, not $file"
          )
      }
    }

  /** Where the object of [source] goes. */
  fun objectOf(target: PlannedTarget, source: Path): Path {
    val relative =
      target.packageDirectory.relativize(source).joinToString("/") {
        if (it.toString() == "..") "__" else it.toString()
      }
    return target.outputDirectory.resolve("obj").resolve(relative + OutputNames.objectSuffix(os))
  }

  /** The include directories [target] gives its dependents. */
  fun publicIncludes(target: PlannedTarget): List<Path> {
    val settings = target.target.settings
    if (settings.includePrefix == null && settings.stripIncludePrefix == null) {
      return settings.headers.map { target.packageDirectory.resolve(it).normalize() }
    }
    return listOf(stagedIncludes(target))
  }

  /**
   * Copies the public headers of [target] to where `includePrefix` and `stripIncludePrefix` place
   * them: `include/core.h` with the prefix `greeter` and `include` stripped is `greeter/core.h`.
   */
  fun stageHeaders(target: PlannedTarget) {
    val settings = target.target.settings
    if (settings.includePrefix == null && settings.stripIncludePrefix == null) return
    val staged = stagedIncludes(target)
    val strip = settings.stripIncludePrefix?.let { target.packageDirectory.resolve(it).normalize() }
    for (directory in settings.headers) {
      val start = target.packageDirectory.resolve(directory).normalize()
      if (!Files.isDirectory(start)) throw ProjectException("${target.label}: no directory $start")
      Files.walk(start).use { files ->
        files.filter(Files::isRegularFile).forEach { file ->
          val base = if (strip != null && file.startsWith(strip)) strip else target.packageDirectory
          val relative = base.relativize(file).toString()
          val destination =
            staged.resolve(settings.includePrefix?.let { "$it/$relative" } ?: relative)
          Files.createDirectories(destination.parent)
          if (
            !Files.exists(destination) ||
              Files.getLastModifiedTime(destination) != Files.getLastModifiedTime(file)
          ) {
            Files.copy(
              file,
              destination,
              java.nio.file.StandardCopyOption.REPLACE_EXISTING,
              java.nio.file.StandardCopyOption.COPY_ATTRIBUTES,
            )
          }
        }
      }
    }
  }

  private fun stagedIncludes(target: PlannedTarget) = target.outputDirectory.resolve("include")

  /** The command that compiles [source] of [target] into [objectFile] and writes [depfile]. */
  fun compile(target: PlannedTarget, source: Path, objectFile: Path, depfile: Path): List<String> {
    val settings = target.target.settings
    val language = target.language
    val cpp = language == Language.CPP || language == Language.OBJCPP
    val command = mutableListOf<String>()
    command += if (cpp) compiler.cxx else compiler.c
    command += compiler.targetFlags
    command += listOf("-c", source.toString(), "-o", objectFile.toString())
    command += listOf("-MD", "-MF", depfile.toString())
    if (cpp) {
      settings.cppStandard?.let { command += "-std=" + cppStandard(it, settings.gnuExtensions) }
    } else {
      settings.cStandard?.let { command += "-std=" + cStandard(it, settings.gnuExtensions) }
    }
    command += optimizeFlags(settings.optimize ?: defaultOptimize())
    command += debugFlags(settings.debugInfo ?: defaultDebugInfo())
    command +=
      when (settings.warnings) {
        Warnings.ALL -> listOf("-Wall", "-Wextra")
        Warnings.DEFAULT -> emptyList()
        Warnings.NONE -> listOf("-w")
      }
    if (settings.warningsAsErrors) command += "-Werror"
    if (os != Os.WINDOWS && positionIndependent(target)) command += "-fPIC"
    if (language == Language.OBJC || language == Language.OBJCPP) {
      if (settings.arc) command += "-fobjc-arc"
    }
    command += sanitizeFlags(settings.sanitizers)
    command += ltoFlags(settings.lto)
    val defines =
      settings.defines +
        settings.privateDefines +
        target.libraries.flatMap { it.target.settings.defines }
    command += defines.distinct().map { "-D$it" }
    val includes =
      publicIncludes(target) +
        settings.privateHeaders.map { target.packageDirectory.resolve(it).normalize() } +
        target.libraries.flatMap(::publicIncludes)
    command += includes.distinct().map { "-I$it" }
    settings.precompiledHeader?.let {
      command += listOf("-include", target.packageDirectory.resolve(it).normalize().toString())
    }
    command += settings.flags
    return command
  }

  /** The command that archives [objects] into the static library of [target]. */
  fun archive(target: PlannedTarget, objects: List<Path>): List<String> =
    compiler.archiver + listOf("rcs", target.product.toString()) + objects.map(Path::toString)

  /** The command that links [objects] and the libraries of [target] into its product. */
  fun link(target: PlannedTarget, objects: List<Path>): List<String> {
    val settings = target.target.settings
    val everything = listOf(target) + target.libraries
    val cpp = everything.any { it.language == Language.CPP || it.language == Language.OBJCPP }
    val command = mutableListOf<String>()
    command += if (cpp) compiler.cxx else compiler.c
    command += compiler.targetFlags
    command += objects.map(Path::toString)
    for (library in target.libraries) {
      when (library.kind) {
        TargetKind.STATICLIB ->
          if (library.target.settings.alwaysLink) {
            command += wholeArchive(library.product)
          } else {
            command += library.product.toString()
          }
        TargetKind.SHAREDLIB -> command += library.product.toString()
        else -> Unit
      }
    }
    command += listOf("-o", target.product.toString())
    if (target.kind == TargetKind.SHAREDLIB) command += sharedFlags(target)
    command += sanitizeFlags(settings.sanitizers)
    command += ltoFlags(settings.lto)
    if (settings.strip && os != Os.MACOS) command += "-s"
    val rpaths =
      settings.rpath +
        target.libraries
          .filter { it.kind == TargetKind.SHAREDLIB }
          .map { it.product.parent.toString() }
    if (os != Os.WINDOWS) command += rpaths.distinct().map { "-Wl,-rpath,$it" }
    command += everything.flatMap { it.target.settings.links }.distinct().map { "-l$it" }
    if (os == Os.MACOS) {
      command +=
        everything
          .flatMap { it.target.settings.frameworks }
          .distinct()
          .flatMap {
            listOf("-framework", it)
          }
    }
    settings.linkerScript?.let {
      command += "-Wl,-T," + target.packageDirectory.resolve(it).normalize()
    }
    if (os == Os.WINDOWS) {
      settings.defFile?.let {
        command += "-Wl,/DEF:" + target.packageDirectory.resolve(it).normalize()
      }
      if (target.kind == TargetKind.BIN && settings.subsystem == Subsystem.WINDOWS) {
        command += "-Wl,/SUBSYSTEM:WINDOWS"
      }
      if (settings.resources.isNotEmpty()) {
        throw ProjectException(
          "${target.label}: Windows resource scripts are not built by this Tazel yet"
        )
      }
    }
    command += everything.flatMap { it.target.settings.linkFlags }
    return command
  }

  /** Whether the objects of [target] are compiled as position-independent code. */
  fun positionIndependent(target: PlannedTarget): Boolean =
    when (target.kind) {
      TargetKind.SHAREDLIB -> true
      TargetKind.STATICLIB -> target.target.settings.pic ?: plan.linkedIntoSharedLibrary(target)
      else -> false
    }

  private fun sharedFlags(target: PlannedTarget): List<String> {
    val settings = target.target.settings
    val fileName = target.product.fileName.toString()
    val flags = mutableListOf<String>()
    when (os) {
      Os.MACOS -> {
        flags += listOf("-dynamiclib", "-Wl,-install_name,@rpath/$fileName")
        settings.exports.forEach { flags += "-Wl,-exported_symbol,_$it" }
      }
      Os.WINDOWS -> flags += "-shared"
      else -> {
        flags += "-shared"
        if (settings.soVersion != null) flags += "-Wl,-soname,$fileName"
        if (settings.exports.isNotEmpty()) {
          val script = target.outputDirectory.resolve("exports.map")
          Files.writeString(
            script,
            "{\n  global:\n" +
              settings.exports.joinToString("") { "    $it;\n" } +
              "  local: *;\n};\n",
          )
          flags += "-Wl,--version-script,$script"
        }
      }
    }
    return flags
  }

  private fun wholeArchive(archive: Path): List<String> =
    when (os) {
      Os.MACOS -> listOf("-Wl,-force_load,$archive")
      Os.WINDOWS -> listOf("-Wl,/WHOLEARCHIVE:$archive")
      else -> listOf("-Wl,--whole-archive", archive.toString(), "-Wl,--no-whole-archive")
    }

  private fun defaultOptimize() = if (mode == BuildMode.RELEASE) Optimize.SPEED else Optimize.NONE

  private fun defaultDebugInfo() = if (mode == BuildMode.RELEASE) DebugInfo.NONE else DebugInfo.FULL

  private fun optimizeFlags(optimize: Optimize) =
    when (optimize) {
      Optimize.NONE -> listOf("-O0")
      Optimize.SIZE -> listOf("-Os")
      Optimize.SPEED -> listOf("-O2")
    }

  private fun debugFlags(debugInfo: DebugInfo) =
    when (debugInfo) {
      DebugInfo.NONE -> emptyList()
      DebugInfo.FULL -> listOf("-g")
      DebugInfo.SPLIT ->
        if (os == Os.LINUX || os == Os.BSD) listOf("-g", "-gsplit-dwarf") else listOf("-g")
    }

  private fun sanitizeFlags(sanitizers: Set<com.progmasoft.tazel.dsl.Sanitizer>) =
    if (sanitizers.isEmpty()) emptyList()
    else listOf("-fsanitize=" + sanitizers.joinToString(",") { it.name.lowercase() })

  private fun ltoFlags(lto: Lto) =
    when (lto) {
      Lto.NONE -> emptyList()
      Lto.THIN -> listOf("-flto=thin")
      Lto.FULL -> listOf("-flto")
    }

  private fun cStandard(standard: CStandard, gnu: Boolean) =
    (if (gnu) "gnu" else "c") + standard.name.removePrefix("C")

  private fun cppStandard(standard: CppStandard, gnu: Boolean): String {
    val year = if (standard == CppStandard.CPP26) "2c" else standard.name.removePrefix("CPP")
    return (if (gnu) "gnu++" else "c++") + year
  }
}
