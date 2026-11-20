// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Output;

namespace Progmasoft.Tazel.Cli;

/// <summary>A command line that cannot be taken, with a hint when there is one.</summary>
public sealed class CommandLineException : Exception
{
    /// <summary>What cannot be taken, and how to put it right.</summary>
    public CommandLineException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public CommandLineException()
    {
    }

    /// <summary>What cannot be taken, and why.</summary>
    public CommandLineException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>How Tazel prints, for every command.</summary>
internal sealed record OutputOptions(When Color, When Progress, bool Quiet);

/// <summary>What <c>build</c>, <c>run</c> and <c>test</c> take beyond their labels.</summary>
internal sealed record BuildFlags
{
    public BuildMode? Mode { get; init; }

    public Platform? Platform { get; init; }

    public int? Jobs { get; init; }

    public bool KeepGoing { get; init; }

    public List<string> FeaturesOn { get; } = [];

    public List<string> FeaturesOff { get; } = [];

    public List<string> CompilerOptions { get; } = [];

    public List<string> CCompilerOptions { get; } = [];

    public List<string> CppCompilerOptions { get; } = [];

    public List<string> LinkerOptions { get; } = [];

    public List<string> ZigCompilerOptions { get; } = [];
}

/// <summary>A command line, read into what it asks for; later stages do not look at its text again.</summary>
internal abstract record Command(OutputOptions Output);

internal sealed record BuildCommand(OutputOptions Output, IReadOnlyList<string> Labels, BuildFlags Flags) : Command(Output);

internal sealed record TestCommand(OutputOptions Output, IReadOnlyList<string> Labels, BuildFlags Flags) : Command(Output);

internal sealed record RunCommand(OutputOptions Output, string? Label, BuildFlags Flags, IReadOnlyList<string> Arguments)
    : Command(Output);

internal sealed record CleanCommand(OutputOptions Output, bool Expunge) : Command(Output);

internal sealed record PackageCommand(OutputOptions Output) : Command(Output);

internal sealed record CustomCommandLine(OutputOptions Output, IReadOnlyList<string> Words) : Command(Output);

internal sealed record VersionCommand(OutputOptions Output) : Command(Output);

internal sealed record HelpCommand(OutputOptions Output, string? Topic, bool Long = false) : Command(Output);

internal sealed record CoverageCommand(OutputOptions Output, IReadOnlyList<string> Labels, BuildFlags Flags) : Command(Output);

/// <summary>Which query: of the target graph, of the configured targets, or of the actions.</summary>
internal enum QueryKind
{
    Query,
    CQuery,
    AQuery,
}

internal sealed record QueryCommand(OutputOptions Output, QueryKind Kind, string Expression, BuildFlags Flags, string Format)
    : Command(Output);

internal sealed record FetchCommand(OutputOptions Output) : Command(Output);

internal sealed record ExplainCommand(OutputOptions Output, string Code, bool Long) : Command(Output);

internal sealed record VendorCommand(OutputOptions Output) : Command(Output);

/// <summary>Searching Tazel Portal Modules, or the versions of one module.</summary>
internal enum PortalAction
{
    Search,
    Info,
}

internal sealed record PortalModulesCommand(OutputOptions Output, PortalAction Action, string Query, bool Exact)
    : Command(Output);

internal sealed record DaemonCommand(OutputOptions Output, string Action) : Command(Output);

/// <summary>
/// Reads command lines by hand, into typed values. Long options take their value after whitespace, <c>=</c> or
/// <c>:</c>; short options also joined (<c>-hbuild</c> is not an option, as <c>-h</c> takes none). A list is
/// accepted only after <c>=</c> or <c>:</c>, separated by commas with no spaces; <c>\,</c> is a comma of a value.
/// Options may stand anywhere before <c>--</c>.
/// </summary>
internal static class CommandLine
{
    /// <summary>The commands, as <c>tazel help</c> lists them.</summary>
    public static IReadOnlyList<string> Commands { get; } =
        ["build", "run", "test", "coverage", "query", "cquery", "aquery", "fetch", "vendor", "clean", "package", "custom",
            "portal-modules", "explain", "version", "help", "dotnet"];

    private enum Kind
    {
        Flag,
        Value,
        List,
    }

    private sealed record Option(string Long, Kind Kind, IReadOnlyList<string>? Values, params string[] Scope);

