<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Changelog

## Unreleased

- Progmasoft.Tazel.Packaging, a new NuGet package, reads and writes Tazel packages as NuGet.Packaging does for NuGet:
  package identities, reproducible `.tazel.tar.xz` archives and safe unpacking, `.sha256sums` files, the plugin
  manifest, and SPDX license expressions with the SPDX License List 3.29.0. Tazel and the plugin SDK use it.

- `License` in `MODULE.csx` is checked as an SPDX license expression against that list (`spdx/`, updated by
  `dotnet run spdx/update.cs -- <version>`). A misspelt identifier is refused with the closest ones, a deprecated one
  with what replaces it, a malformed expression with what is wrong: `error[E0509]`. The check is a fuzz target.

- `.tazelignore`: gitignore rules, `regex:` and `!regex:`, `pragma:once`, `import:` and `include:`, nested per
  directory, for package discovery, patterns, source globs and module archives; explicit paths and labels are never
  ignored. `tazel ignore create-from|validate|add|delete|del`. Error `E0013`.

- The same sources give the same objects and programs on Windows: clang compiles with
  `-mno-incremental-linker-compatible` and links with `/Brepro` (MinGW: `--no-insert-timestamp`), and every action runs
  with `SOURCE_DATE_EPOCH` (0 unless set). Before, each compilation stamped the time into the object, so the archive,
  the program and its cached test ran again after any recompilation.

- `--recompile` and `--no-cache` for `build`, `run`, `test` and `coverage`: every compilation runs, or every action and
  test runs with no cache read or written. zig compiles again with an empty cache of its own.

- `tazel tool launch <name>` runs `TAZEL_TOOL_EXECUTABLE`, or the first `tazel-<name>` of `PATH` this user may run,
  with its arguments as written, and passes its exit status on; `tazel tool create-wrapper <name> [-o dir]` writes a
  `tazel-<name>` that runs `<name>`. Error `E0507`.

- Configuration in layers: `$TAZEL_HOME/User/`, the directories above, `$TAZEL_HOME/Projects/<key>/` and the current
  directory, for `tazel_options.txt`, `tazel_custom.txt` and the main module's `tazel_envs.txt`. The project key is
  Agenceland's. New commands `tazel options validate|get|what-is|update`, `tazel custom-cmds validate` and
  `tazel envs validate`, with `--user`, `--project`, `--local` and `--create`.

- `--dotnet-settings Name=Value[,...]` and `dotnet-settings: [ ... ];` in `tazel_options.txt` set the .NET runtime of
  the Tazel process: the GC heap hard limits (in bytes or percent, whole or per object heap), the GC latency mode, and
  the thread pool's minimum and maximum threads. `tazel dotnet get <setting>` reads them; `tazel dotnet what-is <term>`
  explains fourteen terms of .NET offline. Errors `E0213` and `E0506`.

- `tazel_options.txt`, `tazel_custom.txt` and `tazel_envs.txt` take directives: `.include "file"` and
  `.include <file>` (below `$TAZEL_HOME/Include/`), `.import`, `.pragma once`, and `.if`, `.elif`, `.else`, `.end` on
  conditions over `host_system`, `target_system`, `defined("NAME")` and `env("NAME")`. `tazel_options.txt` chooses the
  platform before it reads what depends on it. A line that starts with a dot and a letter is a directive; a name that
  starts with a dot goes in quotes. Errors `E0006` to `E0012`.

- Breaking: a package's project file is `BUILD.csx` and nothing else; a directory with `BUILD.tzl`, `BUILD.tazel`,
  `BUILD` or `Tazel.build` stops the build with a message. `Depends` takes labels only, such as `"//:math"`; the value a
  declaration returns is no longer taken. `tazel limited-time C#`, until 1.0.0, renames those files and writes the
  targets `Depends` named by their variables as labels, without running any project file.

## 0.2.0 - 2026-11-27

- `IncludeDirectories = ["."]` names the package's own directory, as `includes = ["."]` does in Bazel; it was refused.

- Building for DragonFly BSD with zig says that zig brings no libc for it, instead of zig's own error, and what does
  build for it. Tests build Zig, C and C++ for FreeBSD and OpenBSD with zig and check that the files are theirs.

