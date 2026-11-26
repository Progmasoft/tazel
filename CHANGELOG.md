<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Changelog

## Unreleased

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
