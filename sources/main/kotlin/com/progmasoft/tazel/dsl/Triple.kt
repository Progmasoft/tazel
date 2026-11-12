/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

/** An LLVM target triple, such as `aarch64-apple-darwin`, with the [os] and [arch] it names. */
data class TargetTriple(val text: String, val os: Os, val arch: Arch) {
  override fun toString(): String = text

  companion object {
    /** Reads [text]; a triple whose processor or system Tazel does not know is refused. */
    fun parse(text: String): TargetTriple {
      val parts = text.split('-')
      if (parts.size < 2 || parts.any(String::isEmpty)) {
        throw ProjectException(
          "'$text' is not an LLVM target triple such as x86_64-unknown-linux-gnu"
        )
      }
      val arch =
        archOf(parts[0])
          ?: throw ProjectException("the target triple '$text' has a processor Tazel does not know")
      val os =
        parts.drop(1).firstNotNullOfOrNull(::osOf)
          ?: throw ProjectException("the target triple '$text' has a system Tazel does not know")
      return TargetTriple(text, os, arch)
    }

    /** The triple of the machine Tazel runs on. */
    fun host(): TargetTriple {
      val arch =
        when (System.getProperty("os.arch").lowercase()) {
          "amd64",
          "x86_64" -> "x86_64"
          "aarch64",
          "arm64" -> "aarch64"
          "riscv64" -> "riscv64"
          else -> throw ProjectException("Tazel does not know the processor of this machine")
        }
      val name = System.getProperty("os.name").lowercase()
      val rest =
        when {
          name.startsWith("windows") -> "pc-windows-msvc"
          name.startsWith("mac") -> "apple-darwin"
          name.startsWith("linux") -> "unknown-linux-gnu"
          name.startsWith("freebsd") -> "unknown-freebsd"
          else -> throw ProjectException("Tazel does not know the system of this machine")
        }
      return parse("$arch-$rest")
    }

    private fun archOf(part: String): Arch? =
      when {
        part == "x86_64" || part == "amd64" -> Arch.X86_64
        part.matches(Regex("i[3-6]86")) || part == "x86" -> Arch.X86
        part == "aarch64" || part == "arm64" || part == "arm64e" -> Arch.ARM64
        part.startsWith("arm") || part.startsWith("thumb") -> Arch.ARM
        part == "riscv32" -> Arch.RISCV32
        part == "riscv64" -> Arch.RISCV64
        part == "powerpc64" || part == "powerpc64le" || part == "ppc64" || part == "ppc64le" ->
          Arch.POWERPC64
        part == "s390x" -> Arch.S390X
        part == "loongarch64" -> Arch.LOONGARCH64
        else -> null
      }

    private fun osOf(part: String): Os? =
      when {
        part.startsWith("windows") || part == "mingw32" -> Os.WINDOWS
        part.startsWith("linux") -> Os.LINUX
        part.startsWith("darwin") || part.startsWith("macos") -> Os.MACOS
        listOf("freebsd", "netbsd", "openbsd", "dragonfly").any(part::startsWith) -> Os.BSD
        listOf("solaris", "illumos", "aix", "haiku", "hurd").any(part::startsWith) -> Os.UNIX
        else -> null
      }
  }
}
