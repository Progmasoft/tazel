/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

// Settings only some languages or kinds have. Each is an extension on the target scope of exactly
// those, so writing one in another target is a compile error of the project file.

package com.progmasoft.tazel.dsl

import kotlin.time.Duration

// ----- By language -----

/** Default: the newest standard the compiler supports. */
var CcTargetScope<LanguageMark.CStandardFamily, KindMark>.standard: CStandard?
  @JvmName("getCStandard") get() = settings.cStandard
  @JvmName("setCStandard")
  set(value) {
    settings = settings.copy(cStandard = value)
  }

/** Default: the newest standard the compiler supports. */
var CcTargetScope<LanguageMark.CppStandardFamily, KindMark>.standard: CppStandard?
  @JvmName("getCppStandard") get() = settings.cppStandard
  @JvmName("setCppStandard")
  set(value) {
    settings = settings.copy(cppStandard = value)
  }

/** Automatic reference counting; default: true. */
var CcTargetScope<LanguageMark.Objective, KindMark>.arc: Boolean
  get() = settings.arc
  set(value) {
    settings = settings.copy(arc = value)
  }

/** Makes the program a `.app` bundle; default: a plain program. */
fun CcTargetScope<LanguageMark.Objective, KindMark.Bin>.bundle(block: BundleScope.() -> Unit) {
  if (settings.bundle != null) throw ProjectException("bundle { } is written twice")
  settings = settings.copy(bundle = BundleScope().apply(block).build())
}

/** The C header a Zig library offers. */
fun ZigTargetScope<KindMark.Library>.headers(vararg directories: String) {
  settings =
    settings.copy(
      headers = settings.headers + directories.map { requireRelativePath(it, "header directory") }
    )
}

// ----- By kind -----

/** What `tazel run` and `tazel test` pass to the program. */
fun TargetScope<KindMark.Runnable>.args(vararg arguments: String) {
  settings = settings.copy(args = settings.args + arguments)
}

/** The environment `tazel run` and `tazel test` give the program. */
fun TargetScope<KindMark.Runnable>.env(vararg variables: Pair<String, String>) {
  variables.forEach { (name, _) -> requireText(name, "an environment variable name") }
  settings = settings.copy(env = settings.env + variables)
}

/** Directories searched for shared libraries when the output runs, such as `$ORIGIN/../lib`. */
fun CcTargetScope<LanguageMark, KindMark.Linked>.rpath(vararg directories: String) {
  settings = settings.copy(rpath = settings.rpath + directories.map { requireText(it, "an rpath") })
}

var CcTargetScope<LanguageMark, KindMark.Linked>.linkerScript: String?
  get() = settings.linkerScript
  set(value) {
    settings = settings.copy(linkerScript = value?.let { requireRelativePath(it, "linkerScript") })
  }

/** Windows resource scripts (`.rc`): icon, version, manifest. */
fun CcTargetScope<LanguageMark, KindMark.Linked>.resources(vararg scripts: String) {
  settings =
    settings.copy(
      resources = settings.resources + scripts.map { requireRelativePath(it, "resource script") }
    )
}

/** The Windows subsystem; default: CONSOLE. */
var CcTargetScope<LanguageMark, KindMark.Bin>.subsystem: Subsystem
  get() = settings.subsystem
  set(value) {
    settings = settings.copy(subsystem = value)
  }

/** Position-independent code; default: true when a sharedlib depends on it. */
var CcTargetScope<LanguageMark, KindMark.StaticLib>.pic: Boolean?
  get() = settings.pic
  set(value) {
    settings = settings.copy(pic = value)
  }

/** Links every object of the archive (whole-archive); default: false. */
var CcTargetScope<LanguageMark, KindMark.StaticLib>.alwaysLink: Boolean
  get() = settings.alwaysLink
  set(value) {
    settings = settings.copy(alwaysLink = value)
  }

/** Symbol patterns the library exports; default: every symbol that is not hidden. */
fun CcTargetScope<LanguageMark, KindMark.SharedLib>.exports(vararg patterns: String) {
  settings =
    settings.copy(exports = settings.exports + patterns.map { requireText(it, "an export") })
}

/** `libname.so.1`; default: none. */
var CcTargetScope<LanguageMark, KindMark.SharedLib>.soVersion: String?
  get() = settings.soVersion
  set(value) {
    settings = settings.copy(soVersion = value?.let { requireText(it, "soVersion") })
  }

/** The Windows module-definition file. */
var CcTargetScope<LanguageMark, KindMark.SharedLib>.defFile: String?
  get() = settings.defFile
  set(value) {
    settings = settings.copy(defFile = value?.let { requireRelativePath(it, "defFile") })
  }

// ----- Tests -----

/** Default: MEDIUM. */
var TargetScope<KindMark.Test>.size: TestSize
  get() = settings.size
  set(value) {
    settings = settings.copy(size = value)
  }

/** Default: the timeout of [size]. */
var TargetScope<KindMark.Test>.timeout: Duration?
  get() = settings.timeout
  set(value) {
    if (value != null && !value.isPositive()) throw ProjectException("a test timeout is positive")
    settings = settings.copy(timeout = value)
  }

/** A failing flaky test is run again, up to 3 times. */
var TargetScope<KindMark.Test>.flaky: Boolean
  get() = settings.flaky
  set(value) {
    settings = settings.copy(flaky = value)
  }

/** Parts the test is split into (`TEST_TOTAL_SHARDS`, `TEST_SHARD_INDEX`); default: 1. */
var TargetScope<KindMark.Test>.shards: Int
  get() = settings.shards
  set(value) {
    if (value < 1) throw ProjectException("shards is $value; a test has at least 1")
    settings = settings.copy(shards = value)
  }

fun TargetScope<KindMark.Test>.tags(vararg tags: TestTag) {
  settings = settings.copy(tags = settings.tags + tags)
}

/** Links the libraries of the test statically; default: true. */
var CcTargetScope<LanguageMark, KindMark.Test>.linkStatic: Boolean
  get() = settings.linkStatic
  set(value) {
    settings = settings.copy(linkStatic = value)
  }
