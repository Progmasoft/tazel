/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import com.progmasoft.tazel.label.Labels

@DslMarker annotation class TazelDsl

/** What `module { }` says about the module. */
data class ModuleInfo(
  val name: String,
  val version: String?,
  val description: String?,
  val authors: List<String>,
  val license: String?,
)

/** A dependency from ViGet: [name] of [account], at [version], with the label `@[labelName]//`. */
data class RemoteDependency(
  val account: String,
  val name: String,
  val version: String,
  val alias: String?,
  val testOnly: Boolean,
) {
  val labelName: String
    get() = alias ?: name
}

/** Local `.vipkg` files under one label name, `@[name]//`. */
data class LocalDependency(
  val name: String,
  val packages: List<FileSelection>,
  val testOnly: Boolean,
)

/** What `viget { }` publishes. */
data class VigetSettings(
  val push: Boolean = false,
  val pushTargets: List<String> = emptyList(),
  val excludes: List<FileSelection> = emptyList(),
)

/** A remote cache. */
data class RemoteCache(
  val url: String,
  val read: Boolean,
  val write: Boolean,
  val token: String?,
  val publicKey: String?,
  val signingKey: String?,
)

/** What `cache { }` says. */
data class CacheSettings(
  val local: Boolean = true,
  val keep: Int = 8,
  val remotes: List<RemoteCache> = emptyList(),
)

/** A remote execution service. */
data class RemoteExecution(val url: String, val token: String?)

/** What `execution { }` says; [jobs] `null` means one per processor. */
data class ExecutionSettings(
  val jobs: Int? = null,
  val memory: ByteSize? = null,
  val keepGoing: Boolean = false,
  val remotes: List<RemoteExecution> = emptyList(),
)

/** A `MODULE.kts` after it ran. */
data class ModuleFile(
  val info: ModuleInfo,
  val remoteDependencies: List<RemoteDependency>,
  val localDependencies: List<LocalDependency>,
  val viget: VigetSettings,
  val cache: CacheSettings,
  val execution: ExecutionSettings,
) {
  /** The label names of the dependencies, each with whether only test targets may use it. */
  val dependencyNames: Map<String, Boolean>
    get() =
      remoteDependencies.associate { it.labelName to it.testOnly } +
        localDependencies.associate { it.name to it.testOnly }
}

/** The top level of a `MODULE.kts`. */
@TazelDsl
class ModuleFileScope internal constructor() {
  private var info: ModuleInfo? = null
  private var dependencies: DependenciesScope? = null
  private var viget: VigetSettings? = null
  private var cache: CacheSettings? = null
  private var execution: ExecutionSettings? = null

  fun module(block: ModuleScope.() -> Unit) {
    once(info, "module")
    info = ModuleScope().apply(block).build()
  }

  fun dependencies(block: DependenciesScope.() -> Unit) {
    once(dependencies, "dependencies")
    dependencies = DependenciesScope().apply(block)
  }

  fun viget(block: VigetScope.() -> Unit) {
    once(viget, "viget")
    viget = VigetScope().apply(block).build()
  }

  fun cache(block: CacheScope.() -> Unit) {
    once(cache, "cache")
    cache = CacheScope().apply(block).build()
  }

  fun execution(block: ExecutionScope.() -> Unit) {
    once(execution, "execution")
    execution = ExecutionScope().apply(block).build()
  }

  internal fun build(): ModuleFile {
    val info = info ?: throw ProjectException("MODULE.kts has no module { name = ... }")
    val dependencies = dependencies ?: DependenciesScope()
    val viget = viget ?: VigetSettings()
    val file =
      ModuleFile(
        info,
        dependencies.remote,
        dependencies.local,
        viget,
        cache ?: CacheSettings(),
        execution ?: ExecutionSettings(),
      )
    val names =
      file.remoteDependencies.map { it.labelName } + file.localDependencies.map { it.name }
    names
      .groupBy { it }
      .filterValues { it.size > 1 }
      .keys
      .firstOrNull()
      ?.let { throw ProjectException("two dependencies are called @$it; give one an alias") }
    if (viget.push) {
      if (info.version == null) throw ProjectException("viget { push = true } needs module.version")
      if (info.description == null) {
        throw ProjectException("viget { push = true } needs module.description")
      }
      if (info.authors.isEmpty()) {
        throw ProjectException("viget { push = true } needs at least one module author")
      }
      if (viget.pushTargets.isEmpty()) {
        throw ProjectException("viget { push = true } needs pushTarget(...)")
      }
    }
    return file
  }

  private fun once(previous: Any?, block: String) {
    if (previous != null) throw ProjectException("MODULE.kts has $block { } twice")
  }
}

@TazelDsl
class ModuleScope internal constructor() {
  var name: String? = null
  var version: String? = null
  var description: String? = null
  var license: String? = null
  private val authors = mutableListOf<String>()

  fun authors(vararg names: String) {
    names.forEach { authors += requireText(it, "an author") }
  }

  internal fun build(): ModuleInfo =
    ModuleInfo(
      name = Labels.requireName(name ?: throw ProjectException("module { } has no name"), "module"),
      version = version?.let { requireSemVer(it, "module.version") },
      description = description?.let { requireText(it, "module.description") },
      authors = authors.toList(),
      license = license?.let { requireText(it, "module.license") },
    )
}

