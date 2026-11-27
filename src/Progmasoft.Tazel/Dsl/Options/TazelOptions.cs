// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Lexer;

namespace Progmasoft.Tazel.Dsl.Options;

/// <summary>How much of something a MicroVM gets: as much as Tazel chooses, or exactly an amount.</summary>
/// <param name="Amount">The amount, or null for automatic.</param>
public sealed record Allocation(int? Amount)
{
    /// <summary>As much as Tazel chooses for this machine.</summary>
    public static Allocation Auto { get; } = new((int?)null);
}

/// <summary>The options of <c>tazel_options.txt</c>; each one not written keeps its default.</summary>
public sealed record TazelOptions
{
    /// <summary>The file of the options of Tazel, in a directory a command runs in or above it.</summary>
    public static string FileName { get; } = "tazel_options.txt";

    /// <summary>
    /// Whether what a project brings to run, custom commands and the build among it, runs inside a MicroVM.
    /// </summary>
    public bool SandboxingWithMicroVM { get; init; }

    /// <summary>Whether the MicroVM reaches the network.</summary>
    public bool MicroVMNetwork { get; init; }

    /// <summary>The memory of the MicroVM, in MiB.</summary>
    public Allocation MicroVMMemory { get; init; } = Allocation.Auto;

    /// <summary>The processors of the MicroVM.</summary>
    public Allocation MicroVMCpus { get; init; } = Allocation.Auto;

    /// <summary>The platform commands build for unless <c>--platform</c> names one; default: the host's.</summary>
    public Platform? Platform { get; init; }

    /// <summary>
    /// <c>dotnet-settings: [ "Name=Value" ... ];</c>: settings of the .NET runtime of the Tazel process, applied before
    /// those of <c>--dotnet-settings</c>; none by default.
    /// </summary>
    public IReadOnlyList<Runtime.DotnetSetting> DotnetSettings { get; init; } = [];
}

/// <summary>What is wrong with a <c>tazel_options.txt</c>, with where it is.</summary>
public sealed class TazelOptionsException : Exception
{
    /// <summary>A problem of the file.</summary>
    public TazelOptionsException(string message)
        : base(message)
    {
    }

    /// <summary>A problem of the file, with all it takes to show it well.</summary>
    public TazelOptionsException(Diagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Plain)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>The place, the line, the help and the joke, when the problem has a place.</summary>
    public Diagnostic? Diagnostic { get; }

    /// <summary>Nothing to say.</summary>
    public TazelOptionsException()
    {
    }

    /// <summary>A problem and its cause.</summary>
    public TazelOptionsException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Reads and finds <c>tazel_options.txt</c>.</summary>
public static class TazelOptionsFile
{
    private const int MinMemory = 256;
    private const int MaxMemory = 1_048_576;
    private const int MaxCpus = 256;

    private static readonly string[] Keys =
        ["sandboxing-with-microvm", "microvm-network", "microvm-memory", "microvm-cpus", "platform", "dotnet-settings"];

    /// <summary>
    /// Reads the text of a <c>tazel_options.txt</c>, with the comments and strings of <c>tazel_custom.txt</c>.
    /// </summary>
    public static TazelOptions Parse(string text) => Parse(text, TazelOptions.FileName);

    /// <summary>
    /// The options of <paramref name="text"/>; <paramref name="file"/> names it in messages and a quoted include is
    /// found beside <paramref name="path"/>. It is read twice: first without what depends on <c>target_system</c>, which
    /// chooses the platform (<paramref name="requested"/>, else the file's, else this machine's), then for that
    /// platform. A platform chosen inside a condition on <c>target_system</c> is an error.
    /// </summary>
    public static TazelOptions Parse(string text, string file, string? path = null, Platform? requested = null)
    {
        var machine = Configuration.ConfigurationContext.ForThisMachine();
        var chosen = Read(text, file, path, machine);
        var platform = requested ?? chosen.Platform ?? Driver.Host.Platform;
        return Read(text, file, path, machine with { Target = Configuration.SystemFacts.Of(platform) });
    }

    private static TazelOptions Read(string text, string file, string? path, Configuration.ConfigurationContext context)
    {
        IReadOnlyList<Token> tokens;
        try
        {
            tokens = Configuration.ConfigurationResolver.Resolve(path ?? file, file, text, Configuration.ConfigurationFamily.Options, context);
        }
        catch (CustomCommandsException error)
        {
            throw error.Diagnostic is { } diagnostic ? new TazelOptionsException(diagnostic) : new TazelOptionsException(error.Message, error);
        }

        return new Reader(tokens, file, text).Options();
    }

