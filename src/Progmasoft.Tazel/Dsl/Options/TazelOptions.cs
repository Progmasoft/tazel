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
    public const string FileName = "tazel_options.txt";

    /// <summary>
    /// Whether what a project brings to run, custom commands and the build among it, runs inside a MicroVM.
    /// </summary>
    public bool SandboxingWithMicrovm { get; init; }

    /// <summary>Whether the MicroVM reaches the network.</summary>
    public bool MicrovmNetwork { get; init; }

    /// <summary>The memory of the MicroVM, in MiB.</summary>
    public Allocation MicrovmMemory { get; init; } = Allocation.Auto;

    /// <summary>The processors of the MicroVM.</summary>
    public Allocation MicrovmCpus { get; init; } = Allocation.Auto;

    /// <summary>The platform commands build for unless <c>--platform</c> names one; default: the host's.</summary>
    public Platform? Platform { get; init; }
}

/// <summary>What is wrong with a <c>tazel_options.txt</c>, with where it is.</summary>
public sealed class TazelOptionsException : Exception
{
    /// <summary>A problem of the file.</summary>
    public TazelOptionsException(string message)
        : base(message)
    {
    }

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
        ["sandboxing-with-microvm", "microvm-network", "microvm-memory", "microvm-cpus", "platform"];

    /// <summary>
    /// Reads the text of a <c>tazel_options.txt</c>, with the comments and strings of <c>tazel_custom.txt</c>;
    /// <paramref name="file"/> names it in messages.
    /// </summary>
    public static TazelOptions Parse(string text, string file = TazelOptions.FileName)
    {
        IReadOnlyList<Token> tokens;
        try
        {
            tokens = new CustomLexer(text, file).Tokens();
        }
        catch (CustomCommandsException error)
        {
            throw new TazelOptionsException(error.Message, error);
        }

        return new Reader(tokens, file).Options();
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
    public static (TazelOptions Options, string? SandboxFile) Effective(string directory)
    {
        var files = Find(directory).Select(file => (File: file, Options: Read(file))).ToList();
        if (files.Count == 0)
        {
            return (new TazelOptions(), null);
        }

        var asking = files.FirstOrDefault(entry => entry.Options.SandboxingWithMicrovm).File;
        return (files[0].Options with { SandboxingWithMicrovm = asking is not null }, asking);
    }

    private static TazelOptions Read(string file)
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

        return Parse(text, file);
    }

    private sealed class Reader(IReadOnlyList<Token> tokens, string file)
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
                    throw Fail(key, $"'{key.Text}' is not an option; the options are {string.Join(", ", Keys)}");
                }

                if (!seen.Add(key.Text))
                {
                    throw Fail(key, $"'{key.Text}' is set twice");
                }

                Take(TokenKind.Colon, $"':' after {key.Text}");
                if (_position >= tokens.Count || tokens[_position].Kind is not (TokenKind.Name or TokenKind.String))
                {
                    throw Fail(_position < tokens.Count ? tokens[_position] : key, $"expected a value for {key.Text}");
                }

                var value = tokens[_position++];
                options = key.Text switch
                {
                    "sandboxing-with-microvm" => options with { SandboxingWithMicrovm = Boolean(value) },
                    "microvm-network" => options with { MicrovmNetwork = Boolean(value) },
                    "microvm-memory" => options with { MicrovmMemory = Amount(value, MinMemory, MaxMemory, "MiB") },
                    "microvm-cpus" => options with { MicrovmCpus = Amount(value, 1, MaxCpus, "processors") },
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

        private TazelOptionsException Fail(Token token, string message) =>
            new($"{file}:{token.Line}:{token.Column}: {message}");

        private Token Take(TokenKind kind, string wanted)
        {
            if (_position >= tokens.Count || tokens[_position].Kind != kind)
            {
                throw Fail(_position < tokens.Count ? tokens[_position] : tokens[^1], $"expected {wanted}");
            }

            return tokens[_position++];
        }

        private bool Boolean(Token value) => value is { Kind: TokenKind.Name, Text: "true" } ? true
            : value is { Kind: TokenKind.Name, Text: "false" } ? false
            : throw Fail(value, "expected true or false");

        // "auto" is a string, in any of the three quotes; a number stands bare.
        private Allocation Amount(Token value, int min, int max, string unit)
        {
            if (value.Kind == TokenKind.String)
            {
                return value.Text == "auto" ? Allocation.Auto : throw Fail(value, "the only string here is \"auto\"");
            }

            if (value.Text.Length == 0 || value.Text[0] == '0' || value.Text.Length > 9 ||
                !value.Text.All(char.IsAsciiDigit))
            {
                throw Fail(value, $"expected a number of {unit} or \"auto\"");
            }

            var amount = int.Parse(value.Text, CultureInfo.InvariantCulture);
            return amount < min || amount > max
                ? throw Fail(value, $"{amount} is outside {min} to {max} {unit}")
                : new Allocation(amount);
        }

        private Platform PlatformOf(Token value) =>
            value.Kind == TokenKind.Name && Platforms.TryParse(value.Text, out var platform)
                ? platform
                : throw Fail(value, $"'{value.Text}' is not a platform; the platforms are {string.Join(", ", Platforms.Names)}");
    }
}
