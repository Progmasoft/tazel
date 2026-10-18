<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# Contributing to Tazel

Tazel is the build system and package manager of Tala. This page covers what a change needs: the tools, the checks CI
runs, where tests go and the conventions the code follows. [How Tazel works](Documents/ARCHITECTURE.md) explains the
code itself.

Questions about the language or the compiler belong in [Progmasoft/tala](https://github.com/Progmasoft/tala). Report a
security problem privately to support@progmasoft.com, not in an issue.

## Setting up

You need JDK 25 or newer. Gradle comes with the wrapper; do not use a Gradle installed on the machine. The developer
script needs the `kotlin` launcher as well (see the [README](README.md#building-and-testing)).

```powershell
kotlin helpers/develop.main.kts doctor
kotlin helpers/develop.main.kts check
```

`check` is what CI runs on Ubuntu and Windows: formatting, compilation with every warning an error, and every test. It
needs no Tala compiler.

To try a change by hand, build the launcher and point it at a compiler:

```powershell
kotlin helpers/develop.main.kts build
$env:TAZEL_TALAC = "C:\path\to\talac.exe"
.\build\install\tazel\bin\tazel.bat build
```

## Tests

The tests live in `sources/test/kotlin`, one package beside the package they test.

| Test | Covers |
| --- | --- |
| `cli/CommandLineTest` | the parser: every command, flag, value and refusal, and the hints |
| `cli/TazelTest` | whole commands against projects written to a temporary directory |
| `dsl/ProjectDslTest`, `dsl/DependencyDslTest` | the DSL blocks and their checks |
| `dsl/ProjectLoaderTest` | discovery and running a project file, including the whole [`BUILD.kts`](BUILD.kts) |
| `dsl/plugin/PluginInfrastructureTest` | the preamble, resolution, digests and the plugin runtime, with JARs built in the test |
| `driver/SourceFilesTest` | source discovery and exclusion patterns |
| `lock/LockfileDumpTest` | the text dump |

`TazelTest` runs a real `Tazel` with a stand-in for `Processes`. The stand-in records every command line, and for `talac`
it writes the file a compiler would leave, so a test can check both the arguments Tazel passed and what it did with the
result. The tools are empty files in a `tools` directory that the test puts on `PATH`. Add a case there for every change
in what a command does; add a parser case to `CommandLineTest` for every change in what a command accepts.

[`BUILD.kts`](BUILD.kts) at the root is the whole DSL in one file and a test evaluates it. A DSL change updates that file,
so the example and the DSL cannot drift apart.

## Conventions

- **The command line is written by hand.** Tazel uses no command-line library: a word is read once, in
  `cli/CommandLine.kt`, and becomes a typed `Command`. No later step looks at the raw arguments. (The developer script
  uses Clikt; that does not change how Tazel itself reads its command line.)
- **Errors a user can fix are messages, not stack traces.** A wrong project file throws `ProjectConfigurationException`;
  a command that cannot go on throws `Stop`, with a hint when a word looks like a typo. Both become one line on standard
  error and exit code `1`. A wrong command line is exit code `2`.
- **Tazel compiles nothing.** It starts Talac, `talafmt` and `talalint` as programs and never links with them. Every
  compiler setting is written onto the command line, the ones at their default too.
- **Refuse by name what is not there yet.** A command or setting that is not implemented says so and exits with `1`;
  it does not do something else instead.
- **Paths stay inside the project.** Every path a project file names is resolved against the project directory and
  refused when it leaves it. Source discovery does not follow symbolic links, and a local plugin is checked again after
  its real path is resolved.
- **Formatting is ktfmt** (Google style) through Spotless; run `kotlin helpers/develop.main.kts format` before a commit.
  The compiler treats every warning as an error.
- **Public declarations have KDoc**, in US English, saying what they are for rather than how they work.
- **Dependencies are locked** in `gradle.lockfile`. After changing one, run `.\gradlew.bat dependencies --write-locks`
  and commit the lock file with the change.

## Adding a command

1. Add it to `CommandName` and give it a type in `Command`, in `cli/CommandLine.kt`.
2. Parse its arguments in the same file, and add its usage line in `cli/Main.kt` for `tazel help <command>`.
3. Handle it in `Tazel.run`, in `cli/Tazel.kt`.
4. Test it in `CommandLineTest` and `TazelTest`.
5. Describe it in [the command reference](Documents/CLI.md), and in [project files](Documents/PROJECT_FILES.md) when it
   reads something new from them.
6. Note it under `## Unreleased` in the [changelog](CHANGELOG.md).

## Pull requests

Every change to `main` goes through a pull request. "CI Required" must pass: it covers `gradlew check` on Ubuntu and
Windows and the developer script. CodeQL, zizmor and Dependency Review run as well. Keep a pull request to one change,
and write its description and commit messages in US English, saying what changed and why.

## Releases

A release is tagged `tazel-<version>`. To prepare one:

```powershell
kotlin helpers/develop.main.kts version --set 0.2.0
kotlin helpers/develop.main.kts check
```

`version --set` writes the version into `build.gradle.kts` and turns the notes under `## Unreleased` into the section
of that version, dated today. CI runs `version` on every pull request, so the build and the changelog cannot disagree.
