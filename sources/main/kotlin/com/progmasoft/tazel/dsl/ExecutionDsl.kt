/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

private const val MEBIBYTE = 1L shl 20

/**
 * A machine that compiles for this one. [token] is left out of [toString], so it never reaches a
 * log.
 */
data class RemoteExecutionSettings(
  val url: String,
  val token: String? = null,
) {
  override fun toString() =
    "RemoteExecutionSettings(url=$url, token=${if (token == null) "none" else "set"})"
}

/**
 * How a command spends the machine. What is left null is decided at run time: the number of
 * processors for [jobs] and [cpus], no limit for [memory].
 */
data class ExecutionSettings(
  val jobs: Int? = null,
  val cpus: Int? = null,
  val memory: Long? = null,
  val memoryPerJob: Long = DEFAULT_MEMORY_PER_JOB,
  val keepGoing: Boolean = false,
  val incremental: Boolean = true,
  val profile: String? = null,
  val remote: RemoteExecutionSettings? = null,
) {
  companion object {
    const val DEFAULT_MEMORY_PER_JOB = 1024 * MEBIBYTE
  }
}

@DslMarker annotation class ExecutionDsl

@ExecutionDsl
class RemoteExecutionScope internal constructor() {
  var url: String? = null

  /** Sent as a bearer token. An empty one, such as a variable that is not set, counts as none. */
  var token: String? = null

  internal fun build() =
    remoteExecutionSettings(
      url ?: throw ProjectConfigurationException("execution.remote.url is required"),
      token,
    )
}

@ExecutionDsl
class ExecutionScope internal constructor() {
  /** How many compiler runs and test suites run at once. */
  var jobs: Int? = null

  /** How many processors a command may keep busy; [jobs] never goes above it. */
  var cpus: Int? = null

  /** How much memory the jobs of a command may hold together, such as "8g". */
  var memory: String? = null

  /** What one job is reckoned to hold; [memory] divided by this limits [jobs]. */
  var memoryPerJob: String? = null

  /** Whether the other jobs go on after one failed. */
  var keepGoing: Boolean = false

  /** Whether a debug build keeps an object per source, so that it lowers only what changed. */
  var incremental: Boolean = true

  /** Writes a Chrome trace of the jobs to this file, relative to the project file. */
  var profile: String? = null

  private var remote: RemoteExecutionSettings? = null

  fun remote(block: RemoteExecutionScope.() -> Unit) {
    if (remote != null) {
      throw ProjectConfigurationException("execution.remote may be configured only once")
    }
    remote = RemoteExecutionScope().apply(block).build()
  }

  private fun requirePositive(
    value: Int?,
    name: String,
  ) = value?.also {
    if (it < 1) {
      throw ProjectConfigurationException("execution.$name is $it and has to be at least 1")
    }
  }

  internal fun build() =
    ExecutionSettings(
      requirePositive(jobs, "jobs"),
      requirePositive(cpus, "cpus"),
      memory?.let { byteSize(it, "execution.memory") },
      memoryPerJob?.let { byteSize(it, "execution.memoryPerJob") }
        ?: ExecutionSettings.DEFAULT_MEMORY_PER_JOB,
      keepGoing,
      incremental,
      profile?.let { requireProjectDirectory(it, "execution.profile") },
      remote,
    )
}

/** The settings of a remote executor, whether a project file or a command line named it. */
fun remoteExecutionSettings(
  url: String,
  token: String?,
): RemoteExecutionSettings {
  val secret = token?.takeIf(String::isNotBlank)
  // The value is not part of the message: it is a secret.
  if (secret != null && secret.any(Char::isISOControl)) {
    throw ProjectConfigurationException("the token of a remote executor holds a control character")
  }
  return RemoteExecutionSettings(requireCacheUrl(url), secret)
}

private val BYTE_SIZE = Regex("""(\d{1,12})\s*([kmgt]?)(?:i?b)?""", RegexOption.IGNORE_CASE)

/** The bytes of a size such as `512m`, `8g` or `1024`; [what] names it in the message. */
fun byteSize(
  text: String,
  what: String,
): Long {
  val match =
    BYTE_SIZE.matchEntire(text.trim())
      ?: throw ProjectConfigurationException("$what is '$text', and a size looks like 512m or 8g")
  val (digits, unit) = match.destructured
  val shift =
    when (unit.lowercase()) {
      "k" -> 10
      "m" -> 20
      "g" -> 30
      "t" -> 40
      else -> 0
    }
  val size = digits.toLong()
  if (size == 0L || size > Long.MAX_VALUE shr shift) {
    throw ProjectConfigurationException("$what is '$text', which is no usable size")
  }
  return size shl shift
}

/**
 * How many jobs run at once: what was asked for, or one per processor, and never more than the
 * processors or the memory allow. One job always runs, however little there is to spend.
 */
fun effectiveJobs(
  jobs: Int?,
  cpus: Int?,
  memory: Long?,
  memoryPerJob: Long,
  processors: Int,
): Int =
  listOfNotNull(
      jobs ?: cpus ?: processors,
      cpus,
      memory?.let { (it / memoryPerJob).coerceIn(1, Int.MAX_VALUE.toLong()).toInt() },
    )
    .min()
    .coerceAtLeast(1)
