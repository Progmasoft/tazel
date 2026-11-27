// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Lexer;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Dsl.Options;

namespace Progmasoft.Tazel.Cli;

/// <summary>The kinds of text files the configuration commands work on.</summary>
internal enum ConfigurationKind
{
    Options,
    Custom,
    Envs,
}

/// <summary><c>tazel options</c>: validate, get, what-is or update.</summary>
internal sealed record OptionsCommand(OutputOptions Output, string Action, string? Name, string? Value, LayerKind? Scope, bool Create)
    : Command(Output);

/// <summary><c>tazel custom-cmds validate</c> and <c>tazel envs validate</c>.</summary>
internal sealed record ValidateCommand(OutputOptions Output, ConfigurationKind Kind, LayerKind? Scope) : Command(Output);

/// <summary>What <c>tazel options what-is</c> says of an option.</summary>
internal sealed record OptionSchema(string Name, string Type, string Default, string Meaning);

/// <summary>The commands that read and write the text files: what each layer holds, and checking them.</summary>
internal sealed partial class Commands
{
    /// <summary>The options of tazel_options.txt, as tazel options what-is explains them.</summary>
    internal static IReadOnlyList<OptionSchema> Schema { get; } =
    [
        new("sandboxing-with-microvm", "true or false", "false",
            "Whether what a project brings to run, the build and custom commands among it, runs inside a MicroVM. true in any " +
            "layer stays true: no layer above and no command line turns it off."),
        new("microvm-network", "true or false", "false", "Whether the MicroVM reaches the network."),
        new("microvm-memory", "auto, or a whole number of MiB from 256 to 1048576", "auto", "The memory of the MicroVM."),
        new("microvm-cpus", "auto, or a whole number from 1 to 256", "auto", "The processors of the MicroVM."),
        new("platform", "a platform name, as --platform takes it", "this machine's",
            "The platform commands build for unless --platform names one. It is chosen before what depends on target_system " +
            "is read, so it may not be set inside a condition on target_system."),
        new("dotnet-settings", "a list of strings, Name=Value each: [ \"GCHeapHardLimit=512M\" ]", "none",
            "Settings of the .NET runtime of the Tazel process, merged name by name across the layers, before those of " +
            "--dotnet-settings. tazel help dotnet --long lists the thirteen."),
    ];

    private int Options(OptionsCommand command) => command.Action switch
    {
        "validate" => Validate(ConfigurationKind.Options, command.Scope),
        "get" => OptionGet(command.Name!, command.Scope),
        "what-is" => OptionWhatIs(command.Name!),
        _ => OptionUpdate(command.Name!, command.Value!, command.Scope, command.Create),
    };

    private static string FileName(ConfigurationKind kind) => kind switch
    {
        ConfigurationKind.Options => TazelOptions.FileName,
        ConfigurationKind.Custom => CustomCommandFiles.FileName,
        _ => TazelEnvsFile.FileName,
    };

    // The layers of one kind here, or only the one a scope flag names.
    private IReadOnlyList<Layer> Layers(ConfigurationKind kind, LayerKind? scope)
    {
        try
        {
            var all = ConfigurationLayers.For(FileName(kind), workingDirectory);
            if (scope is not { } only)
            {
                return all;
            }

            var file = ConfigurationLayers.FileOf(only, FileName(kind), workingDirectory);
            return File.Exists(file) ? [new Layer(only, file)] : [];
        }
        catch (ConfigurationHomeException error)
        {
            throw new CommandLineException(error.Message, error);
        }
    }

