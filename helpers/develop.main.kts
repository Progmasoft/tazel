#!/usr/bin/env kotlin
/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

/**
 * Developer commands for the Tazel repository.
 *
 * Run from anywhere inside the checkout:
 * ```
 * kotlin helpers/develop.main.kts --help
 * ```
 *
 * Every Gradle step goes through the checked-in wrapper, so the commands behave the same on
 * Windows, Linux and macOS. The script needs only the `kotlin` launcher and JDK 25. Each program it
 * starts is one step: its output streams as it is, and a table of the steps and their times closes
 * every command that ran more than one.
 */
@file:DependsOn("com.github.ajalt.clikt:clikt-jvm:5.1.0")

import com.github.ajalt.clikt.core.CliktCommand
import com.github.ajalt.clikt.core.CliktError
import com.github.ajalt.clikt.core.Context
import com.github.ajalt.clikt.core.ProgramResult
import com.github.ajalt.clikt.core.UsageError
import com.github.ajalt.clikt.core.parse
import com.github.ajalt.clikt.core.subcommands
import com.github.ajalt.clikt.parameters.arguments.argument
import com.github.ajalt.clikt.parameters.arguments.multiple
import com.github.ajalt.clikt.parameters.arguments.optional
import com.github.ajalt.clikt.parameters.options.flag
import com.github.ajalt.clikt.parameters.options.multiple
import com.github.ajalt.clikt.parameters.options.option
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import java.time.Duration
import java.time.LocalDate
import java.util.concurrent.TimeUnit
import kotlin.system.exitProcess

/** The repository root: the nearest directory above this script with `settings.gradle.kts`. */
val repository: File =
  generateSequence(__FILE__.absoluteFile.parentFile) { it.parentFile }
    .firstOrNull { File(it, "settings.gradle.kts").isFile }
    ?: error("helpers/develop.main.kts must live inside the Tazel checkout")

val isWindows = System.getProperty("os.name").startsWith("Windows")

// ---------------------------------------------------------------------------------------------
// Steps

/** One program the script ran, for the summary. */
data class StepRecord(val title: String, val duration: Duration, val failed: Boolean)

val steps = mutableListOf<StepRecord>()
val inActions = System.getenv("GITHUB_ACTIONS") == "true"

fun formatDuration(duration: Duration): String =
  if (duration.toSeconds() < 60) "%.1fs".format(duration.toMillis() / 1000.0)
  else "${duration.toMinutes()}m${duration.toSecondsPart()}s"

/**
 * Runs [command] in [directory] as one step and returns its exit code. On GitHub Actions the output
 * is a collapsible group.
 */
fun step(title: String, command: List<String>, directory: File = repository): Int {
  if (inActions) println("::group::$title") else println("==> $title")
  val started = System.nanoTime()
  val code = ProcessBuilder(command).directory(directory).inheritIO().start().waitFor()
  val duration = Duration.ofNanos(System.nanoTime() - started)
  if (inActions) println("::endgroup::")
  steps += StepRecord(title, duration, code != 0)
  val result = if (code == 0) "finished" else "failed with exit code $code"
  println("==> $title $result in ${formatDuration(duration)}")
  return code
}

/** Runs a program and returns what it printed, or null when it is missing or fails. */
fun capture(vararg command: String, directory: File = repository): String? =
  runCatching {
      val process = ProcessBuilder(*command).directory(directory).redirectErrorStream(true).start()
      val output = process.inputStream.bufferedReader().readText()
      if (!process.waitFor(30, TimeUnit.SECONDS)) {
        process.destroyForcibly()
        return null
      }
      if (process.exitValue() == 0) output.trim() else null
    }
    .getOrNull()

/** The Gradle wrapper of the checkout, started the way the host needs. */
fun gradleCommand(vararg arguments: String): List<String> =
  (if (isWindows) listOf("cmd", "/c", File(repository, "gradlew.bat").path)
  else listOf(File(repository, "gradlew").path)) + arguments

/** Runs the Gradle wrapper with [arguments] and returns its exit code. */
fun gradle(vararg arguments: String): Int =
  step("gradlew ${arguments.joinToString(" ")}", gradleCommand(*arguments))

