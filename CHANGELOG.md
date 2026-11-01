<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-2.0 -->

# Changelog

## Unreleased

- `OptLevel.OS` and `OptLevel.OZ` optimize for size; they need Tala 0.2.0.
- A command is a graph of units: a step of a plugin, the compilation of a target, a test suite.
  Each unit waits only for what it depends on, so the projects of a workspace run their plugin
  steps side by side, and with `--keep-going` a step that fails stops only its own project.
- `tazel custom` names may hold several words, and `<N>` and `<*>` pass arguments of the
  command line to a program: `build with: ("tazel" "build" <1> <2>);`.

## 0.4.0 - 2026-10-31

Two changes need an edit in existing project files:

- `project.name` is always required. A project file without it, such as the root
  of a workspace with no `project` block, has to add it.
- `compiler.version` is gone from project files. Loading a project no longer starts
  talac to ask for its version.

Everything else adds to what was there:

- Compiler runs and test suites run in parallel, one job per processor unless
  `--jobs`, `--cpus`, `--memory` or the `execution` block says otherwise.
- `--keep-going`, `--profile`, and `--no-incremental` for debug builds, which now
  keep an object per source when talac can.
- `--remote-executor` and `execution.remote` compile on another machine, and
  `tazel remote serve` is the server.
- `tazel jvm start-daemon --max-heap` limits the heap of the daemon.
- `tazel custom` runs the commands of `tazel_custom.txt`.
- Project files move to the Progmasoft Linking Exception, Version 2.0. Earlier
  releases can rely on Version 2.0 too.
- `tazel fetch` fetches the dependencies of a project ahead of time.
- `tazel cache status`, `clean`, `push` and `pull` look after the build caches.
- `tazel cache serve` is a server for the remote build cache, and `tazel cache
  keygen` makes the keys that sign its results. A project with
  `buildCache.remote.publicKey` uses only results signed for that key, that
  target and that cache key.
- A project file that reads a variable with `System.getenv` gets the value of the
  command under the daemon too: when it differs, the command runs without the
  daemon and the next one starts a new daemon.
- A compiler run says why it runs: `Sources/Math.tala changed`, `the settings
  changed`, `the compiler changed`. The last line of a command counts what the
  cache saved, and a test suite that failed last time runs first.
- `buildCache { local { enabled, maxEntries } remote { url, read, write, token } }`
  chooses where results are kept. The local cache is asked first; a remote cache
  shares results between machines over `GET` and `PUT`, needs `https` except on
  this machine, and is a warning rather than a failure when it does not answer.
- Build cache keys follow the contents of the compiler and include the host, so
  they are the same on every machine with that compiler.
- `compiler { staticTalaRTLibrary = true }` links TalaRT into the output and
  `false` keeps it beside the output; `null`, the default, writes no option and
  leaves the choice to Talac.
- `tazel test` builds and runs the test suites. Each suite is compiled with
  `talac --test` from the sources of the selection and the sources of the suite,
  and its runner starts in the project directory. A failing test does not stop
  the suites after it; the command ends with the totals and exit code `1`. This
  needs a Talac that has `--test`, the release after 0.1.0.
- `check`, `build`, `run` and `test` skip a compiler run when the sources, the
  settings and the compiler are those of an earlier run, and bring its files back
  from `.tazel/cache`, so going back to an earlier branch or build mode compiles
  nothing. Warnings of the earlier run are shown again.
- A test suite that passed is not run again until its sources, the settings, the
  compiler or a file of its test directory change.
- `--no-cache` on `check`, `build`, `run` and `test` does the work anyway.
- TalaRT moves into the output directory with the program, so a program built
  with the shared TalaRT starts from there.

## 0.3.0 - 2026-10-29

- Options take their value after a space, `=` or `:`, and short options also
  joined: `-eFirst`, `-e First`, `-e=First`, `-e:First`, `--executable:First`.
  After `=` or `:` an option that may be repeated takes a list:
  `-e=First,Second`. A hint for a mistyped value keeps the spelling it was
  written with.
- `--color always|auto|never`, `--progress always|auto|never` and `--quiet`
  choose how Tazel writes; they may stand anywhere before `--`.
- `tazel test` explains that Talac does not compile `[Fact]` tests yet, instead
  of saying that Tala has no test framework.

## 0.2.0 - 2026-10-29

- Compiled project files are cached per user; a later run only evaluates them.
- Every project evaluation has its own context, so project files can be
  evaluated on several threads at once.
- `tazel jvm start-daemon`, `restart-daemon` and `stop-daemon` manage a JVM
  daemon that keeps Tazel warm; `TAZEL_DAEMON` chooses when a command goes to
  it.
- `TAZEL_TIMINGS=1` prints how long each phase of a run took.
- Plugin API 2, published as `com.progmasoft.tazel:tazel-api` from the new module
  `api/`. A plugin can add build steps through `PluginExtensions`; `tazel build`
  and `tazel run` run them before the Tala targets compile, with a
  `BuildContext` that runs programs or captures what they print, finds them on
  `PATH`, tells the project and stops the build. A
  project built only by its plugins needs no Tala target. `onFinalize` now
  receives a `ProjectSummary` instead of the internal plan, and the context
  tells the host. `ProjectPlugin` no longer states the publisher, name and
  version of the plugin: they come from its descriptor alone, and the service
  entry of the JAR names the class Tazel creates. Plugins built for API 1 are
  not loaded.
- A ViGet plugin missing from `.tazel/plugins` is downloaded from
  `viget.progmasoft.com/dslplugins/tazel` and checked against the SHA-256 its
  index lists; `TAZEL_VIGET_URL` names a mirror.
- The descriptor of a plugin can describe it for ViGet: `description`,
  `author.N`, `license`, `homepage` and `repository`.
- The Gradle plugin `com.progmasoft.tazel.plugin`, from the new module
  `gradle-plugin/`, builds plugins: it adds the repository of the API,
  `tazelApi()` and the `tazelPlugin { }` block, from which it writes the
  descriptor and the service entry.
- The Maven group of Tazel is `com.progmasoft.tazel`.

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
