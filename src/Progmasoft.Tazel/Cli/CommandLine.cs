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
internal sealed record OutputOptions(When Color, When Progress, bool Quiet, MessageFormat Format = MessageFormat.RichStdio);

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
internal abstract record Command(OutputOptions Output)
{
    /// <summary>The settings of --dotnet-settings, Name=Value each, checked; for this Tazel process alone.</summary>
    public IReadOnlyList<Runtime.DotnetSetting> DotnetSettings { get; init; } = [];
}

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

/// <summary><c>tazel dotnet get &lt;setting&gt;</c>: what this process has for a .NET setting.</summary>
internal sealed record DotnetGetCommand(OutputOptions Output, string Setting) : Command(Output);

/// <summary><c>tazel dotnet what-is &lt;term&gt;</c>: what a term of .NET means.</summary>
internal sealed record DotnetWhatIsCommand(OutputOptions Output, string Term) : Command(Output);

/// <summary><c>tazel limited-time C#</c>: the migration to BUILD.csx and labels in Depends, until Tazel 1.0.0.</summary>
internal sealed record MigrateCommand(OutputOptions Output) : Command(Output);

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

/// <summary><c>tazel cache keygen</c>: a key pair for signing the results of a remote cache.</summary>
internal sealed record CacheKeygenCommand(OutputOptions Output, Cache.SigningAlgorithm Algorithm) : Command(Output);

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
            "portal-modules", "cache", "explain", "version", "help", "dotnet", "limited-time", "options", "custom-cmds", "envs", "tool"];

    private enum Kind
    {
        Flag,
        Value,
        List,

        // A list after whitespace too: --dotnet-settings A=1,B=2.
        ListAlways,
    }

    private sealed record Option(string Long, Kind Kind, IReadOnlyList<string>? Values, params string[] Scope);

    private static readonly string[] Building = ["build", "run", "test", "coverage", "cquery", "aquery"];

    private static readonly Option[] Options =
    [
        new("color", Kind.Value, ["always", "auto", "never"]),
        new("progress", Kind.Value, ["always", "auto", "never"]),
        new("quiet", Kind.Flag, null),
        new("dotnet-settings", Kind.ListAlways, null),
        new("user", Kind.Flag, null, "options", "custom-cmds", "envs"),
        new("project", Kind.Flag, null, "options", "custom-cmds", "envs"),
        new("local", Kind.Flag, null, "options", "custom-cmds", "envs"),
        new("create", Kind.Flag, null, "options"),
        new("output-dir", Kind.Value, null, "tool"),
        new("message-format", Kind.Value, ["rich-stdio", "script-friendly-stdio", "jsonl", "xml"]),
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
        new("algorithm", Kind.Value, ["ed25519", "rsa-4096", "ml-dsa"], "cache"),
        new("output", Kind.Value, ["label", "label_kind"], "query", "cquery", "aquery"),
    ];

    /// <summary>Reads <paramref name="arguments"/>.</summary>
    public static Command Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var scanned = Scan(arguments);
        // Every --dotnet-settings, in order, each entry checked here; on a name given twice the last wins later.
        var settings = scanned.Seen.Where(entry => entry.Option.Long == "dotnet-settings")
            .SelectMany(entry => entry.Values)
            .Select(entry =>
            {
                try
                {
                    return Runtime.DotnetSettings.Parse(entry, "--dotnet-settings");
                }
                catch (Runtime.DotnetSettingsException error)
                {
                    throw new CommandLineException(error.Message, error);
                }
            })
            .ToList();
        return Make(scanned) with { DotnetSettings = settings };
    }

    private static Command Make(Scanned scanned)
    {
        var command = scanned.Command;
        var unscoped = scanned.Seen.FirstOrDefault(entry =>
            entry.Option.Scope.Length > 0 && !entry.Option.Scope.Contains(command, StringComparer.Ordinal));
        if (unscoped.Option is not null)
        {
            throw new CommandLineException($"{unscoped.Written} is not an option of tazel {command}");
        }

        var output = OutputOf(scanned);
        if (scanned.Has("help") && command != "help")
        {
            return new HelpCommand(output, command);
        }

        if (command == "portal-modules")
        {
            // After -- a query may start with a dash.
            scanned.Positional.AddRange(scanned.AfterDashes);
            scanned.AfterDashes.Clear();
        }

        if (scanned.AfterDashes.Count > 0 && command != "run")
        {
            throw new CommandLineException($"tazel {command} takes nothing after --; only tazel run passes arguments on");
        }

        return Makers.TryGetValue(command, out var make) ? make(scanned, output) : MakeHelp(scanned, output);
    }

    private static OutputOptions OutputOf(Scanned scanned) => new(
        ParseWhen(Last(scanned.Seen, "color")) ?? When.Auto,
        ParseWhen(Last(scanned.Seen, "progress")) ?? When.Auto,
        scanned.Has("quiet"),
        ParseFormat(Last(scanned.Seen, "message-format")) ?? MessageFormat.RichStdio);

    /// <summary>The words of a command line, read: the command, the words that are not options, and the options.</summary>
    private sealed record Scanned(
        string Command, List<string> Positional, List<string> AfterDashes, List<(Option Option, List<string> Values, string Written)> Seen)
    {
        public bool Has(string option) => Seen.Any(entry => entry.Option.Long == option);
    }

    private static void ReadOption(
        string argument, IReadOnlyList<string> arguments, ref int index, List<(Option, List<string>, string)> seen)
    {
        if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            ReadLong(argument, arguments, ref index, seen);
        }
        else
        {
            ReadShort(argument, arguments, ref index, seen);
        }
    }

    private static Scanned Scan(IReadOnlyList<string> arguments)
    {
        string? command = null;
        var positional = new List<string>();
        var afterDashes = new List<string>();
        var seen = new List<(Option Option, List<string> Values, string Written)>();
        var index = 0;
        while (index < arguments.Count)
        {
            var argument = arguments[index++];
            if (command == "custom" || (command == "tool" && positional is ["launch", _, ..]))
            {
                // Everything after `tazel custom`, and after the name of `tazel tool launch`, belongs to what runs,
                // its options and -- too.
                positional.Add(argument);
            }
            else if (argument == "--")
            {
                afterDashes.AddRange(arguments.Skip(index));
                break;
            }
            else if (argument.StartsWith('-') && argument.Length > 1)
            {
                ReadOption(argument, arguments, ref index, seen);
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

        return new Scanned(command ?? "help", positional, afterDashes, seen);
    }

    /// <summary>How each command is made from what was scanned; tazel help is the rest.</summary>
    private static readonly Dictionary<string, Func<Scanned, OutputOptions, Command>> Makers = new(StringComparer.Ordinal)
    {
        ["build"] = (scanned, output) => new BuildCommand(output, scanned.Positional, Flags(scanned.Seen)),
        ["test"] = (scanned, output) => new TestCommand(output, scanned.Positional, Flags(scanned.Seen)),
        ["coverage"] = (scanned, output) => new CoverageCommand(output, scanned.Positional, Flags(scanned.Seen)),
        ["run"] = MakeRun,
        ["query"] = (scanned, output) => MakeQuery(scanned, output, QueryKind.Query),
        ["cquery"] = (scanned, output) => MakeQuery(scanned, output, QueryKind.CQuery),
        ["aquery"] = (scanned, output) => MakeQuery(scanned, output, QueryKind.AQuery),
        ["explain"] = (scanned, output) => Explain(output, scanned.Positional, scanned.Has("long")),
        ["fetch"] = (scanned, output) => Bare(scanned, new FetchCommand(output)),
        ["vendor"] = (scanned, output) => Bare(scanned, new VendorCommand(output)),
        ["portal-modules"] = (scanned, output) => PortalModules(output, scanned.Positional, scanned.Has("exact")),
        ["clean"] = (scanned, output) => Bare(scanned, new CleanCommand(output, scanned.Has("expunge"))),
        ["package"] = (scanned, output) => Bare(scanned, new PackageCommand(output)),
        ["custom"] = (scanned, output) => scanned.Positional.Count == 0
            ? throw new CommandLineException("tazel custom needs the name of a command of tazel_custom.txt")
            : new CustomCommandLine(output, scanned.Positional),
        ["version"] = (scanned, output) => Bare(scanned, new VersionCommand(output)),
        // The one word after it is case-sensitive: C#, as the language is written.
        ["limited-time"] = (scanned, output) => scanned.Positional is ["C#"]
            ? new MigrateCommand(output)
            : throw new CommandLineException("tazel limited-time takes one word, C#: tazel limited-time C#"),
        ["dotnet"] = MakeDaemon,
        ["options"] = MakeOptions,
        ["tool"] = MakeTool,
        ["custom-cmds"] = (scanned, output) => MakeValidate(scanned, output, ConfigurationKind.Custom),
        ["envs"] = (scanned, output) => MakeValidate(scanned, output, ConfigurationKind.Envs),
        ["cache"] = MakeCache,
    };

    private static Command Bare(Scanned scanned, Command command)
    {
        NoPositional(scanned.Command, scanned.Positional);
        return command;
    }

    private static Command MakeRun(Scanned scanned, OutputOptions output)
    {
        var positional = scanned.Positional;
        return positional.Count > 1
            ? throw new CommandLineException(
                $"tazel run takes one target; the program's own arguments go after --: tazel run {positional[0]} -- {string.Join(' ', positional.Skip(1))}")
            : new RunCommand(output, positional.FirstOrDefault(), Flags(scanned.Seen), scanned.AfterDashes);
    }

    private static Command MakeQuery(Scanned scanned, OutputOptions output, QueryKind kind) =>
        scanned.Positional.Count == 0
            ? throw new CommandLineException($"tazel {scanned.Command} needs an expression, such as deps(//app:greeter)")
            : new QueryCommand(output, kind, string.Join(' ', scanned.Positional), Flags(scanned.Seen), Last(scanned.Seen, "output") ?? "label");

    // --user, --project or --local: one at most; null for none.
    private static Dsl.Configuration.LayerKind? Scope(Scanned scanned)
    {
        var given = new[] { ("user", Dsl.Configuration.LayerKind.User), ("project", Dsl.Configuration.LayerKind.Project), ("local", Dsl.Configuration.LayerKind.Local) }
            .Where(flag => scanned.Has(flag.Item1)).ToList();
        return given.Count switch
        {
            0 => null,
            1 => given[0].Item2,
            _ => throw new CommandLineException($"--{given[0].Item1} and --{given[1].Item1} name two layers; give one"),
        };
    }

    private static Command MakeOptions(Scanned scanned, OutputOptions output)
    {
        var positional = scanned.Positional;
        var scope = Scope(scanned);
        if (scanned.Has("create") && positional is not ["update", ..])
        {
            throw new CommandLineException("--create is an option of tazel options update");
        }

        return positional switch
        {
            ["validate"] => new OptionsCommand(output, "validate", null, null, scope, false),
            ["get", var name] => new OptionsCommand(output, "get", name, null, scope, false),
            ["what-is", var name] => scope is null ? new OptionsCommand(output, "what-is", name, null, null, false)
                : throw new CommandLineException("tazel options what-is reads no file, so it takes no layer"),
            ["update", var name, var value] => scope == Dsl.Configuration.LayerKind.Local
                ? throw new CommandLineException("tazel options update writes ./tazel_options.txt without a flag; --local is for reading")
                : new OptionsCommand(output, "update", name, value, scope, scanned.Has("create")),
            _ => throw new CommandLineException(
                "tazel options takes validate, get <setting>, what-is <setting> or update <setting> <value>" +
                (positional.Count > 0 && Suggestions.Nearest(positional[0], ["validate", "get", "what-is", "update"]) is { } near ? $"; did you mean {near}?" : "")),
        };
    }

    private static Command MakeTool(Scanned scanned, OutputOptions output)
    {
        var positional = scanned.Positional;
        var directory = Last(scanned.Seen, "output-dir");
        if (directory is not null && positional is not ["create-wrapper", ..])
        {
            throw new CommandLineException("-o and --output-dir are options of tazel tool create-wrapper");
        }

        return positional switch
        {
            ["launch", var name, .. var rest] => new ToolLaunchCommand(output, name, rest),
            ["create-wrapper", var name] => new ToolWrapperCommand(output, name, directory),
            ["launch"] or ["create-wrapper"] => throw new CommandLineException($"tazel tool {positional[0]} needs the name of a tool"),
            ["create-wrapper", ..] => throw new CommandLineException("tazel tool create-wrapper takes one name"),
            _ => throw new CommandLineException("tazel tool takes launch <name> [arguments] or create-wrapper <name> [-o dir]" +
                (positional.Count > 0 && Suggestions.Nearest(positional[0], ["launch", "create-wrapper"]) is { } near ? $"; did you mean {near}?" : "")),
        };
    }

    private static Command MakeValidate(Scanned scanned, OutputOptions output, ConfigurationKind kind) => scanned.Positional is ["validate"]
        ? new ValidateCommand(output, kind, Scope(scanned))
        : throw new CommandLineException($"tazel {scanned.Command} takes validate");

    private static Command MakeDaemon(Scanned scanned, OutputOptions output)
    {
        var actions = new[] { "start-daemon", "restart-daemon", "stop-daemon", "get", "what-is" };
        var positional = scanned.Positional;
        if (positional is ["get", var setting])
        {
            return new DotnetGetCommand(output, setting);
        }

        if (positional is ["what-is", var term])
        {
            return new DotnetWhatIsCommand(output, term);
        }

        if (positional is ["get" or "what-is", ..])
        {
            throw new CommandLineException($"tazel dotnet {positional[0]} takes one {(positional[0] == "get" ? "setting" : "term")}");
        }

        if (positional.Count == 1 && actions.Contains(positional[0], StringComparer.Ordinal))
        {
            return new DaemonCommand(output, positional[0]);
        }

        var near = positional.Count == 1 ? Suggestions.Nearest(positional[0], actions) : null;
        throw new CommandLineException(
            $"tazel dotnet takes get, what-is, start-daemon, restart-daemon or stop-daemon{(near is null ? "" : $"; did you mean {near}?")}");
    }

    private static Command MakeCache(Scanned scanned, OutputOptions output)
    {
        if (scanned.Positional is not ["keygen"])
        {
            throw new CommandLineException("tazel cache takes keygen: tazel cache keygen [--algorithm ed25519|rsa-4096|ml-dsa]");
        }

        var algorithm = Last(scanned.Seen, "algorithm") switch
        {
            "rsa-4096" => Cache.SigningAlgorithm.Rsa4096,
            "ml-dsa" => Cache.SigningAlgorithm.MlDsa65,
            _ => Cache.SigningAlgorithm.Ed25519,
        };
        return new CacheKeygenCommand(output, algorithm);
    }

    private static Command MakeHelp(Scanned scanned, OutputOptions output) =>
        scanned.Positional.Count > 1
            ? throw new CommandLineException("tazel help takes one command")
            : new HelpCommand(output, scanned.Positional.FirstOrDefault(), scanned.Has("long"));

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

    private static CommandLineException UnknownOption(string name, string rest)
    {
        var near = Suggestions.Nearest(name, Options.Select(o => o.Long));
        return new CommandLineException($"--{name} is not an option{(near is null ? "" : $"; did you mean --{near}{rest}?")}");
    }

    private static void ReadLong(
        string argument, IReadOnlyList<string> arguments, ref int index, List<(Option, List<string>, string)> seen)
    {
        var body = argument[2..];
        var separator = body.IndexOfAny(['=', ':']);
        var name = separator < 0 ? body : body[..separator];
        var option = Options.FirstOrDefault(o => o.Long == name) ?? throw UnknownOption(name, separator < 0 ? "" : body[separator..]);
        List<string> values;
        if (option.Kind == Kind.Flag)
        {
            values = separator < 0 ? [] : throw new CommandLineException($"--{name} takes no value");
        }
        else if (separator >= 0)
        {
            var text = body[(separator + 1)..];
            values = option.Kind is Kind.List or Kind.ListAlways ? new List<string>(SplitList(text)) : new List<string> { text };
            Check(option, values, $"--{name}{body[separator]}");
        }
        else
        {
            // Whitespace gives one value, whatever it holds.
            values = index >= arguments.Count ? throw new CommandLineException($"--{name} needs a value")
                : option.Kind == Kind.ListAlways ? [.. SplitList(arguments[index++])] : [arguments[index++]];
            Check(option, values, $"--{name} ");
        }

        if (values.Any(value => value.Length == 0))
        {
            throw new CommandLineException($"--{name} has an empty value");
        }

        seen.Add((option, values, $"--{name}"));
    }

    private static void ReadShort(
        string argument, IReadOnlyList<string> arguments, ref int index, List<(Option, List<string>, string)> seen)
    {
        if (argument == "-o")
        {
            var value = index < arguments.Count ? arguments[index++] : throw new CommandLineException("-o needs a directory");
            seen.Add((Options.First(o => o.Long == "output-dir"), value.Length > 0 ? [value] : throw new CommandLineException("-o has an empty value"), "-o"));
            return;
        }

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

    /// <summary>
    /// The message format a command line asks for, read on its own for a command line that cannot be taken: the last
    /// <c>--message-format</c> before <c>--</c> with a value Tazel knows, else rich-stdio.
    /// </summary>
    public static MessageFormat FormatOf(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var format = MessageFormat.RichStdio;
        for (var i = 0; i < arguments.Count && arguments[i] != "--"; i++)
        {
            const string Name = "--message-format";
            var argument = arguments[i];
            string? value = argument == Name && i + 1 < arguments.Count ? arguments[i + 1]
                : argument.StartsWith(Name, StringComparison.Ordinal) && argument.Length > Name.Length &&
                    argument[Name.Length] is '=' or ':' ? argument[(Name.Length + 1)..]
                : null;
            format = ParseFormat(value) ?? format;
        }

        return format;
    }

    private static MessageFormat? ParseFormat(string? text) => text switch
    {
        "rich-stdio" => MessageFormat.RichStdio,
        "script-friendly-stdio" => MessageFormat.ScriptFriendlyStdio,
        "jsonl" => MessageFormat.Jsonl,
        "xml" => MessageFormat.Xml,
        _ => null,
    };

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

    /// <summary>A query is module or group:module, and group:module with --exact.</summary>
    private static void CheckModuleQuery(string action, string query, bool exact)
    {
        var parts = query.Split(':');
        if (exact && (parts.Length != 2 || parts[0].Length == 0))
        {
            throw new CommandLineException(
                $"with -e or --exact, tazel portal-modules {action} needs the group too: group:{query.TrimStart(':')}");
        }

        if (parts.Length > 2 || parts.Any(part => part.Length == 0))
        {
            throw new CommandLineException($"'{query}' is not module or group:module");
        }
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
        CheckModuleQuery(positional[0], query, exact);
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
