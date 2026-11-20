// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Lexer;
using Progmasoft.Tazel.Dsl.Environment;

namespace Progmasoft.Tazel.Dsl.Envs;

/// <summary>A value of <c>tazel_envs.txt</c>: a string, the output of a program, or the output of a shell line.</summary>
public abstract record EnvValue
{
    /// <summary>A string, as written.</summary>
    /// <param name="Value">The string.</param>
    public sealed record Text(string Value) : EnvValue;

    /// <summary><c>("program" "argument")</c>: a program started directly; the value is what it prints.</summary>
    /// <param name="Arguments">The program and its arguments, each string one argument.</param>
    public sealed record Program(IReadOnlyList<string> Arguments) : EnvValue;

    /// <summary><c>s("line")</c>: a line for /bin/sh, or cmd.exe on Windows; the value is what it prints.</summary>
    /// <param name="Line">The line.</param>
    public sealed record Shell(string Line) : EnvValue;
}

/// <summary>One entry of <c>tazel_envs.txt</c>, with where it is.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Value">Its value.</param>
/// <param name="Line">The line it is on.</param>
public sealed record EnvEntry(string Name, EnvValue Value, int Line);

/// <summary>
/// Reads <c>tazel_envs.txt</c> with the lexer of <c>tazel_custom.txt</c> and <c>tazel_options.txt</c>: the same comments
/// and strings, <c>name: value;</c> one after another, each value ended by <c>;</c>.
/// </summary>
public static class TazelEnvsFile
{
    /// <summary>The file, beside the MODULE.csx of a module.</summary>
    public const string FileName = "tazel_envs.txt";

    /// <summary>The entries of <paramref name="text"/>; <paramref name="file"/> names it in messages.</summary>
    public static IReadOnlyList<EnvEntry> Parse(string text, string file = FileName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new CustomLexer(text, file).Tokens();
        var entries = new List<EnvEntry>();
        var names = new Dictionary<string, Token>(StringComparer.Ordinal);
        var i = 0;
        while (i < tokens.Count)
        {
            var name = Take(TokenKind.Name, "the name of a value");
            if (names.TryGetValue(name.Text, out var first))
            {
                throw Fail(name, $"'{name.Text}' is set twice", "the second one",
                    $"keep one of them; the first is on line {first.Line}",
                    "Two values, one name. Tazel is not going to flip a coin.");
            }

            names[name.Text] = name;
            Take(TokenKind.Colon, $"':' after {name.Text}");
            EnvValue value;
            if (Peek() is { Kind: TokenKind.String } literal)
            {
                i++;
                value = new EnvValue.Text(literal.Text);
            }
            else if (Peek() is { Kind: TokenKind.Name, Text: "s" })
            {
                i++;
                var open = Take(TokenKind.Open, "'(' after s");
                var strings = Strings();
                Take(TokenKind.Close, "')' after the line of s(...)");
                value = strings.Count == 1
                    ? new EnvValue.Shell(strings[0])
                    : throw Fail(open, "s(...) takes one string, the whole line", "this s(...)",
                        $"write one line: {name.Text}: s(`echo $HOME`);", "One shell, one line. It is not a group chat.");
            }
            else if (Peek() is { Kind: TokenKind.Open })
            {
                var open = Take(TokenKind.Open, "'('");
                var strings = Strings();
                Take(TokenKind.Close, "')' after the strings of a program");
                value = strings.Count > 0
                    ? new EnvValue.Program(strings)
                    : throw Fail(open, "a program names at least itself", "these parentheses are empty",
                        $"name the program first: {name.Text}: (\"rustc\" \"--version\");",
                        "Empty parentheses: a program that forgot its own name.");
            }
            else if (Peek() is { } stranger)
            {
                throw Fail(stranger, $"expected the value of {name.Text}", "this is not a value",
                    $"a value is a string, a program or a shell line: {name.Text}: '0.1.0'; or {name.Text}: (\"git\" \"describe\"); or {name.Text}: s(`date`);",
                    stranger.Kind == TokenKind.Name ? $"'{stranger.Text}' without quotes is a name, and names are not values. Quotes, please!"
                        : "Tazel expected a value and got something else entirely.");
            }
            else
            {
                throw new CustomCommandsException(Diagnostic.After(text, file, tokens[i - 1], $"expected the value of {name.Text}",
                    "the value goes here", $"for example {name.Text}: '0.1.0';", "A name, a colon, and then... silence."));
            }

            Take(TokenKind.Semicolon, $"';' after the value of {name.Text}");
            entries.Add(new EnvEntry(name.Text, value, name.Line));
        }

        return entries;

        Token? Peek() => i < tokens.Count ? tokens[i] : null;

        Token Take(TokenKind kind, string wanted)
        {
            if (Peek() is { } token && token.Kind == kind)
            {
                i++;
                return token;
            }

            var help = kind switch
            {
                TokenKind.Semicolon => "end every value with ';': example-version: '0.1.0';",
                TokenKind.Colon => "a name and its value are joined by ':': example-version: '0.1.0';",
                TokenKind.Close => "close it with ')'",
                TokenKind.Open => "open it with '(': s(`date`)",
                _ => "an entry is: name: value;",
            };
            if (i > 0 && (Peek() is null || kind is TokenKind.Semicolon or TokenKind.Colon))
            {
                throw new CustomCommandsException(Diagnostic.After(text, file, tokens[i - 1], $"expected {wanted}",
                    kind switch
                    {
                        TokenKind.Semicolon => "a ';' goes here",
                        TokenKind.Colon => "a ':' goes here",
                        TokenKind.Close => "a ')' goes here",
                        _ => "something goes here",
                    },
                    help, Jokes.Missing(kind)));
            }

            throw Fail(Peek()!, $"expected {wanted}", "not this", help, Jokes.Missing(kind));
        }

        List<string> Strings()
        {
            var strings = new List<string>();
            while (Peek() is { Kind: TokenKind.String } token)
            {
                i++;
                strings.Add(token.Text);
            }

            if (Peek() is { Kind: TokenKind.Placeholder } placeholder)
            {
                throw Fail(placeholder, "a value of tazel_envs.txt takes no <N> or <*>", "this placeholder",
                    "write the argument itself, in quotes", "Nobody passes arguments to a file. It just sits there.");
            }

            return strings;
        }

        CustomCommandsException Fail(Token token, string message, string label, string help, string joke) =>
            new(Diagnostic.AtToken(text, file, token, message, label, help, joke));
    }
}

