/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import kotlin.time.Duration
import kotlin.time.Duration.Companion.minutes

/** The operating system of a target triple. */
enum class Os {
  WINDOWS,
  LINUX,
  MACOS,
  BSD,
  UNIX,
}

/** The processor of a target triple. */
enum class Arch {
  X86,
  X86_64,
  ARM,
  ARM64,
  RISCV32,
  RISCV64,
  POWERPC64,
  S390X,
  LOONGARCH64,
}

/** Which build a command makes; `tazel build` makes [DEBUG] unless told otherwise. */
enum class BuildMode {
  DEBUG,
  RELEASE,
}

/** The compiler of a C, C++, Objective-C or Objective-C++ target. */
enum class Compiler {
  CLANG,
  GCC,
  MSVC,
  CLANG_CL,
  ZIG_CC,
}

/** The language standard of a C or Objective-C target. */
enum class CStandard {
  C89,
  C99,
  C11,
  C17,
  C23,
}

/** The language standard of a C++ or Objective-C++ target. */
enum class CppStandard {
  CPP11,
  CPP14,
  CPP17,
  CPP20,
  CPP23,
  CPP26,
}

/** How many warnings the compiler gives. */
enum class Warnings {
  ALL,
  DEFAULT,
  NONE,
}

/** Debug information: none, in the objects, or in separate files. */
enum class DebugInfo {
  NONE,
  FULL,
  SPLIT,
}

/** What the optimizer of a C-family compiler aims at. */
enum class Optimize {
  NONE,
  SIZE,
  SPEED,
}

/** The release mode of a Zig target: ReleaseSafe, ReleaseFast, ReleaseSmall. */
enum class ZigOptimize {
  SAFE,
  SPEED,
  SIZE,
}

/** Link-time optimization. */
enum class Lto {
  NONE,
  THIN,
  FULL,
}

/** A sanitizer the target is compiled and linked with. */
enum class Sanitizer {
  ADDRESS,
  UNDEFINED,
  THREAD,
  MEMORY,
  LEAK,
}

/** The Windows subsystem of a program. */
enum class Subsystem {
  CONSOLE,
  WINDOWS,
}

/** How big a test is; it chooses the timeout a test has when it sets none. */
enum class TestSize(val timeout: Duration) {
  SMALL(1.minutes),
  MEDIUM(5.minutes),
  LARGE(15.minutes),
  ENORMOUS(60.minutes),
}

/** How `tazel test` treats a test. */
enum class TestTag {
  /** Only run when its label is named, never through a pattern. */
  MANUAL,
  /** Runs alone, with no other test at the same time. */
  EXCLUSIVE,
  /** Runs on this machine, never on remote execution. */
  LOCAL,
  /** Its result is never taken from the cache. */
  NO_CACHE,
}
