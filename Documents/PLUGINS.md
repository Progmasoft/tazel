<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0 -->

# Writing a plugin

A plugin is a JAR that extends the project DSL. It can add blocks a project file uses, such as `cmake { }`, add steps to
the build of the project, record values in its plan and check the project once it is complete. This page shows how a
plugin is built, how Tazel finds and verifies it, and what it may do once it runs.

A plugin is trusted build logic. It runs inside Tazel with the full access of the JVM; Tazel checks that the JAR is the
one the project asked for, and does not sandbox what it does.

## Using a plugin

A project declares its plugins in a `plugins` block:

```kotlin
plugins {
  plugin("Publisher") {
    name = "CMake"
    version = "0.1.0"
  }

  localPlugin {
    path = "Tools/plugin.jar"
  }
}
```

- `plugin("Publisher") { name = ...; version = ... }` asks for a plugin of ViGet. Tazel looks in the plugin cache of the
  project, `.tazel/plugins`, first; when no matching version is there, it downloads the plugin from
  `https://viget.progmasoft.com/dslplugins/tazel/<Publisher>/<Name>/` into the cache. Without `version`, the newest
  version that is not yanked and was built for the plugin API of this Tazel is taken; a version that is named is taken
  even when it was yanked.
- `localPlugin { path = ... }` names a JAR inside the project directory. Its publisher, name and version come from the
  JAR itself.

Tazel reads this block **before** it runs the rest of the file, with a small parser that runs no code. That parser
accepts only what the example shows: `plugin("...")` and `localPlugin`, with fields written as plain string literals; a
value computed from a variable or a string template is refused. Comments and strings elsewhere in the file are skipped,
so a `plugins` word inside them is not mistaken for the block.

When the script runs, the block declares the same plugins again, and every plugin the parser found must be declared.
Write the block at the top level of the file: inside an `if` that does not run, its plugins stay undeclared and the
command fails with `resolved plugins were not declared by the script`.

## Building a plugin

A plugin is built with Gradle and the Gradle plugin `com.progmasoft.tazel.plugin`, whose version is the version of Tazel
the plugin is built for:

```kotlin
// build.gradle.kts of the plugin
plugins {
  kotlin("jvm") version "2.4.0"
  id("com.progmasoft.tazel.plugin") version "0.3.0"
}

version = "0.1.0"

kotlin { jvmToolchain(25) }

tazelPlugin {
  publisher = "Publisher"
  name = "CMake"
  pluginClass = "com.example.cmake.CMakePlugin"
  imports = listOf("com.example.cmake.dsl.*")
  description = "Configures and builds CMake projects."
  authors = listOf("Ada Lovelace <ada@example.com>")
  license = "MIT"
  homepage = "https://example.com/cmake"
  repository = "https://github.com/example/cmake-plugin"
}

dependencies {
  tazelApi()
}
```

The Gradle plugin

- adds the repository of the API, `https://maven.progmasoft.com/tazel`, for the group `com.progmasoft.tazel` only;
- makes `tazelApi()` mean `compileOnly` and `testImplementation` of `com.progmasoft.tazel:tazel-api` in its own version.
  The API is not bundled: Tazel provides it, and the Kotlin standard library, when it loads the plugin;
- writes the descriptor and the service entry below from `tazelPlugin { }` into the JAR. The version of the plugin is
  the version of the project unless `tazelPlugin.version` sets another.

`gradlew jar` then writes the plugin JAR.

## The JAR

A plugin JAR holds:

```text
com/example/cmake/CMakePlugin.class
com/example/cmake/dsl/CMake.class                                  the functions it adds to the DSL
META-INF/tazel-plugin.properties                                   the descriptor
META-INF/services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
```

### The descriptor

```properties
publisher=Publisher
name=CMake
version=0.1.0
apiVersion=2
imports=com.example.cmake.dsl.*
description=Configures and builds CMake projects.
author.0=Ada Lovelace <ada@example.com>
license=MIT
homepage=https\://example.com/cmake
repository=https\://github.com/example/cmake-plugin
```

| Key | Rule |
| --- | --- |
| `publisher` | one identifier: the name of a Progmasoft Account |
| `name` | one identifier |
| `version` | an exact semantic version |
| `apiVersion` | the plugin API it was built for; Tazel loads only `2`, the current `PROJECT_PLUGIN_API_VERSION` |
| `imports` | optional; comma-separated packages or classes the project file imports by default, such as `com.example.cmake.dsl.*` |
| `description`, `author.0`, `author.1`, ..., `license`, `homepage`, `repository` | optional for Tazel; ViGet shows them on the page of the plugin. `license` is an SPDX expression. |

An import must be a dotted name, optionally ending in `.*`; anything else is refused.

### The service

`META-INF/services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin` names the class that implements `ProjectPlugin`:

```text
com.example.cmake.CMakePlugin
```

The entry names exactly one class; a line may end in a `#` comment. The class implements `ProjectPlugin` and has a
public constructor without parameters. Who the plugin is comes from the descriptor alone: the class does not state its
publisher, name or version.

## The API

The API is `com.progmasoft.tazel:tazel-api`, the packages `com.progmasoft.api.tazel` and below. A plugin implements
`ProjectPlugin`:

```kotlin
fun interface ProjectPlugin {
  fun apply(context: ProjectPluginContext)
}

interface ProjectPluginContext {
  val projectRoot: Path
  val host: Host

  fun registerExtension(name: String)
  fun contribute(key: String, value: String)
  fun onFinalize(action: (ProjectSummary) -> Unit)
}
```

