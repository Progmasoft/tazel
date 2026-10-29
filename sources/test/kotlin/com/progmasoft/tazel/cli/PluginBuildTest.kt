/*
 * SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
 * SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1
 */

package com.progmasoft.tazel.cli

import com.github.ajalt.mordant.terminal.Terminal
import com.github.ajalt.mordant.terminal.TerminalRecorder
import com.progmasoft.api.tazel.dsl.plugin.PROJECT_PLUGIN_API_VERSION
import com.progmasoft.api.tazel.dsl.plugin.PluginExtensions
import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.api.tazel.dsl.plugin.ProjectPluginContext
import com.progmasoft.tazel.driver.Processes
import com.progmasoft.tazel.driver.Toolchain
import com.progmasoft.tazel.dsl.OperatingSystem
import java.nio.file.Files
import java.nio.file.Path
import java.util.jar.JarEntry
import java.util.jar.JarOutputStream
import kotlin.io.path.outputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * A plugin of the kind CC and CMake are: its JAR holds the descriptor and the service entry, and
 * its DSL, [fixtureCompile], adds a step that runs a program.
 */
class FixtureBuildPlugin : ProjectPlugin {
  override fun apply(context: ProjectPluginContext) = context.registerExtension("native")
}

/** The DSL of [FixtureBuildPlugin]: compiles [source] with [compiler] into the output directory. */
fun fixtureCompile(
  compiler: String,
  source: String,
  failWith: String? = null,
) {
  PluginExtensions.require("native").buildStep("compile") { context ->
    if (failWith != null) context.fail(failWith)
    val program = context.projectRoot.resolve(compiler).toString()
    // A probe whose output is read and not shown.
    val probe = context.capture(listOf(program, "--version"))
    if (probe.exitCode != 0 || "fixture compiler" !in probe.output) context.fail("the probe failed")
    val name = context.project.name ?: "program"
    val output = context.outputDirectory.resolve("$name${context.host.executableExtension}")
    context.status("Compiling", source)
    val exitCode =
      context.run(
        listOf(program, source, "-o", output.toString()),
        environment = mapOf("NATIVE_MODE" to context.buildMode.name),
      )
    if (exitCode != 0) context.fail("$compiler exited with $exitCode")
    Files.writeString(context.workDirectory.resolve("done"), "yes")
  }
}

class PluginBuildTest {
  private class Fixture(val root: Path) : Processes {
    val recorder = TerminalRecorder()
    val commands = mutableListOf<Triple<List<String>, Path, Map<String, String>>>()
    var exitCode = 0

    override fun run(
      command: List<String>,
      directory: Path,
    ) = runWith(command, directory, emptyMap())

    override fun runWith(
      command: List<String>,
      directory: Path,
      environment: Map<String, String>,
    ): Int {
      commands += Triple(command, directory, environment)
      return exitCode
    }

    val captured = mutableListOf<List<String>>()

    override fun runGatheredWith(
      command: List<String>,
      directory: Path,
      environment: Map<String, String>,
    ): com.progmasoft.tazel.driver.GatheredRun {
      captured += command
      return com.progmasoft.tazel.driver.GatheredRun(0, "fixture compiler 1.0\n")
    }

    override fun capture(command: List<String>) = "talac 0.1.0\n"

    fun write(
      file: String,
      text: String,
    ) {
      val path = root.resolve(file)
      Files.createDirectories(path.parent)
      Files.writeString(path, text.trimIndent())
    }

    fun build(): Int {
      val parsed = parseCommandLine(listOf("build"))
      check(parsed is ParseResult.Parsed)
      return Tazel(
          Terminal(terminalInterface = recorder),
          root,
          OperatingSystem.LINUX,
          Toolchain(OperatingSystem.LINUX, mapOf("PATH" to root.toString()), null),
          this,
          "1.2.3",
        )
        .run(parsed.command)
    }
  }

