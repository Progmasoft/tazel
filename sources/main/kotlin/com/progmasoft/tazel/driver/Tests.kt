/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import com.progmasoft.tazel.dsl.TestTag
import com.progmasoft.tazel.label.TargetLabel
import java.nio.file.Files
import java.nio.file.Path
import kotlin.time.Duration

/** How a test ended. */
enum class TestStatus(val passed: Boolean) {
  PASSED(true),
  /** Passed after it failed: only a flaky test is run again. */
  FLAKY(true),
  /** Passed before, and nothing it runs with changed since. */
  CACHED(true),
  FAILED(false),
  TIMEOUT(false),
}

/** The result of one test target, all of its shards together. */
data class TestResult(
  val label: TargetLabel,
  val status: TestStatus,
  val elapsed: Duration,
  /** One log per shard, of its last attempt. */
  val logs: List<Path>,
)

/**
 * Runs test targets as Bazel runs them: each in its output directory, beside its data files, with
 * its args and env, split into [shards][com.progmasoft.tazel.dsl.TargetSettings.shards] that see
 * `TEST_TOTAL_SHARDS` and `TEST_SHARD_INDEX`, stopped at its timeout, and run up to three times
 * when it is flaky. A test that passed is not run again while its program, its data, its args and
 * its env are the same, unless it is tagged NO_CACHE. Tests tagged EXCLUSIVE run alone, after the
 * others.
 */
class TestRunner(
  private val processes: Processes,
  private val out: Appendable,
  private val jobs: Int,
  private val fileSets: FileSets,
  private val digests: Digests = Digests(),
) {
  /** Runs [tests], each with [extraArguments] after its own args. */
  fun run(
    tests: List<PlannedTarget>,
    extraArguments: List<String> = emptyList(),
  ): List<TestResult> {
    val (exclusive, shared) = tests.partition { TestTag.EXCLUSIVE in it.target.settings.tags }
    val results =
      JobPool(jobs, keepGoing = true).map(shared, { "test ${it.label}" }) {
        test(it, extraArguments)
      } + exclusive.map { test(it, extraArguments) }
    return results.sortedBy { it.label.toString() }
  }

  private fun test(target: PlannedTarget, extraArguments: List<String>): TestResult {
    val settings = target.target.settings
    val directory = target.product.parent
    val command = listOf(target.product.toString()) + settings.args + extraArguments
    val key = cacheKey(target, command)
    val record = directory.resolve(target.product.fileName.toString() + ".passed")
    val cacheable = TestTag.NO_CACHE !in settings.tags
    if (cacheable && Files.isRegularFile(record) && Files.readString(record) == key) {
      val logs = (0 until settings.shards).map { logOf(target, it) }
      return report(TestResult(target.label, TestStatus.CACHED, Duration.ZERO, logs))
    }
    Files.deleteIfExists(record)
    val attempts = if (settings.flaky) 3 else 1
    var elapsed = Duration.ZERO
    var status = TestStatus.PASSED
    val logs = mutableListOf<Path>()
    for (shard in 0 until settings.shards) {
      val environment = buildMap {
        putAll(settings.env)
        put("TEST_TARGET", target.label.toString())
        put("TEST_TMPDIR", temporaryDirectory(target, shard).toString())
        if (settings.shards > 1) {
          put("TEST_TOTAL_SHARDS", settings.shards.toString())
          put("TEST_SHARD_INDEX", shard.toString())
        }
      }
      val log = logOf(target, shard)
      var shardStatus = TestStatus.FAILED
      for (attempt in 1..attempts) {
        val run =
          processes.runLogged(command, directory, environment, log, settings.effectiveTimeout)
        elapsed += run.elapsed
        shardStatus =
          when (run.exitCode) {
            null -> TestStatus.TIMEOUT
            0 -> if (attempt == 1) TestStatus.PASSED else TestStatus.FLAKY
            else -> TestStatus.FAILED
          }
        if (shardStatus.passed) break
      }
      logs.add(log)
      status = worse(status, shardStatus)
    }
    if (status.passed && cacheable) Files.writeString(record, key)
    return report(TestResult(target.label, status, elapsed, logs))
  }

  /** What a passing result depends on: the program, its data files, its command line and env. */
  private fun cacheKey(target: PlannedTarget, command: List<String>): String {
    val settings = target.target.settings
    val data = fileSets.expand(target.packageDirectory, settings.data)
    val parts =
      listOf(digests.of(target.product).orEmpty()) +
        data.map { "${target.packageDirectory.relativize(it)}=${digests.of(it)}" } +
        command.drop(1) +
        settings.env.toSortedMap().map { (name, value) -> "$name=$value" } +
        listOf("shards=${settings.shards}", "timeout=${settings.effectiveTimeout}")
    return Digests.sha256(parts.joinToString("\u0000").toByteArray())
  }

  private fun worse(a: TestStatus, b: TestStatus): TestStatus {
    val order =
      listOf(
        TestStatus.CACHED,
        TestStatus.PASSED,
        TestStatus.FLAKY,
        TestStatus.FAILED,
        TestStatus.TIMEOUT,
      )
    return if (order.indexOf(b) > order.indexOf(a)) b else a
  }

  private fun logOf(target: PlannedTarget, shard: Int): Path {
    val shards = target.target.settings.shards
    val name = if (shards > 1) "test.shard-${shard + 1}-of-$shards.log" else "test.log"
    return target.outputDirectory.resolve(name)
  }

  private fun temporaryDirectory(target: PlannedTarget, shard: Int): Path {
    val directory = target.outputDirectory.resolve("test-tmp").resolve(shard.toString())
    if (Files.exists(directory)) directory.toFile().deleteRecursively()
    return Files.createDirectories(directory)
  }

  private fun report(result: TestResult): TestResult {
    val time =
      if (result.status == TestStatus.CACHED) "(cached)"
      else "in %.1fs".format(java.util.Locale.ROOT, result.elapsed.inWholeMilliseconds / 1000.0)
    synchronized(out) {
      out.appendLine("%12s %s %s".format(result.status.name, result.label, time))
      if (!result.status.passed) {
        for (log in result.logs) {
          val lines = if (Files.isRegularFile(log)) Files.readAllLines(log) else emptyList()
          lines.takeLast(LOG_LINES).forEach { out.appendLine("    $it") }
          out.appendLine("    (log: $log)")
        }
      }
    }
    return result
  }

  private companion object {
    /** How much of the log of a failed test is shown; the whole log stays in its file. */
    const val LOG_LINES = 40
  }
}
