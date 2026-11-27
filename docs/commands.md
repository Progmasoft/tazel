<!-- SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com> -->
<!-- SPDX-License-Identifier: MIT -->

# Commands

| Command | What it does |
| --- | --- |
| `tazel build [labels]` | builds the targets of the labels; without a label, those of the package of the current directory |
| `tazel test [labels]` | builds the tests of the labels and runs them |
| `tazel run <label> [-- arguments]` | builds one executable or test and runs it |
| `tazel coverage [labels]` | runs the tests with LLVM coverage and writes `.tazel/coverage/coverage.lcov` |
| `tazel query <expression>` | answers about the targets and what they depend on |
| `tazel cquery <expression>` | the same, for the platform and mode of the command |
| `tazel aquery <expression>` | prints the actions of the targets |
| `tazel fetch` | downloads and checks the modules and plugins the module depends on, without building |
| `tazel vendor` | copies the modules of Tazel Portal Modules and their indexes into `vendor/` |
| `tazel portal-modules search\|info [-e] [--] <module\|group:module>` | searches Tazel Portal Modules, or shows one module |
| `tazel clean [--expunge]` | removes the outputs, the test logs, zig's cache and the compiled project files; `--expunge` removes all of `.tazel` |
| `tazel cache keygen [--algorithm ed25519\|rsa-4096\|ml-dsa]` | prints a key pair for signing the results of a remote cache ([Remote cache](remote-cache.md)) |
| `tazel package` | writes `.tazel/package/$group.$module-$version.tazel.tar.xz` and its `.sha256sums` |
| `tazel custom <name> [arguments]` | runs a command of the `tazel_custom.txt` of the current directory |
| `tazel version`, `tazel help [command] [--long]` | print the version, or what Tazel or one command does |

## Options

Long options take their value after whitespace, `=` or `:`: `--platform linux-aarch64`, `--platform=linux-aarch64`,
`--platform:linux-aarch64`. A list goes after `=` or `:`, separated by commas without spaces, and `\,` is a comma of a
value; a value after whitespace is one value. A list and a repeated option keep their order:
`--compiler-options=-O2,-g` is `--compiler-options -O2 --compiler-options -g`. Options may stand anywhere before `--`.
A misspelt command, option or value gets a hint that keeps how it was written: `--color:awlyas` suggests
`--color:always`.