/** Fails the command with [code] unless it is zero. */
fun requireSuccess(code: Int) {
  if (code != 0) throw ProgramResult(code)
}

/** Prints the steps as a table once more than one ran. */
fun printSummary() {
  if (steps.size < 2) return
  val width = steps.maxOf { it.title.length }
  println()
  println("Step".padEnd(width) + "  Result  Time")
  for (step in steps) {
    val result = if (step.failed) "failed" else "ok    "
    println(step.title.padEnd(width) + "  $result  ${formatDuration(step.duration)}")
  }
  System.getenv("GITHUB_STEP_SUMMARY")?.let { path ->
    val rows =
      steps.joinToString("") {
        "| `${it.title}` | ${if (it.failed) "failed" else "ok"} | ${formatDuration(it.duration)} |\n"
      }
    File(path).appendText("\n| Step | Result | Time |\n| --- | --- | --- |\n$rows")
  }
}

// ---------------------------------------------------------------------------------------------
// Versions and the changelog

/** Matches `major.minor.patch`, the version form Tazel releases use. */
val versionPattern = Regex("""(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})""")
val buildVersionPattern = Regex("""(?m)^version = "([^"]*)"\r?$""")

fun parseVersion(text: String): String {
  if (!versionPattern.matches(text)) {
    throw UsageError("$text is not a version; expected major.minor.patch, such as 0.1.0")
  }
  return text
}

/** The version build.gradle.kts declares, or null. */
fun declaredVersion(build: String): String? = buildVersionPattern.find(build)?.groupValues?.get(1)

fun changelogHeading(version: String) =
  Regex("""(?m)^## ${Regex.escape(version)} - \d{4}-\d{2}-\d{2}\r?$""")

/** The notes of [version] in [changelog], without the heading, or null. */
fun releaseNotes(changelog: String, version: String): String? {
  val text = changelog.replace("\r\n", "\n")
  val heading = changelogHeading(version).find(text) ?: return null
  val body = text.substring(heading.range.last + 1)
  val end = body.indexOf("\n## ").let { if (it < 0) body.length else it }
  return body.substring(0, end).trim('\n').ifEmpty { null }?.plus("\n")
}

/**
 * Turns the Unreleased notes of [changelog] into the section of [version], dated [today]. Returns
 * null when there is nothing under Unreleased; a changelog that has the version is returned as it
 * is.
 */
fun startRelease(changelog: String, version: String, today: LocalDate): String? {
  if (changelogHeading(version).containsMatchIn(changelog)) return changelog
  val unreleased = "## Unreleased\n"
  val index = changelog.indexOf(unreleased)
  if (index < 0) throw UsageError("CHANGELOG.md has no \"## Unreleased\" section")
  val pending = changelog.substring(index + unreleased.length).substringBefore("\n## ")
  if (pending.isBlank()) return null
  return changelog.replaceFirst(unreleased, "$unreleased\n## $version - $today\n")
}

// ---------------------------------------------------------------------------------------------
// Markdown links

val skippedDirectories =
  setOf(".git", "build", ".gradle", ".kotlin", ".tazel", "node_modules", ".codex", ".claude")
val inlineLink = Regex("""!?\[[^\]]*]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)""")
val referenceLink = Regex("""(?m)^\s{0,3}\[[^\]]+]:\s*<?(\S+?)>?(?:\s+.*)?$""")
val markdownHeading = Regex("""(?m)^#{1,6}[ \t]+(.+?)[ \t#]*$""")
val fence = Regex("""^\s*(```|~~~)""")
val inlineCode = Regex("`[^`\n]*`")

/** Blanks fenced blocks, and inline code when [inline], keeping the line breaks. */
fun stripCode(text: String, inline: Boolean): String {
  var inside = false
  return text.split("\n").joinToString("\n") { line ->
    when {
      fence.containsMatchIn(line) -> {
        inside = !inside
        ""
      }
      inside -> ""
      inline -> inlineCode.replace(line) { " ".repeat(it.value.length) }
      else -> line
    }
  }
}

