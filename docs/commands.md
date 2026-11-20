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
| `tazel clean [--expunge]` | removes the outputs, the test logs and zig's cache; `--expunge` removes all of `.tazel` |
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
| `--help`, `-h` | all | the help of the command |
| `--mode debug\|release` | build, run, test | `debug` is the default |
| `--platform <name>` | build, run, test | the platform to build for |
| `--jobs <n>` | build, run, test | how many actions run at once |
| `--keep-going` | build, run, test | go on with what does not depend on a failed action |
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
```

The nearest `tazel_options.txt` at or above the current directory holds, except that a sandbox asked for by any file
above stays asked for. This Tazel has no MicroVM to start, so with `sandboxing-with-microvm: true` it refuses every
command that would run what a project brings (`build`, `run`, `test`, `package`, `custom`) rather than run it on this
machine.

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
them. The longest name that fits the command line wins, and `--` ends the name.

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

example-version: '0.1.0'
example-output: ("rustc" "--help")
shell-expansion: s(`$HOME`)
```

`tazel_envs.txt` sits beside the `MODULE.csx` of a module and names values that its `MODULE.csx` and `BUILD.csx`
files read:

```cs
using Progmasoft.Tazel.Dsl.Environment;

var exampleVersion = GetEnvironment.From("example-version"); // "0.1.0"; null for a name the file does not have
```

- It is read with the lexer of `tazel_custom.txt`: the same comments and strings; the entries follow one another
  with no `;` between them.
- A string is the value as written. `("program" "argument")` starts a program directly, each string one argument;
  `s("line")` gives one line to `/bin/sh` on Linux, macOS and the BSDs and to `cmd.exe` on Windows. Their value is
  what they print, less the line endings at its end. A program runs when its value is first asked for, once per
  command, in the root of the module with Tazel's environment; one that fails stops the project file that asked.
- A value is not an environment variable: it reaches no shell, no program Tazel runs and not Tazel itself; only
  `GetEnvironment.From` reads it.

