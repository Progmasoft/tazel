/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.api.tazel.dsl.plugin

enum class OperatingSystem {
  LINUX,
  MACOS,
  WINDOWS,
  FREEBSD,
  OPENBSD,
  NETBSD,
  UNKNOWN,
}

enum class Architecture {
  X86_64,
  AARCH64,
  ARMV7H,
  RISCV64,
  UNKNOWN,
}

/** The machine Tazel runs on. */
data class Host(
  val os: OperatingSystem,
  val architecture: Architecture,
) {
  /** The extension the host gives an executable file: `.exe` on Windows, none elsewhere. */
  val executableExtension: String
    get() = if (os == OperatingSystem.WINDOWS) ".exe" else ""
}

/** The build mode of the project. */
enum class BuildMode {
  DEBUG,
  RELEASE,
}
