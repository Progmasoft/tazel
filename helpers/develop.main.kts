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
 * Windows, Linux and macOS. The script needs only the `kotlin` launcher and JDK 25.
 */
@file:DependsOn("com.github.ajalt.clikt:clikt-jvm:5.1.0")

import com.github.ajalt.clikt.core.CliktCommand
import com.github.ajalt.clikt.core.Context
import com.github.ajalt.clikt.core.ProgramResult
import com.github.ajalt.clikt.core.UsageError
import com.github.ajalt.clikt.core.main
import com.github.ajalt.clikt.core.subcommands
import com.github.ajalt.clikt.parameters.arguments.argument
import com.github.ajalt.clikt.parameters.arguments.multiple
import com.github.ajalt.clikt.parameters.arguments.optional
import com.github.ajalt.clikt.parameters.options.flag
import com.github.ajalt.clikt.parameters.options.option
import java.io.File
import java.time.LocalDate

/** The repository root: the nearest directory above this script with `settings.gradle.kts`. */
val repository: File =
  generateSequence(__FILE__.absoluteFile.parentFile) { it.parentFile }
    .firstOrNull { File(it, "settings.gradle.kts").isFile }
    ?: error("helpers/develop.main.kts must live inside the Tazel checkout")

val isWindows = System.getProperty("os.name").startsWith("Windows")

/** Runs the Gradle wrapper with [arguments] in the repository and returns its exit code. */
fun gradle(vararg arguments: String): Int {
  val wrapper =
    if (isWindows) listOf("cmd", "/c", File(repository, "gradlew.bat").path)
    else listOf(File(repository, "gradlew").path)
  return ProcessBuilder(wrapper + arguments).directory(repository).inheritIO().start().waitFor()
}

/** Fails the command with Gradle's exit code unless it is zero. */
fun requireSuccess(code: Int) {
  if (code != 0) throw ProgramResult(code)
}

class Develop : CliktCommand(name = "develop") {
  override fun help(context: Context) = "Build, test and release helpers for Tazel."

  override fun run() = Unit
}

class Doctor : CliktCommand() {
  override fun help(context: Context) = "Explain whether this host can build Tazel."

  override fun run() {
    val java = System.getProperty("java.specification.version")
    val javaOk = java.toIntOrNull()?.let { it >= 25 } == true
    report(javaOk, "JDK $java runs this script; Tazel needs 25 or newer")
    val wrapper = File(repository, if (isWindows) "gradlew.bat" else "gradlew")
    report(wrapper.isFile, "Gradle wrapper at ${wrapper.relativeTo(repository)}")
    if (!isWindows) report(wrapper.canExecute(), "the Gradle wrapper is executable")
    if (!javaOk || !wrapper.isFile) throw ProgramResult(1)
  }

  private fun report(ok: Boolean, what: String) = echo("${if (ok) "ok  " else "FAIL"} $what")
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
  override fun help(context: Context) = "Run the unit tests."

  override fun run() = requireSuccess(gradle("test"))
}

class Check : CliktCommand() {
  override fun help(context: Context) =
    "Run what CI runs: formatting, compilation with warnings as errors, and every test."

  override fun run() = requireSuccess(gradle("check"))
}

class Format : CliktCommand() {
  override fun help(context: Context) = "Rewrite the sources in the project's format."

  override fun run() = requireSuccess(gradle("spotlessApply"))
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

class Clean : CliktCommand() {
  override fun help(context: Context) =
    "Remove the build output and the Gradle and Kotlin caches of this checkout."

  private val generated = listOf("build", ".gradle", ".kotlin")

  override fun run() {
    for (name in generated) {
      val target = File(repository, name).canonicalFile
      // Never follow a link or a path that leaves the checkout.
      if (target.parentFile != repository.canonicalFile) {
        throw UsageError("refusing to remove $target: it is outside ${repository.path}")
      }
      if (!target.exists()) continue
      if (!target.deleteRecursively()) {
        echo("Could not remove all of $name; stop the Gradle daemon and retry.", err = true)
        throw ProgramResult(1)
      }
      echo("Removed $name")
    }
  }
}

/** Matches `major.minor.patch`, the version form Tazel releases use. */
val versionPattern = Regex("""(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)""")
val buildVersionPattern = Regex("""(?m)^version = "([^"]*)"$""")

class Version : CliktCommand() {
  override fun help(context: Context) =
    "Check that build.gradle.kts and CHANGELOG.md agree on VERSION, by default the version " +
      "build.gradle.kts declares; with --set, write VERSION first."

  private val requested by argument("VERSION", help = "major.minor.patch").optional()
  private val set by
    option(
        "--set",
        help =
          "Write VERSION into build.gradle.kts and open its CHANGELOG section with today's date",
      )
      .flag()

  private val build = File(repository, "build.gradle.kts")
  private val changelog = File(repository, "CHANGELOG.md")

  override fun run() {
    val version = requested ?: declared() ?: throw UsageError("build.gradle.kts has no version")
    if (!versionPattern.matches(version)) throw UsageError("$version is not major.minor.patch")
    if (set) {
      if (requested == null) throw UsageError("--set needs a VERSION")
      write(version)
    }
    val problems = buildList {
      val declared = declared()
      if (declared != version) add("build.gradle.kts declares version \"$declared\"")
      if (!changelogHeading(version).containsMatchIn(changelog.readText())) {
        add("CHANGELOG.md has no \"## $version - YYYY-MM-DD\" section")
      }
    }
    problems.forEach { echo("FAIL $it", err = true) }
    if (problems.isNotEmpty()) throw ProgramResult(1)
    echo("Version $version is consistent.")
  }

  private fun declared() = buildVersionPattern.find(build.readText())?.groupValues?.get(1)

  private fun changelogHeading(version: String) =
    Regex("""(?m)^## ${Regex.escape(version)} - \d{4}-\d{2}-\d{2}$""")

  private fun write(version: String) {
    val text = build.readText()
    if (!buildVersionPattern.containsMatchIn(text)) {
      throw UsageError("build.gradle.kts has no top-level version line")
    }
    build.writeText(buildVersionPattern.replaceFirst(text, "version = \"$version\""))
    val log = changelog.readText()
    if (!changelogHeading(version).containsMatchIn(log)) {
      val unreleased = "## Unreleased\n"
      if (!log.contains(unreleased))
        throw UsageError("CHANGELOG.md has no \"## Unreleased\" section")
      // The notes gathered under Unreleased become the notes of the new version.
      changelog.writeText(
        log.replaceFirst(unreleased, "$unreleased\n## $version - ${LocalDate.now()}\n")
      )
    }
  }
}

Develop()
  .subcommands(Doctor(), Build(), Test(), Check(), Format(), Run(), Clean(), Version())
  .main(args)
