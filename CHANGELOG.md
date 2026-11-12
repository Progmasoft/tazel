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