@TazelDsl
class DependenciesScope internal constructor() {
  internal val remote = mutableListOf<RemoteDependency>()
  internal val local = mutableListOf<LocalDependency>()

  /** A package of [account] on ViGet that every target may depend on. */
  fun implementation(account: String, block: RemoteDependencyScope.() -> Unit) {
    remote += RemoteDependencyScope().apply(block).build(account, testOnly = false)
  }

  /** A package of [account] on ViGet that only test targets may depend on. */
  fun testImplementation(account: String, block: RemoteDependencyScope.() -> Unit) {
    remote += RemoteDependencyScope().apply(block).build(account, testOnly = true)
  }

  /** Local `.vipkg` files that every target may depend on. */
  fun localImplementation(block: LocalDependencyScope.() -> Unit) {
    local += LocalDependencyScope().apply(block).build(testOnly = false)
  }

  /** Local `.vipkg` files that only test targets may depend on. */
  fun localTestImplementation(block: LocalDependencyScope.() -> Unit) {
    local += LocalDependencyScope().apply(block).build(testOnly = true)
  }
}

@TazelDsl
class RemoteDependencyScope internal constructor() {
  var name: String? = null
  var version: String? = null
  /** The label name instead of [name]: `@alias//:target`. */
  var alias: String? = null

  internal fun build(account: String, testOnly: Boolean): RemoteDependency {
    val name =
      Labels.requireName(
        name ?: throw ProjectException("a dependency of $account has no name"),
        "dependency",
      )
    return RemoteDependency(
      account = Labels.requireName(account, "account"),
      name = name,
      version =
        requireSemVer(
          version ?: throw ProjectException("the dependency $account/$name has no version"),
          "the version of $account/$name",
        ),
      alias = alias?.let { Labels.requireName(it, "alias") },
      testOnly = testOnly,
    )
  }
}

@TazelDsl
class LocalDependencyScope internal constructor() {
  var name: String? = null
  private val packages = mutableListOf<FileSelection>()

  fun include(vararg paths: String) {
    paths.forEach { packages += FilePath(requireRelativePath(it, "local package")) }
  }

  fun include(vararg globs: Glob) {
    packages += globs
  }

  internal fun build(testOnly: Boolean): LocalDependency {
    val name =
      Labels.requireName(
        name ?: throw ProjectException("a local dependency has no name"),
        "dependency",
      )
    if (packages.isEmpty()) throw ProjectException("the local dependency $name includes nothing")
    return LocalDependency(name, packages.toList(), testOnly)
  }
}

@TazelDsl
class VigetScope internal constructor() {
  var push: Boolean = false
  private val targets = mutableListOf<String>()
  private val excludes = mutableListOf<FileSelection>()

  /** The targets the package on ViGet holds; their `BUILD.kts` must be public. */
  fun pushTarget(vararg labels: String) {
    targets += labels
  }

  fun exclude(vararg paths: String) {
    paths.forEach { excludes += FilePath(requireRelativePath(it, "viget exclude")) }
  }

  fun exclude(vararg globs: Glob) {
    excludes += globs
  }

  internal fun build() = VigetSettings(push, targets.toList(), excludes.toList())
}

@TazelDsl
class CacheScope internal constructor() {
  var local: Boolean = true
  /** How many results each target keeps in the local cache. */
  var keep: Int = 8
  private val remotes = mutableListOf<RemoteCache>()

  fun remote(url: String, block: RemoteCacheScope.() -> Unit = {}) {
    remotes += RemoteCacheScope().apply(block).build(requireUrl(url))
  }

  internal fun build(): CacheSettings {
    if (keep < 1) throw ProjectException("cache.keep is $keep; it keeps at least 1")
    return CacheSettings(local, keep, remotes.toList())
  }
}

@TazelDsl
class RemoteCacheScope internal constructor() {
  var read: Boolean = true
  var write: Boolean = false
  var token: String? = null
  var publicKey: String? = null
  var signingKey: String? = null

  internal fun build(url: String) = RemoteCache(url, read, write, token, publicKey, signingKey)
}

@TazelDsl
class ExecutionScope internal constructor() {
  var jobs: Int? = null
  var memory: ByteSize? = null
  var keepGoing: Boolean = false
  private val remotes = mutableListOf<RemoteExecution>()

  fun remote(url: String, block: RemoteExecutionScope.() -> Unit = {}) {
    remotes += RemoteExecutionScope().apply(block).build(requireUrl(url))
  }

  internal fun build(): ExecutionSettings {
    jobs?.let { if (it < 1) throw ProjectException("execution.jobs is $it; it runs at least 1") }
    memory?.let {
      if (it.bytes <= 0) throw ProjectException("execution.memory must be more than 0 bytes")
    }
    return ExecutionSettings(jobs, memory, keepGoing, remotes.toList())
  }
}

@TazelDsl
class RemoteExecutionScope internal constructor() {
  var token: String? = null

  internal fun build(url: String) = RemoteExecution(url, token)
}

private fun requireUrl(url: String): String {
  if (!url.startsWith("https://")) throw ProjectException("'$url' is not an https:// address")
  return url
}
