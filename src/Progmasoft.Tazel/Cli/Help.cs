// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;

namespace Progmasoft.Tazel.Cli;

/// <summary>What <c>tazel help</c> prints: the general help or one command's, short or with <c>--long</c>.</summary>
internal static class Help
{
    private const string BuildOptions = """
        Options of build, run, test, coverage, cquery and aquery:
          --mode debug|release          debug (the default) or optimized
          --platform <name>             the platform to build for; default: tazel_options.txt, else this machine's
          --jobs <n>                    how many actions run at once; default: MODULE.csx, else the processors
          --keep-going                  go on with what does not depend on a failed action
          --feature <name>              turn a toolchain feature on for every target
          --no-feature <name>           turn a toolchain feature off for every target
          --compiler-options <option>   for every C, C++, Objective-C and Objective-C++ file
          --c-compiler-options <option> for .c and .m files
          --cpp-compiler-options <opt>  for .cpp, .cc, .cxx and .mm files
          --linker-options <option>     for every link
          --zig-compiler-options <opt>  for every zig command
        A list of values goes after = or : without spaces (--feature=asan,lto); \, is a comma of a value.
        """;

    private const string Labels = """
        Labels: //dir:name, //:name, :name (in the current package), @dependency//dir:name, //dir/... (a package and
        those below it), //... (the whole module). Without a label, the package of the current directory.
        """;

    private sealed record Topic(string Short, string Long);

    private static readonly Dictionary<string, Topic> Topics = new(System.StringComparer.Ordinal)
    {
        ["build"] = new("tazel build [labels] [options]: builds the targets of the labels.", $"""
            tazel build [labels] [options]

            Builds the targets of the labels. //... leaves out targets tagged manual and those that do not build for
            the platform, and says how many it left out. Each action runs again only when its command, an input or an
            output changed by content; outputs go to .tazel/out/<platform>-<mode>/.

            {Labels}

            {BuildOptions}

            Examples:
              tazel build //...
              tazel build //app:greeter --mode release
              tazel build //... --platform linux-aarch64 --feature=lto
            """),
        ["test"] = new("tazel test [labels] [options]: builds the tests of the labels and runs them.", $"""
            tazel test [labels] [options]

            Builds the tests of the labels and runs each in its runfiles directory, with its own TEST_TMPDIR, the
            shard variables, and a timeout from its TestSize (1, 5, 15, 60 minutes) or its Timeout; a flaky test runs
            up to three times, and a test tagged exclusive runs alone. A test that passed is not run again while its
            program, data, arguments and environment are the same; it shows as PASSED (cached), and a test tagged
            no-cache runs every time. Logs go to .tazel/testlogs/; the output of a test that failed is printed, then a
            table of every test.

            {Labels}

            {BuildOptions}
            """),
        ["coverage"] = new("tazel coverage [labels] [options]: runs the tests with coverage and writes its report.", $"""
            tazel coverage [labels] [options]

            Builds the tests of the labels and what they use with LLVM source-based coverage (clang:
            -fprofile-instr-generate -fcoverage-mapping), runs them as tazel test does, merges their profiles with
            llvm-profdata, and writes .tazel/coverage/coverage.lcov with llvm-cov, with a summary. The outputs go to
            .tazel/out/<platform>-<mode>-coverage/, apart from those of tazel build. Zig tests run without coverage.

            {Labels}

            {BuildOptions}
            """),
        ["run"] = new("tazel run <label> [options] [-- arguments]: builds one program and runs it.", $"""
            tazel run <label> [options] [-- arguments]

            Builds one executable or test and runs it in its runfiles directory, where its RuntimeData is, with
            Tazel's environment and the program's EnvironmentVariables. Its Arguments and what follows -- are passed
            to it, and its exit status is tazel's.

            {BuildOptions}
            """),
        ["query"] = new("tazel query <expression>: answers about the targets and what they depend on.", """
            tazel query <expression> [--output label|label_kind]

            Answers about the target graph from the project files alone.
              //dir:name, //dir/..., //...    targets and target patterns
              deps(x), deps(x, depth)          x and what it depends on
              rdeps(u, x), rdeps(u, x, depth)  what in u depends on x
              kind(k, x)                       the targets of x whose kind (CC.StaticLibrary, Zig.Test...) holds k
              tests(x)                         the tests of x
              x + y, x - y, x ^ y              union, difference, intersection; also union, except, intersect
            An operator stands apart, with spaces around it.

            Examples:
              tazel query 'deps(//app:greeter)'
              tazel query 'rdeps(//..., //core:core)' --output label_kind
              tazel query '//... - tests(//...)'
            """),
        ["cquery"] = new("tazel cquery <expression>: query for the platform and mode of the command.", $"""
            tazel cquery <expression> [--output label|label_kind] [options]

            Answers as tazel query does, for the platform and mode of the command: what does not build for the
            platform is left out, and each target is printed with its configuration, such as (linux-amd64-debug).

            {BuildOptions}
            """),
        ["aquery"] = new("tazel aquery <expression>: prints the actions of the targets.", $"""
            tazel aquery <expression> [options]

            Prints the actions of the targets of the expression, for the platform and mode of the command: what each
            runs, reads and writes. Nothing runs.

            {BuildOptions}
            """),
        ["fetch"] = new("tazel fetch: downloads and checks what the module depends on.", """
            tazel fetch

            Downloads and checks the modules of Tazel Portal Modules and the plugins the module depends on, and unpacks
            its local modules and plugins, without building. It takes no arguments.
            """),
        ["vendor"] = new("tazel vendor: copies the modules of Tazel Portal Modules into vendor/.", """
            tazel vendor

            Copies every module of Tazel Portal Modules the module depends on into vendor/, with its .sha256sums.
            Builds take an archive from vendor/ before they download it, so a module with vendor/ builds offline.
            """),
        ["portal-modules"] = new("tazel portal-modules search|info [-e] [--] <module|group:module>: Tazel Portal Modules.", """
            tazel portal-modules search [-e|--exact] [--] <module|group:module>
            tazel portal-modules info [-e|--exact] [--] <module|group:module>

            search lists the modules whose group:module holds the query; info prints the versions of one module, with
            yanked ones marked, their push dates and dependencies.
              -e, --exact   only group:module, exactly; the group is required with it
            Without -e the group may be left out; for info, a name that is a module of more than one group needs it.
            -- ends the options, so a query may start with a dash.

            Examples:
              tazel portal-modules search json
              tazel portal-modules info com.example:json -e
            """),
        ["clean"] = new("tazel clean [--expunge]: removes what builds made.", """
            tazel clean [--expunge]

            Removes the outputs of every platform and mode, the test logs, the coverage report, zig's cache and the
            archives of tazel package; --expunge removes all of .tazel, the unpacked dependencies and plugins too.
            """),
        ["package"] = new("tazel package: writes the archive of the module.", """
            tazel package

            Writes .tazel/package/$group.$module-$version.tazel.tar.xz and its .sha256sums. The archive is the same
            bytes for the same files: sorted, owned by no one, dated at the epoch. Files starting with a dot, the
            directories of other modules and what PortalModules' Exclude names are left out.
            """),
        ["custom"] = new("tazel custom <name> [arguments]: runs a command of tazel_custom.txt.", """
            tazel custom <name> [arguments]

            Runs a command of the tazel_custom.txt of the current directory. The longest name that fits wins; -- ends
            the name, and what follows is arguments, which <1>, <2>... and <*> take. Everything after custom belongs to
            the command, its options too.
            """),
        ["explain"] = new("tazel explain <code> [--long]: what an error code means and how to fix it.", """
            tazel explain <code> [--long]

            Every error of Tazel has a code, E and four digits, as in error[E0406]. tazel explain prints what the code
            means and a fixed example; --long says why, and shows the wrong text and the right one side by side.
            The first two digits say where an error comes from:
              00  the text files (their comments, strings and placeholders)
              01  the command line
              02  tazel_options.txt
              03  tazel_custom.txt
              04  tazel_envs.txt
              05  MODULE.csx, BUILD.csx and builds
              06  labels

            Examples:
              tazel explain E0406
              tazel explain E0406 --long
            """),
        ["version"] = new("tazel version: prints the version of Tazel.", """
            tazel version

            Prints the version of Tazel, the .NET runtime it runs on and the platform of this machine.
            """),
        ["help"] = new("tazel help [command] [--long]: prints help.", """
            tazel help [command] [--long]

            Prints what Tazel can do, or what one command does; --long says more.
            """),
        ["dotnet"] = new("tazel dotnet start-daemon|restart-daemon|stop-daemon: the daemon of Tazel.", """
            tazel dotnet start-daemon|restart-daemon|stop-daemon

            The daemon that keeps the .NET runtime of Tazel warm between commands.
            """),
    };

