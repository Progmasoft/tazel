// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Progmasoft.Tazel.Cli;

/// <summary>What <c>tazel help</c> prints.</summary>
internal static class Help
{
    private const string BuildOptions = """
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

    private static readonly Dictionary<string, string> Topics = new(System.StringComparer.Ordinal)
    {
        ["build"] = $"""
            tazel build [labels] [options]

            Builds the targets of the labels: //dir:name, //:name, :name, //dir/... or //...; without a label, the
            package of the current directory. //... leaves out targets tagged manual and those that do not build for
            the platform.

            {BuildOptions}
            """,
        ["test"] = $"""
            tazel test [labels] [options]

            Builds the tests of the labels and runs each in its runfiles directory, with its own TEST_TMPDIR and a
            timeout from its TestSize; a flaky test is run up to three times. Logs go to .tazel/testlogs.

            {BuildOptions}
            """,
        ["run"] = $"""
            tazel run <label> [options] [-- arguments]

            Builds one executable or test and runs it in its runfiles directory, with Tazel's environment and the
            program's EnvironmentVariables; Arguments and what follows -- are passed to it.

            {BuildOptions}
            """,
        ["clean"] = """
            tazel clean [--expunge]

            Removes the outputs of every platform and mode, the test logs and zig's cache; --expunge removes all of
            .tazel, the unpacked dependencies too.
            """,
        ["package"] = """
            tazel package

            Writes the archive of the module, $group.$module-$version.tazel.tar.xz, and its .sha256sums to
            .tazel/package. The files of PortalModules' Exclude are left out.
            """,
        ["custom"] = """
            tazel custom <name> [arguments]

            Runs a command of the tazel_custom.txt of the current directory. The longest name that fits wins; -- ends
            the name, and what follows is arguments.
            """,
        ["version"] = """
            tazel version

            Prints the version of Tazel.
            """,
        ["help"] = """
            tazel help [command]

            Prints what Tazel can do, or what one command does.
            """,
        ["dotnet"] = """
            tazel dotnet start-daemon|restart-daemon|stop-daemon

            The daemon that keeps the .NET runtime of Tazel warm between commands.
            """,
    };

    /// <summary>The help of <paramref name="topic"/>, or the general help.</summary>
    public static string Text(string? topic)
    {
        if (topic is not null)
        {
            return Topics.TryGetValue(topic, out var text)
                ? text.TrimEnd()
                : throw new CommandLineException(
                    $"'{topic}' is not a command of tazel{(Suggestions.Nearest(topic, Topics.Keys) is { } near ? $"; did you mean {near}?" : "")}");
        }

        return """
            Tazel builds C, C++, Objective-C, Objective-C++ and Zig. A module has a MODULE.csx at its root and a
            BUILD.csx in each package; targets are named by labels such as //app:greeter.

            Commands:
              build     build targets
              test      build and run tests
              run       build and run one program
              clean     remove what builds made
              package   write the archive of the module
              custom    run a command of tazel_custom.txt
              version   print the version
              help      print help; tazel help <command> for one command

            Options of every command:
              --color always|auto|never     colors; default: auto
              --progress always|auto|never  the [done/total] counter; default: auto
              --quiet                       only problems and the program's own output
              --help, -h                    the help of the command
            """;
    }
}