    private static readonly string[] Building = ["build", "run", "test", "coverage", "cquery", "aquery"];

    private static readonly Option[] Options =
    [
        new("color", Kind.Value, ["always", "auto", "never"]),
        new("progress", Kind.Value, ["always", "auto", "never"]),
        new("quiet", Kind.Flag, null),
        new("help", Kind.Flag, null),
        new("platform", Kind.Value, Platforms.Names, Building),
        new("mode", Kind.Value, ["debug", "release"], Building),
        new("jobs", Kind.Value, null, Building),
        new("keep-going", Kind.Flag, null, Building),
        new("feature", Kind.List, null, Building),
        new("no-feature", Kind.List, null, Building),
        new("compiler-options", Kind.List, null, Building),
        new("c-compiler-options", Kind.List, null, Building),
        new("cpp-compiler-options", Kind.List, null, Building),
        new("linker-options", Kind.List, null, Building),
        new("zig-compiler-options", Kind.List, null, Building),
        new("expunge", Kind.Flag, null, "clean"),
        new("long", Kind.Flag, null, "help", "explain"),
        new("exact", Kind.Flag, null, "portal-modules"),
        new("output", Kind.Value, ["label", "label_kind"], "query", "cquery", "aquery"),
    ];

    /// <summary>Reads <paramref name="arguments"/>.</summary>
    public static Command Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? command = null;
        var positional = new List<string>();
        var afterDashes = new List<string>();
        var seen = new List<(Option Option, List<string> Values, string Written)>();
        var index = 0;
        while (index < arguments.Count)
        {
            var argument = arguments[index++];
            if (command == "custom")
            {
                // Everything after `tazel custom` belongs to the custom command, its options too.
                positional.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                afterDashes.AddRange(arguments.Skip(index));
                break;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                ReadLong(argument, arguments, ref index, seen);
            }
            else if (argument.StartsWith('-') && argument.Length > 1)
            {
                ReadShort(argument, seen);
            }
            else if (command is null)
            {
                command = Commands.Contains(argument, StringComparer.Ordinal) ? argument : throw UnknownCommand(argument);
            }
            else
            {
                positional.Add(argument);
            }
        }

        command ??= "help";
        foreach (var (option, _, written) in seen)
        {
            if (option.Scope.Length > 0 && !option.Scope.Contains(command, StringComparer.Ordinal))
            {
                throw new CommandLineException($"{written} is not an option of tazel {command}");
            }
        }

        var output = new OutputOptions(
            ParseWhen(Last(seen, "color")) ?? When.Auto,
            ParseWhen(Last(seen, "progress")) ?? When.Auto,
            seen.Any(entry => entry.Option.Long == "quiet"));
        if (seen.Any(entry => entry.Option.Long == "help") && command != "help")
        {
            return new HelpCommand(output, command);
        }

        if (command == "portal-modules")
        {
            // After -- a query may start with a dash.
            positional.AddRange(afterDashes);
            afterDashes.Clear();
        }

        if (afterDashes.Count > 0 && command != "run")
        {
            throw new CommandLineException($"tazel {command} takes nothing after --; only tazel run passes arguments on");
        }

