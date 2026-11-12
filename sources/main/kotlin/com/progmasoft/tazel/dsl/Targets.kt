/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import kotlin.time.Duration

/** The language a target compiles. */
enum class Language(val keyword: String) {
  C("c"),
  CPP("cpp"),
  OBJC("objc"),
  OBJCPP("objcpp"),
  ZIG("zig"),
}

/** What a target produces. */
enum class TargetKind(val keyword: String) {
  BIN("bin"),
  STATICLIB("staticlib"),
  SHAREDLIB("sharedlib"),
  TEST("test");

  val isLibrary: Boolean
    get() = this == STATICLIB || this == SHAREDLIB
}

/**
 * Type markers of the language of a target scope. They are never instantiated; the scopes carry
 * them so that a setting a language does not have is a compile error in the project file.
 */
sealed interface LanguageMark {
  sealed interface CStandardFamily : LanguageMark

  sealed interface CppStandardFamily : LanguageMark

  sealed interface Objective : LanguageMark

  interface C : CStandardFamily

  interface Cpp : CppStandardFamily

  interface ObjC : CStandardFamily, Objective

  interface ObjCpp : CppStandardFamily, Objective

  interface Zig : LanguageMark
}

/** Type markers of the kind of a target scope, like [LanguageMark]. */
sealed interface KindMark {
  /** bin and test: Tazel runs them. */
  sealed interface Runnable : KindMark

  /** bin, sharedlib and test: the linker makes them. */
  sealed interface Linked : KindMark

  sealed interface Library : KindMark

  interface Bin : Runnable, Linked

  interface StaticLib : Library

  interface SharedLib : Library, Linked

  interface Test : Runnable, Linked
}

/** The `.app` bundle of an Objective-C or Objective-C++ program. */
data class Bundle(
  val identifier: String,
  val infoPlist: String?,
  val resources: List<FileSelection>,
  val codesignIdentity: String?,
)

/**
 * Everything a target block sets, for one triple and one build mode. A field a language or kind
 * does not have stays at its default.
 */
data class TargetSettings(
  val sources: List<FileSelection> = emptyList(),
  val excludes: List<FileSelection> = emptyList(),
  val headers: List<String> = emptyList(),
  val privateHeaders: List<String> = emptyList(),
  val textualHeaders: List<FileSelection> = emptyList(),
  val includePrefix: String? = null,
  val stripIncludePrefix: String? = null,
  val triples: List<String> = emptyList(),
  val depends: List<String> = emptyList(),
  val compiler: Compiler? = null,
  val cStandard: CStandard? = null,
  val cppStandard: CppStandard? = null,
  val gnuExtensions: Boolean = false,
  val defines: List<String> = emptyList(),
  val privateDefines: List<String> = emptyList(),
  val flags: List<String> = emptyList(),
  val warnings: Warnings = Warnings.DEFAULT,
  val warningsAsErrors: Boolean = false,
  val precompiledHeader: String? = null,
  val debugInfo: DebugInfo? = null,
  val optimize: Optimize? = null,
  val zigOptimize: ZigOptimize? = null,
  val lto: Lto = Lto.NONE,
  val strip: Boolean = false,
  val sanitizers: Set<Sanitizer> = emptySet(),
  val links: List<String> = emptyList(),
  val linkFlags: List<String> = emptyList(),
  val rpath: List<String> = emptyList(),
  val frameworks: List<String> = emptyList(),
  val data: List<FileSelection> = emptyList(),
  val outputName: String? = null,
  val args: List<String> = emptyList(),
  val env: Map<String, String> = emptyMap(),
  val pic: Boolean? = null,
  val alwaysLink: Boolean = false,
  val exports: List<String> = emptyList(),
  val soVersion: String? = null,
  val defFile: String? = null,
  val linkerScript: String? = null,
  val resources: List<String> = emptyList(),
  val subsystem: Subsystem = Subsystem.CONSOLE,
  val arc: Boolean = true,
  val bundle: Bundle? = null,
  val size: TestSize = TestSize.MEDIUM,
  val timeout: Duration? = null,
  val flaky: Boolean = false,
  val shards: Int = 1,
  val tags: Set<TestTag> = emptySet(),
  val linkStatic: Boolean = true,
) {
  /** The timeout of a test: its own, or the one of its [size]. */
  val effectiveTimeout: Duration
    get() = timeout ?: size.timeout
}

