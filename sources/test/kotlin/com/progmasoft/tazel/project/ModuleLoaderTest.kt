/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0
 */

package com.progmasoft.tazel.project

import com.progmasoft.tazel.dsl.BuildMode
import com.progmasoft.tazel.dsl.CppStandard
import com.progmasoft.tazel.dsl.Lto
import com.progmasoft.tazel.dsl.Optimize
import com.progmasoft.tazel.dsl.ProjectException
import com.progmasoft.tazel.dsl.Sanitizer
import com.progmasoft.tazel.dsl.TargetKind
import com.progmasoft.tazel.dsl.TargetTriple
import com.progmasoft.tazel.dsl.TestSize
import com.progmasoft.tazel.dsl.TestTag
import com.progmasoft.tazel.dsl.ZigOptimize
import com.progmasoft.tazel.label.Labels
import com.progmasoft.tazel.label.PackageId
import com.progmasoft.tazel.label.TargetLabel
import java.nio.file.Files
import java.nio.file.Path
import kotlin.io.path.createDirectories
import kotlin.io.path.writeText
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue
import kotlin.time.Duration.Companion.minutes

class ModuleLoaderTest {
  private val loader = ModuleLoader()
  private val linux = TargetTriple.parse("x86_64-unknown-linux-gnu")
  private val windows = TargetTriple.parse("x86_64-pc-windows-msvc")
  private val mac = TargetTriple.parse("aarch64-apple-darwin")

  /** The reference module, which uses every setting of the accepted DSL. */
  private val reference: Module by lazy { loader.load(copyReference()) }

  private fun label(text: String) = Labels.target(text, PackageId.ROOT)

  @Test
  fun theReferenceModuleLoads() {
    val module = reference
    assertEquals("greeter", module.file.info.name)
    assertEquals("1.2.0", module.file.info.version)
    assertEquals(listOf("Leitwolf", "Helmut"), module.file.info.authors)
    assertEquals(
      mapOf(
        "toml" to false,
        "json-lib" to false,
        "catch3" to true,
        "local-lib" to false,
        "libs" to false,
        "test-lib" to true,
      ),
      module.file.dependencyNames,
    )
    assertEquals(listOf("//core/:core", "//app/:greeter"), module.file.viget.pushTargets)
    assertEquals(16L * 1024 * 1024 * 1024, module.file.execution.memory?.bytes)
    assertEquals(
      listOf("app", "core", "mac", "tests", "tools"),
      module.packages.keys.map { it.path }.sorted(),
    )
  }

  @Test
  fun everyTargetConfiguresForEachOfItsTriplesAndModes() {
    val module = reference
    val labels = module.select(Labels.selection("//...", PackageId.ROOT))
    assertEquals(11, labels.size)
    for (target in labels) {
      for (mode in BuildMode.entries) {
        for (triple in module.triplesOf(target, mode, linux)) {
          val configured = module.configure(target, triple, mode)
          assertEquals(target, configured.label)
        }
      }
    }
  }

  @Test
  fun aBlockSeesTheTripleAndModeItRunsFor() {
    val greeter = label("//app/:greeter")
    val debug = reference.configure(greeter, linux, BuildMode.DEBUG).settings
    assertEquals(Optimize.NONE, debug.optimize)
    assertEquals(setOf(Sanitizer.ADDRESS, Sanitizer.UNDEFINED), debug.sanitizers)
    assertEquals(listOf("m", "pthread", "dl"), debug.links)
    assertEquals(CppStandard.CPP23, debug.cppStandard)
    assertEquals(mapOf("GREETER_LOG" to "debug"), debug.env)

    val release = reference.configure(greeter, windows, BuildMode.RELEASE).settings
    assertEquals(Optimize.SPEED, release.optimize)
    assertEquals(Lto.THIN, release.lto)
    assertTrue(release.strip)
    assertEquals(listOf("m", "pthread", "ws2_32"), release.links)
    assertEquals(listOf("greeter.rc"), release.resources)

    val arm = reference.configure(greeter, mac, BuildMode.DEBUG).settings
    assertEquals(listOf("CoreFoundation"), arm.frameworks)
    assertEquals(listOf("NEON=1"), arm.defines)
  }

