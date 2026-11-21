<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Changelog

## Unreleased

- A remote cache: `Cache.Remote` takes results of actions from a cache that speaks Bazel's HTTP protocol, such as
  bazel-remote, and gives it the results of actions run here. Keys do not depend on where the module is; headers a
  compiler found are checked by digest before a result is taken, and every file by its SHA-256. A cache that cannot
  be reached is said once, and the build goes on.
- Project files import `System`, so `Environment.GetEnvironmentVariable` works as the examples write it.

The first version of Tazel, written in C# for .NET 10.

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
