/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import java.time.Duration
import java.util.Locale
import java.util.concurrent.ConcurrentLinkedQueue

/** The phases of a run whose duration Tazel can report. */
enum class Phase(val label: String) {
  JVM_STARTUP("JVM startup"),
  IPC("IPC"),
  SCRIPT_HOST("Kotlin scripting host"),
  PLUGINS("plugin resolution and class loading"),
  COMPILATION("script compilation"),
  CACHE_LOAD("compiled script from cache"),
  EVALUATION("script evaluation"),
  COMMAND("command"),
}

/**
 * Durations of the phases of one run. Each phase is measured on its own, so a slow run shows which
 * part was slow: starting the JVM, the scripting host, plugins, compiling or evaluating the project
 * file, the command itself, or the round trip to the daemon.
 */
class Timings(val enabled: Boolean) {
  private val records = ConcurrentLinkedQueue<Pair<Phase, Duration>>()

  fun record(
    phase: Phase,
    duration: Duration,
  ) {
    if (enabled) records += phase to duration
  }

  /** Runs [block] and records how long it took under [phase]. */
  inline fun <T> measure(
    phase: Phase,
    block: () -> T,
  ): T {
    if (!enabled) return block()
    val start = System.nanoTime()
    try {
      return block()
    } finally {
      record(phase, Duration.ofNanos(System.nanoTime() - start))
    }
  }

  /** The total of each phase that was measured, in the order of [Phase]. */
  fun totals(): Map<Phase, Duration> =
    records
      .groupBy({ it.first }, { it.second })
      .mapValues { (_, durations) -> durations.fold(Duration.ZERO, Duration::plus) }
      .toSortedMap()

  /** One line per measured phase, aligned for a terminal. */
  fun report(): List<String> {
    val totals = totals()
    val width = totals.keys.maxOfOrNull { it.label.length } ?: 0
    return totals.map { (phase, duration) ->
      "${phase.label.padEnd(width)}  ${String.format(Locale.ROOT, "%.1f", duration.toNanos() / 1_000_000.0)} ms"
    }
  }

  companion object {
    /** Measuring is on when `TAZEL_TIMINGS` is `1` or `true`. */
    fun fromEnvironment(environment: (String) -> String? = System::getenv): Timings =
      Timings(environment("TAZEL_TIMINGS")?.lowercase() in setOf("1", "true"))

    val disabled = Timings(false)
  }
}
