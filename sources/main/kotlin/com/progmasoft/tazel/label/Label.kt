/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.label

/** A label that cannot be read, with the reason. */
class LabelException(message: String) : IllegalArgumentException(message)

/**
 * A package: the directory of one `BUILD.kts`, relative to the root of its module, in a module that
 * is this one ([repository] `null`) or a dependency (its name, alias or local block name).
 */
data class PackageId(val repository: String?, val path: String) {
  /** `//dir/` or `@name//dir/`; the root package is `//`. */
  override fun toString(): String {
    val prefix = repository?.let { "@$it" } ?: ""
    return if (path.isEmpty()) "$prefix//" else "$prefix//$path/"
  }

  companion object {
    /** The root package of this module. */
    val ROOT = PackageId(null, "")
  }
}

/** What a command selects: one target, or every target of some packages. */
sealed interface Selection

/** One target: `//dir/:name`, `//:name`, `@dep//dir/:name`. */
data class TargetLabel(val packageId: PackageId, val name: String) : Selection {
  override fun toString(): String = "$packageId:$name"
}

/** `//dir/...`: the package [packageId] and every package below it; `//...` is the whole module. */
data class PackagePattern(val packageId: PackageId) : Selection {
  override fun toString(): String = "$packageId..."

  /** Whether [other] is this package or one below it. */
  fun covers(other: PackageId): Boolean =
    other.repository == packageId.repository &&
      (packageId.path.isEmpty() ||
        other.path == packageId.path ||
        other.path.startsWith(packageId.path + "/"))
}

/** Reads labels as the accepted forms spell them; nothing else is a label. */
object Labels {
  private val NAME = Regex("[A-Za-z0-9_+.-]+")

  /** A target label; `:name` is a target of [current]. Patterns are refused. */
  fun target(text: String, current: PackageId): TargetLabel =
    when (val selection = selection(text, current)) {
      is TargetLabel -> selection
      is PackagePattern ->
        throw LabelException("'$text' names packages, not one target; write //dir/:name")
    }

  /** A target label or a package pattern; `:name` is a target of [current]. */
  fun selection(text: String, current: PackageId): Selection {
    if (text.startsWith(":")) return TargetLabel(current, targetName(text.substring(1), text))
    val repository: String?
    val rest: String
    if (text.startsWith("@")) {
      val slashes = text.indexOf("//")
      if (slashes < 0) throw LabelException("'$text' has no //; write @name//:target")
      repository = checkedName(text.substring(1, slashes), "dependency name", text)
      rest = text.substring(slashes)
    } else {
      repository = null
      rest = text
    }
    if (!rest.startsWith("//")) {
      throw LabelException("'$text' is not a label; write //dir/:name, //:name or :name")
    }
    val body = rest.substring(2)
    if (body == "...") return PackagePattern(PackageId(repository, ""))
    if (body.endsWith("/...")) {
      return PackagePattern(PackageId(repository, packagePath(body.removeSuffix("/..."), text)))
    }
    val colon = body.lastIndexOf(':')
    if (colon < 0) {
      val shown = body.trimEnd('/')
      throw LabelException("'$text' names no target; write //$shown/:$shown or //$shown/...")
    }
    val directory = body.substring(0, colon)
    val path =
      when {
        directory.isEmpty() -> ""
        directory.endsWith("/") -> packagePath(directory.removeSuffix("/"), text)
        else -> throw LabelException("'$text': the package ends with /; write //$directory/:...")
      }
    return TargetLabel(PackageId(repository, path), targetName(body.substring(colon + 1), text))
  }

  /** Checks a target or package name as a project file writes it. */
  fun requireName(name: String, what: String): String = checkedName(name, what, name)

  private fun packagePath(path: String, text: String): String {
    if (path.isEmpty()) throw LabelException("'$text' has an empty package")
    for (segment in path.split('/')) {
      if (segment == "." || segment == "..") {
        throw LabelException("'$text': a package path has no . or .. segments")
      }
      checkedName(segment, "package directory", text)
    }
    return path
  }

  private fun targetName(name: String, text: String) = checkedName(name, "target name", text)

  private fun checkedName(name: String, what: String, text: String): String {
    if (!NAME.matches(name)) {
      throw LabelException(
        "'$text': the $what '$name' must be letters, digits and _ + . - " +
          "(kebab-case is recommended)"
      )
    }
    return name
  }
}
