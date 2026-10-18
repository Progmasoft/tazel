<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# Writing a plugin

A plugin is a JAR that extends the project DSL. It can add functions a project file calls, record values in the plan of
the project and check the project once it is complete. This page shows how a plugin is built, how Tazel finds and
verifies it, and what it may do once it runs.

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

- `plugin("Publisher") { name = ...; version = ... }` asks for a plugin of ViGet. `version` is optional; without it the
  highest version present is used. Until the ViGet client exists, the JAR has to be in the cache of the project,
  `.tazel/plugins`, in any folder below it.
- `localPlugin { path = ... }` names a JAR inside the project directory. Its publisher, name and version come from the
  JAR itself.

Tazel reads this block **before** it runs the rest of the file, with a small parser that runs no code. That parser
accepts only what the example shows: `plugin("...")` and `localPlugin`, with fields written as plain string literals; a
value computed from a variable or a string template is refused. Comments and strings elsewhere in the file are skipped,
so a `plugins` word inside them is not mistaken for the block.

When the script runs, the block declares the same plugins again, and every plugin the parser found must be declared.
Write the block at the top level of the file: inside an `if` that does not run, its plugins stay undeclared and the
command fails with `resolved plugins were not declared by the script`.

## The JAR

A plugin JAR holds three things:

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
apiVersion=1
imports=com.example.cmake.dsl.*
```

| Key | Rule |
| --- | --- |
| `publisher` | one identifier: the name of a Progmasoft Account |
| `name` | one identifier |
| `version` | an exact semantic version |
| `apiVersion` | the plugin API it was built for; Tazel loads only `1`, the current `PROJECT_PLUGIN_API_VERSION` |
| `imports` | optional; comma-separated packages or classes the project file imports by default, such as `com.example.cmake.dsl.*` |

An import must be a dotted name, optionally ending in `.*`; anything else is refused.

### The service

`META-INF/services/com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin` names the class that implements `ProjectPlugin`:

```text
com.example.cmake.CMakePlugin
```

Each plugin JAR provides exactly one such class for its coordinate, and its `publisher`, `name`, `version` and
`apiVersion` must be those of the descriptor.

## The API

The API lives in `com.progmasoft.api.tazel.dsl.plugin`:

```kotlin
interface ProjectPlugin {
  val publisher: String
  val name: String
  val version: String
  val apiVersion: Int get() = PROJECT_PLUGIN_API_VERSION

  fun apply(context: ProjectPluginContext)
}

interface ProjectPluginContext {
  val projectRoot: Path

  fun registerExtension(name: String)
  fun contribute(key: String, value: String)
  fun onFinalize(action: (ProjectPlan) -> Unit)
}
```

| Call | What it does |
| --- | --- |
| `projectRoot` | the directory of the project file |
| `registerExtension(name)` | claims a DSL name, such as `cmake`, for this plugin. A name is one identifier, and two plugins cannot claim the same one. |
| `contribute(key, value)` | records a value in the plan of the project. A key starts with a letter or `_` and holds at most 128 letters, digits, `_`, `.` or `-`; each key is written once per plugin, and the value cannot be empty. |
| `onFinalize(action)` | runs `action` with the finished `ProjectPlan` after the script ended, for checks that need the whole project |

The extensions and contributions of each plugin end up in `ProjectPlan.plugins`.

## An example

```kotlin
package com.example.cmake

import com.progmasoft.api.tazel.dsl.plugin.ProjectPlugin
import com.progmasoft.api.tazel.dsl.plugin.ProjectPluginContext

class CMakePlugin : ProjectPlugin {
  override val publisher = "Publisher"
  override val name = "CMake"
  override val version = "0.1.0"

  override fun apply(context: ProjectPluginContext) {
    context.registerExtension("cmake")
    context.contribute("generator", "Ninja")
    context.onFinalize { plan ->
      if (plan.executables.isEmpty()) error("the CMake plugin needs an executable target")
    }
  }
}
```

The function a project file calls checks that the extension it belongs to was registered by its own plugin:

```kotlin
package com.example.cmake.dsl

import com.progmasoft.tazel.dsl.plugin.PluginRuntime

class CMakeScope {
  var minimumVersion: String? = null
}

fun cmake(block: CMakeScope.() -> Unit) {
  PluginRuntime.requireExtension("cmake", publisher = "Publisher", plugin = "CMake")
  CMakeScope().apply(block)
}
```

A failure inside `apply` or inside a finalizer stops the command with the coordinate of the plugin and the message of
the exception, for example `plugin 'Publisher.CMake' failed while finalizing the project: the CMake plugin needs an
executable target`.

## Building the JAR

A plugin compiles against the JAR of Tazel and does not bundle it; Tazel provides the API and the Kotlin standard library
when it loads the plugin.

```kotlin
// build.gradle.kts of the plugin
plugins { kotlin("jvm") version "2.4.0" }

kotlin { jvmToolchain(25) }

dependencies {
  compileOnly(files("<Tazel installation>/lib/tala-project-0.1.0.jar"))
}
```

`gradlew jar` then writes the plugin JAR. Put the descriptor and the service file under `src/main/resources/META-INF/`.

## How Tazel loads a plugin

1. **Resolve.** A local plugin must be a regular file, not a symbolic link, inside the project directory, also after its
   real path is resolved. A ViGet plugin is looked up in `.tazel/plugins` by publisher, name and, when given, version.
2. **Verify.** Tazel reads the descriptor, checks every key and computes the SHA-256 digest of the JAR. When a file
   `<plugin>.jar.sha256` lies beside the JAR, its first word must be that digest, in lower-case hex.
3. **Activate.** The JARs are put on a class loader of their own, the `ProjectPlugin` services are loaded, matched against
   their descriptors, and `apply` runs for each plugin in the order of the declarations.
4. **Configure.** The project file runs. Its `plugins` block declares the same plugins again; each must have been resolved
   in step 1 with the version the block names.
5. **Finalize.** After the script, every finalizer runs with the finished plan, and the plugin entries are added to it.
6. **Lock.** The publisher, name, version, API version and digest of every plugin are recorded in `LOCK.sqlite3`; see
   [The lockfile](LOCKFILE.md).

## Trying a plugin

The quickest loop is a project of its own that names the JAR as a local plugin:

```kotlin
plugins {
  localPlugin { path = "build/libs/cmake-plugin-0.1.0.jar" }
}

project { name = "Demo" }

cmake {
  minimumVersion = "3.30"
}
```

`tazel lock resolve` loads the plugin, runs the script and writes the lockfile without starting the compiler;
`tazel lock create-dump` then shows the plugin as the lockfile records it. An error in the descriptor, the digest, `apply`
or a finalizer is reported by either command.
