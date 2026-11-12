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
- The plugin API, `com.progmasoft.tazel:tazel-api` (API version 1), and the Gradle plugin `com.progmasoft.tazel.plugin`,
  taken from the legacy Tazel. `tazelPlugin { }` now holds `group`, `name` and `version`, which the plugin descriptor
  records; the group must be a reversed domain and the version a Semantic Version.