        switch (command)
        {
            case "build":
                return new BuildCommand(output, positional, Flags(seen));
            case "test":
                return new TestCommand(output, positional, Flags(seen));
            case "run":
                if (positional.Count > 1)
                {
                    throw new CommandLineException(
                        $"tazel run takes one target; the program's own arguments go after --: tazel run {positional[0]} -- {string.Join(' ', positional.Skip(1))}");
                }

                return new RunCommand(output, positional.FirstOrDefault(), Flags(seen), afterDashes);
            case "coverage":
                return new CoverageCommand(output, positional, Flags(seen));
            case "query" or "cquery" or "aquery":
                if (positional.Count == 0)
                {
                    throw new CommandLineException($"tazel {command} needs an expression, such as deps(//app:greeter)");
                }

                return new QueryCommand(output, command switch
                {
                    "query" => QueryKind.Query,
                    "cquery" => QueryKind.CQuery,
                    _ => QueryKind.AQuery,
                }, string.Join(' ', positional), Flags(seen), Last(seen, "output") ?? "label");
            case "explain":
                return Explain(output, positional, seen.Any(entry => entry.Option.Long == "long"));
            case "fetch":
                NoPositional(command, positional);
                return new FetchCommand(output);
            case "vendor":
                NoPositional(command, positional);
                return new VendorCommand(output);
            case "portal-modules":
                return PortalModules(output, positional, seen.Any(entry => entry.Option.Long == "exact"));
            case "clean":
                NoPositional(command, positional);
                return new CleanCommand(output, seen.Any(entry => entry.Option.Long == "expunge"));
            case "package":
                NoPositional(command, positional);
                return new PackageCommand(output);
            case "custom":
                return positional.Count == 0
                    ? throw new CommandLineException("tazel custom needs the name of a command of tazel_custom.txt")
                    : new CustomCommandLine(output, positional);
            case "version":
                NoPositional(command, positional);
                return new VersionCommand(output);
            case "dotnet":
                var actions = new[] { "start-daemon", "restart-daemon", "stop-daemon" };
                if (positional.Count != 1 || !actions.Contains(positional[0], StringComparer.Ordinal))
                {
                    var near = positional.Count == 1 ? Suggestions.Nearest(positional[0], actions) : null;
                    throw new CommandLineException(
                        $"tazel dotnet takes start-daemon, restart-daemon or stop-daemon{(near is null ? "" : $"; did you mean {near}?")}");
                }

                return new DaemonCommand(output, positional[0]);
            default:
                return positional.Count > 1
                    ? throw new CommandLineException("tazel help takes one command")
                    : new HelpCommand(output, positional.FirstOrDefault(), seen.Any(entry => entry.Option.Long == "long"));
        }
    }

    /// <summary>Splits a list at commas; <c>\,</c> is a comma of a value.</summary>
    public static IReadOnlyList<string> SplitList(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var items = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("\\,"))
            {
                current.Append(',');
                i += 2;
                continue;
            }

            if (text[i] == ',')
            {
                items.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(text[i]);
            }