    /// <summary>
    /// Reads every layer of a kind as the command that uses it would, includes and conditions resolved, and says
    /// which it checked. Nothing runs: no shell line of tazel_envs.txt, no program, no project file.
    /// </summary>
    private int Validate(ConfigurationKind kind, LayerKind? scope)
    {
        var layers = Layers(kind, scope);
        if (layers.Count == 0)
        {
            reporter.Plain($"no {FileName(kind)} holds here{(scope is null ? "" : " in that layer")}; there is nothing to check");
            return 0;
        }

        var (options, _) = kind == ConfigurationKind.Options ? (new TazelOptions(), (string?)null) : TazelOptionsFile.Effective(workingDirectory);
        var context = ConfigurationContext.ForThisMachine() with
        {
            Target = SystemFacts.Of(options.Platform ?? Driver.Host.Platform),
        };
        foreach (var layer in layers)
        {
            var text = File.ReadAllText(layer.File);
            switch (kind)
            {
                case ConfigurationKind.Options:
                    TazelOptionsFile.Parse(text, Shown(layer.File), layer.File);
                    break;
                case ConfigurationKind.Custom:
                    CustomParser.Parse(text, Shown(layer.File), layer.File, context);
                    break;
                default:
                    TazelEnvsFile.Parse(text, Shown(layer.File), layer.File, context);
                    break;
            }

            reporter.Plain($"valid: {Shown(layer.File)} ({layer.Description})");
        }

        reporter.Status("every branch of .if was checked; the files of branches not taken on this machine were not opened");
        return 0;
    }

    private int OptionGet(string name, LayerKind? scope)
    {
        CheckName(name);
        if (scope is { } only)
        {
            var layer = Layers(ConfigurationKind.Options, only).FirstOrDefault();
            if (layer is null)
            {
                reporter.Plain($"{name}: not set (no {TazelOptions.FileName} in that layer)");
                return 0;
            }

            var machine = ConfigurationContext.ForThisMachine();
            var (one, keys) = TazelOptionsFile.Read(File.ReadAllText(layer.File), Shown(layer.File), layer.File, machine.ForItself());
            reporter.Plain(keys.Contains(name) ? $"{name}: {Shown(one, name)}" : $"{name}: not set in {Shown(layer.File)}");
            return 0;
        }

        var layered = TazelOptionsFile.Layered(workingDirectory);
        var origin = layered.Origins.TryGetValue(name, out var from) ? $"{Shown(from.File)}, {from.Description}" : "default";
        reporter.Plain($"{name}: {Shown(layered.Options, name)} ({origin})");
        return 0;
    }

    private static string Shown(TazelOptions options, string name) => name switch
    {
        "sandboxing-with-microvm" => options.SandboxingWithMicroVM ? "true" : "false",
        "microvm-network" => options.MicroVMNetwork ? "true" : "false",
        "microvm-memory" => options.MicroVMMemory.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
        "microvm-cpus" => options.MicroVMCpus.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "auto",
        "platform" => options.Platform?.Name() ?? $"this machine's, {Driver.Host.Platform.Name()}",
        _ => options.DotnetSettings.Count == 0 ? "none"
            : "[ " + string.Join(' ', Runtime.DotnetSettings.Effective(options.DotnetSettings, []).Values.Select(s => $"\"{s.Name}={Value(s)}\"")) + " ]",
    };

    private static string Value(Runtime.DotnetSetting setting) => Convert.ToString(setting.Value, System.Globalization.CultureInfo.InvariantCulture)!;

    private int OptionWhatIs(string name)
    {
        var schema = CheckName(name);
        reporter.Strong(schema.Name);
        reporter.Plain($"type: {schema.Type}");
        reporter.Plain($"default: {schema.Default}");
        reporter.Plain(schema.Meaning);
        return 0;
    }

    private static OptionSchema CheckName(string name) => Schema.FirstOrDefault(option => option.Name == name)
        ?? throw new CommandLineException($"'{name}' is not an option of {TazelOptions.FileName}" +
            (Suggestions.Nearest(name, Schema.Select(option => option.Name)) is { } near ? $"; did you mean {near}?" : ""));

