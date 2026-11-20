<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Changelog

## Unreleased

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
- Plugins: `Progmasoft.Tazel.PluginApi`, the project SDK `Progmasoft.Tazel.PluginSdk` that packs a plugin with
  `dotnet pack`, the template `dotnet new tazel-plugin`, local plugins loaded each into its own load context, and build
  steps that plugins and `BUILD.csx` files declare.