/** The anchor GitHub gives a heading. */
fun slug(heading: String): String =
  inlineCode
    .replace(heading.trim()) { it.value.trim('`') }
    .lowercase()
    .filter { it == ' ' || it == '-' || it == '_' || it.isLetterOrDigit() }
    .replace(' ', '-')

fun anchors(text: String): Set<String> {
  val seen = mutableMapOf<String, Int>()
  return markdownHeading
    .findAll(stripCode(text, inline = false))
    .map { match ->
      val base = slug(match.groupValues[1])
      val count = seen.merge(base, 1, Int::plus)!! - 1
      if (count == 0) base else "$base-$count"
    }
    .toSet()
}

/** A link that names nothing. */
data class BrokenLink(val file: String, val line: Int, val target: String, val reason: String) {
  override fun toString() = "$file:$line: $target: $reason"
}

/** Checks every relative link of every Markdown file below [root]; returns the broken ones. */
fun checkLinks(root: File): Pair<List<BrokenLink>, Int> {
  val files =
    root
      .walkTopDown()
      .onEnter { it == root || it.name !in skippedDirectories }
      .filter { it.isFile && it.extension.equals("md", ignoreCase = true) }
      .sortedBy { it.path }
      .toList()
  val anchorCache = mutableMapOf<File, Set<String>>()
  val broken = mutableListOf<BrokenLink>()
  var links = 0
  for (file in files) {
    val text = stripCode(file.readText().replace("\r\n", "\n"), inline = true)
    val relative = file.relativeTo(root).invariantSeparatorsPath
    val matches = inlineLink.findAll(text) + referenceLink.findAll(text)
    for (match in matches) {
      val target = match.groupValues[1]
      if ("://" in target || target.startsWith("mailto:")) continue
      links++
      val line = text.substring(0, match.range.first).count { it == '\n' } + 1
      fun report(reason: String) {
        broken += BrokenLink(relative, line, target, reason)
      }
      val path = target.substringBefore('#')
      val anchor = target.substringAfter('#', "")
      var destination = file
      if (path.isNotEmpty()) {
        if (path.startsWith("/")) {
          report("absolute paths do not work outside GitHub; use a relative link")
          continue
        }
        destination = File(file.parentFile, path)
        if (!destination.exists()) {
          report("no such file")
          continue
        }
        if (destination.isDirectory) {
          if (anchor.isNotEmpty()) report("a directory has no anchors")
          continue
        }
      }
      if (anchor.isEmpty() || !destination.extension.equals("md", ignoreCase = true)) continue
      val found =
        anchorCache.getOrPut(destination.canonicalFile) {
          anchors(destination.readText().replace("\r\n", "\n"))
        }
      if (anchor.lowercase() !in found) report("no heading has the anchor #$anchor")
    }
  }
  return broken to links
}

// ---------------------------------------------------------------------------------------------
// Bundles

fun sha256(file: File): String =
  MessageDigest.getInstance("SHA-256").digest(file.readBytes()).joinToString("") {
    "%02x".format(it)
  }

// ---------------------------------------------------------------------------------------------
// Commands

class Develop : CliktCommand(name = "develop") {
  override fun help(context: Context) = "Build, test, pack and release helpers for Tazel."

  override fun run() = Unit
}

/** A tool and what the doctor found about it. */
data class Finding(val name: String, val found: String?, val wanted: String, val ok: Boolean)

class Doctor : CliktCommand() {
  override fun help(context: Context) =
    "Explain whether this host can build Tazel, and which Tala tools Tazel would start."

