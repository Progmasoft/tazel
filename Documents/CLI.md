<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MPL-2.0 WITH AdditionRef-Progmasoft-Exception-1.1 -->

# The `tazel` command

Tazel starts Talac, `talafmt` and `talalint` as separate programs. Their own command lines are described
with them: [Talac](https://talac.tala.run/docs/command-line), and the READMEs of [talafmt](https://github.com/Progmasoft/talafmt) and [talalint](https://github.com/Progmasoft/talalint).
Exit codes are shared: `0` success, `1` the work failed, `2` the command line itself was wrong. `tazel run`
hands through the exit code of the program it ran.

```text
tazel [output options] <command> [arguments]
```

`tazel` works on the project the current directory belongs to: the nearest directory, upward, that holds a
[project file](PROJECT_FILES.md).

| Command | What it does |
| --- | --- |
| `tazel check` | checks the sources without writing an executable |
| `tazel build` | builds the targets |
| `tazel run [-- arguments]` | builds the executables and runs them |
| `tazel test` | builds and runs the test suites |
| `tazel lock resolve` | resolves the dependencies and renews `LOCK.sqlite3` |
| `tazel lock create-dump [--path File]` | writes the lockfile as text, to `LOCK.dump` or to the file `--path` names |
| `tazel lock graph [options]` | prints the dependency graph; renews `LOCK.sqlite3` unless `--locked` |
| `tazel update` | updates the dependencies of the system and of the project |
| `tazel install [--global \| -g] Publisher.Name` | downloads a package from ViGet into the project, or into the system |
| `tazel vipkg create` | writes a local `.vipkg` for every target, without testing or publishing |
| `tazel viget push` | builds, tests and publishes the package to ViGet for the first time |
| `tazel viget update` | builds, tests and publishes a new version of the package |
| `tazel viget vipkg-desc Publisher.Name` | prints the description (`project.description`) of a package of ViGet |
| `tazel fmt [-- options]` | formats the sources with `talafmt` |
| `tazel lint [--workspace] [-- options]` | lints the sources with `talalint` |
| `tazel jvm start-daemon` | starts the JVM daemon that keeps Tazel warm between commands |
| `tazel jvm restart-daemon` | stops the daemon and starts it again |
| `tazel jvm stop-daemon` | stops the daemon |
| `tazel version` | prints the version of Tazel |
| `tazel help [command]` | prints the help, or the help of one command |

`tazel check` starts Talac with `--emit=metadata` and the compiler options of the project file. `tazel build` starts it
with `--emit=executable` and moves the result into the output directory of the build mode, under the name of the target,
with TalaRT beside it when Talac left the shared library.

## Incremental builds

`check`, `build`, `run` and `test` start Talac only when something that decides its output changed: the contents or the
names of the sources, the settings of the project file, or the compiler. Talac compiles the sources of a target as one
program, so a target is the unit: one changed source compiles the whole target, and the other targets are left alone.

```text
   Compiling Demo (Demo, 12 sources)
    Finished build in 4.21s

       Fresh Demo (Demo, 12 sources)
    Finished build in 0.31s
```

Contents count and times do not, so a file that was only touched changes nothing. Results stay in `.tazel/cache` of the
project, the last eight of each target, so going back to an earlier branch or build mode shows `Restored` and compiles
nothing. A program that was deleted or changed comes back the same way. The warnings of the run that made a result are
shown again every time it is used. A run that failed leaves no result. The steps of plugins are not skipped: they run in
every build, before the sources are read, so what they generate counts.

`--no-cache` does the work anyway and still records the result. `.tazel/cache` can be deleted at any time.

## Test suites

`tazel test` builds and runs every test suite of the selection, one after the other:

```text
   Compiling Unit (Demo, 14 sources)
     Testing Unit (Demo)
PASS Math.Adds
Tests/Unit/MathTests.tala:9:9: expectation failed: Math.Clamp(50, 0, 10) == 9
    Math.Clamp(50, 0, 10) is 10
FAIL Math.Clamps (1 failed expectation)
2 tests, 1 passed, 1 failed
error: 1 suite, 2 tests, 1 passed, 1 failed: 'Unit' failed
```

A suite is compiled by `talac --test` from what the selection compiles, the executables and the libraries, followed by the
sources of its `testDir`. Sources that lie in the `testDir` of any suite are left out of the first part, so a suite never
sees the tests of another. The runner is written to `.tazel/tests/<suite>/` and starts in the project directory, where a
test finds its files by paths relative to the project.

`tazel test` needs a Talac that has `--test`. A suite without a `.tala` source is an error, so a mistyped `testDir` cannot
pass silently. A failing test does not stop the suites after it; a suite that does not compile ends the command. The exit
code is `1` when a test failed, and also when a runner stopped before it wrote its totals, whatever its exit code was.

A suite that passed is not run again while everything it can see stays the same: its sources, the settings, the compiler
and every file below its `testDir`, data files included. It then shows `Cached` and counts in the totals. A suite that
failed always runs again. A test that reads anything else, such as a file outside the project or the network, needs
`--no-cache`; so does a run in CI that has to prove the tests pass.

## Selecting targets

`check`, `build`, `run`, `test` and `lint` take a selection. Without one they work on everything. For `test`, a selection
chooses the projects and the sources the suites are compiled with; every suite of a selected project runs.

```text
tazel build --vipkg Name          tazel build -p Name
tazel build --executable Name     tazel build -e Name
tazel build --library Name        tazel build -l Name
```

Each flag may be written several times, and the kinds may be combined; [Writing options](#writing-options) shows the
spellings. `--vipkg` keeps the named ViPkgs; once `--executable` or `--library` is written, only named targets remain. A name
that selects nothing is an error.

## Writing options

An option takes its value in any of these spellings; the short ones work the same for `-p`, `-l` and `-i`, and the long
ones for every option with a value, such as `--path`, `--invert` and `--depth`:

```text
tazel build -eFirst -eSecond                 # short, joined
tazel build -e First -e Second               # short, after a space
tazel build -e=First,Second                  # short, after =
tazel build -e:First,Second                  # short, after :
tazel build --executable First --executable Second
tazel build --executable=First,Second
tazel build --executable:First,Second
```

After `=` or `:` an option that may be written several times takes a list separated by commas; joined or after a space a
value is one name, so `-e First,Second` is refused. The value of an option that is written once is everything after the
first `=` or `:`, so `--path=C:\Locks\old.sql` keeps its colon. A value after a space may not start with `-`, and a flag
without a value, such as `--locked`, refuses one: `--locked=yes` is an error. A hint for a mistyped value is spelled the
way the option was written: `--color:awlyas` suggests `--color:always`.

## Output options

These may stand anywhere before `--`, before the command or after it:

| Option | Effect |
| --- | --- |
| `--color always\|auto\|never` | colors. `auto`, the default, uses them on a terminal that has them; `always` also in a pipe or in CI |
| `--progress always\|auto\|never` | the progress bar. `auto`, the default, draws it on an interactive terminal that can move its cursor; `always` draws it everywhere |
| `--quiet` | leaves out the status lines, and the progress bar unless `--progress` asks for it. Warnings, errors and what a command is for, such as the graph of `tazel lock graph` or the output of the program `tazel run` starts, are still printed |

```text
tazel --quiet build
tazel build --color=never --progress:never
```

## Writing the lockfile as text

`tazel lock create-dump` writes `LOCK.dump` beside the project file. `--path` names another file, of any name and
extension, relative to the directory `tazel` runs in:

```text
tazel lock create-dump --path Locks.sql
tazel lock create-dump --path reviews/Locks.sql
```

The folder of the file has to exist. Tazel does not create it: a mistyped folder fails with an error instead of leaving
a dump somewhere unexpected. A `--path` that names a folder is refused as well.

## The dependency graph

`tazel lock graph` prints what each project depends on. The graph is what the lockfile records, so `LOCK.sqlite3` is
written first when it is missing or no longer records what the project file declares. At the root of a workspace every
member is a root of the graph.

```text
Demo
├── Publisher.Json 1.1.0 [by feature Json]
├── Publisher.Name 1.0.0
├── Local.vipkg (local)
└── [test]
    └── Publisher.Name 2.0.0
```

A dependency that a feature of the project brings in (`feature = "Json"`) is shown when that feature is on, which by
default means it is one of the `defaultFeatures` of the project.

| Option | Effect |
| --- | --- |
| `--locked` | leaves `LOCK.sqlite3` as it is; a lockfile that is missing or out of date is an error |
| `--all-features` | turns every feature of the project on |
| `--duplicates`, `-d` | shows only the ViGet packages that are there in more than one version, each with its dependents |
| `--invert Package`, `-i Package` | shows the projects that depend on one package |
| `--depth Levels` | prints at most this many levels below each root; `0` prints the roots alone |

`--duplicates` and `--invert` cannot be written together. What `--invert` names depends on how it is written:

| Written | Meaning |
| --- | --- |
| `MyPackage` | every project below the current directory whose `project.name` is `MyPackage`. Its dependents are the projects there that use a ViGet package of that name, from any publisher, or a local `MyPackage.vipkg`. Folders whose name starts with a dot are not searched. |
| `Publisher.Name` | that ViGet package, in every version the projects declare |
| `Libraries/Name.vipkg` | that local package. A word that ends in `.vipkg` is always a local package. |

Tazel does not fetch packages from ViGet yet, so a ViGet package appears without the dependencies of its own.

## Output

Tazel prints a status line for each step, such as `Compiling Demo (Demo, 3 sources)`, and closes a command with the
time it took: `Finished build in 1.84s`; `--quiet` leaves them out. While `check`, `build`, `run`, `fmt` and `lint` work through their targets on an
interactive terminal, a progress bar under the status lines shows the step in hand, how many are done and the time so
far; the output of `talac`, `talafmt` and `talalint` is then printed above the bar. In a pipe, in CI or on a terminal
that cannot move its cursor no bar is drawn, and the tools write to the terminal directly; `--progress` and `--color`
choose otherwise. The program that `tazel run`
starts always writes directly. The terminal output uses Mordant.

## Mistyped words

A command, an action, a flag or a target name that is not known is refused with exit code `2` (`1` for a target name,
which is checked against the project). When it is close to a known one, a hint under the error says what was probably
meant, with a short aside:

```text
error: 'biuld' is not a command of tazel; write tazel help for the commands
 hint: did you mean tazel build? So close the keys almost had it.
```

Case is compared first, so `Build` suggests `build`; otherwise one edit is allowed for a short word and two for a
longer one, two swapped letters counting as one edit.

## Passing arguments on

Everything after `--` goes, unread, to the program the command starts:

```text
tazel run -- --port 8080           # to the program that was built
tazel fmt -- --check               # to talafmt
tazel lint -- --fix                # to talalint
tazel lint --workspace -- --fix
```

## Workspaces

At the root of a workspace, `build`, `check`, `run` and `test` work on every member. `fmt` and `lint` work on the default
members when the workspace names some and on the root otherwise; `tazel lint --workspace` lints every member. Inside the
directory of a member, that member is the project.

## Finding the tools

A tool is looked for in three places, in this order: the path named by `TAZEL_TALAC`, `TAZEL_TALAFMT` or `TAZEL_TALALINT`;
the installation `tazel` runs from; the directories of `PATH`.

## Plugins

A plugin a project declares with `plugin("Publisher") { ... }` is looked for in the plugin cache of the project,
`.tazel/plugins`. When the cache has no matching version, Tazel downloads it from ViGet into
`.tazel/plugins/<Publisher>/<Name>/<Version>/` and checks its SHA-256 against the index of the plugin; later runs use the
cache without asking ViGet. `TAZEL_VIGET_URL` replaces the address of ViGet, `https://viget.progmasoft.com`, for a mirror.
See [Writing a plugin](PLUGINS.md).

`build` and `run` run the steps that plugins add to the build of a project before its Tala targets compile; `check`
and `test` do not run them.

## Compiled project files

The first run compiles the project file; later runs load the compiled script from the cache and only evaluate it. An
entry is used only when everything the compilation depends on is the same: the script text and name, the Kotlin compiler
version, the JVM target, the default imports and compiler options, Tazel's classpath and the SHA-256 of each plugin JAR.
Otherwise the file is compiled again. An entry that cannot be read is treated as missing. The cache keeps the 256 most
recently used entries.

The cache lives in `TAZEL_CACHE_DIR` when it is set, otherwise in `%LOCALAPPDATA%\Progmasoft\Tazel\Cache` on Windows,
`~/Library/Caches/Progmasoft/Tazel` on macOS and `$XDG_CACHE_HOME/progmasoft/tazel` (`~/.cache/progmasoft/tazel`)
elsewhere. Deleting it is always safe.

## The JVM daemon

Every `tazel` starts a JVM, and the first project file it evaluates loads the Kotlin compiler and scripting host. The
daemon is a JVM that stays up and runs the commands instead, so those classes are loaded once. It does not replace the
cache of compiled project files; a command in the daemon uses the cache as well.

`tazel jvm start-daemon` starts it, unless one is already running. `tazel jvm stop-daemon` lets the commands it is
running finish, for at most a minute, and stops it; a daemon that does not answer is ended. `tazel jvm restart-daemon`
does both. These three commands always run in the process that read them.

While a daemon is running, every other command goes to it and prints what it would have printed itself. The daemon runs
the command with the arguments, working directory, environment and terminal of the client, and the programs it starts
read what is typed into the client. Each command has its own project contexts, so several clients can be served at once;
at most eight commands run at the same time and the rest wait. `TAZEL_DAEMON` says when a command goes to the daemon:

| `TAZEL_DAEMON` | A command |
| --- | --- |
| `auto` (the default) | goes to the daemon when one is running, and runs directly otherwise |
| `start` | goes to the daemon, which is started first when none is running |
| `off` | always runs directly |

A daemon serves one user and one installation. Its files are in `TAZEL_DAEMON_DIR` when it is set, otherwise in the
`daemon` directory under `%LOCALAPPDATA%\Progmasoft\Tazel` on Windows, `~/Library/Application Support/Progmasoft/Tazel` on
macOS and `$XDG_STATE_HOME/progmasoft/tazel` (`~/.local/state/progmasoft/tazel`) elsewhere: a Unix domain socket, a token,
a lock, the process id and the log. The directory is readable only by the user, and a client must present the token the
daemon wrote there. A client of a different version or protocol is refused and runs the command directly; another
installation, version or Java runtime has a daemon of its own. The lock keeps a second daemon of the same installation
from starting, and the files of a daemon that died are removed by the next client.

The daemon stops by itself after `TAZEL_DAEMON_IDLE_MINUTES` without a client, 180 by default; `0` keeps it running. Its
heap is limited to `TAZEL_DAEMON_MAX_HEAP` (`2g` by default) and its class metadata to 1 GiB; a daemon that runs out of
either exits, and the next command runs directly or starts a new one. When a client goes away in the middle of a command,
the programs the command started are stopped.

## Timings

With `TAZEL_TIMINGS=1`, Tazel prints how long each phase of the run took, on the standard error stream: JVM startup, the
Kotlin scripting host, plugin resolution and class loading, script compilation or loading it from the cache, script
evaluation, and the whole command. A command that ran in the daemon also shows IPC: the time between sending the
command and its end that the daemon did not spend on the command. JVM startup is then the startup of the client.

## Not implemented yet

These are refused by name with exit code `1`:

- library targets: the compiler cannot write a library yet. A library that sits beside an executable is skipped with a
  warning; a library selected by name is an error.
- projects with dependencies: nothing fetches a dependency or hands it to the compiler yet. `tazel lock resolve` still
  records what the project declares.
- `tazel vipkg create`, `tazel update`, `tazel install`, `tazel viget push`, `tazel viget update` and
  `tazel viget vipkg-desc`.

`check`, `build` and `run` compile for the host. The `targets` of the project file are recorded and not built.
