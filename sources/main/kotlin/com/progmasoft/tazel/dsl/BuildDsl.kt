/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.label.Labels

/**
 * A target as its `BUILD.kts` declares it. Its block is not run when the file runs: it runs for
 * each triple and build mode the target is built for, see [configure].
 */
class TargetDeclaration
internal constructor(
  val name: String,
  val language: Language,
  val kind: TargetKind,
  private val block: (TargetTriple, BuildMode) -> TargetSettings,
) {
  /** What the block sets for [triple] in [mode]. */
  fun configure(triple: TargetTriple, mode: BuildMode): TargetSettings = block(triple, mode)
}

/** A `BUILD.kts` after it ran. */
data class BuildFile(val isPublic: Boolean, val targets: List<TargetDeclaration>)

/** The top level of a `BUILD.kts`. */
@TazelDsl
class BuildFileScope internal constructor() {
  /** false: only targets of this file may depend on its targets; true: every package and module. */
  var isPublic: Boolean = false

  private val targets = mutableListOf<TargetDeclaration>()

  val c = CcTargets<LanguageMark.C>(Language.C, ::declare)
  val cpp = CcTargets<LanguageMark.Cpp>(Language.CPP, ::declare)
  val objc = CcTargets<LanguageMark.ObjC>(Language.OBJC, ::declare)
  val objcpp = CcTargets<LanguageMark.ObjCpp>(Language.OBJCPP, ::declare)
  val zig = ZigTargets(::declare)

  private fun declare(declaration: TargetDeclaration) {
    Labels.requireName(declaration.name, "target name")
    if (targets.any { it.name == declaration.name }) {
      throw ProjectException("the target :${declaration.name} is declared twice")
    }
    targets += declaration
  }

  internal fun build() = BuildFile(isPublic, targets.toList())
}

/** `c`, `cpp`, `objc`, `objcpp`: the four kinds of target of one C-family language. */
class CcTargets<L : LanguageMark>
internal constructor(
  private val language: Language,
  private val declare: (TargetDeclaration) -> Unit,
) {
  fun bin(name: String, block: CcTargetScope<L, KindMark.Bin>.() -> Unit) =
    add(name, TargetKind.BIN, block)

  fun staticlib(name: String, block: CcTargetScope<L, KindMark.StaticLib>.() -> Unit) =
    add(name, TargetKind.STATICLIB, block)

  fun sharedlib(name: String, block: CcTargetScope<L, KindMark.SharedLib>.() -> Unit) =
    add(name, TargetKind.SHAREDLIB, block)

  fun test(name: String, block: CcTargetScope<L, KindMark.Test>.() -> Unit) =
    add(name, TargetKind.TEST, block)

  private fun <K : KindMark> add(
    name: String,
    kind: TargetKind,
    block: CcTargetScope<L, K>.() -> Unit,
  ) {
    declare(
      TargetDeclaration(name, language, kind) { triple, mode ->
        CcTargetScope<L, K>(triple, mode).apply(block).settings
      }
    )
  }
}

/** `zig`: the four kinds of Zig target. */
class ZigTargets internal constructor(private val declare: (TargetDeclaration) -> Unit) {
  fun bin(name: String, block: ZigTargetScope<KindMark.Bin>.() -> Unit) =
    add(name, TargetKind.BIN, block)

  fun staticlib(name: String, block: ZigTargetScope<KindMark.StaticLib>.() -> Unit) =
    add(name, TargetKind.STATICLIB, block)

  fun sharedlib(name: String, block: ZigTargetScope<KindMark.SharedLib>.() -> Unit) =
    add(name, TargetKind.SHAREDLIB, block)

  fun test(name: String, block: ZigTargetScope<KindMark.Test>.() -> Unit) =
    add(name, TargetKind.TEST, block)

  private fun <K : KindMark> add(
    name: String,
    kind: TargetKind,
    block: ZigTargetScope<K>.() -> Unit,
  ) {
    declare(
      TargetDeclaration(name, Language.ZIG, kind) { triple, mode ->
        ZigTargetScope<K>(triple, mode).apply(block).settings
      }
    )
  }
}