  private fun fixture(
    pluginClass: String = FixtureBuildPlugin::class.java.name,
    block: Fixture.() -> Unit,
  ) {
    val root = Files.createTempDirectory("tazel-plugin-build-")
    try {
      val fixture = Fixture(root)
      JarOutputStream(root.resolve("native.jar").outputStream()).use { jar ->
        jar.putNextEntry(JarEntry("META-INF/tazel-plugin.properties"))
        jar.write(
          """
          publisher=Publisher
          name=Native
          version=1.0.0
          apiVersion=$PROJECT_PLUGIN_API_VERSION
          imports=com.progmasoft.tazel.cli.fixtureCompile
          """
            .trimIndent()
            .toByteArray()
        )
        jar.closeEntry()
        jar.putNextEntry(JarEntry("META-INF/services/${ProjectPlugin::class.java.name}"))
        jar.write(pluginClass.toByteArray())
        jar.closeEntry()
      }
      fixture.block()
    } finally {
      root.toFile().deleteRecursively()
    }
  }

  @Test
  fun aProjectWithoutTalaTargetsIsBuiltByTheStepsOfItsPlugin() = fixture {
    write(
      "BUILD.kts",
      """
      plugins {
        localPlugin { path = "native.jar" }
      }
      project { name = "Demo" }
      fixtureCompile("cc", "main.c")
      """,
    )

    assertEquals(ExitCode.SUCCESS, build(), recorder.stderr())

    // The probe ran through capture: gathered, not on the output of Tazel.
    assertEquals(listOf(listOf(root.resolve("cc").toString(), "--version")), captured)
    assertTrue("fixture compiler" !in recorder.stdout())

    val (command, directory, environment) = commands.single()
    assertEquals(root.resolve("cc").toString(), command.first())
    assertEquals(
      listOf("main.c", "-o", root.resolve("build/debug/Demo").toString()),
      command.drop(1),
    )
    assertEquals(root, directory)
    assertEquals(mapOf("NATIVE_MODE" to "DEBUG"), environment)
    assertTrue(Files.isRegularFile(root.resolve(".tazel/build/Publisher.Native/compile/done")))
    assertTrue("Compiling" in recorder.stdout())
  }

  @Test
  fun aServiceEntryThatNamesNoClassOfTheJarIsReported() =
    fixture(pluginClass = "com.example.Missing") {
      write(
        "BUILD.kts",
        """
        plugins {
          localPlugin { path = "native.jar" }
        }
        fixtureCompile("cc", "main.c")
        """,
      )

      assertEquals(ExitCode.FAILURE, build())
      assertTrue(
        "plugin 'Publisher.Native' names com.example.Missing, which its JAR does not hold" in
          recorder.stderr(),
        recorder.stderr(),
      )
    }

  @Test
  fun aServiceThatIsNoPluginIsReported() =
    fixture(pluginClass = "java.lang.Object") {
      write(
        "BUILD.kts",
        """
        plugins {
          localPlugin { path = "native.jar" }
        }
        """,
      )

      assertEquals(ExitCode.FAILURE, build())
      assertTrue("java.lang.Object does not implement ProjectPlugin" in recorder.stderr())
    }

  @Test
  fun aFailedProgramStopsTheBuild() = fixture {
    write(
      "BUILD.kts",
      """
      plugins {
        localPlugin { path = "native.jar" }
      }
      fixtureCompile("cc", "main.c")
      """,
    )
    exitCode = 2

    assertEquals(ExitCode.FAILURE, build())
    assertTrue("Publisher.Native compile: cc exited with 2" in recorder.stderr(), recorder.stderr())
  }

  @Test
  fun aStepCanStopTheBuildWithAMessage() = fixture {
    write(
      "BUILD.kts",
      """
      plugins {
        localPlugin { path = "native.jar" }
      }
      fixtureCompile("cc", "main.c", failWith = "no C compiler was found")
      """,
    )

    assertEquals(ExitCode.FAILURE, build())
    assertTrue("no C compiler was found" in recorder.stderr(), recorder.stderr())
    assertEquals(emptyList(), commands)
  }
}
