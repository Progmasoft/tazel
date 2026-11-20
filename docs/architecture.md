<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Architecture

## Projects

| Project | What it is |
| --- | --- |
| `src/Progmasoft.Tazel.Dsl` | the types project files are written against; scripts reference this assembly alone |
| `src/Progmasoft.Tazel` | the `tazel` command |
| `src/Progmasoft.Tazel.PluginApi` | the API of plugins and their build steps (NuGet `Progmasoft.Tazel.PluginApi`) |
| `src/Progmasoft.Tazel.PluginSdk` | the MSBuild project SDK of plugins and the task that packs them |
| `src/Progmasoft.Tazel.PluginTemplates` | `dotnet new tazel-plugin` |
| `src/Shared` | the reproducible archive writer, compiled into tazel and the SDK task |
| `tests/Progmasoft.Tazel.Tests` | xUnit tests, with CsCheck for properties and Snapshooter for snapshots |
| `benchmarks/Progmasoft.Tazel.Benchmarks` | BenchmarkDotNet measurements of the readers |
| `fuzz/Progmasoft.Tazel.Fuzz` | SharpFuzz targets for libFuzzer |

## Namespaces of tazel

| Namespace | What it does |
| --- | --- |
| `Progmasoft.Tazel.Cli` | reads the command line into typed commands, selects targets, carries commands out |
| `Progmasoft.Tazel.Label` | reads labels and package patterns |
| `Progmasoft.Tazel.Project` | finds the module and its packages, runs the project files, checks targets and what they depend on |
| `Progmasoft.Tazel.Driver` | chooses toolchains, plans actions, runs them with the action cache, runs tests |
| `Progmasoft.Tazel.Archive` | writes and unpacks `.tazel.tar.xz` archives and reads `.sha256sums` |
| `Progmasoft.Tazel.Output` | prints through Spectre.Console |
| `Progmasoft.Tazel.Dsl.Custom`, `.Lexer`, `.Parser` | `tazel_custom.txt` |
| `Progmasoft.Tazel.Dsl.Options` | `tazel_options.txt`, with the lexer of `tazel_custom.txt` |
| `Progmasoft.Tazel.Dsl.Envs` | `tazel_envs.txt` and the values `GetEnvironment.From` reads, with the same lexer |

## A build

1. `CommandLine` reads the arguments into a `BuildCommand`, `TestCommand` or `RunCommand`; nothing later looks at the
   text again.
2. `Workspace` finds the nearest `MODULE.csx`, runs it, unpacks and loads the archives it depends on, and runs every
   `BUILD.csx` of each module at once. A project file declares its targets and toolchains through the static methods of
   the preludes, which record them for the file that is running.
3. `Workspace` checks each target: its settings, what it depends on, visibility, and that no targets depend on each
   other.
4. `Selector` turns the labels into targets, leaving out of patterns what is `manual` or does not build for the
   platform.
5. `ToolchainChooser` picks the toolchain of each family; `Planner` turns each target into actions: one compilation
   per file, then the archive or the link, or one zig command.
6. `Executor` runs the actions as soon as what they read is made, at most `--jobs` at once, skipping each one the
   `ActionCache` finds current.
7. `TestRunner` runs the tests that were built, in their runfiles directories, and the reporter prints the summary.

## The archive of a module

`$group.$module-$version.tazel.tar.xz` holds the files of the module below one directory of that name: sorted, owned
by no one, dated at the epoch, `0644` or `0755`, compressed by Lzma.Net with one thread. Two archives of the same files
are the same bytes. Unpacking refuses links, devices, absolute paths, `..`, more than one top directory, more than
100,000 entries and more than 1 GiB.
