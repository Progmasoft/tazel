<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0 -->

# The lockfile

`LOCK.sqlite3` lies beside the project file. It is an SQLite database that records what the project declares: its
dependencies, with their features, and the plugins that evaluated the project file, with the digest of each JAR. It
records declarations, not a solved dependency graph; Tazel cannot fetch packages from ViGet yet, so there is nothing to
solve.

Commit `LOCK.sqlite3` with the project file. Its text form, `LOCK.dump`, is for reading and reviewing a change.

## When it is written

| Command | What happens to `LOCK.sqlite3` |
| --- | --- |
| `tazel lock resolve` | written for the project and for every member of its workspace |
| `tazel check`, `build`, `run` | written before a project is compiled, when it no longer records what the project declares |
| `tazel lock graph` | written when it is missing or out of date; with `--locked` an out-of-date file is an error instead |
| `tazel lock create-dump` | read only |

"Out of date" is decided by content: Tazel reads the file and compares what it records with what the project file
declares now. A file of an older format, a damaged file and a missing one are all out of date.

A new lockfile is written to a temporary file in the same directory and then moved over the old one, atomically where
the file system allows it, so an interrupted write never leaves half a lockfile behind.

## Format 4

The `metadata` table holds the format: `format_version` is `4`. A lockfile of another format is not read; Tazel asks
for `tazel lock resolve`, which writes the current one.

| Table | Key | Columns |
| --- | --- | --- |
| `metadata` | `key` | `value`; holds `format_version` |
| `packages` | `name`, `scope` | `stability`, `version`, `feature` (the feature of this project that brings the package in, or `NULL`), `default_features` (`0` or `1`) |
| `package_features` | `package_name`, `scope`, `feature` | `enabled` (`0` or `1`): the features of the dependency itself that the project turns on or off |
| `local_packages` | `path`, `scope` | the `.vipkg` files the project names, relative to the project file |
| `plugins` | `publisher`, `name` | `version`, `api_version`, `sha256` (64 hex digits) |

`name` in `packages` is the coordinate of the package, `Publisher.Name`. `scope` is `BUILD` for `dependency` and
`localDependency`, `TEST` for `testDependency` and `localTestDependency`. Every table is `WITHOUT ROWID`: a row is identified by
its key alone, so one package cannot be recorded twice in one scope.

For this project file:

```kotlin
dependencies {
  dependency("Publisher") {
    name = "Toml"
    version = "1.0.0"
    feature = "TOML"
    defaultFeatures = false
    feature("Serde") { enabled = true }
  }
  localDependency { path = "Libraries/Shared.vipkg" }
}
```

the lockfile holds:

| Table | Row |
| --- | --- |
| `packages` | `Publisher.Toml`, `BUILD`, `STABLE`, `1.0.0`, `TOML`, `0` |
| `package_features` | `Publisher.Toml`, `BUILD`, `Serde`, `1` |
| `local_packages` | `Libraries/Shared.vipkg`, `BUILD` |

## The text dump

```text
tazel lock create-dump
tazel lock create-dump --path reviews/Locks.sql
```

`LOCK.dump` is SQL that SQLite can replay: a header comment, `PRAGMA foreign_keys=OFF;`, then one transaction with the
`CREATE TABLE` statement of every table followed by its rows. Tables come in the order of their names and rows in the
order of their columns, so the same lockfile always gives the same bytes and a diff shows only what changed.

To turn a dump back into a database:

```text
sqlite3 LOCK.sqlite3 < LOCK.dump
```

The dump is never written over the binary lockfile, and its folder has to exist; see
[the command](CLI.md#writing-the-lockfile-as-text).

## The dependency graph

`tazel lock graph` draws what the lockfiles record, so it renews them first. It shows each project of the workspace as
a root, its ViGet and local packages below it, and the test dependencies under `[test]`. A package that a feature
brings in is shown only while that feature is on. The options are described with [the command](CLI.md#the-dependency-graph).
