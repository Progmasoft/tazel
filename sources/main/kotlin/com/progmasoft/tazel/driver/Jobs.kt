/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.Executors
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
    val failures = ConcurrentLinkedQueue<Exception>()
    val stopped = AtomicBoolean()
    val results = arrayOfNulls<Any?>(items.size)
    val job = { index: Int ->
      if (!stopped.get()) {
        try {
          val item = items[index]
          results[index] = profile?.measure(name(item)) { work(item) } ?: work(item)
        } catch (failure: Exception) {
          failures += failure
          if (!keepGoing) stopped.set(true)
        }
      }
    }
    if (jobs <= 1 || items.size <= 1) {
      items.indices.forEach(job)
    } else {
      val pool = Executors.newFixedThreadPool(minOf(jobs, items.size))
      items.indices.forEach { index -> pool.execute { job(index) } }
      pool.shutdown()
      pool.awaitTermination(Long.MAX_VALUE, TimeUnit.NANOSECONDS)
    }
    when (failures.size) {
      0 -> Unit
      1 -> throw failures.single()
      else -> throw JobFailures(failures.toList())
    }
    @Suppress("UNCHECKED_CAST")
    return results.toList() as List<R>
  }
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