            i++;
        }

        items.Add(current.ToString());
        return items;
    }

    private static void ReadLong(
        string argument, IReadOnlyList<string> arguments, ref int index, List<(Option, List<string>, string)> seen)
    {
        var body = argument[2..];
        var separator = body.IndexOfAny(['=', ':']);
        var name = separator < 0 ? body : body[..separator];
        var option = Options.FirstOrDefault(o => o.Long == name);
        if (option is null)
        {
            var near = Suggestions.Nearest(name, Options.Select(o => o.Long));
            var rest = separator < 0 ? "" : body[separator..];
            throw new CommandLineException(
                $"--{name} is not an option{(near is null ? "" : $"; did you mean --{near}{rest}?")}");
        }

        List<string> values;
        if (option.Kind == Kind.Flag)
        {
            if (separator >= 0)
            {
                throw new CommandLineException($"--{name} takes no value");
            }

            values = [];
        }
        else if (separator >= 0)
        {
            var text = body[(separator + 1)..];
            values = option.Kind == Kind.List ? [.. SplitList(text)] : [text];
            Check(option, values, $"--{name}{body[separator]}");
        }
        else
        {
            if (index >= arguments.Count)
            {
                throw new CommandLineException($"--{name} needs a value");
            }

            // Whitespace gives one value, whatever it holds.
            values = [arguments[index++]];
            Check(option, values, $"--{name} ");
        }

        if (values.Any(value => value.Length == 0))
        {
            throw new CommandLineException($"--{name} has an empty value");
        }

        seen.Add((option, values, $"--{name}"));
    }

    private static void ReadShort(string argument, List<(Option, List<string>, string)> seen)
    {
        if (argument == "-h")
        {
            seen.Add((Options.First(o => o.Long == "help"), [], "-h"));
            return;
        }

        if (argument == "-e")
        {
            seen.Add((Options.First(o => o.Long == "exact"), [], "-e"));
            return;
        }

        throw new CommandLineException($"{argument} is not an option; tazel help lists them");
    }

    private static void Check(Option option, List<string> values, string written)
    {
        if (option.Values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            if (!option.Values.Contains(value, StringComparer.Ordinal))
            {
                var near = Suggestions.Nearest(value, option.Values);
                throw new CommandLineException(
                    $"--{option.Long} takes {string.Join(", ", option.Values)}, not '{value}'" +
                    (near is null ? "" : $"; did you mean {written}{near}?"));
            }
        }
    }

    private static string? Last(List<(Option Option, List<string> Values, string)> seen, string name) =>
        seen.LastOrDefault(entry => entry.Option.Long == name).Values?.LastOrDefault();

    private static IEnumerable<string> All(List<(Option Option, List<string> Values, string)> seen, string name) =>
        seen.Where(entry => entry.Option.Long == name).SelectMany(entry => entry.Values);

    private static When? ParseWhen(string? text) => text switch
    {
        "always" => When.Always,
        "never" => When.Never,
        "auto" => When.Auto,
        _ => null,
    };

    private static BuildFlags Flags(List<(Option Option, List<string> Values, string)> seen)
    {
        int? jobs = null;
        if (Last(seen, "jobs") is { } jobsText)
        {
            jobs = int.TryParse(jobsText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed is > 0 and <= 4096
                ? parsed
                : throw new CommandLineException($"--jobs takes a number from 1 to 4096, not '{jobsText}'");
        }

        Platform? platform = Last(seen, "platform") is { } name && Platforms.TryParse(name, out var found) ? found : null;
        var flags = new BuildFlags
        {
            Mode = Last(seen, "mode") switch
            {
                "debug" => BuildMode.Debug,
                "release" => BuildMode.Release,
                _ => null,
            },
            Platform = platform,
            Jobs = jobs,
            KeepGoing = seen.Any(entry => entry.Option.Long == "keep-going"),
        };
        flags.FeaturesOn.AddRange(All(seen, "feature"));
        flags.FeaturesOff.AddRange(All(seen, "no-feature"));
        flags.CompilerOptions.AddRange(All(seen, "compiler-options"));
        flags.CCompilerOptions.AddRange(All(seen, "c-compiler-options"));
        flags.CppCompilerOptions.AddRange(All(seen, "cpp-compiler-options"));
        flags.LinkerOptions.AddRange(All(seen, "linker-options"));
        flags.ZigCompilerOptions.AddRange(All(seen, "zig-compiler-options"));
        return flags;
    }

    private static PortalModulesCommand PortalModules(OutputOptions output, List<string> positional, bool exact)
    {
        var actions = new[] { "search", "info" };
        if (positional.Count == 0 || !actions.Contains(positional[0], StringComparer.Ordinal))
        {
            var near = positional.Count > 0 ? Suggestions.Nearest(positional[0], actions) : null;
            throw new CommandLineException(
                $"tazel portal-modules takes search or info{(near is null ? "" : $"; did you mean {near}?")}");
        }

        if (positional.Count != 2)
        {
            throw new CommandLineException($"tazel portal-modules {positional[0]} takes one module: module or group:module");
        }

        var query = positional[1];
        var colon = query.IndexOf(':', StringComparison.Ordinal);
        if (exact && colon <= 0)
        {
            throw new CommandLineException(
                $"with -e or --exact, tazel portal-modules {positional[0]} needs the group too: group:{query.TrimStart(':')}");
        }

        if (colon >= 0 && (colon == 0 || colon == query.Length - 1 || query.IndexOf(':', colon + 1) >= 0))
        {
            throw new CommandLineException($"'{query}' is not module or group:module");
        }

        return new PortalModulesCommand(output, positional[0] == "search" ? PortalAction.Search : PortalAction.Info, query, exact);
    }

    private static ExplainCommand Explain(OutputOptions output, List<string> positional, bool @long)
    {
        if (positional.Count != 1)
        {
            throw new CommandLineException("tazel explain takes one error code, such as tazel explain E0406");
        }

        var code = positional[0].ToUpperInvariant();
        if (!ErrorCodes.IsCode(code))
        {
            throw new CommandLineException($"'{positional[0]}' is not an error code: E and four digits, such as E0406");
        }

        if (ErrorCodes.Find(code) is null)
        {
            var near = Suggestions.Nearest(code, ErrorCodes.Codes.Select(entry => entry.Code).ToList());
            throw new CommandLineException($"Tazel has no error {code}{(near is null ? "" : $"; did you mean {near}?")}");
        }

        return new ExplainCommand(output, code, @long);
    }

    private static void NoPositional(string command, List<string> positional)
    {
        if (positional.Count > 0)
        {
            throw new CommandLineException($"tazel {command} takes no '{positional[0]}'");
        }
    }

    private static CommandLineException UnknownCommand(string word)
    {
        var near = Suggestions.Nearest(word, Commands);
        return new CommandLineException(
            near is null ? $"'{word}' is not a command of tazel; tazel help lists them" : $"'{word}' is not a command of tazel; did you mean tazel {near}?");
    }
}