- `--message-format rich-stdio|script-friendly-stdio|jsonl|xml`: the rich terminal output as before, plain lines that
  start with a fixed word for scripts and logs, one JSON object per message per line, or one XML document. Errors of
  the command line come in the format it asks for.

- A build reads each file once: a header that every compilation includes is looked at once, not once per
  compilation.

- Project files are compiled once: the compiled `MODULE.csx` and `BUILD.csx` files are kept in `.tazel/scripts/`,
  so a command whose project files did not change does not start the C# compiler (a build with nothing to do on a
  module of a dozen packages: about 3 s before, under half a second now). `tazel clean` removes them. An exception a
  project file throws names its line. The release archives are compiled ahead of time for their platform
  (ReadyToRun), so commands start faster.

- The last entry of `tazel_envs.txt`, `tazel_custom.txt` and `tazel_options.txt` may end the file without its `;`;
  between two entries the `;` is needed, and its absence is still `error[E0406]` and `error[E0305]`.

- Signed remote cache results: `PublicKey` and `SigningKey` of `Remote.From`, written as `ed25519:`, `rsa-4096:` or
  `ml-dsa-65:` and the key in base64. Writers sign each result; readers take only results signed with their key's
  algorithm and verified by it. `tazel cache keygen [--algorithm ed25519|rsa-4096|ml-dsa]` makes a pair; a key Tazel
  cannot read is `error[E0505]`.

- A remote cache: `Cache.Remote` takes results of actions from a cache that speaks Bazel's HTTP protocol, such as
  bazel-remote, and gives it the results of actions run here. Keys do not depend on where the module is; headers a
  compiler found are checked by digest before a result is taken, and every file by its SHA-256. A cache that cannot
  be reached is said once, and the build goes on.
- Project files reach the whole base class library of .NET; each namespace is named with `using`, and none is
  imported for them.

## 0.1.0 - 2026-11-20

The first release of Tazel, written in C# for .NET 10. Archives for Linux, macOS and Windows,
and the NuGet packages of the plugin API, the plugin SDK and its templates.

- Project files are C# scripts: `MODULE.csx` at the root of a module, `BUILD.csx` in each package, run with the C#
  scripting of Roslyn.
- Targets of the families `CC`, `ObjectiveC` and `Zig`, labels as in Bazel (`//dir:x`), visibility per target, and
  `TargetConstraints` that `//...` follows.
- Toolchains as in Bazel, with features of flags per action, registered in `MODULE.csx`; this machine's clang, gcc or
  zig without one.
- `tazel build`, `test`, `run`, `clean`, `package`, `custom`, `version` and `help`, with `--platform`, `--mode`,
  `--feature`, `--no-feature` and the options for the compilers and the linker.
- An action cache by content, depfiles for headers, parallel actions, runfiles, sharded and flaky tests.
- Reproducible `.tazel.tar.xz` archives of modules, and dependencies on them checked by SHA-256.
- `tazel_options.txt` (with `platform:`), `tazel_custom.txt`, and `tazel_envs.txt` with `GetEnvironment.From`.
- Errors with codes (`error[E0406]:`), the line and a mark under the problem, help, and `tazel explain <code>`
  with `--long`.
- `tazel coverage` (LLVM source-based, an LCOV report), `tazel query`, `cquery` and `aquery`, `tazel fetch`,
  `tazel vendor`, and `tazel portal-modules search` and `info`; `tazel help --long`.
- Modules of Tazel Portal Modules as dependencies: each at the highest version asked for, checked by SHA-256,
  from `vendor/` first.
- The MicroVM: with `sandboxing-with-microvm`, `tazel custom` runs in a Linux guest under Cloud Hypervisor on KVM
  that sees the module and the host's `/usr`, read-only, and nothing else; its agent `tazel-guest`, and
  `microvm/build.cs`, which builds the guest image.
- A test that passed is not run again while its program, the shared libraries beside it, its data, arguments,
  environment, shards, timeout and retries are the same; `no-cache` runs it every time.
- Plugins: `Progmasoft.Tazel.PluginApi`, the project SDK `Progmasoft.Tazel.PluginSdk` that packs a plugin with
  `dotnet pack`, the template `dotnet new tazel-plugin`, local plugins loaded each into its own load context, and build
  steps that plugins and `BUILD.csx` files declare.