/**
 * The receiver of a target block. A block runs once for each triple and build mode it is built for;
 * [OS], [ARCH] and [BUILD_MODE] say which.
 */
@TazelDsl
abstract class TargetScope<out K : KindMark>
internal constructor(
  private val triple: TargetTriple,
  private val buildMode: BuildMode,
) {
  internal var settings = TargetSettings()

  @Suppress("PropertyName", "VariableNaming")
  val OS: Os
    get() = triple.os

  @Suppress("PropertyName", "VariableNaming")
  val ARCH: Arch
    get() = triple.arch

  @Suppress("PropertyName", "VariableNaming")
  val BUILD_MODE: BuildMode
    get() = buildMode

  fun sources(vararg paths: String) {
    settings =
      settings.copy(
        sources = settings.sources + paths.map { FilePath(requireRelativePath(it, "source")) }
      )
  }

  fun sources(vararg globs: Glob) {
    settings = settings.copy(sources = settings.sources + globs)
  }

  /** Labels of the libraries this target uses: `//dir/:name`, `:name`, `@dep//:name`. */
  fun depends(vararg labels: String) {
    settings = settings.copy(depends = settings.depends + labels)
  }

  /** LLVM target triples this target is built for; without any, the host's. */
  fun targetTriple(vararg triples: String) {
    triples.forEach(TargetTriple::parse)
    settings = settings.copy(triples = settings.triples + triples)
  }

  /** Files copied beside the output. */
  fun data(vararg paths: String) {
    settings =
      settings.copy(
        data = settings.data + paths.map { FilePath(requireRelativePath(it, "data file")) }
      )
  }

  fun data(vararg globs: Glob) {
    settings = settings.copy(data = settings.data + globs)
  }

  /** The file name of the output without prefix and suffix; default: the target name. */
  var outputName: String?
    get() = settings.outputName
    set(value) {
      settings = settings.copy(outputName = value?.let { requireText(it, "outputName") })
    }
}

