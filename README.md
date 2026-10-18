<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# Tazel

Tazel is the build system and package manager of [Tala](https://tala.run/). The name joins Tala and Bazel.
It moved from the [compiler repository](https://github.com/Progmasoft/tala) with its history on 2026-10-18.

Tazel is a Kotlin program for the JVM (25 or newer). It reads the project file, finds the sources and starts the compiler,
the formatter or the linter as separate programs: Talac, `talafmt` and `talalint`. It compiles nothing itself and shares
no code with them.

```powershell
tazel check
tazel build
tazel run -- arguments for the program
tazel fmt -- --check
tazel lint -- --fix
```

[The `tazel` command](Documents/CLI.md) lists every command and [Tala project files](Documents/PROJECT_FILES.md)
describes the project file. [`BUILD.kts`](BUILD.kts) is the whole DSL in one file; a test evaluates it.
The documentation is also published at [tazel.tala.run](https://tazel.tala.run/).

## Layout

| Package | Holds |
| --- | --- |
| `com.progmasoft.tazel.cli` | the command line: the typed parser, the commands, `main` |
| `com.progmasoft.tazel.dsl` | the project DSL, its model and the loader that evaluates a project file |
| `com.progmasoft.tazel.dsl.plugin` | the plugin host: preamble, resolution, verification, activation |
| `com.progmasoft.tazel.driver` | source discovery, compiler arguments, tool lookup, workspaces |
| `com.progmasoft.tazel.lock` | `LOCK.sqlite3` and `LOCK.dump` |
| `com.progmasoft.api.tazel.dsl.plugin` | the API a plugin is written against |

The command line is read by a parser written by hand; there is no command-line library. Terminal output goes through
Mordant. The project file is compiled and run inside Tazel by the embedded Kotlin scripting host, so no Kotlin installation
is needed beside the JRE.

## Building and testing

```powershell
.\gradlew.bat check          # format check and tests
.\gradlew.bat installDist    # build\install\tazel\bin\tazel(.bat)
```

The same steps, and a few more, are available as one Kotlin script built on Clikt. It needs the `kotlin` launcher (the
GitHub runners have it; IntelliJ IDEA ships one under `plugins/Kotlin/kotlinc/bin`) and JDK 25:

```powershell
kotlin helpers/develop.main.kts --help
kotlin helpers/develop.main.kts doctor          # JDK and Gradle wrapper
kotlin helpers/develop.main.kts check           # what CI runs
kotlin helpers/develop.main.kts run -- lock graph
kotlin helpers/develop.main.kts version         # build.gradle.kts and CHANGELOG.md agree
kotlin helpers/develop.main.kts version --set 0.2.0
kotlin helpers/develop.main.kts clean           # build/, .gradle/, .kotlin/
```

`version --set` writes the version into `build.gradle.kts` and turns the notes under `## Unreleased` in `CHANGELOG.md`
into the section of that version, dated today. The command line of Tazel itself stays hand-written; Clikt is used only
by this developer script.

Every resolved dependency is locked in `gradle.lockfile`, which OSV-Scanner also reads. After changing a dependency, run
`.\gradlew.bat dependencies --write-locks` and commit the lock file with the change.

Tazel finds a tool through `TAZEL_TALAC`, `TAZEL_TALAFMT` or `TAZEL_TALALINT`, then in the installation it runs from, then
on `PATH`. To try a development build against a compiler built by Bazel:

```powershell
$env:TAZEL_TALAC = "..\bazel-bin\Talac\Cli\talac.exe"
.\build\install\tazel\bin\tazel.bat build
```

The tests run whole commands against projects on disk with stand-in tools that record their command line, so they need no
compiler.
