/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock

/** Several jobs failed; a command reports each of [failures]. */
class JobFailures(val failures: List<Exception>) : Exception("${failures.size} jobs failed")

/** The jobs of a command as a Chrome trace, which `chrome://tracing` and Perfetto read. */
class BuildProfile {
  private class Event(
    val name: String,
    val thread: Long,
    val start: Long,
    val duration: Long,
  )

  private val begun = System.nanoTime()
  private val events = ConcurrentLinkedQueue<Event>()

  /** Runs [work] and records it under [name]. */
  fun <T> measure(
    name: String,
    work: () -> T,
  ): T {
    val start = System.nanoTime()
    try {
      return work()
    } finally {
      val end = System.nanoTime()
      events +=
        Event(name, Thread.currentThread().threadId(), (start - begun) / 1000, (end - start) / 1000)
    }
  }

  fun write(file: Path) {
    file.parent?.let(Files::createDirectories)
    val lines =
      events.sortedBy(Event::start).joinToString(",\n") {
        val name = it.name.replace("\\", "\\\\").replace("\"", "\\\"")
        """{"name":"$name","ph":"X","pid":1,"tid":${it.thread},""" +
          """"ts":${it.start},"dur":${it.duration}}"""
      }
    Files.writeString(file, "[\n$lines\n]\n")
  }
}

/**
 * Runs the jobs of a command, at most [jobs] at a time and in the calling thread when that is one.
 *
 * After a failure no job starts unless [keepGoing] asks for it; the ones running are let finish, so
 * no compiler is left writing behind the back of the command.
 */
class JobPool(
  private val jobs: Int,
  private val keepGoing: Boolean,
  private val profile: BuildProfile? = null,
) {
  /** Whether jobs can overlap, so that what each one prints has to be kept together. */
  val concurrent: Boolean
    get() = jobs > 1

  /** The results of [work] for each of [items], in their order. */
  fun <T, R> map(
    items: List<T>,
    name: (T) -> String,
    work: (T) -> R,
  ): List<R> {
    val units = items.mapIndexed { index, item ->
      BuildUnit(index.toString(), name(item)) { work(item) }
    }
    return run(BuildGraph(units)).let { results -> units.map { results.getValue(it.id) } }
  }

  /**
   * The results of the units of [graph], by [BuildUnit.id]. A unit starts when what it depends on
   * has finished, and never after one of those failed.
   */
  fun <R> run(graph: BuildGraph<R>): Map<String, R> {
    val results = ConcurrentHashMap<String, Result<R>>()
    val failures = mutableListOf<Exception>()
    val stopped = AtomicBoolean()
    val waiting = graph.units.associate { it.id to it.dependsOn.size }.toMutableMap()
    val ready = ArrayDeque(graph.units.filter { it.dependsOn.isEmpty() })
    val finished = LinkedBlockingQueue<BuildUnit<R>>()
    val attempt = { unit: BuildUnit<R> ->
      try {
        if (!stopped.get()) {
          results[unit.id] = Result(profile?.measure(unit.name, unit.work) ?: unit.work())
        }
      } catch (failure: Exception) {
        synchronized(failures) { failures += failure }
        if (!keepGoing) stopped.set(true)
      }
    }
    // What failed is never counted down from, so what waits for it stays waiting.
    val release = { unit: BuildUnit<R> ->
      if (results.containsKey(unit.id)) {
        for (next in graph.dependents[unit.id].orEmpty()) {
          val left = waiting.getValue(next.id) - 1
          waiting[next.id] = left
          if (left == 0) ready += next
        }
      }
    }
    if (jobs <= 1 || graph.units.size <= 1) {
      while (ready.isNotEmpty()) ready.removeFirst().let { unit ->
        attempt(unit)
        release(unit)
      }
    } else {
      val pool = Executors.newFixedThreadPool(minOf(jobs, graph.units.size))
      var running = 0
      while (ready.isNotEmpty() || running > 0) {
        while (ready.isNotEmpty()) {
          val unit = ready.removeFirst()
          running++
          pool.execute {
            try {
              attempt(unit)
            } finally {
              finished.put(unit)
            }
          }
        }
        finished.take().let { unit ->
          running--
          release(unit)
        }
      }
      pool.shutdown()
      pool.awaitTermination(Long.MAX_VALUE, TimeUnit.NANOSECONDS)
    }
    when (failures.size) {
      0 -> Unit
      1 -> throw failures.single()
      else -> throw JobFailures(failures.toList())
    }
    return results.mapValues { (_, result) -> result.value }
  }

  // A unit may well return null, which a concurrent map cannot hold.
  private class Result<R>(val value: R)
}

/**
 * One lock per directory. Talac writes beside its first input, under that input's name, with TalaRT
 * next to it, and it can only be told what to compile: two runs in one directory would overwrite
 * each other, so they take turns. Runs in different directories overlap.
 */
class DirectoryLocks {
  private val locks = java.util.concurrent.ConcurrentHashMap<Path, ReentrantLock>()

  fun <T> withDirectory(
    directory: Path,
    work: () -> T,
  ): T =
    locks.computeIfAbsent(directory.toAbsolutePath().normalize()) { ReentrantLock() }.withLock(work)
}
