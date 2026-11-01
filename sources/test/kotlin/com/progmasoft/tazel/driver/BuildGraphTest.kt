/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.driver

import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue

class BuildGraphTest {
  private val order = ConcurrentLinkedQueue<String>()

  private fun unit(
    id: String,
    vararg dependsOn: String,
    work: () -> String = { id },
  ) =
    BuildUnit(id, id, dependsOn.toSet()) {
      order += id
      work()
    }

  private fun rejection(vararg units: BuildUnit<String>) =
    assertFailsWith<IllegalArgumentException> { BuildGraph(units.toList()) }.message.orEmpty()

  @Test
  fun aGraphRefusesWhatCannotRun() {
    assertContains(rejection(unit("a"), unit("a")), "repeated")
    assertContains(rejection(unit("a", "b")), "not there")
    assertContains(rejection(unit("a", "b"), unit("b", "a")), "each other")
    assertContains(rejection(unit("a", "a")), "each other")
  }

  @Test
  fun aUnitWaitsForWhatItDependsOn() {
    for (jobs in listOf(1, 4)) {
      order.clear()
      val results =
        JobPool(jobs, keepGoing = false)
          .run(
            BuildGraph(
              listOf(
                unit("last", "left", "right"),
                unit("left", "first"),
                unit("right", "first"),
                unit("first"),
              )
            )
          )
      assertEquals(setOf("first", "left", "right", "last"), results.keys)
      assertEquals("first", order.first())
      assertEquals("last", order.last())
    }
  }

  @Test
  fun unitsWithoutAnOrderOverlap() {
    val both = CountDownLatch(2)
    val meet = {
      both.countDown()
      assertTrue(both.await(30, TimeUnit.SECONDS))
      "met"
    }
    val results =
      JobPool(2, keepGoing = false)
        .run(BuildGraph(listOf(unit("a", work = meet), unit("b", work = meet))))
    assertEquals(mapOf("a" to "met", "b" to "met"), results)
  }

  @Test
  fun aFailureStopsWhatDependsOnItAndTheRestWithoutKeepGoing() {
    for (jobs in listOf(1, 4)) {
      order.clear()
      val failure =
        assertFailsWith<IllegalStateException> {
          JobPool(jobs, keepGoing = false)
            .run(
              BuildGraph(
                listOf(unit("bad") { error("no") }, unit("after", "bad"), unit("later", "after"))
              )
            )
        }
      assertEquals("no", failure.message)
      assertEquals(listOf("bad"), order.toList())
    }
  }

  @Test
  fun keepGoingFinishesWhatDoesNotDependOnAFailure() {
    for (jobs in listOf(1, 4)) {
      order.clear()
      assertFailsWith<JobFailures> {
        JobPool(jobs, keepGoing = true)
          .run(
            BuildGraph(
              listOf(
                unit("bad") { error("no") },
                unit("blocked", "bad"),
                unit("worse") { error("no") },
                unit("fine"),
                unit("after", "fine"),
              )
            )
          )
      }
      assertEquals(setOf("bad", "worse", "fine", "after"), order.toSet())
    }
  }

  @Test
  fun aUnitMayReturnNothing() {
    val results =
      JobPool(1, keepGoing = false).run(BuildGraph(listOf(BuildUnit<String?>("a") { null })))
    assertEquals(setOf("a"), results.keys)
    assertEquals(null, results.getValue("a"))
  }

  @Test
  fun mapKeepsTheOrderOfItsItems() {
    assertEquals(
      listOf(2, 4, 6),
      JobPool(4, keepGoing = false).map(listOf(1, 2, 3), Int::toString) { it * 2 },
    )
  }
}
