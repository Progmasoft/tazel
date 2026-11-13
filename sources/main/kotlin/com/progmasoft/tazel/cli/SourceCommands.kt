/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.cli

import com.progmasoft.tazel.driver.BuildPlan
import com.progmasoft.tazel.driver.CcToolchain
import com.progmasoft.tazel.driver.FileSets
import com.progmasoft.tazel.driver.Formatter
import com.progmasoft.tazel.driver.Linter
import com.progmasoft.tazel.driver.compileEntries
import com.progmasoft.tazel.driver.ownedFiles
import com.progmasoft.tazel.driver.writeCompileCommands
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.TargetTriple
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption

/** `tazel fmt` and `tazel lint`: the tools that read sources without building them. */
internal class SourceCommands(private val environment: Environment, private val out: Appendable) {
  private val host = TargetTriple.host()
  private val fileSets = FileSets(ignoreCase = host.os == Os.WINDOWS)

  /**
   * Formats the files the selected targets own, on every triple they are built for, with
   * clang-format and `zig fmt`; with `--check` it only reports the files that are not formatted.
   */
  fun format(options: BuildOptions): Int {
    val (module, labels, _) = BuildCommands(environment, out).select(options, Purpose.FORMAT)
    val files = linkedSetOf<Path>()
    for (label in labels) {
      for (triple in module.triplesOf(label, options.mode, host)) {
        val target = module.configure(label, triple, options.mode)
        files += ownedFiles(module.declaration(label).first.directory, target.settings, fileSets)
      }
    }
    val result =
      Formatter(environment.programs, environment.processes, out)
        .format(files.toList(), options.check, options.programArguments, module.root)
    val verb = if (options.check) "Checked" else "Formatted"
    out.appendLine(
      "%12s %d file(s); %d run(s) failed".format(verb, result.filesChecked, result.failures)
    )
    return if (result.failures == 0) ExitCode.SUCCESS else ExitCode.FAILURE
  }

  /**
   * Writes the compilation database of the selected targets and what they depend on, then checks
   * the C-family sources of the selected targets with clang-tidy; with `--fix` it applies the fixes
   * clang-tidy offers.
   */
  fun lint(options: BuildOptions): Int {
    val (module, labels, triple) = BuildCommands(environment, out).select(options, Purpose.BUILD)
    val plan = BuildPlan(module, triple, options.mode)
    labels.forEach(plan::add)
    val entries = compileEntries(plan, CcToolchain(environment.programs, host), fileSets)
    val database = writeCompileCommands(plan.outputRoot, entries)
    // One place per module that does not change with the triple, for clangd (CompilationDatabase:
    // .tazel) and other editors to be pointed at.
    val shared = module.root.resolve(".tazel/compile_commands.json")
    Files.copy(database, shared, StandardCopyOption.REPLACE_EXISTING)
    for (target in plan.targets) {
      if (target.label in labels && target.language == Language.ZIG) {
        out.appendLine("%12s %s: Zig has no linter in Tazel".format("Skipped", target.label))
      }
    }
    val jobs = module.file.execution.jobs ?: Runtime.getRuntime().availableProcessors()
    val result =
      Linter(environment.programs, environment.processes, out, jobs)
        .lint(
          entries.filter { it.label in labels },
          plan.outputRoot,
          options.fix,
          options.programArguments,
        )
    out.appendLine(
      "%12s %d file(s); %d failed; compile_commands.json in %s"
        .format("Linted", result.filesChecked, result.failures, shared.parent)
    )
    return if (result.failures == 0) ExitCode.SUCCESS else ExitCode.FAILURE
  }
}
