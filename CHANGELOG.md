<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0 -->

# Changelog

Versions follow [Semantic Versioning](https://semver.org). The earlier Tazel, 0.1.0 to 0.4.0.3, has its changelog in
[Progmasoft/tazel-legacy](https://github.com/Progmasoft/tazel-legacy/blob/main/CHANGELOG.md).

## Unreleased

- A Kotlin program for the JVM that prints its version and help.
- Labels: `//:x`, `//dir/:x`, `:x`, `@name//:x`, and the patterns `//dir/...` and `//...`.
- `MODULE.kts` and `BUILD.kts` are read: the module, its dependencies, `viget`, `cache` and `execution`;
  `c`, `cpp`, `objc`, `objcpp` and `zig` targets of the four kinds, with `isPublic` per file. A setting that a
  language or kind does not have is a compile error of the project file. A target block runs for each triple and
  build mode; its labels are resolved and visibility is checked.
- `tazel custom`: Tazel Custom Commands from `tazel_custom.txt`, and Tazel Options from `tazel_options.txt`, taken
  from the legacy Tazel with their tests; a sandbox asked for stops a custom command, since Tazel has no MicroVM to start.
- The plugin API, `com.progmasoft.tazel:tazel-api` (API version 1), and the Gradle plugin `com.progmasoft.tazel.plugin`,
  taken from the legacy Tazel. `tazelPlugin { }` now holds `group`, `name` and `version`, which the plugin descriptor
  records; the group must be a reversed domain and the version a Semantic Version.
- `tazel build` and `tazel run` for C, C++, Objective-C, Objective-C++ and Zig targets, with `--release` and
  `--triple`. Builds are incremental per action: each compile, archive, link and Zig build runs again only when its command
  line, its environment or the content of a file it read changed (headers from the depfile of the compiler), or an
  output it wrote is gone or altered. Touching a file reruns nothing, and an object rebuilt to the same bytes reruns
  neither its archive nor its program. Clang, GCC and
  `zig cc` are found on PATH; Clang and `zig cc` build for other triples. Outputs go to `.tazel/out/<triple>/<mode>`.
- `tazel test` builds and runs test targets: each in its output directory beside its data, with its args and env,
  split into shards with `TEST_TOTAL_SHARDS` and `TEST_SHARD_INDEX`, stopped at its timeout (from `size` unless set),
  run up to three times when `flaky`, with tests tagged `EXCLUSIVE` alone and `MANUAL` ones only when named. A test
  that passed is not run again while its program, data, arguments and environment are the same, unless `NO_CACHE`.
- `tazel fmt` formats the sources, header directories and textual headers of the selected targets, on every triple
  they are built for, with clang-format (C, C++, Objective-C, Objective-C++) and `zig fmt`; `--check` reports what is
  not formatted and changes nothing. Each tool reads its own configuration, such as `.clang-format`.
- `tazel lint` writes the compilation database of the selected targets and their libraries to
  `.tazel/out/<triple>/<mode>/compile_commands.json`, copies it to `.tazel/compile_commands.json`, and runs clang-tidy
  on each C-family translation unit of the selected targets; `--fix` applies its fixes, one unit at a time. Zig
  targets are skipped. For both commands, what follows `--` goes to the tool.
