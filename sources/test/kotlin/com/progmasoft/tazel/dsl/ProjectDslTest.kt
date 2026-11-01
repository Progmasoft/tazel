/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.dsl

import java.io.ByteArrayOutputStream
import java.io.PrintStream
import kotlin.test.BeforeTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ProjectDslTest {
  @BeforeTest
  fun resetRuntime() {
    ProjectRuntime.reset()
  }

  private fun demo(entry: String = "Demo.Main") {
    sources {
      executable {
        name = "Demo"
        this.entry = entry
      }
    }
  }

  @Test
  fun acceptsAnyNamespaceQualifiedEntryClassName() {
    project { name = "Demo" }
    demo()
    val plan = ProjectRuntime.build()

    assertEquals(ProjectIdentity("Demo", null, null), plan.identity)
    assertEquals(listOf("Sources"), plan.executables.map { it.srcDir })
    assertEquals("Demo.Main", plan.executables.single().entry)
    assertEquals(BuildMode.DEBUG, plan.compiler.buildMode)
    assertEquals(OptLevel.O0, plan.compiler.effectiveOptLevel)
    assertTrue(plan.compiler.xppOptimizationPasses)
    assertTrue(plan.compiler.xmmOptimizationPasses)

    listOf("Namespace.Program", "Namespace.Namespace.Program", "Company.Tool.Bootstrap").forEach {
      entry ->
      ProjectRuntime.reset()
      project { name = "Demo" }
      demo(entry)
      assertEquals(entry, ProjectRuntime.build().executables.single().entry)
    }
  }

  @Test
  fun namesTargetsAfterTheProjectUnlessTheyNameThemselves() {
    project { name = "Artifacts" }
    sources {
      executable { entry = "Artifacts.Program" }
      library {
        name = "Runtime"
        namespace = "Artifacts.Runtime"
        viPkgType(ViPkgType.TALALIB, ViPkgType.STATICLIB)
      }
    }

    val plan = ProjectRuntime.build()
    assertEquals("Artifacts", plan.executables.single().name)
    assertEquals("Artifacts.Runtime", plan.libraries.single().namespace)
    assertEquals(
      listOf(ViPkgType.TALALIB, ViPkgType.STATICLIB),
      plan.libraries.single().viPkgTypes,
    )

    ProjectRuntime.reset()
    val problem =
      assertFailsWith<ProjectConfigurationException> {
        sources { executable { entry = "Nameless.Main" } }
      }
    assertTrue(problem.message.orEmpty().contains("project requires name"))
  }

  @Test
  fun permitsAnyProjectFieldSubsetWhenPublishingIsDisabled() {
    project { name = "PrivateCompiler" }
    demo()
    assertEquals(ProjectIdentity("PrivateCompiler", null, null), ProjectRuntime.build().identity)

    ProjectRuntime.reset()
    project {
      name = "PrivateCompiler"
      version = "0.3.1"
      stability = Stability.DEV
    }
    demo()
    assertEquals(
      ProjectIdentity("PrivateCompiler", Stability.DEV, "0.3.1"),
      ProjectRuntime.build().identity,
    )
  }

  @Test
  fun aReleaseCandidateHasItsOwnStability() {
    project {
      name = "PrivateCompiler"
      version = "0.3.1"
      stability = Stability.RC
    }
    demo()
    assertEquals(
      ProjectIdentity("PrivateCompiler", Stability.RC, "0.3.1"),
      ProjectRuntime.build().identity,
    )
  }

  @Test
  fun requiresEveryProjectFieldWhenPublishingIsEnabled() {
    fun rejectionFor(block: ProjectScope.() -> Unit): ProjectConfigurationException {
      ProjectRuntime.reset()
      project(block)
      sources {
        viget { push = true }
        executable {
          name = "Published"
          entry = "Published.Main"
        }
      }
      return assertFailsWith { ProjectRuntime.build() }
    }

    assertEquals("project requires name", rejectionFor {}.message)
    assertTrue(rejectionFor { name = "Published" }.message.orEmpty().contains("version, stability"))
  }

  @Test
  fun rejectsMissingOrUnqualifiedEntry() {
    assertFailsWith<ProjectConfigurationException> { ProjectRuntime.build() }
    assertFailsWith<ProjectConfigurationException> { demo("Main") }
    ProjectRuntime.reset()
    assertFailsWith<ProjectConfigurationException> { demo("Namespace.") }
  }

  @Test
  fun configuresTheWholeProjectAndCompilerSurface() {
    project {
      name = "Compiler"
      version = "0.3.0"
      stability = Stability.NIGHTLY
      description = "Compiler project"
      authors("Leitwolf", "Helmut")
      defaultFeatures("TOML")
    }
    compiler {
      standard = talaStandardOf(26)
      backend = Backend.LLVM
      buildMode = BuildMode.RELEASE
      werror = true
      warnings = Warnings.ALL
      wshadow = true
      xppOptimizationPasses = false
      xmmOptimizationPasses = false
      optLevel = OptLevel.OG
      llvm {
        compiler = LlvmCompiler.ORC
        lto = LlvmLto.THIN
        args("-time-passes", "-print-after-all")
      }
    }
    outdirs {
      release = "out/release"
      debug = "out/debug"
    }
    targets { target("x86_64-pc-windows-msvc", "aarch64-apple-darwin") }
    pml { enabled = false }
    workspace {
      members("./Server", "../Shared", "Client/")
      defaultMembers("Server", "../Shared")
      exclude("OldProject")
    }
    sources {
      viget {
        push = true
        exclude("build/**")
      }
      executable {
        srcDir = "Source"
        exclude("Generated/**")
        entry = "Compiler.Main"
      }
      test("Unit") {
        testDir = "Tests/Unit"
        exclude("Fixtures/**")
      }
      test("Integration") {}
    }

    val plan = ProjectRuntime.build()
    assertEquals(
      ProjectIdentity("Compiler", Stability.NIGHTLY, "0.3.0", "Compiler project"),
      plan.identity,
    )
    assertEquals(listOf("Leitwolf", "Helmut"), plan.authors)
    assertEquals(listOf("TOML"), plan.defaultFeatures)
    assertEquals(OptLevel.OG, plan.compiler.effectiveOptLevel)
    assertEquals(LlvmCompiler.ORC, plan.compiler.llvmCompiler)
    assertEquals(LlvmLto.THIN, plan.compiler.llvmLto)
    assertEquals(listOf("-time-passes", "-print-after-all"), plan.compiler.llvmArguments)
    assertTrue(plan.compiler.shadowWarnings)
    assertEquals("out/release", plan.outputDirectory)
    assertEquals(listOf("x86_64-pc-windows-msvc", "aarch64-apple-darwin"), plan.targets)
    assertEquals(
      Workspace(
        listOf("Server", "../Shared", "Client"),
        listOf("Server", "../Shared"),
        listOf("OldProject"),
      ),
      plan.workspace,
    )
    assertFalse(plan.pmlEnabled)
    assertTrue(plan.pushSources)
    assertEquals(listOf("build/**"), plan.pushExcludes)
    assertEquals(listOf("Generated/**"), plan.executables.single().exclude)
    assertEquals(
      listOf(
        TestSuite("Unit", "Tests/Unit", listOf("Fixtures/**")),
        TestSuite("Integration", "Tests/Integration", null),
      ),
      plan.testSuites,
    )
  }

  @Test
  fun aProjectFileReadsBackWhatItWrote() {
    ProjectRuntime.reset()
    project {
      name = "Echo"
      version = "1.0.0"
    }
    compiler { buildMode = BuildMode.RELEASE }

    assertEquals("Echo", project.name)
    assertEquals("1.0.0", project.version)
    assertEquals("26", compiler.standard.toString())
    assertEquals(OptLevel.O3, compiler.optLevel)
  }

  @Test
  fun derivesTheOptimizationLevelFromTheBuildMode() {
    project { name = "Demo" }
    demo()
    assertEquals(OptLevel.O0, ProjectRuntime.build().compiler.effectiveOptLevel)

    ProjectRuntime.reset()
    project { name = "Demo" }
    compiler { buildMode = BuildMode.RELEASE }
    demo()
    assertEquals(OptLevel.O3, ProjectRuntime.build().compiler.effectiveOptLevel)
  }

  @Test
  fun rejectsCompilerSettingsThatContradictEachOther() {
    compiler {
      warnings = Warnings.NONE
      wshadow = true
    }
    demo()
    assertFailsWith<ProjectConfigurationException> { ProjectRuntime.build() }

    assertFailsWith<ProjectConfigurationException> { talaStandardOf(23) }
    assertFailsWith<ProjectConfigurationException> { compiler { llvm { args("-a,-b") } } }
  }

  @Test
  fun aWorkspaceRootNeedsNoSourcesOfItsOwn() {
    project { name = "Demo" }
    workspace { members("Server", "Client") }
    val plan = ProjectRuntime.build()
    assertTrue(plan.executables.isEmpty())
    assertEquals(listOf("Server", "Client"), plan.workspace?.members)

    ProjectRuntime.reset()
    assertFailsWith<ProjectConfigurationException> {
      workspace {
        members("Server")
        defaultMembers("Client")
      }
    }
    assertFailsWith<ProjectConfigurationException> { workspace { members(".") } }
  }

  @Test
  fun excludeDefaultsRemainNullAcrossEverySourceSection() {
    project { name = "Demo" }
    demo()
    val plan = ProjectRuntime.build()

    assertNull(plan.pushExcludes)
    assertNull(plan.executables.single().exclude)
    assertTrue(plan.testSuites.isEmpty())
  }

  @Test
  fun rejectsDuplicateOrInvalidTestSuiteNames() {
    assertFailsWith<ProjectConfigurationException> {
      sources {
        test("Unit") {}
        test("Unit") {}
      }
    }
    assertFailsWith<ProjectConfigurationException> { sources { test("unit tests") {} } }
  }

  @Test
  fun rejectsDirectoriesOutsideTheProject() {
    assertFailsWith<ProjectConfigurationException> {
      sources {
        executable {
          name = "Escape"
          srcDir = "../Elsewhere"
          entry = "Escape.Main"
        }
      }
    }
    assertFailsWith<ProjectConfigurationException> { outdirs { release = "C:/out" } }
  }

  @Test
  fun keepsTheHostValuesAndTheTwoHelpers() {
    assertTrue(OS in OperatingSystem.entries)
    assertTrue(ARCH in Architecture.entries)
    assertEquals(OperatingSystem.MACOS, operatingSystemFromName("Mac OS X"))
    assertEquals(Architecture.AARCH64, architectureFromName("arm64"))

    assertEquals(
      "unsupported host",
      assertFailsWith<ProjectAbort> { panic("unsupported host") }.message,
    )

    val output = ByteArrayOutputStream()
    val previous = System.err
    try {
      System.setErr(PrintStream(output, true, Charsets.UTF_8))
      eprint("problem")
      eprint(7)
    } finally {
      System.setErr(previous)
    }
    assertEquals("problem7", output.toString(Charsets.UTF_8))
  }
}