  @Test
  fun dependenciesResolveToLibrariesAndDependencies() {
    val greeter = reference.configure(label("//app/:greeter"), linux, BuildMode.DEBUG)
    assertEquals(
      listOf(
        ModuleDependency(label("//core/:core"), TargetKind.STATICLIB),
        ExternalDependency(label("@toml//:toml")),
        ExternalDependency(label("@json-lib//:json")),
      ),
      greeter.dependencies,
    )
    val mac = reference.configure(label("//mac/:greeter-mac"), mac, BuildMode.DEBUG)
    assertEquals(
      TargetLabel(PackageId(null, "mac"), "bridge"),
      mac.dependencies.last().label,
    )
  }

  @Test
  fun testsAndZigTargetsKeepTheirSettings() {
    val unit = reference.configure(label("//tests/:unit"), linux, BuildMode.DEBUG).settings
    assertEquals(TestSize.SMALL, unit.size)
    assertEquals(2.minutes, unit.effectiveTimeout)
    assertEquals(4, unit.shards)
    assertEquals(setOf(TestTag.MANUAL, TestTag.EXCLUSIVE), unit.tags)
    val tool = reference.configure(label("//tools/:tool"), linux, BuildMode.RELEASE).settings
    assertEquals(ZigOptimize.SAFE, tool.zigOptimize)
    assertEquals(
      listOf(TargetTriple.parse("x86_64-unknown-linux-gnu"), mac),
      reference.triplesOf(label("//tools/:tool"), BuildMode.DEBUG, windows),
    )
    assertEquals(
      listOf(windows),
      reference.triplesOf(label("//tools/:zig-lib"), BuildMode.DEBUG, windows),
    )
  }

  @Test
  fun aTargetIsNotConfiguredForATripleItDoesNotName() {
    val error =
      assertFailsWith<ProjectException> {
        reference.configure(label("//mac/:greeter-mac"), linux, BuildMode.DEBUG)
      }
    assertTrue("is not built for" in error.message!!, error.message)
  }

  @Test
  fun thePackageOfADirectoryIsTheNearestBuildFile() {
    val module = reference
    assertEquals("core", module.packageOf(module.root.resolve("core/src/deep"))?.id?.path)
    assertEquals(null, module.packageOf(module.root))
    assertEquals(module.root, loader.findRoot(module.root.resolve("core/src")))
  }

  @Test
  fun aPrivatePackageIsNotVisibleFromAnother() {
    val root =
      module(
        "BUILD.kts" to """c.bin("app") { sources("main.c"); depends("//lib/:lib") }""",
        "lib/BUILD.kts" to """c.staticlib("lib") { sources("lib.c") }""",
      )
    val module = loader.load(root)
    val error =
      assertFailsWith<ProjectException> {
        module.configure(label("//:app"), linux, BuildMode.DEBUG)
      }
    assertTrue("lib/BUILD.kts has isPublic = false" in error.message!!, error.message)
  }

  @Test
  fun onlyTestTargetsUseTestDependencies() {
    val root =
      module(
        "BUILD.kts" to
          """
          c.bin("app") { sources("main.c"); depends("@catch3//:main") }
          c.test("unit") { sources("t.c"); depends("@catch3//:main") }
          """,
        moduleFile =
          """
          module { name = "m" }
          dependencies { testImplementation("Progmasoft") { name = "catch3"; version = "3.1.0" } }
          """,
      )
    val module = loader.load(root)
    module.configure(label("//:unit"), linux, BuildMode.DEBUG)
    val error =
      assertFailsWith<ProjectException> {
        module.configure(label("//:app"), linux, BuildMode.DEBUG)
      }
    assertTrue("test dependency" in error.message!!, error.message)
  }

  @Test
  fun aTargetDependsOnlyOnLibraries() {
    val root =
      module(
        "BUILD.kts" to
          """
          c.bin("tool") { sources("tool.c") }
          c.bin("app") { sources("main.c"); depends(":tool") }
          """
      )
    val error =
      assertFailsWith<ProjectException> {
        loader.load(root).configure(label("//:app"), linux, BuildMode.DEBUG)
      }
    assertTrue("depends only on libraries" in error.message!!, error.message)
  }