/** The receiver of a C, C++, Objective-C or Objective-C++ target block. */
class CcTargetScope<out L : LanguageMark, out K : KindMark>
internal constructor(triple: TargetTriple, buildMode: BuildMode) :
  TargetScope<K>(triple, buildMode) {
  fun exclude(vararg paths: String) {
    settings =
      settings.copy(
        excludes = settings.excludes + paths.map { FilePath(requireRelativePath(it, "exclude")) }
      )
  }

  fun exclude(vararg globs: Glob) {
    settings = settings.copy(excludes = settings.excludes + globs)
  }

  /** Include directories this target and its dependents see. */
  fun headers(vararg directories: String) {
    settings =
      settings.copy(
        headers = settings.headers + directories.map { requireRelativePath(it, "header directory") }
      )
  }

  /** Include directories only this target sees. */
  fun privateHeaders(vararg directories: String) {
    settings =
      settings.copy(
        privateHeaders =
          settings.privateHeaders + directories.map { requireRelativePath(it, "header directory") }
      )
  }

  /** Files that are included and never compiled alone. */
  fun textualHeaders(vararg paths: String) {
    settings =
      settings.copy(
        textualHeaders =
          settings.textualHeaders +
            paths.map { FilePath(requireRelativePath(it, "textual header")) }
      )
  }

  fun textualHeaders(vararg globs: Glob) {
    settings = settings.copy(textualHeaders = settings.textualHeaders + globs)
  }

  var includePrefix: String?
    get() = settings.includePrefix
    set(value) {
      settings =
        settings.copy(includePrefix = value?.let { requireRelativePath(it, "includePrefix") })
    }

  var stripIncludePrefix: String?
    get() = settings.stripIncludePrefix
    set(value) {
      settings =
        settings.copy(
          stripIncludePrefix = value?.let { requireRelativePath(it, "stripIncludePrefix") }
        )
    }

  /** Default: the compiler found on the machine. */
  var compiler: Compiler?
    get() = settings.compiler
    set(value) {
      settings = settings.copy(compiler = value)
    }

  var gnuExtensions: Boolean
    get() = settings.gnuExtensions
    set(value) {
      settings = settings.copy(gnuExtensions = value)
    }

  /** Definitions this target and its dependents are compiled with. */
  fun defines(vararg definitions: String) {
    settings =
      settings.copy(defines = settings.defines + definitions.map { requireText(it, "a define") })
  }

  /** Definitions only this target is compiled with. */
  fun privateDefines(vararg definitions: String) {
    settings =
      settings.copy(
        privateDefines = settings.privateDefines + definitions.map { requireText(it, "a define") }
      )
  }

  /** Compiler flags, given as they are. */
  fun flags(vararg flags: String) {
    settings = settings.copy(flags = settings.flags + flags)
  }

  var warnings: Warnings
    get() = settings.warnings
    set(value) {
      settings = settings.copy(warnings = value)
    }

  var warningsAsErrors: Boolean
    get() = settings.warningsAsErrors
    set(value) {
      settings = settings.copy(warningsAsErrors = value)
    }

  var precompiledHeader: String?
    get() = settings.precompiledHeader
    set(value) {
      settings =
        settings.copy(
          precompiledHeader = value?.let { requireRelativePath(it, "precompiledHeader") }
        )
    }

  /** Default: FULL in DEBUG, NONE in RELEASE. */
  var debugInfo: DebugInfo?
    get() = settings.debugInfo
    set(value) {
      settings = settings.copy(debugInfo = value)
    }

  /** Default: NONE in DEBUG, SPEED in RELEASE. */
  var optimize: Optimize?
    get() = settings.optimize
    set(value) {
      settings = settings.copy(optimize = value)
    }

  var lto: Lto
    get() = settings.lto
    set(value) {
      settings = settings.copy(lto = value)
    }

  var strip: Boolean
    get() = settings.strip
    set(value) {
      settings = settings.copy(strip = value)
    }

  fun sanitize(vararg sanitizers: Sanitizer) {
    settings = settings.copy(sanitizers = settings.sanitizers + sanitizers)
  }

  /** Libraries the linker takes by name, such as `m` for libm. */
  fun links(vararg libraries: String) {
    settings =
      settings.copy(links = settings.links + libraries.map { requireText(it, "a library") })
  }

  fun linkFlags(vararg flags: String) {
    settings = settings.copy(linkFlags = settings.linkFlags + flags)
  }

  /** Apple frameworks, such as `Foundation`. */
  fun frameworks(vararg names: String) {
    settings =
      settings.copy(frameworks = settings.frameworks + names.map { requireText(it, "a framework") })
  }
}

/** The receiver of a Zig target block; Zig targets have no compiler and no standard. */
class ZigTargetScope<out K : KindMark>
internal constructor(triple: TargetTriple, buildMode: BuildMode) :
  TargetScope<K>(triple, buildMode) {
  /** Default: Debug in DEBUG, SAFE in RELEASE. */
  var optimize: ZigOptimize?
    get() = settings.zigOptimize
    set(value) {
      settings = settings.copy(zigOptimize = value)
    }
}

@TazelDsl
class BundleScope internal constructor() {
  var identifier: String? = null
  var infoPlist: String? = null
  var codesignIdentity: String? = null
  private val resources = mutableListOf<FileSelection>()

  fun resources(vararg paths: String) {
    paths.forEach { resources += FilePath(requireRelativePath(it, "bundle resource")) }
  }

  fun resources(vararg globs: Glob) {
    resources += globs
  }

  internal fun build(): Bundle =
    Bundle(
      identifier =
        requireText(
          identifier ?: throw ProjectException("bundle { } has no identifier"),
          "identifier",
        ),
      infoPlist = infoPlist?.let { requireRelativePath(it, "infoPlist") },
      resources = resources.toList(),
      codesignIdentity = codesignIdentity,
    )
}
