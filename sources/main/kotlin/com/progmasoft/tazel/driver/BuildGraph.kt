/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

/**
 * One piece of work of a command: a build step of a plugin, the compilation of a target, a test
 * suite. It starts once all of [dependsOn] have finished. [name] is what a profile shows; [id] is
 * unique in the graph, while two projects may each have a target of the same name.
 */
class BuildUnit<R>(
  val id: String,
  val name: String = id,
  val dependsOn: Set<String> = emptySet(),
  val work: () -> R,
)

/** The units of a command, in the order they should start when nothing else decides. */
class BuildGraph<R>(val units: List<BuildUnit<R>>) {
  /** The units that wait for each unit. */
  val dependents: Map<String, List<BuildUnit<R>>> =
    units
      .flatMap { unit -> unit.dependsOn.map { it to unit } }
      .groupBy({ (id, _) -> id }, { (_, unit) -> unit })

  init {
    val ids = units.map(BuildUnit<R>::id)
    val repeated = ids.groupBy { it }.filterValues { it.size > 1 }.keys
    require(repeated.isEmpty()) { "units are repeated: $repeated" }
    val missing = units.flatMap(BuildUnit<R>::dependsOn).toSet() - ids.toSet()
    require(missing.isEmpty()) { "units depend on what is not there: $missing" }
    require(!cyclic()) { "the units depend on each other" }
  }

  // A unit can start once what it waits for has, so a cycle is what never gets to start.
  private fun cyclic(): Boolean {
    val waiting = units.associate { it.id to it.dependsOn.size }.toMutableMap()
    val ready = ArrayDeque(units.filter { it.dependsOn.isEmpty() })
    var started = 0
    while (ready.isNotEmpty()) {
      started++
      for (unit in dependents[ready.removeFirst().id].orEmpty()) {
        val left = waiting.getValue(unit.id) - 1
        waiting[unit.id] = left
        if (left == 0) ready += unit
      }
    }
    return started != units.size
  }
}