| Call | What it does |
| --- | --- |
| `projectRoot` | the directory of the project file |
| `host` | the operating system and the architecture Tazel runs on |
| `registerExtension(name)` | claims a DSL name, such as `cmake`, for this plugin. A name is one identifier, and two plugins cannot claim the same one. |
| `contribute(key, value)` | records a value in the plan of the project. A key starts with a letter or `_` and holds at most 128 letters, digits, `_`, `.` or `-`; each key is written once per plugin, and the value cannot be empty. |
| `onFinalize(action)` | runs `action` with a `ProjectSummary` (name, version, build mode, output directory) after the script ended, for checks that need the whole project |

The extensions and contributions of each plugin end up in the plan of the project and in its lockfile.

### Blocks and build steps

The block a project file writes, such as `cmake { }`, is a function the plugin defines in one of its `imports`. It asks
`PluginExtensions` for the scope of its extension, which fails unless the project declared the plugin that registered
it, and adds the work of the block as build steps:

```kotlin
package com.example.cmake.dsl

import com.progmasoft.api.tazel.dsl.TazelPluginDsl
import com.progmasoft.api.tazel.dsl.plugin.PluginExtensions

@TazelPluginDsl
class CMakeScope {
  var enabled = false
  internal val definitions = linkedMapOf<String, String>()

  fun define(vararg values: Pair<String, String>) = definitions.putAll(values)
}

fun cmake(block: CMakeScope.() -> Unit) {
  val settings = CMakeScope().apply(block)
  if (!settings.enabled) return
  val scope = PluginExtensions.require("cmake")
  scope.buildStep("configure") { context ->
    val cmake = context.findProgram("cmake") ?: context.fail("cmake was not found on PATH")
    val exitCode =
      context.run(
        listOf(cmake.toString(), "-S", ".", "-B", context.workDirectory.toString()) +
          settings.definitions.map { (key, value) -> "-D$key=$value" }
      )
    if (exitCode != 0) context.fail("cmake exited with $exitCode")
  }
}
```

`tazel build` and `tazel run` run the steps of a project in the order they were added, before its Tala targets compile.
A project whose plugins add steps needs no Tala target. A step runs with a `BuildContext`:

| Member | What it is |
| --- | --- |
| `projectRoot`, `host`, `buildMode` | the project, the machine and `DEBUG` or `RELEASE` |
| `project` | the `ProjectSummary` of the project: its name, version, build mode and output directory |
| `outputDirectory` | the absolute directory the build mode of the project writes artifacts to |
| `workDirectory` | a directory of the step for intermediate files: `.tazel/build/<Publisher>.<Name>/<step>` of the project, the same in every build |
| `status(verb, message)`, `warn(message)` | prints a line in the output of Tazel |
| `run(command, directory, environment)` | runs a program, with variables added to those of Tazel, and returns its exit code; what it prints goes to the output of Tazel |
| `capture(command, directory, environment)` | runs a program like `run`, but returns its exit code and what it printed instead of showing it: for a probe whose failure is an answer, or a program whose output the step reads |
| `findProgram(name)` | the program on `PATH`, with the executable extension of the host, or `null` |
| `fail(message)` | stops the build with the coordinate of the plugin, the name of the step and the message |

`@TazelPluginDsl` marks the scopes of a plugin, so that a nested block cannot reach the members of the block around it by
mistake.

A failure inside `apply`, a finalizer or a step stops the command with the coordinate of the plugin and the message of
the exception, for example `plugin 'Publisher.CMake' failed while finalizing the project: the CMake plugin needs a
CMakeLists.txt`.

## How Tazel loads a plugin

1. **Resolve.** A local plugin must be a regular file, not a symbolic link, inside the project directory, also after its
   real path is resolved. A ViGet plugin is looked up in `.tazel/plugins` by publisher, name and, when given, version;
   when it is not there, its index, `index.json`, and its JAR, `<Version>/<Name>-<Version>.jar`, are fetched from ViGet,
   and the JAR is kept only when its SHA-256 is the one the index lists.
2. **Verify.** Tazel reads the descriptor, checks every key and computes the SHA-256 digest of the JAR. When a file
   `<plugin>.jar.sha256` lies beside the JAR, its first word must be that digest, in lower-case hex.
3. **Activate.** The JARs are put on a class loader of their own; for each plugin, the class its service entry names is
   created with its public constructor, and `apply` runs in the order of the declarations.
4. **Configure.** The project file runs. Its `plugins` block declares the same plugins again; each must have been resolved
   in step 1 with the version the block names. The blocks of the plugins add their build steps.
5. **Finalize.** After the script, every finalizer runs with the summary of the project, and the plugin entries are added
   to its plan.
6. **Lock.** The publisher, name, version, API version and digest of every plugin are recorded in `LOCK.sqlite3`; see
   [The lockfile](LOCKFILE.md).
7. **Build.** `tazel build` and `tazel run` run the build steps. The JARs stay open until the command ends.

## Trying a plugin

The quickest loop is a project of its own that names the JAR as a local plugin:

```kotlin
plugins {
  localPlugin { path = "build/libs/cmake-plugin-0.1.0.jar" }
}

project { name = "Demo" }

cmake {
  enabled = true
  define("BUILD_TESTS" to "OFF")
}
```

`tazel build` runs the steps of the plugin. `tazel lock resolve` loads the plugin, runs the script and writes the
lockfile without building anything; `tazel lock create-dump` then shows the plugin as the lockfile records it.