  @Test
  fun aSettingTheKindDoesNotHaveIsACompileError() {
    for (body in
      listOf(
        """c.bin("x") { exports("api_*") }""",
        """c.staticlib("x") { args("--port") }""",
        """cpp.bin("x") { standard = CStandard.C23 }""",
        """c.bin("x") { arc = false }""",
        """zig.bin("x") { compiler = Compiler.CLANG }""",
        """c.bin("x") { size = TestSize.SMALL }""",
        """cpp.bin("x") { bundle { identifier = "a" } }""",
      )) {
      val error =
        assertFailsWith<ProjectException>(body) { loader.load(module("BUILD.kts" to body)) }
      assertTrue(error.message!!.startsWith("BUILD.kts:1:"), error.message)
    }
  }

  @Test
  fun mistakesInProjectFilesAreReported() {
    val cases =
      listOf(
        """c.bin("x") { sources("a.c") }; c.bin("x") { sources("b.c") }""" to "declared twice",
        """c.bin("x") { sources("../../outside.c") }""" to "outside the module",
        """c.bin("x") { depends("//nowhere/:x") }""" to "there is no nowhere/BUILD.kts",
        """c.bin("x") { depends("@nothing//:x") }""" to "@nothing is not a dependency",
        """c.bin("x") { depends("//core") }""" to "//core/:core",
        """c.bin("x") { targetTriple("not-a-triple") }""" to "Tazel does not know",
        """zig.bin("x") { sources(glob("*.zig")) }""" to "one root file",
        """c.bin("x") { sources("/etc/passwd") }""" to "is absolute",
      )
    for ((body, expected) in cases) {
      val error =
        assertFailsWith<ProjectException>(body) {
          val module = loader.load(module("BUILD.kts" to body))
          module.configure(label("//:x"), linux, BuildMode.DEBUG)
        }
      assertTrue(expected in error.message!!, "$body: ${error.message}")
    }
  }

  @Test
  fun mistakesInTheModuleFileAreReported() {
    val cases =
      listOf(
        "" to "has no module",
        """module { name = "m" }; module { name = "n" }""" to "module { } twice",
        """module { name = "m"; version = "1.2" }""" to "not a Semantic Version",
        """module { name = "m" }; viget { push = true }""" to "needs module.version",
        """module { name = "m" }
          dependencies {
            implementation("A") { name = "x"; version = "1.0.0" }
            implementation("B") { name = "x"; version = "1.0.0" }
          }""" to
          "give one an alias",
        """module { name = "m" }; viget { pushTarget("//:x") }""" to "needs isPublic = true",
      )
    for ((moduleFile, expected) in cases) {
      val error =
        assertFailsWith<ProjectException>(moduleFile) {
          loader.load(module("BUILD.kts" to """c.bin("x") { }""", moduleFile = moduleFile))
        }
      assertTrue(expected in error.message!!, "$moduleFile: ${error.message}")
    }
  }

  @Test
  fun nestedModulesAndHiddenDirectoriesAreNotPackages() {
    val root =
      module(
        "BUILD.kts" to """c.bin("x") { }""",
        ".tazel/BUILD.kts" to "this is not Kotlin",
        "other/MODULE.kts" to "this is not Kotlin either",
        "other/BUILD.kts" to "nor this",
      )
    assertEquals(listOf(""), loader.load(root).packages.keys.map { it.path })
  }

  private fun module(
    vararg files: Pair<String, String>,
    moduleFile: String = """module { name = "m" }""",
  ): Path {
    val root = Files.createTempDirectory("tazel-module")
    root.resolve("MODULE.kts").writeText(moduleFile.trimIndent())
    for ((path, text) in files) {
      val file = root.resolve(path)
      file.parent.createDirectories()
      file.writeText(text.trimIndent())
    }
    return root
  }

  private fun copyReference(): Path {
    val source =
      Path.of(
          checkNotNull(javaClass.getResource("/reference/MODULE.kts")) { "no reference module" }
            .toURI()
        )
        .parent
    val root = Files.createTempDirectory("tazel-reference")
    Files.walk(source).use { paths ->
      paths.forEach { path ->
        val target = root.resolve(source.relativize(path).toString())
        if (Files.isDirectory(path)) target.createDirectories() else Files.copy(path, target)
      }
    }
    return root
  }
}