  override fun run() {
    val java = System.getProperty("java.specification.version")
    val javaOk = java.toIntOrNull()?.let { it >= 25 } == true
    val wrapper = File(repository, if (isWindows) "gradlew.bat" else "gradlew")
    val required =
      listOf(
        Finding("JDK", java, "25 or newer", javaOk),
        Finding("Gradle wrapper", wrapper.takeIf { it.isFile }?.name, "checked in", wrapper.isFile),
        Finding(
          "Git",
          capture("git", "--version")?.removePrefix("git version "),
          "any",
          capture("git", "--version") != null,
        ),
      )
    // Tazel finds each tool through its environment variable, then PATH. They are needed only
    // to build Tala projects, not to build Tazel.
    val tala =
      listOf("talac", "talafmt", "talalint").map { tool ->
        val variable = "TAZEL_${tool.uppercase()}"
        val configured = System.getenv(variable)?.takeIf { it.isNotBlank() }
        val version =
          (configured ?: tool).let { capture(it, "--version") }?.lineSequence()?.firstOrNull()
        val source = if (configured != null) "$variable" else "PATH"
        Finding(tool, version?.let { "$it ($source)" }, "optional", true)
      }
    val all = required + tala
    val width = all.maxOf { it.name.length }
    for (finding in all) {
      val state =
        when {
          !finding.ok -> "FAIL"
          finding.found == null -> "----"
          else -> "ok  "
        }
      echo(
        "$state ${finding.name.padEnd(width)}  ${finding.found ?: "not found"}  (${finding.wanted})"
      )
    }
    if (!isWindows && wrapper.isFile && !wrapper.canExecute()) {
      echo("FAIL the Gradle wrapper is not executable; run chmod +x gradlew")
      throw ProgramResult(1)
    }
    if (required.any { !it.ok }) throw ProgramResult(1)
    echo("\nThe host can build Tazel.")
  }
}

class Build : CliktCommand() {
  override fun help(context: Context) =
    "Compile Tazel and install the launcher under build/install/tazel."

  override fun run() {
    requireSuccess(gradle("installDist"))
    val launcher = if (isWindows) "tazel.bat" else "tazel"
    echo("Installed ${File(repository, "build/install/tazel/bin/$launcher").path}")
  }
}

class Test : CliktCommand() {
  override fun help(context: Context) =
    "Run the unit tests, or those whose name matches each --tests filter."

  private val filters by
    option("--tests", help = "Gradle test filter, such as *LockTest").multiple()

  override fun run() =
    requireSuccess(gradle("test", *filters.flatMap { listOf("--tests", it) }.toTypedArray()))
}

class Check : CliktCommand() {
  override fun help(context: Context) =
    "Run what CI runs: the release metadata, the Markdown links, formatting, compilation with " +
      "warnings as errors, and every test."

  override fun run() {
    val build = File(repository, "build.gradle.kts").readText()
    val version = declaredVersion(build) ?: throw UsageError("build.gradle.kts has no version")
    echo("ok   build.gradle.kts declares $version")
    val (broken, links) = checkLinks(repository)
    broken.forEach { echo("FAIL $it", err = true) }
    if (broken.isNotEmpty()) throw ProgramResult(1)
    echo("ok   all $links relative Markdown links resolve")
    requireSuccess(gradle("check"))
  }
}

class Format : CliktCommand() {
  override fun help(context: Context) =
    "Rewrite the sources in the project's format, or with --check only report them."

  private val check by option("--check", help = "Only report sources that are not formatted").flag()

  override fun run() = requireSuccess(gradle(if (check) "spotlessCheck" else "spotlessApply"))
}

class Run : CliktCommand() {
  override fun help(context: Context) =
    "Run Tazel from the sources; put its arguments after --, as in `run -- lock graph`."

  private val arguments by argument().multiple()

  override fun run() {
    // `gradlew run --args` would join the arguments into one string and split it again, so an
    // argument with a space would not survive. Install the launcher and start it directly.
    requireSuccess(gradle("installDist", "--quiet"))
    val bin = File(repository, "build/install/tazel/bin")
    val launcher =
      if (isWindows) listOf("cmd", "/c", File(bin, "tazel.bat").path)
      else listOf(File(bin, "tazel").path)
    requireSuccess(ProcessBuilder(launcher + arguments).inheritIO().start().waitFor())
  }
}

class Bundle : CliktCommand() {
  override fun help(context: Context) =
    "Build the distribution archives, run the installed launcher with `version`, and write a " +
      ".sha256 file beside each archive."

