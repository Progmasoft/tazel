/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.Compiler
import com.progmasoft.tazel.dsl.Language
import com.progmasoft.tazel.dsl.Os
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.TargetTriple

/** A C-family compiler found on the machine, for one triple. */
data class CcCompiler(
  val compiler: Compiler,
  /** The driver of C and Objective-C, such as `[clang]` or `[zig, cc]`. */
  val c: List<String>,
  /** The driver of C++ and Objective-C++, which also links when any of those is in a program. */
  val cxx: List<String>,
  /** What makes static libraries, such as `[llvm-ar]`. */
  val archiver: List<String>,
  /** The flags that choose the triple; none for the host's. */
  val targetFlags: List<String>,
)

/**
 * Finds the compilers of C, C++, Objective-C and Objective-C++. Tazel picks no tool versions: it
 * calls the compiler it finds on `PATH`. Without a choice, Clang is used, then GCC, then `zig cc`.
 */
class CcToolchain(private val programs: Programs, private val host: TargetTriple) {
  private val cache = java.util.concurrent.ConcurrentHashMap<Pair<Compiler?, String>, CcCompiler>()

  fun compiler(choice: Compiler?, language: Language, triple: TargetTriple): CcCompiler =
    cache.getOrPut(choice to triple.text) { find(choice, language, triple) }

  private fun find(choice: Compiler?, language: Language, triple: TargetTriple): CcCompiler {
    val chosen =
      choice
        ?: when {
          has("clang") -> Compiler.CLANG
          has("gcc") && language != Language.OBJC && language != Language.OBJCPP -> Compiler.GCC
          has("zig") -> Compiler.ZIG_CC
          else ->
            throw ProjectException(
              "no C-family compiler on PATH: Tazel looks for clang, gcc and zig"
            )
        }
    val cross = triple.text != host.text
    return when (chosen) {
      Compiler.CLANG ->
        CcCompiler(
          chosen,
          listOf(require("clang")),
          listOf(require("clang++")),
          listOf(programs.find("llvm-ar")?.toString() ?: require("ar")),
          if (cross) listOf("--target=${triple.text}") else emptyList(),
        )
      Compiler.GCC -> {
        if (cross) {
          throw ProjectException(
            "GCC builds only for this machine ($host), not for $triple; " +
              "use Compiler.CLANG or Compiler.ZIG_CC to build for other triples"
          )
        }
        CcCompiler(
          chosen,
          listOf(require("gcc")),
          listOf(require("g++")),
          listOf(require("ar")),
          emptyList(),
        )
      }
      Compiler.ZIG_CC -> {
        val zig = require("zig")
        CcCompiler(
          chosen,
          listOf(zig, "cc"),
          listOf(zig, "c++"),
          listOf(zig, "ar"),
          if (cross) listOf("-target", zigTriple(triple)) else emptyList(),
        )
      }
      Compiler.MSVC,
      Compiler.CLANG_CL ->
        throw ProjectException(
          "Compiler.$chosen is not supported by this Tazel yet; use Compiler.CLANG or Compiler.ZIG_CC"
        )
    }
  }

  private fun has(name: String) = programs.find(name) != null

  private fun require(name: String): String =
    programs.find(name)?.toString() ?: throw ProjectException("$name is not on PATH")
}

/** The triple Zig spells for [triple]: `aarch64-apple-darwin` is `aarch64-macos`. */
fun zigTriple(triple: TargetTriple): String {
  val parts = triple.text.split('-')
  val rest = if (parts.size >= 3 && parts[1] in VENDORS) parts.drop(2) else parts.drop(1)
  val first = rest.first()
  val os =
    if (first.startsWith("darwin") || first.startsWith("macos")) "macos"
    else first.trimEnd { it.isDigit() || it == '.' }
  val arch = if (parts[0] == "arm64") "aarch64" else parts[0]
  return (listOf(arch, os) + rest.drop(1)).joinToString("-")
}

private val VENDORS = setOf("unknown", "pc", "apple", "w64", "none", "suse", "redhat")

/** The file names a triple gives the outputs of a target named [name]. */
object OutputNames {
  fun executable(name: String, os: Os) = if (os == Os.WINDOWS) "$name.exe" else name

  fun staticLibrary(name: String, os: Os) = if (os == Os.WINDOWS) "$name.lib" else "lib$name.a"

  fun sharedLibrary(name: String, os: Os, soVersion: String?) =
    when (os) {
      Os.WINDOWS -> "$name.dll"
      Os.MACOS -> if (soVersion == null) "lib$name.dylib" else "lib$name.$soVersion.dylib"
      else -> if (soVersion == null) "lib$name.so" else "lib$name.so.$soVersion"
    }

  fun objectSuffix(os: Os) = if (os == Os.WINDOWS) ".obj" else ".o"
}
