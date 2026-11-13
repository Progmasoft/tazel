/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetSettings
import java.nio.file.Files
import java.nio.file.Path

/**
 * The extensions clang-format formats: the sources of the four C-family languages and their
 * headers.
 */
private val CLANG_FORMAT_EXTENSIONS =
  setOf(
    "c",
    "cc",
    "cpp",
    "cxx",
    "c++",
    "m",
    "mm",
    "h",
    "hh",
    "hpp",
    "hxx",
    "h++",
    "inl",
    "ipp",
    "tpp",
  )

private val HEADER_EXTENSIONS = setOf("h", "hh", "hpp", "hxx", "h++", "inl", "ipp", "tpp", "inc")

/**
 * How many files one run of a formatter gets, so that no command line grows past the limit of the
 * system.
 */
private const val FILES_PER_RUN = 200

/**
 * The files of a target that its formatter owns: its sources less its excludes, the headers below
 * its header directories, and its textual headers.
 */
fun ownedFiles(directory: Path, settings: TargetSettings, fileSets: FileSets): List<Path> {
  val files = linkedSetOf<Path>()
  files += fileSets.expand(directory, settings.sources, settings.excludes)
  for (headers in settings.headers + settings.privateHeaders) {
    val start = directory.resolve(headers).normalize()
    if (!Files.isDirectory(start)) continue
    Files.walk(start).use { paths ->
      paths
        .filter { Files.isRegularFile(it) && extensionOf(it) in HEADER_EXTENSIONS }
        .sorted()
        .forEach(files::add)
    }
  }
  files += fileSets.expand(directory, settings.textualHeaders)
  return files.toList()
}

private fun extensionOf(file: Path) =
  file.fileName.toString().substringAfterLast('.', "").lowercase()

/** What a formatter or linter run found. */
data class ToolResult(val filesChecked: Int, val failures: Int)

/**
 * Formats files with the tools of their languages: clang-format for C, C++, Objective-C and
 * Objective-C++ and their headers, `zig fmt` for Zig. Each tool finds its own configuration, such
 * as the `.clang-format` above a file.
 */
class Formatter(
  private val programs: Programs,
  private val processes: Processes,
  private val out: Appendable,
) {
  /** Formats [files] in place, or with [check] only reports those that are not formatted. */
  fun format(
    files: List<Path>,
    check: Boolean,
    extraArguments: List<String>,
    directory: Path,
  ): ToolResult {
    val clang = files.filter { extensionOf(it) in CLANG_FORMAT_EXTENSIONS }
    val zig = files.filter { extensionOf(it) == "zig" }
    var failures = 0
    if (clang.isNotEmpty()) {
      val tool =
        programs.find("clang-format") ?: throw ProjectException("clang-format is not on PATH")
      val flags = if (check) listOf("--dry-run", "--Werror") else listOf("-i")
      for (chunk in clang.chunked(FILES_PER_RUN)) {
        failures +=
          run(
            listOf(tool.toString()) + flags + extraArguments + chunk.map(Path::toString),
            directory,
          )
      }
    }
    if (zig.isNotEmpty()) {
      val tool = programs.find("zig") ?: throw ProjectException("zig is not on PATH")
      val flags = if (check) listOf("fmt", "--check") else listOf("fmt")
      for (chunk in zig.chunked(FILES_PER_RUN)) {
        failures +=
          run(
            listOf(tool.toString()) + flags + extraArguments + chunk.map(Path::toString),
            directory,
          )
      }
    }
    return ToolResult(clang.size + zig.size, failures)
  }

  private fun run(command: List<String>, directory: Path): Int {
    val result = processes.runGathered(command, directory)
    if (result.output.isNotBlank())
      synchronized(out) { out.append(result.output.trimEnd()).appendLine() }
    return if (result.exitCode == 0) 0 else 1
  }
}

/** One entry of a compilation database, as clangd and clang-tidy read it. */
data class CompileEntry(
  val label: com.progmasoft.tazel.label.TargetLabel,
  val directory: Path,
  val file: Path,
  val arguments: List<String>,
  val output: Path,
)