  override fun run() {
    val version =
      declaredVersion(File(repository, "build.gradle.kts").readText())
        ?: throw UsageError("build.gradle.kts has no version")
    requireSuccess(gradle("installDist", "distZip", "distTar"))
    val bin = File(repository, "build/install/tazel/bin")
    val launcher =
      if (isWindows) arrayOf("cmd", "/c", File(bin, "tazel.bat").path)
      else arrayOf(File(bin, "tazel").path)
    // The installed launcher must run on its own and report the version it is released as.
    val answer = capture(*launcher, "version")?.lineSequence()?.lastOrNull()?.trim()
    if (answer != "tazel $version") {
      echo(
        "FAIL the installed tazel answered \"$answer\" to `tazel version`, not \"tazel $version\""
      )
      throw ProgramResult(1)
    }
    echo("ok   the installed launcher reports tazel $version")
    val distributions = File(repository, "build/distributions")
    val archives =
      listOf("zip", "tar").map { File(distributions, "tazel-$version.$it") }.filter { it.isFile }
    if (archives.size != 2) {
      echo("FAIL Gradle did not write both archives to ${distributions.path}")
      throw ProgramResult(1)
    }
    for (archive in archives) {
      val sum = sha256(archive)
      File(archive.path + ".sha256").writeText("$sum  ${archive.name}\n")
      echo("ok   ${archive.path}\n     SHA-256 $sum")
    }
  }
}

class Coverage : CliktCommand() {
  override fun help(context: Context) =
    "Run the tests with JaCoCo and print the line and branch coverage."

  override fun run() {
    requireSuccess(gradle("test", "jacocoTestReport"))
    val report = File(repository, "build/reports/jacoco/test/jacocoTestReport.xml")
    if (!report.isFile) {
      echo("FAIL JaCoCo wrote no report at ${report.path}")
      throw ProgramResult(1)
    }
    // The report's own counters are the last ones of the file, after every package.
    val counters =
      Regex("""<counter type="(\w+)" missed="(\d+)" covered="(\d+)"/>""")
        .findAll(report.readText())
        .associate {
          it.groupValues[1] to (it.groupValues[2].toLong() to it.groupValues[3].toLong())
        }
    for (type in listOf("LINE", "BRANCH", "METHOD")) {
      val (missed, covered) = counters[type] ?: continue
      val percent = if (missed + covered == 0L) 100.0 else 100.0 * covered / (missed + covered)
      echo("%-6s %5.1f%%  (%d of %d)".format(type.lowercase(), percent, covered, missed + covered))
    }
    echo(report.path)
  }
}

class Clean : CliktCommand() {
  override fun help(context: Context) =
    "Remove the build output and the Gradle and Kotlin caches of this checkout."

  private val dryRun by option("--dry-run", help = "Only report what would be removed").flag()
  private val generated = listOf("build", ".gradle", ".kotlin")

  override fun run() {
    var freed = 0L
    for (name in generated) {
      val target = File(repository, name).canonicalFile
      // Never follow a link or a path that leaves the checkout.
      if (target.parentFile != repository.canonicalFile) {
        throw UsageError("refusing to remove $target: it is outside ${repository.path}")
      }
      if (!target.exists()) continue
      val size = target.walkTopDown().filter { it.isFile }.sumOf { it.length() }
      freed += size
      if (dryRun) {
        echo("Would remove $name (${humanSize(size)})")
        continue
      }
      if (!target.deleteRecursively()) {
        echo("Could not remove all of $name; stop the Gradle daemon and retry.", err = true)
        throw ProgramResult(1)
      }
      echo("Removed $name (${humanSize(size)})")
    }
    echo("${if (dryRun) "Would free" else "Freed"} ${humanSize(freed)}")
  }

  private fun humanSize(bytes: Long): String {
    if (bytes < 1024) return "$bytes B"
    var value = bytes.toDouble()
    for (unit in listOf("KiB", "MiB", "GiB")) {
      value /= 1024
      if (value < 1024 || unit == "GiB") return "%.1f %s".format(value, unit)
    }
    return "$bytes B"
  }
}

