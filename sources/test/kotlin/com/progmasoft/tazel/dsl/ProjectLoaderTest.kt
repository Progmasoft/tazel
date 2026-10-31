/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import java.nio.file.Files
import java.nio.file.Path
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProjectLoaderTest {
  private fun inProject(block: (Path) -> Unit) {
    val root = Files.createTempDirectory("tazel-project-").toRealPath()
    try {
      block(root)
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun findsTheFirstProjectFileOfTheOrder() = inProject { root ->
    assertNull(projectFileIn(root))
    Files.writeString(root.resolve("Tazel.build"), "")
    assertEquals("Tazel.build", projectFileIn(root)?.file?.fileName.toString())
    Files.writeString(root.resolve("BUILD"), "")
    assertEquals("BUILD", projectFileIn(root)?.file?.fileName.toString())
    Files.writeString(root.resolve("BUILD.tazel"), "")
    Files.writeString(root.resolve("BUILD.tzl"), "")
    assertEquals("BUILD.tzl", projectFileIn(root)?.file?.fileName.toString())
    Files.writeString(root.resolve("BUILD.kts"), "")
    assertEquals("BUILD.kts", projectFileIn(root)?.file?.fileName.toString())
  }

  @Test
  fun theNamesOfAProjectFileAreCaseSensitive() = inProject { root ->
    Files.writeString(root.resolve("build.kts"), "")
    Files.writeString(root.resolve("tazel.build"), "")
    assertNull(projectFileIn(root))
  }

  @Test
  fun aDirectoryBelongsToTheNearestProjectAboveIt() = inProject { root ->
    Files.writeString(root.resolve("BUILD.kts"), "")
    val nested = Files.createDirectories(root.resolve("Sources/Deep"))
    assertEquals(root, discoverProject(nested).root)

    val outside = Files.createTempDirectory("tazel-no-project-")
    try {
      // The temporary directory may itself sit below a project on a developer machine, so only
      // the message is checked when the search fails.
      runCatching { discoverProject(outside) }
        .exceptionOrNull()
        ?.let { assertTrue(it.message.orEmpty().contains("BUILD.kts")) }
    } finally {
      outside.toFile().deleteRecursively()
    }
  }

  @Test
  fun runsAProjectFileInsideTazel() = inProject { root ->
    Files.writeString(
      root.resolve("BUILD"),
      """
      project {
        name = "Loaded"
        version = "1.2.3"
      }
      compiler {
        buildMode = if (OS == OperatingSystem.UNKNOWN) BuildMode.DEBUG else BuildMode.RELEASE
        warnings = Warnings.ALL
      }
      dependencies {
        localDependency { path = "Dependency.vipkg" }
      }
      sources {
        executable { entry = "Loaded.Program" }
      }
      check(compiler.version == "9.9.9")
      check(project.name == "Loaded")
      """
        .trimIndent(),
    )

    val loaded = ProjectLoader("9.9.9").load(projectFileIn(root)!!)

    assertEquals("Loaded", loaded.plan.identity?.name)
    assertEquals("Loaded.Program", loaded.plan.executables.single().entry)
    assertEquals(BuildMode.RELEASE, loaded.plan.compiler.buildMode)
    assertEquals("9.9.9", loaded.plan.compiler.version)
    assertEquals(
      listOf(LocalPackageDependency("Dependency.vipkg")),
      loaded.plan.dependencies.local,
    )
  }

  @Test
  fun projectFilesAreEvaluatedInParallelWithoutSharingAProject() = inProject { root ->
    // Both scripts wait for each other in the middle of their evaluation, so they are certainly
    // evaluated at the same time; each must still build only its own project.
    val barrier = java.util.concurrent.CyclicBarrier(2)
    ParallelFixture.barrier = barrier
    val names = listOf("First", "Second")
    val locations = names.map { name ->
      val directory = Files.createDirectories(root.resolve(name))
      Files.writeString(
        directory.resolve("BUILD.kts"),
        """
        project { name = "$name" }
        com.progmasoft.tazel.dsl.ParallelFixture.barrier!!.await(30, java.util.concurrent.TimeUnit.SECONDS)
        sources { executable { entry = "$name.Program" } }
        check(project.name == "$name")
        """
          .trimIndent(),
      )
      projectFileIn(directory)!!
    }
    val loader = ProjectLoader("1.0.0")
    val pool = java.util.concurrent.Executors.newFixedThreadPool(2)
    try {
      val plans =
        locations
          .map { location -> pool.submit<ProjectPlan> { loader.load(location).plan } }
          .map { it.get(5, java.util.concurrent.TimeUnit.MINUTES) }
      assertEquals(names, plans.map { it.identity?.name })
      assertEquals(names.map { "$it.Program" }, plans.map { it.executables.single().entry })
    } finally {
      pool.shutdownNow()
      ParallelFixture.barrier = null
    }
  }

  @Test
  fun theDslBelongsToTheEvaluationThatRunsIt() = inProject { root ->
    Files.writeString(
      root.resolve("BUILD.kts"),
      "project { name = \"Bound\" }\nsources { executable { entry = \"Bound.Program\" } }",
    )
    val thread = Thread {
      ProjectLoader("1.0.0").load(projectFileIn(root)!!)
      // Once the evaluation is over, nothing of it stays bound to the thread.
      assertFailsWith<ProjectConfigurationException> { ProjectRuntime.current() }
    }
    var failure: Throwable? = null
    thread.setUncaughtExceptionHandler { _, error -> failure = error }
    thread.start()
    thread.join()
    failure?.let { throw it }
  }

  @Test
  fun theSampleProjectFileOfTazelUsesTheWholeDsl() {
    // Gradle runs the tests in the directory of Tazel, where the sample lives.
    val plan = ProjectLoader("0.0.0").load(projectFileIn(Path.of(""))!!).plan

    assertEquals("Example", plan.identity?.name)
    assertEquals(listOf("Server", "../Shared", "Client"), plan.workspace?.members)
    assertEquals(4, plan.dependencies.declarations.size + plan.dependencies.local.size - 1)
    assertEquals(listOf("Unit", "Integration"), plan.testSuites.map(TestSuite::name))
    assertEquals(listOf(ViPkgType.TALALIB, ViPkgType.STATICLIB), plan.libraries.single().viPkgTypes)
    assertTrue(plan.pushSources)
  }

  @Test
  fun reportsWhatAProjectFileGotWrong() = inProject { root ->
    val file = root.resolve("BUILD.kts")
    val loader = ProjectLoader()
    fun problem(script: String): String {
      Files.writeString(file, script)
      return assertFailsWith<ProjectConfigurationException> { loader.load(projectFileIn(root)!!) }
        .message
        .orEmpty()
    }

    assertTrue(problem("this is not Kotlin").startsWith("BUILD.kts:1:"))
    assertEquals("BUILD.kts: no build on this host", problem("panic(\"no build on this host\")"))
    assertTrue(problem("sources { executable { entry = \"Main\" } }").contains("name is required"))
    assertTrue(problem("compiler { wundef = true }").contains("wundef"))
    assertTrue(problem("error(\"boom\")").contains("boom"))
  }
}

/** Lets the project files of a test wait for each other. */
object ParallelFixture {
  @Volatile var barrier: java.util.concurrent.CyclicBarrier? = null
}