/**
 * The compilation database of a plan: one entry for each translation unit of its C-family targets,
 * with the command `tazel build` would run, less its depfile options.
 */
fun compileEntries(
  plan: BuildPlan,
  toolchain: CcToolchain,
  fileSets: FileSets,
): List<CompileEntry> {
  val entries = mutableListOf<CompileEntry>()
  for (target in plan.targets) {
    if (target.language == Language.ZIG) continue
    val settings = target.target.settings
    val commands =
      CcCommands(plan, toolchain.compiler(settings.compiler, target.language, plan.triple))
    commands.stageHeaders(target)
    val sources =
      commands.translationUnits(
        target,
        fileSets.expand(target.packageDirectory, settings.sources, settings.excludes),
      )
    for (source in sources) {
      val objectFile = commands.objectOf(target, source)
      val depfile = objectFile.resolveSibling(objectFile.fileName.toString() + ".d")
      val command = commands.compile(target, source, objectFile, depfile).toMutableList()
      val md = command.indexOf("-MD")
      if (md >= 0 && command.getOrNull(md + 1) == "-MF") repeat(3) { command.removeAt(md) }
      entries += CompileEntry(target.label, target.packageDirectory, source, command, objectFile)
    }
  }
  return entries
}

/** Writes [entries] as `compile_commands.json` into [directory] and returns the file. */
fun writeCompileCommands(directory: Path, entries: List<CompileEntry>): Path {
  val text =
    entries.joinToString(",\n", prefix = "[\n", postfix = "\n]\n") { entry ->
      "  {\"directory\": ${json(entry.directory.toString())}, \"file\": ${json(entry.file.toString())}, " +
        "\"output\": ${json(entry.output.toString())}, \"arguments\": [" +
        entry.arguments.joinToString(", ") { json(it) } +
        "]}"
    }
  Files.createDirectories(directory)
  val file = directory.resolve("compile_commands.json")
  Files.writeString(file, text)
  return file
}

private fun json(text: String): String {
  val escaped = StringBuilder("\"")
  for (character in text) {
    when {
      character == '"' -> escaped.append("\\\"")
      character == '\\' -> escaped.append("\\\\")
      character == '\n' -> escaped.append("\\n")
      character == '\r' -> escaped.append("\\r")
      character == '\t' -> escaped.append("\\t")
      character < ' ' -> escaped.append("\\u%04x".format(character.code))
      else -> escaped.append(character)
    }
  }
  return escaped.append('"').toString()
}

/**
 * Runs clang-tidy on translation units, with the compilation database in [database], [jobs] at a
 * time.
 */
class Linter(
  private val programs: Programs,
  private val processes: Processes,
  private val out: Appendable,
  private val jobs: Int,
) {
  fun lint(
    entries: List<CompileEntry>,
    database: Path,
    fix: Boolean,
    extraArguments: List<String>,
  ): ToolResult {
    if (entries.isEmpty()) return ToolResult(0, 0)
    val tool = programs.find("clang-tidy") ?: throw ProjectException("clang-tidy is not on PATH")
    // Fixes to one header from two translation units at once would overwrite each other.
    val parallel = if (fix) 1 else jobs
    val failures =
      JobPool(parallel, keepGoing = true).map(entries, { "lint ${it.file}" }) { entry ->
        val command =
          listOf(tool.toString(), "-p", database.toString(), "--quiet") +
            (if (fix) listOf("--fix") else emptyList()) +
            extraArguments +
            entry.file.toString()
        val result = processes.runGathered(command, entry.directory)
        val output =
          result.output.lines().filterNot { it.matches(Regex("""^\d+ warnings? generated\.$""")) }
        if (output.any(String::isNotBlank)) {
          synchronized(out) { out.append(output.joinToString("\n").trimEnd()).appendLine() }
        }
        if (result.exitCode == 0) 0 else 1
      }
    return ToolResult(entries.size, failures.sum())
  }
}