class Version : CliktCommand() {
  override fun help(context: Context) =
    "Check that build.gradle.kts and CHANGELOG.md agree on VERSION, by default the version " +
      "build.gradle.kts declares; with --set, write VERSION first; with --release, also check " +
      "that the tag tazel-VERSION is new."

  private val requested by argument("VERSION", help = "major.minor.patch").optional()
  private val set by
    option(
        "--set",
        help =
          "Write VERSION into build.gradle.kts and open its CHANGELOG section with today's date",
      )
      .flag()
  private val release by
    option("--release", help = "Also require that the tag tazel-VERSION does not exist").flag()

  private val build = File(repository, "build.gradle.kts")
  private val changelog = File(repository, "CHANGELOG.md")

  override fun run() {
    val version =
      parseVersion(
        requested
          ?: declaredVersion(build.readText())
          ?: throw UsageError("build.gradle.kts has no version")
      )
    if (set) {
      if (requested == null) throw UsageError("--set needs a VERSION")
      write(version)
    }
    val problems = buildList {
      val declared = declaredVersion(build.readText())
      if (declared != version) add("build.gradle.kts declares version \"$declared\"")
      if (releaseNotes(changelog.readText(), version) == null) {
        add("CHANGELOG.md has no notes under \"## $version - YYYY-MM-DD\"")
      }
      if (release) {
        // A tag made on GitHub is not in the checkout until it is fetched, so origin is asked
        // as well as the local tags.
        val tag = "tazel-$version"
        val local = capture("git", "tag", "--list", tag)
        val remote = capture("git", "ls-remote", "--tags", "origin", "refs/tags/$tag")
        when {
          local == null || remote == null ->
            add("git could not list the tags of the checkout and origin")
          local.isNotEmpty() || remote.isNotEmpty() ->
            add("the tag $tag exists already; release tags never move")
        }
      }
    }
    problems.forEach { echo("FAIL $it", err = true) }
    if (problems.isNotEmpty()) throw ProgramResult(1)
    echo("Version $version is consistent${if (release) " and ready to be tagged" else ""}.")
  }

  private fun write(version: String) {
    val text = build.readText()
    if (!buildVersionPattern.containsMatchIn(text)) {
      throw UsageError("build.gradle.kts has no top-level version line")
    }
    build.writeText(buildVersionPattern.replaceFirst(text, "version = \"$version\""))
    // The notes gathered under Unreleased become the notes of the new version.
    val updated =
      startRelease(changelog.readText(), version, LocalDate.now())
        ?: throw UsageError(
          "CHANGELOG.md has nothing under \"## Unreleased\" to release as $version"
        )
    changelog.writeText(updated)
  }
}

class ReleaseNotes : CliktCommand(name = "release-notes") {
  override fun help(context: Context) =
    "Print the CHANGELOG.md section of VERSION, for the release page."

  private val version by argument("VERSION", help = "major.minor.patch")
  private val output by option("--output", help = "Write the notes to this file")

  override fun run() {
    val notes =
      releaseNotes(File(repository, "CHANGELOG.md").readText(), parseVersion(version))
        ?: throw UsageError("CHANGELOG.md has no notes under \"## $version - YYYY-MM-DD\"")
    output?.let { File(it).writeText(notes) } ?: echo(notes, trailingNewline = false)
  }
}

class Info : CliktCommand() {
  override fun help(context: Context) =
    "Report the checkout, its version and the tools on PATH, without changing anything."

  override fun run() {
    val build = File(repository, "build.gradle.kts").readText()
    echo("Checkout: ${repository.path}")
    echo("Host: ${System.getProperty("os.name")} ${System.getProperty("os.arch")}")
    echo("Version: ${declaredVersion(build) ?: "none"}")
    echo("JDK: ${System.getProperty("java.version")} (${System.getProperty("java.vendor")})")
    echo("Kotlin script runtime: ${KotlinVersion.CURRENT}")
    // Never the remotes, the environment or credentials: only what describes the checkout.
    echo("Branch: ${capture("git", "branch", "--show-current") ?: "unknown"}")
    echo("Revision: ${capture("git", "rev-parse", "HEAD") ?: "unknown"}")
    val dirty = capture("git", "status", "--porcelain", "--untracked-files=no")
    echo("Tracked changes: ${dirty?.isNotEmpty() ?: "unknown"}")
  }
}

