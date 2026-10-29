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

## Documentation

- [The `tazel` command](Documents/CLI.md): every command, target selection, workspaces, the dependency graph.
- [Tala project files](Documents/PROJECT_FILES.md): the project file, block by block. [`BUILD.kts`](BUILD.kts) is the
  whole DSL in one file; a test evaluates it.
- [The lockfile](Documents/LOCKFILE.md): what `LOCK.sqlite3` records, when it is written, and its text dump.
- [Writing a plugin](Documents/PLUGINS.md): the descriptor, the API, and how Tazel verifies and loads a plugin.
- [How Tazel works](Documents/ARCHITECTURE.md): one command followed through the code.
- [Contributing](CONTRIBUTING.md): checks, tests, conventions, adding a command, releases.

The site of Tazel is [tazel.tala.run](https://tazel.tala.run/).

## Layout

| Package | Holds |
| --- | --- |
| `com.progmasoft.tazel.cli` | the command line: the typed parser, the commands, `main` |
| `com.progmasoft.tazel.dsl` | the project DSL, its model and the loader that evaluates a project file |
| `com.progmasoft.tazel.dsl.plugin` | the plugin host: preamble, resolution, the ViGet download, verification, activation, build steps |
| `com.progmasoft.tazel.driver` | source discovery, compiler arguments, tool lookup, workspaces |
| `com.progmasoft.tazel.lock` | `LOCK.sqlite3` and `LOCK.dump` |
| `com.progmasoft.api.tazel.dsl.plugin` | the API a plugin is written against, in `api/` (`com.progmasoft.tazel:tazel-api`) |
| `com.progmasoft.tazel.gradle` | the Gradle plugin `com.progmasoft.tazel.plugin` for building plugins, in `gradle-plugin/` |

The command line is read by a parser written by hand; there is no command-line library. Terminal output goes through
Mordant. The project file is compiled and run inside Tazel by the embedded Kotlin scripting host, so no Kotlin installation
is needed beside the JRE.

## Building and testing

```powershell
.\gradlew.bat check          # format check and tests
.\gradlew.bat installDist    # build\install\tazel\bin\tazel(.bat)
```

The same steps, and a few more, are available as one Kotlin script built on Clikt. It needs JDK 25 and the `kotlin` launcher.
The GitHub runners have it. Elsewhere, unpack `kotlin-compiler-<version>.zip` from the
[Kotlin releases](https://github.com/JetBrains/kotlin/releases) and put its `kotlinc/bin` on `PATH`; the launcher
under `plugins/Kotlin/kotlinc/bin` in IntelliJ IDEA works too:

```powershell
kotlin helpers/develop.main.kts --help
kotlin helpers/develop.main.kts doctor          # JDK, Gradle wrapper, Git, and the Tala tools Tazel would start
kotlin helpers/develop.main.kts check           # what CI runs, with the Markdown links
kotlin helpers/develop.main.kts test --tests '*LockTest'
kotlin helpers/develop.main.kts run -- lock graph
kotlin helpers/develop.main.kts bundle          # distribution archives, smoke-tested, with .sha256 files
kotlin helpers/develop.main.kts coverage        # JaCoCo line, branch and method coverage
kotlin helpers/develop.main.kts version         # build.gradle.kts and CHANGELOG.md agree
kotlin helpers/develop.main.kts version --set 0.2.0
kotlin helpers/develop.main.kts version --release 0.2.0   # and the tag tazel-0.2.0 is new
kotlin helpers/develop.main.kts release-notes 0.2.0
kotlin helpers/develop.main.kts info            # checkout, version, JDK; nothing changes
kotlin helpers/develop.main.kts verify-docs     # every relative Markdown link and anchor
kotlin helpers/develop.main.kts clean --dry-run # build/, .gradle/, .kotlin/
kotlin helpers/develop.main.kts self-test       # the script's own logic
```

Every program the script starts is one step; a table of the steps and their times closes a command that ran more than
one, and on GitHub Actions each step is a collapsible group that also goes to the job summary. `bundle` builds the
`.zip` and `.tar` distributions, runs the installed launcher with `version`, and writes the SHA-256 of each archive
beside it; the archives keep no file times, so a commit packs into the same bytes.

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
