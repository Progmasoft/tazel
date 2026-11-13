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
 * Builds the targets of a [BuildPlan] as actions: each translation unit is compiled on its own,
 * then each library is archived and each program linked. An action runs only when its command line,
 * environment or the content of a file it read changed, or an output it wrote is gone or altered
 * (see [ActionCache]); what a compiler includes comes from the depfile it writes.
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
  private val digests = Digests()
  private val cache = ActionCache(digests)

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
        Files.createDirectories(objectFile.parent)
        val action =
          Action(
            commands.compile(target, source, objectFile, depfile),
            target.packageDirectory,
            inputs = listOf(source),
            outputs = listOf(objectFile, depfile),
            discovered = {
              depfileInputs(Files.readString(depfile)).map {
                target.packageDirectory.resolve(it).normalize()
              }
            },
          )
        run("Compiling", "${target.label} ${target.packageDirectory.relativize(source)}", action)
      }
    }
    val objects = sources.map { commands.objectOf(target, it) }
    val final =
      BuildUnit(
        finalUnit(target.label),
        "link ${target.label}",
        compiles.map { it.id }.toSet() + after,
      ) {
        val settings = target.target.settings
        val scripts =
          listOfNotNull(settings.linkerScript, settings.defFile).map {
            target.packageDirectory.resolve(it).normalize()
          }
        val (verb, command) =
          if (target.kind == TargetKind.STATICLIB) "Archiving" to commands.archive(target, objects)
          else "Linking" to commands.link(target, objects)
        val action =
          Action(
            command,
            target.packageDirectory,
            inputs = objects + target.libraries.map { it.product } + scripts,
            outputs = listOf(target.product),
          )
        // ar adds to an archive that is there, so an archive is made anew.
        run(verb, target.label.toString(), action) {
          if (target.kind == TargetKind.STATICLIB) Files.deleteIfExists(target.product)
        }
        if (target.kind == TargetKind.SHAREDLIB) linkUnversioned(target)
        copyData(target)
      }
    return compiles + final
  }

  private fun zigUnits(target: PlannedTarget): List<BuildUnit<Unit>> {
    val after = target.direct.map { finalUnit(it.label) }.toSet()
    return listOf(
      BuildUnit(finalUnit(target.label), "zig ${target.label}", after) {
        val command = zigCommand(target)
        val root =
          target.packageDirectory
            .resolve((target.target.settings.sources.single() as FilePath).path)
            .normalize()
        // Zig writes no depfile: every Zig file beside and below the root file is an input.
        val zigFiles =
          Files.walk(root.parent).use { files ->
            files
              .filter { Files.isRegularFile(it) && it.toString().endsWith(".zig") }
              .sorted()
              .toList()
          }
        val action =
          Action(
            command,
            target.packageDirectory,
            inputs = zigFiles + target.libraries.map { it.product },
            outputs = listOf(target.product),
          )
        run("Building", target.label.toString(), action)
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
    if (Files.isSymbolicLink(plain) && Files.readSymbolicLink(plain) == target.product.fileName)
      return
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
      copyIfChanged(file, destination)
    }
  }

  /** Copies [source] to [destination] unless it already holds the same bytes. */
  private fun copyIfChanged(source: Path, destination: Path) {
    if (Files.isRegularFile(destination) && digests.of(destination) == digests.of(source)) return
    Files.createDirectories(destination.parent)
    Files.copy(source, destination, StandardCopyOption.REPLACE_EXISTING)
  }

  /** Runs [action] unless it is current; [before] prepares its outputs. */
  private fun run(verb: String, what: String, action: Action, before: () -> Unit = {}) {
    if (cache.current(action)) return
    synchronized(out) { out.appendLine("%12s %s".format(verb, what)) }
    cache.forget(action)
    before()
    commandsRun.incrementAndGet()
    val result = processes.runGatheredWith(action.command, action.directory, action.environment)
    if (result.output.isNotBlank()) {
      synchronized(out) { out.append(result.output.trimEnd()).appendLine() }
    }
    if (result.exitCode != 0) {
      throw BuildFailure(
        "$what: ${Path.of(action.command.first()).fileName} exited with ${result.exitCode}"
      )
    }
    cache.record(action)
  }
}