class VerifyDocs : CliktCommand(name = "verify-docs") {
  override fun help(context: Context) =
    "Check that every relative Markdown link names a file that exists and every anchor a heading."

  override fun run() {
    val (broken, links) = checkLinks(repository)
    broken.forEach { echo(it.toString()) }
    if (broken.isNotEmpty()) {
      echo("${broken.size} of $links relative links are broken", err = true)
      throw ProgramResult(1)
    }
    echo("All $links relative links resolve.")
  }
}

class SelfTest : CliktCommand(name = "self-test") {
  override fun help(context: Context) =
    "Test the logic of this script: versions, the changelog, and the link checker."

  private var failures = 0

  private fun expect(label: String, passed: Boolean) {
    if (!passed) failures++
    echo("${if (passed) "PASS" else "FAIL"} $label")
  }

  override fun run() {
    expect("a version has three numbers", versionPattern.matches("0.10.2"))
    expect(
      "a prefixed tag or a leading zero is not a version",
      listOf("tazel-0.1.0", "01.0.0", "1.2", "1.2.3.4").none { versionPattern.matches(it) },
    )
    expect(
      "the declared version is read",
      declaredVersion("plugins {}\n\nversion = \"0.1.0\"\r\n") == "0.1.0",
    )
    val changelog = "# Changelog\n\n## Unreleased\n\n- Next.\n\n## 0.1.0 - 2026-10-18\n\nFirst.\n"
    expect("the notes of a version are its section", releaseNotes(changelog, "0.1.0") == "First.\n")
    expect("a missing version has no notes", releaseNotes(changelog, "0.2.0") == null)
    val started = startRelease(changelog, "0.2.0", LocalDate.of(2026, 10, 22))
    expect(
      "a release takes the unreleased notes",
      started != null && releaseNotes(started, "0.2.0") == "- Next.\n",
    )
    expect(
      "an empty Unreleased section is not a release",
      startRelease(started!!, "0.3.0", LocalDate.of(2026, 10, 22)) == null,
    )
    expect("headings become GitHub anchors", slug("`--check` mode") == "--check-mode")
    expect("repeated headings are numbered", anchors("# A\n## A\n") == setOf("a", "a-1"))

    val root = Files.createTempDirectory("develop-self-test").toFile()
    try {
      File(root, "docs").mkdirs()
      File(root, "docs/Guide.md").writeText("# Guide\n\n## Command line\n")
      File(root, "README.md")
        .writeText(
          "[ok](docs/Guide.md#command-line) [web](https://example.com/x)\n" +
            "`[code](missing.md)`\n```\n[fenced](missing.md)\n```\n" +
            "[gone](docs/Missing.md)\n[anchor](docs/Guide.md#nothing)\n"
        )
      File(root, "build").mkdirs()
      File(root, "build/README.md").writeText("[generated](missing.md)\n")
      val (broken, links) = checkLinks(root)
      expect("relative links are counted outside code", links == 3)
      expect(
        "broken files and anchors are reported with their lines",
        broken.map { "${it.line} ${it.reason}" } ==
          listOf("6 no such file", "7 no heading has the anchor #nothing"),
      )
    } finally {
      root.deleteRecursively()
    }
    if (failures != 0) throw ProgramResult(1)
  }
}

val develop =
  Develop()
    .subcommands(
      Doctor(),
      Build(),
      Test(),
      Check(),
      Format(),
      Run(),
      Bundle(),
      Coverage(),
      Clean(),
      Version(),
      ReleaseNotes(),
      Info(),
      VerifyDocs(),
      SelfTest(),
    )
// `main` would exit the process on an error before the summary is printed; parse instead, and
// leave with the status of the error once the steps are listed.
val status =
  try {
    develop.parse(args)
    0
  } catch (error: CliktError) {
    develop.echoFormattedHelp(error)
    error.statusCode
  }

printSummary()

exitProcess(status)