/// <summary>
/// The values of one module's <c>tazel_envs.txt</c>, for <see cref="GetEnvironment.From"/>. A program or shell line runs
/// when its value is first asked for, once per command, with Tazel's environment in the root of the module; its output,
/// less the line endings at its end, is the value. A program that fails stops the project file that asked.
/// </summary>
internal sealed class ModuleEnvironment : IEnvironmentValues
{
    private readonly Dictionary<string, EnvEntry> _entries;
    private readonly ConcurrentDictionary<string, Lazy<string>> _values = new(StringComparer.Ordinal);
    private readonly string _root;
    private readonly string _shown;

    private ModuleEnvironment(string root, string shown, IReadOnlyList<EnvEntry> entries)
    {
        _root = root;
        _shown = shown;
        _entries = entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);
    }

    /// <summary>The values of the module at <paramref name="root"/>, or null when it has no <c>tazel_envs.txt</c>.</summary>
    public static ModuleEnvironment? Load(string root, string shown)
    {
        var file = Path.Combine(root, TazelEnvsFile.FileName);
        return File.Exists(file) ? new ModuleEnvironment(root, shown, TazelEnvsFile.Parse(File.ReadAllText(file), shown)) : null;
    }

    public string? Get(string name)
    {
        if (!_entries.TryGetValue(name, out var entry))
        {
            return null;
        }

        return _values.GetOrAdd(name, _ => new Lazy<string>(() => Evaluate(entry), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;
    }

    private string Evaluate(EnvEntry entry)
    {
        IReadOnlyList<string> command;
        switch (entry.Value)
        {
            case EnvValue.Text text:
                return text.Value;
            case EnvValue.Shell shell:
                command = OperatingSystem.IsWindows()
                    ? [System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d", "/s", "/c", $"\"{shell.Line}\""]
                    : ["/bin/sh", "-c", shell.Line];
                break;
            default:
                var program = ((EnvValue.Program)entry.Value).Arguments;
                var found = Host.Which(program[0]) ?? throw new DslException(
                    $"{_shown}:{entry.Line}: the program '{program[0]}' of {entry.Name} is not found");
                command = [found, .. program.Skip(1)];
                break;
        }

        var result = Processes.CaptureAsync(command, _root, Processes.Inherited(), CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new DslException(
                $"{_shown}:{entry.Line}: {entry.Name} ran {command[0]}, which failed with {result.ExitCode}: {result.Errors.Trim()}");
        }

        return result.Output.TrimEnd('\r', '\n');
    }
}
