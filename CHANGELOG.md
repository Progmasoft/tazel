<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# Changelog

## Unreleased

- Compiled project files are cached per user; a later run only evaluates them.
- Every project evaluation has its own context, so project files can be
  evaluated on several threads at once.
- `tazel jvm start-daemon`, `restart-daemon` and `stop-daemon` manage a JVM
  daemon that keeps Tazel warm; `TAZEL_DAEMON` chooses when a command goes to
  it.
- `TAZEL_TIMINGS=1` prints how long each phase of a run took.

## 0.1.0 - 2026-10-18

The first release of Tazel from a repository of its own. Until 0.6.0 Tazel was
released with the compiler and carried its version; it now starts its own
numbering. It moved with its history from Progmasoft/tala.

- `tazel lock create-dump --path File` writes the dump to any file whose folder
  exists.
- `tazel lock graph` prints the dependency graph, with `--locked`,
  `--all-features`, `--duplicates`, `--invert` and `--depth`.
- `tazel viget vipkg-desc Publisher.Name` is recognized and refused by name
  until the ViGet client exists.
- Mistyped words get a "did you mean" hint, and an interactive terminal shows a
  progress bar.
- `helpers/develop.main.kts`, a Kotlin script built on Clikt, runs the developer
  steps: `doctor`, `build`, `test`, `check`, `format`, `run`, `clean` and
  `version`. CI runs its `doctor` and `version` checks.
- New documents: how Tazel works (one command through the code), writing a
  plugin, the lockfile and its format, and a contributing guide, with an index
  in `Documents/README.md`.
