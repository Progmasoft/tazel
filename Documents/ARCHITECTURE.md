<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# How Tazel works

Tazel is one Kotlin program for the JVM. It reads a project file, decides what to compile and starts other programs to
do the work: Talac compiles, `talafmt` formats, `talalint` lints. Tazel never links with them and shares no code with
them; it passes arguments, waits for the exit code and moves the files they leave behind.

This page follows one command from the terminal to the compiler and names the code that owns each step.

## A command, step by step

```text
tazel build -e Server
  │
  ├─ parseCommandLine         cli/CommandLine.kt   words → a typed Command, or a usage error (exit 2)
  ├─ Tazel.run                cli/Tazel.kt         one branch per command; every failure becomes exit 1
  ├─ discoverProject          dsl/ProjectLoader.kt the nearest project file, upward from the current directory
  ├─ loadWorkspace            driver/Packages.kt   the project, and the members when it is a workspace
  │    └─ ProjectLoader.load  dsl/ProjectLoader.kt plugins first, then the script → a ProjectPlan
  ├─ select                   driver/Packages.kt   the selection (-p, -e, -l) applied to the plan
  ├─ keepLockCurrent          cli/Tazel.kt         LOCK.sqlite3 rewritten when it no longer matches
  ├─ SourceFiles.discover     driver/SourceFiles.kt every .tala file below srcDir, minus the exclusions
  ├─ compilerArguments        driver/CompilerCommand.kt the settings of the plan as talac options
  ├─ Toolchain.require        driver/Toolchain.kt  where talac is
  └─ Processes.run            driver/Processes.kt  talac starts; its exit code decides the step
```

### 1. The command line

`parseCommandLine` is written by hand; Tazel uses no command-line library. It reads the words once and returns either a
`Command` (a sealed interface with one type per command, carrying its typed arguments) or a `Rejected` result with the
message and, for a mistyped word, a hint. No later step looks at the raw arguments again. A rejected line exits with `2`
before any file is read.

Option values come from the next argument (`--vipkg Name`, `--path File`). Everything after `--` is kept, unread, for the
program the command starts.

### 2. Finding and running the project file

`discoverProject` walks up from the current directory and stops at the first directory that holds one of the five project
file names. The names are compared as they are spelled, so `build.kts` is not a project file even on Windows and macOS,
whose file systems would otherwise match it.

`ProjectLoader.load` then works in two phases:

1. **Plugins.** `PluginPreamble` reads only the `plugins { }` block of the file, with a small lexer and parser that do not
   run any code. `PluginResolver` finds each declared JAR, checks its descriptor and digest, and `PluginRuntime` activates
   the plugins. See [Writing a plugin](PLUGINS.md).
2. **The script.** The whole file is compiled and run by the Kotlin scripting host embedded in Tazel
   (`BasicJvmScriptingHost`), with the DSL and the imports of the plugins as default imports. A compiled script is kept
   in `ScriptCache` (`dsl/ScriptCache.kt`) under a SHA-256 of everything its compilation depends on, so a later run
   skips the compilation and only evaluates it. The blocks of the script fill
   one model, `ProjectRuntime`; when the script ends, the model is checked as a whole and becomes an immutable
   `ProjectPlan`. Checks that depend on several blocks, such as the fields `sources.viget.push` requires, run at this point,
   so the order of the blocks never matters.

Only one script runs at a time, because the model it fills is a single object. A compile error in the script is reported
with the file name, line and column; `panic(message)` stops the evaluation with that message.

### 3. Workspaces and selection

`loadWorkspace` loads the root project and, when it declares a `workspace`, every member that is not excluded. Each member
is a project of its own and is loaded the same way. A ViPkg is reported by its `project.name`, or by its directory when it
has none.

`select` applies the selection of the command. It keeps every target when there is none, and fails with the name that
selected nothing otherwise; a near miss gets a "did you mean" hint.

### 4. The lockfile

Before it compiles a project, Tazel compares `LOCK.sqlite3` with what the plan declares and rewrites it when they differ.
The file records declarations, not a solved graph; see [The lockfile](LOCKFILE.md).

### 5. Sources and the compiler

`SourceFiles.discover` walks `srcDir` without following symbolic links and keeps every file whose name ends in `.tala`,
written exactly so. Exclusion patterns are matched by path segment; on Windows they ignore case, as the file system does.
The result is sorted, so the compiler sees the same order on every machine.

`compilerArguments` writes every compiler setting of the plan as an option, the ones at their default too. A build then
follows from the project file alone, not from the defaults of whichever `talac` is installed. The inputs come after
`--`, so a file named like an option is still read as a file.

Talac writes its output beside the first input. Tazel moves it into the output directory of the build mode, under the
name of the target, and fails if the compiler reported success and left nothing.

### 6. Formatting and linting

`tazel fmt` and `tazel lint` load the project the same way and start the tool once per ViPkg, with the tool's own
arguments, then `--`, then every source the ViPkg owns. Test sources are included when the command selects everything.
The tools read their own configuration files; Tazel does not interpret them.

## Errors and exit codes

| Exit code | Meaning | Raised by |
| --- | --- | --- |
| `0` | success | |
| `1` | the work failed: a project file that is wrong, a target that does not exist, a tool that rejected the sources | `ProjectConfigurationException`, `Stop`, `UnknownTargetException`, an SQLite error |
| `2` | the command line itself was wrong | `parseCommandLine` |

`tazel run` hands through the exit code of the program it ran. Every error is printed to standard error as one line,
with a hint line under it when one exists; Tazel prints no stack trace for an error that a user can fix.

## Terminal output

Status lines (`Compiling`, `Checking`, `Finished`) go to standard output, errors and warnings to standard error, all
through Mordant. While several targets are worked through on an interactive terminal, `StepProgress` draws a progress
bar; the output of the tools is then gathered and printed above the bar instead of writing over it. In a pipe or in CI
no bar is drawn and the tools write to the terminal directly.

## Seams for tests

`Tazel` receives everything that touches the machine as a constructor argument: the terminal, the working directory,
the operating system, the `Toolchain` (with its own environment and installation directory) and `Processes`. The tests
pass a recording `Processes` that writes the file a real compiler would leave, so whole commands run against projects on
disk without a compiler. See [Contributing](../CONTRIBUTING.md#tests).

## Packages

| Package | Holds |
| --- | --- |
| `com.progmasoft.tazel.cli` | `main`, the command-line parser, the commands, help, hints, the progress bar, the dependency graph |
| `com.progmasoft.tazel.dsl` | the DSL scopes, the model and `ProjectPlan`, the loader that runs a project file |
| `com.progmasoft.tazel.dsl.plugin` | the plugin preamble, resolution and verification, the plugin runtime |
| `com.progmasoft.tazel.driver` | workspaces and selection, source discovery, compiler arguments, tool lookup, processes |
| `com.progmasoft.tazel.lock` | `LOCK.sqlite3` and its text dump |
| `com.progmasoft.api.tazel.dsl.plugin` | `ProjectPlugin` and `ProjectPluginContext`, the API a plugin is written against |