    private const string GlobalOptions = """
        Options of every command:
          --color always|auto|never     colors; default: auto
          --progress always|auto|never  the [done/total] counter; default: auto
          --quiet                       only problems and the program's own output
          --help, -h                    the help of the command
        """;

    /// <summary>The help of <paramref name="topic"/>, or the general help; <paramref name="long"/> says more.</summary>
    public static string Text(string? topic, bool @long = false)
    {
        if (topic is not null)
        {
            return Topics.TryGetValue(topic, out var text)
                ? (@long ? text.Long : text.Short + $"\nMore: tazel help {topic} --long").TrimEnd()
                : throw new CommandLineException(
                    $"'{topic}' is not a command of tazel{(Suggestions.Nearest(topic, Topics.Keys) is { } near ? $"; did you mean {near}?" : "")}");
        }

        var commands = string.Join('\n', Topics.Select(entry => $"  {entry.Key,-15} {Summary(entry.Value.Short)}"));
        var general = $"""
            Tazel builds C, C++, Objective-C, Objective-C++ and Zig. A module has a MODULE.csx at its root and a
            BUILD.csx in each package; targets are named by labels such as //app:greeter.

            Commands:
            {commands}

            {GlobalOptions}
            tazel help <command> prints the help of one command; --long says more.
            """;
        if (!@long)
        {
            return general.TrimEnd();
        }

        return $"""
            {general.TrimEnd()}

            {Labels}

            {BuildOptions}

            Files Tazel reads:
              MODULE.csx          the module: its name, version, dependencies, plugins, toolchains (a C# script)
              BUILD.csx           the targets of one package (a C# script)
              tazel_options.txt   the options of this machine: the platform, the MicroVM sandbox
              tazel_custom.txt    the commands of tazel custom

            Exit status: 0 when the command did what it was asked, 1 when it failed, 2 for a command line it cannot take.
            """.TrimEnd();
    }

    private static string Summary(string text)
    {
        var colon = text.IndexOf(": ", System.StringComparison.Ordinal);
        return colon < 0 ? text : text[(colon + 2)..].TrimEnd('.');
    }
}
