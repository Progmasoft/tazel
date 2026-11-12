/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.FilePath
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.ZigOptimize
import com.progmasoft.tazel.label.TargetLabel
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.util.concurrent.atomic.AtomicInteger

/** A compiler, archiver or linker that failed, with what it printed. */
class BuildFailure(message: String) : Exception(message)

/** What a build did. */
data class BuildResult(val products: Map<TargetLabel, Path>, val commandsRun: Int)

/**
 * Builds the targets of a [BuildPlan]: each translation unit is compiled on its own and only when
 * its object is out of date (by the depfile the compiler writes and the command line it ran with);
 * a library or program is archived or linked when one of its inputs changed.
 */
class Builder(
  private val plan: BuildPlan,
  private val toolchain: CcToolchain,
  private val programs: Programs,
  private val processes: Processes,
  private val out: Appendable,
  private val host: com.progmasoft.tazel.dsl.TargetTriple,
  private val jobs: Int,
  private val keepGoing: Boolean,
) {
  private val fileSets = FileSets(ignoreCase = host.os == Os.WINDOWS)
  private val commandsRun = AtomicInteger()

  fun build(): BuildResult {
    val units = mutableListOf<BuildUnit<Unit>>()
    for (target in plan.targets) {
      plan.prepare(target)
      units += if (target.language == Language.ZIG) zigUnits(target) else ccUnits(target)
    }
    try {
      JobPool(jobs, keepGoing).run(BuildGraph(units))
    } catch (failures: JobFailures) {
      throw BuildFailure(failures.failures.joinToString("\n") { it.message.orEmpty() })
    }
    return BuildResult(plan.targets.associate { it.label to it.product }, commandsRun.get())
  }

  private fun finalUnit(label: TargetLabel) = "final $label"

  private fun ccUnits(target: PlannedTarget): List<BuildUnit<Unit>> {
    val settings = target.target.settings
    val compiler = toolchain.compiler(settings.compiler, target.language, plan.triple)
    val commands = CcCommands(plan, compiler)
    commands.stageHeaders(target)
    val sources =
      commands.translationUnits(
        target,
        fileSets.expand(target.packageDirectory, settings.sources, settings.excludes),
      )
    val after = target.direct.map { finalUnit(it.label) }.toSet()
    val compiles = sources.map { source ->
      val objectFile = commands.objectOf(target, source)
      BuildUnit("compile ${target.label} $source", "compile $source", after) {
        val depfile = objectFile.resolveSibling(objectFile.fileName.toString() + ".d")
        val command = commands.compile(target, source, objectFile, depfile)
        if (!UpToDate.checkObject(objectFile, depfile, command, target.packageDirectory)) {
          Files.createDirectories(objectFile.parent)
          run(
            "Compiling",
            "${target.label} ${target.packageDirectory.relativize(source)}",
            command,
            target.packageDirectory,
            objectFile,
          )
        }
      }
    }
    val objects = sources.map { commands.objectOf(target, it) }
    val final =
      BuildUnit(
        finalUnit(target.label),
        "link ${target.label}",
        compiles.map { it.id }.toSet() + after,
      ) {
        val inputs = objects + target.libraries.map { it.product }
        val (verb, command) =
          if (target.kind == TargetKind.STATICLIB) "Archiving" to commands.archive(target, objects)
          else "Linking" to commands.link(target, objects)
        if (!UpToDate.check(target.product, command, inputs)) {
          if (target.kind == TargetKind.STATICLIB) Files.deleteIfExists(target.product)
          run(verb, target.label.toString(), command, target.packageDirectory, target.product)
          if (target.kind == TargetKind.SHAREDLIB) linkUnversioned(target)
        }
        copyData(target)
      }
    return compiles + final
  }

  private fun zigUnits(target: PlannedTarget): List<BuildUnit<Unit>> {
    val after = target.direct.map { finalUnit(it.label) }.toSet()
    return listOf(
      BuildUnit(finalUnit(target.label), "zig ${target.label}", after) {
        val command = zigCommand(target)
        run("Building", target.label.toString(), command, target.packageDirectory, target.product)
        copyData(target)
      }
    )
  }

  private fun zigCommand(target: PlannedTarget): List<String> {
    val settings = target.target.settings
    val zig = programs.find("zig")?.toString() ?: throw ProjectException("zig is not on PATH")
    val root =
      target.packageDirectory.resolve((settings.sources.single() as FilePath).path).normalize()
    if (!Files.isRegularFile(root)) throw ProjectException("${target.label}: no file $root")
    val command = mutableListOf(zig)
    command +=
      when (target.kind) {
        TargetKind.BIN -> listOf("build-exe")
        TargetKind.STATICLIB -> listOf("build-lib")
        TargetKind.SHAREDLIB -> listOf("build-lib", "-dynamic")
        TargetKind.TEST -> listOf("test", "--test-no-exec")
      }
    command += root.toString()
    command += "-femit-bin=${target.product}"
    command += listOf("--cache-dir", target.outputDirectory.resolve("zig-cache").toString())
    val optimize =
      settings.zigOptimize ?: if (plan.mode == BuildMode.RELEASE) ZigOptimize.SAFE else null
    command +=
      listOf(
        "-O",
        when (optimize) {
          null -> "Debug"
          ZigOptimize.SAFE -> "ReleaseSafe"
          ZigOptimize.SPEED -> "ReleaseFast"
          ZigOptimize.SIZE -> "ReleaseSmall"
        },
      )
    if (plan.triple.text != host.text) command += listOf("-target", zigTriple(plan.triple))
    if (target.libraries.isNotEmpty()) {
      for (library in target.libraries) {
        library.target.settings.headers.forEach {
          command += "-I" + library.packageDirectory.resolve(it).normalize()
        }
        command += library.product.toString()
      }
      if (target.libraries.any { it.language != Language.ZIG }) command += "-lc"
    }
    return command
  }

  /** `libcore.so` beside `libcore.so.1`, so the linker of a dependent finds the library. */
  private fun linkUnversioned(target: PlannedTarget) {
    if (target.target.settings.soVersion == null) return
    if (plan.triple.os == Os.WINDOWS) return
    val name = target.target.settings.outputName ?: target.label.name
    val plain = target.product.resolveSibling(OutputNames.sharedLibrary(name, plan.triple.os, null))
    Files.deleteIfExists(plain)
    try {
      Files.createSymbolicLink(plain, target.product.fileName)
    } catch (_: Exception) {
      Files.copy(target.product, plain, StandardCopyOption.REPLACE_EXISTING)
    }
  }

  private fun copyData(target: PlannedTarget) {
    val settings = target.target.settings
    if (settings.data.isEmpty()) return
    for (file in fileSets.expand(target.packageDirectory, settings.data)) {
      val destination =
        target.product.resolveSibling(
          target.packageDirectory.relativize(file).toString().replace("..", "__")
        )
      Files.createDirectories(destination.parent)
      Files.copy(
        file,
        destination,
        StandardCopyOption.REPLACE_EXISTING,
        StandardCopyOption.COPY_ATTRIBUTES,
      )
    }
  }

  private fun run(
    verb: String,
    what: String,
    command: List<String>,
    directory: Path,
    output: Path,
  ) {
    synchronized(out) { out.appendLine("%12s %s".format(verb, what)) }
    UpToDate.forget(output)
    commandsRun.incrementAndGet()
    val result = processes.runGathered(command, directory)
    if (result.output.isNotBlank())
      synchronized(out) { out.append(result.output.trimEnd()).appendLine() }
    if (result.exitCode != 0) {
      throw BuildFailure(
        "$what: ${Path.of(command.first()).fileName} exited with ${result.exitCode}"
      )
    }
    UpToDate.record(output, command)
  }
}