| Option | Commands | |
| --- | --- | --- |
| `--color always\|auto\|never` | all | colors |
| `--progress always\|auto\|never` | all | the `[done/total]` counter of a build |
| `--quiet` | all | only problems and the output of what was run |
| `--message-format rich-stdio\|script-friendly-stdio\|jsonl\|xml` | all | how messages are printed; `rich-stdio` is the default |
| `--dotnet-settings Name=Value[,...]` | all | the .NET runtime of this Tazel process ([below](#the-net-runtime-of-tazel)) |
| `--help`, `-h` | all | the help of the command |
| `--mode debug\|release` | build, run, test | `debug` is the default |
| `--platform <name>` | build, run, test | the platform to build for |
| `--jobs <n>` | build, run, test | how many actions run at once |
| `--keep-going` | build, run, test | go on with what does not depend on a failed action |
| `--recompile` | build, run, test, coverage | compile every source again ([below](#recompile-and-no-cache)) |
| `--no-cache` | build, run, test, coverage | run every action and test, and read and write no cache |
| `--feature <name>`, `--no-feature <name>` | build, run, test | turn a toolchain feature on or off for every target |
| `--compiler-options <option>` | build, run, test | every C, C++, Objective-C and Objective-C++ file |
| `--c-compiler-options <option>` | build, run, test | .c and .m files |
| `--cpp-compiler-options <option>` | build, run, test | .cpp, .cc, .cxx and .mm files |
| `--linker-options <option>` | build, run, test | every link |
| `--zig-compiler-options <option>` | build, run, test | every zig command |

The options for the compilers and the linker come after the target's own settings, so the command line wins where
the compiler takes the last flag, and they are part of the key of each action.

The platform comes from `--platform`, else from `tazel_options.txt`, else it is this machine's. `--jobs` and
`--keep-going` win over `Execution` of `MODULE.csx`.

## Recompile and no-cache

| | default | `--recompile` | `--no-cache` |
| --- | --- | --- | --- |
| compiling (C family), compiling and linking (zig) | when something changed | always | always |
| linking, archiving | when something changed | when an object came out different | always |
| plugin and custom build steps | when something changed | when something changed | always |
| local and remote action cache reads | as configured | none for compilations | none |
| action cache writes | as configured | as configured | none |
| test results | as configured | as configured | not read, not written |

Neither flag deletes a cache, changes `--jobs` or changes a key: a record written before is kept, and the next build
checks it against the files, as it checks every record. With both, `--no-cache` holds. A test that fails under
`--no-cache` loses the pass recorded before it. The tag `no-cache` of a test keeps its meaning. Fetched modules are
fetched as always.

zig keeps what it compiled in a cache of its own: a forced zig action runs with an empty one in `.tazel/forced/`,
removed after, so it does compile; zig's global cache, of its standard library and of libc, is kept. A compiler that
caches by itself, such as ccache behind `cc`, is not bypassed.

## Message formats

`--message-format` chooses how Tazel prints its messages. `--color` and `--progress` change `rich-stdio` alone;
`--quiet` leaves out the status lines (`status` and `success`) in every format. What a command prints as its result,
such as the labels of `tazel query`, is an `output` message.

- `rich-stdio`: colours, tables and pictures of problems, drawn with Spectre.Console for a terminal.
- `script-friendly-stdio`: plain lines for scripts and logs. A message is one line that starts with a fixed word,
  and the lines it carries (a compiler's output, a test log, and the `command:`, `help:` and `here:` lines of the
  table below) follow it indented by two spaces. No colours, boxes or
  emoji, and nothing is wrapped. An `output` message is printed as it is; a table is tab-separated, its header first.

  | Message | Line |
  | --- | --- |
  | `status` | `INFO <text>` |
  | `action` | `DONE <description>` |
  | `action-failed` | `FAILED <description>`, then `command: <command line>` |
  | `error` | `error[<code>]: <message>`, then `help: <help>` |
  | `diagnostic` | `error[<code>]: <file>:<line>:<column>: <message>`, then `here: <label>` and `help: <help>` |
  | `warning` | `warning: <message>` |
  | `success` | `SUCCESS <message>` |
  | `test` | `PASSED`, `CACHED`, `FAILED`, `TIMED-OUT`, `FLAKY` or `NOT-BUILT`, then `<label> <seconds> s` |
  | `test-summary` | `TESTS <passed> passed, <failed> failed, <total> in all` |
  | `test-log` | `LOG <label>` |
  | `explanation` | `<code> <title> (<file>)` |

- `jsonl`: one JSON object per message per line, its kind first: `{"kind":"error","code":"E0500","message":"..."}`.
  A field without a value is left out; a command line is a list of strings, a table has `columns` and `rows`.
- `xml`: one `<tazel>` document, written as the messages come and closed when the command ends. A message is an
  element named by its kind with an element per field; the items of a list are `<item>`, the rows of a table
  `<row>` of `<cell>`. A control character that XML cannot hold becomes U+FFFD.

The fields of each kind:

| Kind | Fields |
| --- | --- |
| `status` | `text` |
| `action` | `done`, `total`, `description`, `output` |
| `action-failed` | `description`, `command`, `output` |
| `error` | `code`, `message`, `help`, `joke` |
| `diagnostic` | `code`, `message`, `file`, `line`, `column`, `width`, `label`, `source`, `help`, `joke` |
| `warning`, `success` | `message` |
| `test` | `label`, `status` (`passed`, `cached`, `failed`, `timed-out`, `flaky`, `not-built`), `seconds` |
| `test-summary` | `passed`, `failed`, `total` |
| `test-log` | `label`, `log` |
| `output` | `text` |
| `table` | `columns`, `rows` |
| `explanation` | `code`, `title`, `where`, `text`, `wrong`, `right`, `related` |

## Builds, tests and runs

Each action is a command started directly, without a shell, in the root of the module, with a small environment:
`PATH`, the temporary directory, `HOME`, the SDK variables of macOS and Windows, and `LC_ALL=C`. Outputs go to
`.tazel/out/<platform>-<mode>/`. An action keeps a record beside its first output; it runs again only when its
command line, its environment, an input or an output changed by content. The headers a compilation read come from
its depfile.

A test runs in its runfiles directory, `<program>.runfiles`, where its `RuntimeData` is at its path in the module. Its
environment is `PATH`, its `InheritedEnvironmentVariables`, `TEST_TMPDIR`, `TEST_SRCDIR`, `TEST_TARGET`, the shard
variables and its `EnvironmentVariables`. Its log is `.tazel/testlogs/<package>/<name>/test.log`. A test tagged
`exclusive` runs alone.

A test that passed is not run again while what it runs with is the same: its program and the shared libraries
beside it by content, its data, arguments and environment, its shards, timeout and retries, and the platform and mode.
It shows as `PASSED (cached)` with the log of its last run. A test tagged `no-cache` runs every time, a flaky pass is
not remembered, and `tazel coverage` runs every test.

`tazel run` runs the program in its runfiles directory with Tazel's environment and the program's
`EnvironmentVariables`, its `Arguments` and what follows `--`, and returns its exit status.

## tazel_options.txt

```text
# comment
<# comment #>
sandboxing-with-microvm: false;
microvm-network: false;
microvm-memory: 2048;     # MiB, or "auto"
microvm-cpus: "auto";
platform: linux-amd64;
dotnet-settings: [ "GCHeapHardLimit=512M" 'GCSettings.LatencyMode=Interactive' ];
```

Configuration comes in layers, lowest first: the user's (`$TAZEL_HOME/User/`), every directory above the current one,
the farthest first, this machine's settings of the project (`$TAZEL_HOME/Projects/<key>/`, outside the module), and the
current directory; the command line is above them all. Each option comes from the highest layer that sets it, an
explicit `false` or `auto` as much as any value; `dotnet-settings` merge name by name; a sandbox asked for in any layer
stays asked for. The project key is the module root's canonical path made readable (`-home-ada-src--app`) with the
first 16 hexadecimal digits of its SHA-256 after it, so every directory of a module shares one, and `identity.txt` in
its directory holds that root; a directory there of another root is refused.

`tazel_custom.txt` takes the same layers, a command of a higher layer replacing the one of the same name.
`tazel_envs.txt` too, for the main module only, around its root; a module it depends on takes its own file alone.

```text
tazel options validate [--user | --project | --local]
tazel options get <setting> [--user | --project | --local]
tazel options what-is <setting>
tazel options update <setting> <value> [--user | --project] [--create]
tazel custom-cmds validate [--user | --project | --local]
tazel envs validate [--user | --project | --local]
```

`validate` reads each layer, or the one a flag names, and runs nothing. `get` prints the value that holds and the file
it came from. `update` replaces the value where it stands, or adds the entry, and keeps the rest of the file,
comments too; it writes beside the file and moves it into place under a lock, needs `--create` for a file that is not
there, and leaves an option set inside `.if` or in an included file for you to edit. This Tazel has no MicroVM to start, so with `sandboxing-with-microvm: true` it refuses every
command that would run what a project brings (`build`, `run`, `test`, `package`, `custom`) rather than run it on this
machine.

## The .NET runtime of Tazel

`--dotnet-settings` and `dotnet-settings: [ ... ];` of `tazel_options.txt` set the .NET runtime of the Tazel process
that runs the command, and nothing else: not what it builds, tests or starts, and not `--jobs`. A setting not given
is left to .NET; nothing is kept for a later command.

```text
tazel build //... --dotnet-settings=GCHeapHardLimit=512M,GCSettings.LatencyMode=Interactive
tazel build //... --dotnet-settings GCHeapHardLimit=512M --dotnet-settings:ThreadPool.MinWorkerThreads=8
tazel dotnet get GCHeapHardLimit
tazel dotnet what-is CLI
```

| Setting | Value |
| --- | --- |
| `GCHeapHardLimit`, `GCHeapHardLimitSOH`, `GCHeapHardLimitLOH`, `GCHeapHardLimitPOH` | bytes, with `B`, `K`, `M`, `G` or `T` (KiB and up) |
| `GCHeapHardLimitPercent`, `GCHeapHardLimitSOHPercent`, `GCHeapHardLimitLOHPercent`, `GCHeapHardLimitPOHPercent` | a whole percentage |
| `GCSettings.LatencyMode` | `Batch`, `Interactive`, `LowLatency` or `SustainedLowLatency` |
| `ThreadPool.MinWorkerThreads`, `ThreadPool.MaxWorkerThreads`, `ThreadPool.MinCompletionPortThreads`, `ThreadPool.MaxCompletionPortThreads` | a whole number |

- An entry is split at its first `=`. The option takes `=`, `:` or a space before its value, a comma-separated list
  in each form, and repeats; `\,` is a comma of a value. The file's settings come first and the command line's after
  them, and the last of a name wins.
- The whole set is checked before any of it is applied. Two ways of saying one limit are refused rather than one of
  them ignored: `GCHeapHardLimit` with `GCHeapHardLimitPercent`, a limit of the whole heap with limits per object heap,
  or limits per object heap in bytes and in percent. Limits per object heap need SOH and LOH (POH may be left out);
  their percentages are each below 100, and so is their sum. A minimum of threads is at most their maximum.
- Heap limits go to `AppContext.SetData`, then `GC.RefreshMemoryLimit()` once; a limit below what the process has
  committed is refused by .NET, and that is `error[E0506]`, as is a latency mode or thread count .NET does not take.
- `tazel dotnet get <setting>` prints what the process has: the GC's own report, or `default` where it reports none,
  and for heap limits the memory available to the GC. `tazel dotnet what-is <term>` explains GCHeapHardLimit, RyuJIT,
  .NET, CLI (the Common Language Infrastructure), CIL, CLR, C++/CLI, C#, WorkstationGC, BackgroundGC, daemon, CTS,
  VES and ECMA-335, offline. Tazel has no daemon: `tazel dotnet start-daemon` and the like say so.

## .tazelignore

```gitignore
pragma:once
import:<common.ignore.txt>
include:"config/build.ignore.txt"
*.tmp
**/generated/
!important.log
regex:^src/.*\.generated\.cpp$
!regex:^src/required\.generated\.cpp$
```

A `.tazelignore` holds gitignore rules: comments, `*`, `**`, `?`, `[...]`, anchoring with `/`, a trailing `/` for
directories, `\` escapes, `!` negation, trailing spaces dropped unless escaped, the last matching rule deciding, and
nothing inside an ignored directory brought back. A file applies to its directory and below, after the files above
it. `regex:` and `!regex:` take a .NET regular expression over the path relative to the file's directory, with `/`
between its parts; it must compile and finish within a second on a path. `pragma:once`, `import:` and `include:` read
other files as the directives of the text files do, `<file>` below `$TAZEL_HOME/Include/Ignore/`; there are no
conditions. A gitignore rule that starts with one of these words takes a backslash: `\regex:name`. A `.gitignore` is
not read.

The rules apply to package discovery, `//...` and the other patterns, source globs and module archives. A path or a
label named explicitly is never ignored: a package that `Depends` or the command line names is loaded from its own
directory, without searching ignored ones, and patterns still leave it out. Hidden directories, `.tazel` among them,
are left out before any rule.

```text
tazel ignore create-from .gitignore     # ./.tazelignore from a gitignore file, never over one
tazel ignore validate                   # every .tazelignore of the module, ignored directories too
tazel ignore add '*.log'                # appended to ./.tazelignore unless it is there already
tazel ignore del '*.log'                # every line that is the pattern as written; delete is the same
```

## External tools

```text
tazel tool launch <name> [arguments]
TAZEL_TOOL_EXECUTABLE=bin/formatter tazel tool launch formatter --check src/
tazel tool create-wrapper <name> [-o <dir> | --output-dir <dir>]
```

`tazel tool launch` runs the program `TAZEL_TOOL_EXECUTABLE` names when it is set, absolute or from the current
directory, and nothing else then: an empty value, a missing file or one this user may not run is an error. Otherwise
it runs the first `tazel-<name>` of `PATH` this user may run (on Unix by `access(X_OK)`, on Windows by `PATHEXT`),
skipping empty entries, `.`, relative entries and the current directory, and searching nowhere else. Every argument
after the name reaches the tool as written, empty ones, `--` and `--help` too. The tool runs as a process of its own,
in this directory, with this environment and terminal, and its exit status is Tazel's; with
`sandboxing-with-microvm` it does not run. On Windows a batch file runs through `cmd.exe` with each argument quoted;
an argument holding `"`, `%` or a line break is refused, since `cmd.exe` would change it.

`tazel tool create-wrapper` writes `tazel-<name>` (`tazel-<name>.bat` on Windows) into an existing directory, the
current one by default. It runs `<name>` with its arguments, `exec <name> "$@"` or `<name> %*`; Tazel never overwrites
a file, runs the wrapper or changes `PATH`. A name is a letter or digit, then letters, digits, `.`, `_` and `-`.

## Directives of the text files

`tazel_options.txt`, `tazel_custom.txt` and `tazel_envs.txt` share one grammar: besides their entries they take
directives, lines that start with a dot and need no `;`.

```text
.pragma once
.import <security.options.txt>
.include "config/build.options.txt"
platform: linux-amd64;
.if target_system.os == "linux" && !defined("CI")
    microvm-memory: 4096;
.elif host_system.os == "macos"
    microvm-memory: 2048;
.else
    microvm-memory: auto;
.end
```

- `.include "file"` reads a file in this place, as if its text stood here; the path is relative to the directory of the
  file that names it. `.include <file>` reads one below `$TAZEL_HOME/Include/Options/` (`Custom/`, `Envs/` for the
  other two files), and may not leave it, through `..` or a link. `TAZEL_HOME` is `$HOME/.Tazel`, or
  `%APPDATA%\Tazel` on Windows, when it is not set; set, it must be an absolute path.
- `.import` does the same, once: a file already read is not read again, and an imported file is not included again
  either. `.pragma once`, first in a file, says the same of the file itself. A file that includes itself otherwise is
  an error that shows the chain.
- Paths are taken as written: no wildcards, no URLs, no environment variables; a quoted path is not absolute. The
  name of a file does not matter; `.options.txt`, `.custom.txt` and `.envs.txt` are the custom.
- `.if`, `.elif`, `.else` and `.end` choose the first branch whose condition holds. A branch not taken is read, so its
  mistakes are found, but its includes are not opened. A condition takes `true`, `false`, strings in any of the three
  quotes, `==`, `!=`, `!`, `&&`, `||` and parentheses, and:

  | Name | Value |
  | --- | --- |
  | `host_system.os`, `target_system.os` | `linux`, `windows`, `macos`, `freebsd`, `openbsd`, `dragonfly` |
  | `host_system.arch`, `target_system.arch` | `amd64` or `aarch64`, on every system |
  | `host_system.abi`, `target_system.abi` | `gnu`, `musl`, `msvc` or `default` |
  | `host_system.platform`, `target_system.platform` | the platform's name, as `--platform` takes it |
  | `defined("NAME")` | whether the environment variable is set |
  | `env("NAME")` | its value, `""` when it is not set |

  A condition runs nothing; the environment is read once for the whole command.
- `tazel_options.txt` is read twice. First, without the branches that depend on `target_system`, which choose the
  platform: `--platform`, else the file's `platform`, else this machine's. Then everything is read for that platform.
  A `platform` inside a condition on `target_system`, or in a file such a branch includes, is an error.
- Includes nest 64 deep, `.if` blocks 64 deep, and one configuration reads at most 4,096 files and 1,000,000 tokens.
  The last entry of each file may leave out its `;`. An error in an included file is shown in that file.

## tazel_custom.txt

```text
build-no-cache: ("tazel" 'build' `--no-cache`);
fetch:
    ("cargo" "fetch")
    ("tazel" "fetch");
npm-dev-sh: s('npm dev');
build with: ("tazel" "build" <1> <2>);
```

A command is a name of one or more words, a colon, and steps that run in order until one fails. `("program" "arg")`
starts a program directly; `s("line")` gives one line to the shell (CMD on Windows, `sh` elsewhere). Strings take
double quotes, single quotes or backticks and have no escapes. `<N>` is the Nth argument after the name, `<*>` all of
them. The longest name that fits the command line wins, and `--` ends the name. Each command ends with `;`, which the
last one in the file may leave out, as may the last option of `tazel_options.txt` and the last value of
`tazel_envs.txt`.

## Queries

`tazel query` reads the project files alone: target patterns, `deps(x)`, `deps(x, depth)`, `rdeps(u, x)`,
`rdeps(u, x, depth)`, `kind(k, x)` (the kind is `CC.StaticLibrary`, `Zig.Test`, and so on), `tests(x)`, and `+`, `-`,
`^` or `union`, `except`, `intersect`, with parentheses. An operator stands apart, with spaces around it, as a label
may hold `-` and `+`. `--output label_kind` prints the kind before each label.

```text
tazel query 'deps(//app:greeter)'
tazel query 'rdeps(//..., //core:core)' --output label_kind
tazel cquery '//...' --platform linux-aarch64
tazel aquery //core:core
```

`tazel cquery` leaves out what does not build for the platform of the command and prints each target with its
configuration, such as `//:app (linux-amd64-debug)`. `tazel aquery` prints each action of the targets: what it runs,
reads and writes; nothing runs.

## Coverage

`tazel coverage` builds the tests and what they use with `-fprofile-instr-generate -fcoverage-mapping` into
`.tazel/out/<platform>-<mode>-coverage/`, runs them with `LLVM_PROFILE_FILE` in their log directory, merges the
profiles with llvm-profdata and writes `.tazel/coverage/coverage.lcov` with llvm-cov, with a summary. It needs clang;
llvm-profdata and llvm-cov are taken beside clang, else from PATH. Zig tests run without coverage.

## Tazel Portal Modules

A dependency `Module.From(new() { Group, Name, Version, Alias })` comes from Tazel Portal Modules, at
`https://modules.tazel.net` or the address in `TAZEL_PORTAL_MODULES_URL`. Each module is taken at the highest version
that anything asks for, from the indexes `$group/$module/index.json`; a yanked version takes part in no resolution.
Each archive is checked against its `.sha256sums` and unpacked once below `.tazel/external`. `tazel vendor` copies
the archives and the indexes into `vendor/`, and a build reads `vendor/` first, so a module with it builds offline.

```text
tazel portal-modules search json
tazel portal-modules search com.example:json -e
tazel portal-modules info json
tazel portal-modules info --exact -- com.example:json
```

`search` lists the modules whose `group:module` holds the query, from `index.json` at the top of the portal; with
`-e`/`--exact` only that one, and the group is required. `info` prints the versions of one module, yanked ones marked,
with their push dates and dependencies; without `-e` the group may be left out unless the name is a module of more
than one group. `--` ends the options, so a query may start with a dash.

## tazel_envs.txt

```text
# comment
<# comment #>

example-version: '0.1.0';
example-output: ("rustc" "--help");
shell-expansion: s(`$HOME`);
```

`tazel_envs.txt` sits beside the `MODULE.csx` of a module and names values that its `MODULE.csx` and `BUILD.csx`
files read:

```cs
using Progmasoft.Tazel.Dsl.Environment;

var exampleVersion = GetEnvironment.From("example-version"); // "0.1.0"; null for a name the file does not have
```

- It is read with the lexer of `tazel_custom.txt`: the same comments and strings; every value ends with `;`, which
  the last one in the file may leave out.
- A string is the value as written. `("program" "argument")` starts a program directly, each string one argument;
  `s("line")` gives one line to `/bin/sh` on Linux, macOS and the BSDs and to `cmd.exe` on Windows. Their value is
  what they print, less the line endings at its end. A program runs when its value is first asked for, once per
  command, in the root of the module with Tazel's environment; one that fails stops the project file that asked.
- A value is not an environment variable: it reaches no shell, no program Tazel runs and not Tazel itself; only
  `GetEnvironment.From` reads it.

## Errors

Every error has a code, `E` and four digits, written as `error[E0406]:`. An error in `tazel_envs.txt`,
`tazel_custom.txt` or `tazel_options.txt` shows where it is, the line, a mark under the problem, how to fix it, where
to read more, and a light line:

```text
✖ error[E0406]: expected ';' after the value of example-version
  ╭─[ tazel_envs.txt:1:25 ]
  │
1 │ example-version: '0.1.0'
  │                         ┬
  │                         ╰── a ';' goes here
  │
  ├─ 💡 help: end every value with ';': example-version: '0.1.0';
  ├─ 📖 more: tazel explain E0406
  ╰─ 🎭 Semicolons are small, but they hold this whole file together.
```

A terminal without Unicode gets the same picture in ASCII. Other errors say what went wrong, how to fix it where
there is a way, where to read more, and a light line too; the same problem always gets the same line.

`tazel explain E0406` prints what a code means with a fixed example; `tazel explain E0406 --long` says why and shows
the wrong text and the right one side by side. The first two digits say where an error comes from:

| Codes | From |
| --- | --- |
| `E00xx` | the text files: their comments, strings and placeholders |
| `E01xx` | the command line |
| `E02xx` | `tazel_options.txt` |
| `E03xx` | `tazel_custom.txt` |
| `E04xx` | `tazel_envs.txt` |
| `E05xx` | `MODULE.csx`, `BUILD.csx` and builds |
| `E06xx` | labels |

A code, once given, keeps its meaning.