    /// <summary>Every <c>tazel_options.txt</c> at or above <paramref name="directory"/>, the nearest first.</summary>
    public static IReadOnlyList<string> Find(string directory)
    {
        var found = new List<string>();
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var file = Path.Combine(current.FullName, TazelOptions.FileName);
            // A file that cannot be inspected is an error, never a file that is not there, so a sandbox is not skipped.
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(file);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException problem)
            {
                throw new TazelOptionsException($"{file} could not be inspected: {problem.Message}", problem);
            }
            catch (UnauthorizedAccessException problem)
            {
                throw new TazelOptionsException($"{file} could not be inspected: {problem.Message}", problem);
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                found.Add(file);
            }
        }

        return found;
    }

    /// <summary>
    /// The options that hold in <paramref name="directory"/>: those of the nearest file, except that a sandbox asked
    /// for in any file above stays asked for, so a project cannot turn off what a directory around it requires.
    /// Default options, and no file, without any.
    /// </summary>
    public static (TazelOptions Options, string? SandboxFile) Effective(string directory, Platform? requested = null)
    {
        var files = Find(directory).Select(file => (File: file, Options: Read(file, requested))).ToList();
        if (files.Count == 0)
        {
            return (new TazelOptions(), null);
        }

        var asking = files.FirstOrDefault(entry => entry.Options.SandboxingWithMicroVM).File;
        return (files[0].Options with { SandboxingWithMicroVM = asking is not null }, asking);
    }

    private static TazelOptions Read(string file, Platform? requested)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException problem)
        {
            throw new TazelOptionsException($"{file} could not be read: {problem.Message}", problem);
        }
        catch (UnauthorizedAccessException problem)
        {
            throw new TazelOptionsException($"{file} could not be read: {problem.Message}", problem);
        }

        return Parse(text, file, file, requested);
    }

    private sealed class Reader(IReadOnlyList<Token> tokens, string file, string text)
    {
        private int _position;

        public TazelOptions Options()
        {
            var options = new TazelOptions();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (_position < tokens.Count)
            {
                var key = Take(TokenKind.Name, "the name of an option");
                if (!Keys.Contains(key.Text, StringComparer.Ordinal))
                {
                    var near = Cli.Suggestions.Nearest(key.Text, Keys);
                    throw Fail(Output.ErrorCodes.OptionsUnknown, key, $"'{key.Text}' is not an option", "this one",
                        near is null ? $"the options are {string.Join(", ", Keys)}" : $"did you mean {near}?",
                        $"Tazel read its own manual twice; '{key.Text}' is not in it.");
                }

                if (!seen.Add(key.Text))
                {
                    throw Fail(Output.ErrorCodes.OptionsTwice, key, $"'{key.Text}' is set twice", "the second one", "keep one of them",
                        "Saying it twice does not make it twice as true.");
                }

                Take(TokenKind.Colon, $"':' after {key.Text}");
                if (key.Text == "dotnet-settings")
                {
                    options = options with { DotnetSettings = SettingsList() };
                    if (_position < tokens.Count)
                    {
                        Take(TokenKind.Semicolon, "';' after the list of dotnet-settings");
                    }

                    continue;
                }

                if (_position >= tokens.Count || tokens[_position].Kind is not (TokenKind.Name or TokenKind.String))
                {
                    throw new TazelOptionsException(Diagnostic.After(Output.ErrorCodes.OptionsNoValue, text, file, tokens[_position - 1],
                        $"expected a value for {key.Text}", "the value goes here", $"for example {key.Text}: {Example(key.Text)};",
                        "A name, a colon, and then... silence."));
                }

                var value = tokens[_position++];
                options = key.Text switch
                {
                    "sandboxing-with-microvm" => options with { SandboxingWithMicroVM = Boolean(value) },
                    "microvm-network" => options with { MicroVMNetwork = Boolean(value) },
                    "microvm-memory" => options with { MicroVMMemory = Amount(value, MinMemory, MaxMemory, "MiB") },
                    "microvm-cpus" => options with { MicroVMCpus = Amount(value, 1, MaxCpus, "processors") },
                    _ => options with { Platform = PlatformOf(value) },
                };

                // The last option may end the file without a ';'.
                if (_position < tokens.Count)
                {
                    Take(TokenKind.Semicolon, $"';' after the value of {key.Text}");
                }
            }

            return options;
        }

        // [ "Name=Value" 'Name=Value' `Name=Value` ]: strings in any of the quotes, no commas between them.
        private IReadOnlyList<Runtime.DotnetSetting> SettingsList()
        {
            var open = Take(TokenKind.BracketOpen, "'[' to open the list of dotnet-settings");
            var settings = new List<Runtime.DotnetSetting>();
            while (_position < tokens.Count && tokens[_position].Kind == TokenKind.String)
            {
                var entry = tokens[_position++];
                try
                {
                    settings.Add(Runtime.DotnetSettings.Parse(entry.Text, $"{file}:{entry.Line}"));
                }
                catch (Runtime.DotnetSettingsException error)
                {
                    throw Fail(Output.ErrorCodes.OptionsDotnetSetting, entry, error.Message, "this setting",
                        "write \"Name=Value\" with one of the names of tazel help dotnet --long",
                        "Tazel knows thirteen .NET settings by heart; this was not one of them.");
                }
            }

            if (_position >= tokens.Count || tokens[_position].Kind != TokenKind.BracketClose)
            {
                var at = _position < tokens.Count ? tokens[_position] : tokens[_position - 1];
                throw Fail(Output.ErrorCodes.OptionsDotnetSetting, at,
                    $"the list of dotnet-settings, opened at line {open.Line}, holds strings and ends with ]", "here",
                    "put each setting in quotes, with no commas, and close the list with ]",
                    "A list without its ] is a sentence without its full stop.");
            }

            _position++;
            return settings;
        }

        private TazelOptionsException Fail(string code, Token token, string message, string label, string help, string joke) =>
            new(Diagnostic.AtToken(code, text, file, token, message, label, help, joke));

        private static string Example(string key) => key switch
        {
            "platform" => "linux-amd64",
            "microvm-memory" => "2048",
            "microvm-cpus" => "2",
            _ => "true",
        };

        private Token Take(TokenKind kind, string wanted)
        {
            if (_position < tokens.Count && tokens[_position].Kind == kind)
            {
                return tokens[_position++];
            }

            var code = kind switch
            {
                TokenKind.Semicolon => Output.ErrorCodes.OptionsNoSemicolon,
                TokenKind.Colon => Output.ErrorCodes.OptionsNoColon,
                _ => Output.ErrorCodes.OptionsUnexpected,
            };
            var help = kind switch
            {
                TokenKind.Semicolon => "end the value with ';': platform: linux-amd64;",
                TokenKind.Colon => "an option and its value are joined by ':': platform: linux-amd64;",
                _ => "an option is: name: value;",
            };
            if (_position > 0 && (_position >= tokens.Count || kind is TokenKind.Semicolon or TokenKind.Colon))
            {
                throw new TazelOptionsException(Diagnostic.After(code, text, file, tokens[_position - 1], $"expected {wanted}",
                    kind == TokenKind.Semicolon ? "a ';' goes here" : kind == TokenKind.Colon ? "a ':' goes here" : "something goes here",
                    help, Jokes.Missing(kind)));
            }

            throw Fail(code, tokens[_position], $"expected {wanted}", "not this", help, Jokes.Missing(kind));
        }

        private bool Boolean(Token value) => value is { Kind: TokenKind.Name, Text: "true" } ? true
            : value is { Kind: TokenKind.Name, Text: "false" } ? false
            : throw Fail(Output.ErrorCodes.OptionsNotBoolean, value, "expected true or false", "this one", "write true or false, bare: sandboxing-with-microvm: true;",
                "It is a yes-or-no question. Tazel accepts no 'maybe'.");

        // "auto" is a string, in any of the three quotes; a number stands bare.
        private Allocation Amount(Token value, int min, int max, string unit)
        {
            if (value.Kind == TokenKind.String)
            {
                return value.Text == "auto" ? Allocation.Auto : throw Fail(Output.ErrorCodes.OptionsStringNotAuto, value, "the only string here is \"auto\"", "this string",
                    $"write a number of {unit}, bare, or \"auto\"", "Tazel speaks only one word in quotes here, and it is \"auto\".");
            }

            if (value.Text.Length == 0 || value.Text[0] == '0' || value.Text.Length > 9 ||
                !value.Text.All(char.IsAsciiDigit))
            {
                throw Fail(Output.ErrorCodes.OptionsNotANumber, value, $"expected a number of {unit} or \"auto\"", "this one", $"for example {min * 2} or \"auto\"",
                    "Digits only, and no zero up front: this is a number, not a phone number.");
            }

            var amount = int.Parse(value.Text, CultureInfo.InvariantCulture);
            return amount < min || amount > max
                ? throw Fail(Output.ErrorCodes.OptionsOutOfRange, value, $"{amount} is outside {min} to {max} {unit}", "this number",
                    $"pick a number from {min} to {max}", amount > max ? "Ambitious! A little too ambitious." : "Tazel needs a bit more than that to work with.")
                : new Allocation(amount);
        }

        private Platform PlatformOf(Token value) =>
            value.Kind == TokenKind.Name && Platforms.TryParse(value.Text, out var platform)
                ? platform
                : throw Fail(Output.ErrorCodes.OptionsNotAPlatform, value, $"'{value.Text}' is not a platform", "this one",
                    Cli.Suggestions.Nearest(value.Text, Platforms.Names) is { } near
                        ? $"did you mean {near}?"
                        : $"the platforms are {string.Join(", ", Platforms.Names)}",
                    "Tazel looked on every map; there is no such place.");
    }
}