    /// <summary>
    /// Sets one option in one file, ./tazel_options.txt or the user's or the project's: the value of its entry is
    /// replaced where it stands, or the entry is added at the end, and the rest of the file is kept as it was. An
    /// option set inside .if or in an included file is left for a person to edit. The file is written beside itself
    /// and moved into place, under a lock other Tazel processes wait for.
    /// </summary>
    private int OptionUpdate(string name, string value, LayerKind? scope, bool create)
    {
        CheckName(name);
        var written = name == "dotnet-settings"
            ? "[ " + string.Join(' ', SplitList(value).Select(entry => $"\"{entry}\"")) + " ]"
            : value;
        string file;
        try
        {
            file = ConfigurationLayers.FileOf(scope ?? LayerKind.Local, TazelOptions.FileName, workingDirectory, create);
        }
        catch (ConfigurationHomeException error)
        {
            throw new CommandLineException(error.Message, error);
        }

        if (!File.Exists(file) && !create)
        {
            throw new CommandLineException($"there is no {file}; add --create to make it");
        }

        // The value must be one the option takes.
        TazelOptionsFile.Parse($"{name}: {written};", "the new value", Path.Combine(Path.GetTempPath(), TazelOptions.FileName));
        using var held = Lock(file);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        var updated = Edited(text, file, name, written);
        TazelOptionsFile.Parse(updated, Shown(file), file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var partial = $"{file}.{Guid.NewGuid():N}.partial";
        File.WriteAllText(partial, updated, new UTF8Encoding(false));
        File.Move(partial, file, overwrite: true);
        reporter.Success($"{name}: {written} in {Shown(file)}");
        return 0;
    }

    // The text with the entry of name set to written: replaced where it stands, or added at the end.
    private string Edited(string text, string file, string name, string written)
    {
        var tokens = new CustomLexer(text, Shown(file)).Tokens();
        var depth = 0;
        var found = new List<(int Index, int Depth)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == TokenKind.Directive)
            {
                depth += token.Text == "if" ? 1 : token.Text == "end" ? -1 : 0;
                continue;
            }

            var startsEntry = i == 0 || tokens[i - 1].Kind is TokenKind.Semicolon or TokenKind.Directive;
            if (startsEntry && token is { Kind: TokenKind.Name } && token.Text == name && i + 1 < tokens.Count && tokens[i + 1].Kind == TokenKind.Colon)
            {
                found.Add((i, depth));
            }
        }

        if (found.Any(entry => entry.Depth > 0))
        {
            throw new CommandLineException($"{Shown(file)} sets {name} inside .if; edit it there by hand");
        }

        if (found.Count > 1)
        {
            throw new CommandLineException($"{Shown(file)} sets {name} {found.Count} times; keep one by hand first");
        }

        if (found.Count == 0)
        {
            var (_, keys) = TazelOptionsFile.Read(text, Shown(file), file, ConfigurationContext.ForThisMachine().ForItself());
            if (keys.Contains(name))
            {
                throw new CommandLineException($"{name} is set in a file that {Shown(file)} includes; edit that file");
            }

            // Added at the end, after a ';' the last entry may have left out.
            var last = tokens.LastOrDefault(token => token.Kind != TokenKind.Directive);
            var trimmed = text.TrimEnd();
            var join = trimmed.Length == 0 ? "" : last is { Kind: not TokenKind.Semicolon } && !trimmed.EndsWith(';') ? ";\n" : "\n";
            return trimmed + join + $"{name}: {written};\n";
        }

        // The value runs from after the ':' to the ';', or to the end of the file for its last entry.
        var first = found[0].Index + 2;
        if (first >= tokens.Count || tokens[first].Kind == TokenKind.Semicolon)
        {
            throw new CommandLineException($"{Shown(file)} sets {name} with no value; give it one by hand first");
        }

        var semicolon = tokens.Skip(first).TakeWhile(token => token.Kind != TokenKind.Semicolon).Count();
        var start = Offset(text, tokens[first]);
        var endToken = tokens[first + semicolon - 1];
        var end = Offset(text, endToken) + endToken.Text.Length + (endToken.Kind == TokenKind.String ? 2 : 0);
        return text[..start] + written + text[end..];
    }

    // The offset in text of where a token starts.
    private static int Offset(string text, Token token)
    {
        var offset = 0;
        for (var line = 1; line < token.Line; line++)
        {
            offset = text.IndexOf('\n', offset) + 1;
        }

        return offset + token.Column - 1;
    }

    private static List<string> SplitList(string value) => [.. CommandLine.SplitList(value)];

    // An OS lock on the file's lock in TAZEL_HOME/Locks, released when this process ends, however it ends.
    private static FileStream Lock(string file)
    {
        var locks = Path.Combine(TazelHome.Directory(), "Locks");
        Directory.CreateDirectory(locks);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..16];
        var path = Path.Combine(locks, $"options-{name}.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
    }
}
