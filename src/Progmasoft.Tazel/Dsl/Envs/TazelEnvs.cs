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
    public static string FileName { get; } = "tazel_envs.txt";

    /// <summary>The entries of <paramref name="text"/>, named tazel_envs.txt in messages.</summary>
    public static IReadOnlyList<EnvEntry> Parse(string text) => Parse(text, FileName);

    /// <summary>
    /// The entries of <paramref name="text"/>; <paramref name="file"/> names it in messages. Its directives are
    /// resolved for <c>context</c> (this machine, built for itself, by default), and a quoted include is
    /// found beside <c>path</c>.
    /// </summary>
    public static IReadOnlyList<EnvEntry> Parse(string text, string file) => Parse(text, file, null, null);

    /// <summary><see cref="Parse(string, string)"/>, with includes found beside <paramref name="path"/>.</summary>
    public static IReadOnlyList<EnvEntry> Parse(string text, string file, string? path) => Parse(text, file, path, null);

    /// <summary><see cref="Parse(string, string, string)"/>, resolved for <paramref name="context"/> when it is not null.</summary>
    public static IReadOnlyList<EnvEntry> Parse(string text, string file, string? path, Configuration.ConfigurationContext? context)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Configuration.ConfigurationResolver.Resolve(path ?? file, file, text, Configuration.ConfigurationFamily.Envs,
            context ?? Configuration.ConfigurationContext.ForThisMachine().ForItself());
        return new Reader(tokens, text, file).Entries();
    }

    private sealed class Reader(IReadOnlyList<Token> tokens, string text, string file)
    {
        private int _position;

        public List<EnvEntry> Entries()
        {
            var entries = new List<EnvEntry>();
            var names = new Dictionary<string, Token>(StringComparer.Ordinal);
            while (_position < tokens.Count)
            {
                var name = Take(TokenKind.Name, "the name of a value");
                if (names.TryGetValue(name.Text, out var first))
                {
                    throw Fail(Output.ErrorCodes.EnvsTwice, name, $"'{name.Text}' is set twice", "the second one",
                        $"keep one of them; the first is on line {first.Line}",
                        "Two values, one name. Tazel is not going to flip a coin.");
                }

                names[name.Text] = name;
                Take(TokenKind.Colon, $"':' after {name.Text}");
                var value = Value(name);
                // The last value may end the file without a ';'.
                if (Peek is not null)
                {
                    Take(TokenKind.Semicolon, $"';' after the value of {name.Text}");
                }

                entries.Add(new EnvEntry(name.Text, value, name.Line));
            }

            return entries;
        }

        private Token? Peek => _position < tokens.Count ? tokens[_position] : null;

        /// <summary>A string, s("line") or ("program" "argument").</summary>
        private EnvValue Value(Token name)
        {
            if (Peek is { Kind: TokenKind.String } literal)
            {
                _position++;
                return new EnvValue.Text(literal.Text);
            }

            if (Peek is { Kind: TokenKind.Name, Text: "s" })
            {
                _position++;
                return Shell(name);
            }

            if (Peek is { Kind: TokenKind.Open })
            {
                return Program(name);
            }

            if (Peek is { } stranger)
            {
                throw Fail(Output.ErrorCodes.EnvsNotAValue, stranger, $"expected the value of {name.Text}", "this is not a value",
                    $"a value is a string, a program or a shell line: {name.Text}: '0.1.0'; or {name.Text}: (\"git\" \"describe\"); or {name.Text}: s(`date`);",
                    stranger.Kind == TokenKind.Name ? $"'{stranger.Text}' without quotes is a name, and names are not values. Quotes, please!"
                        : "Tazel expected a value and got something else entirely.");
            }

            throw new CustomCommandsException(Diagnostic.After(Output.ErrorCodes.EnvsNoValue, text, file, tokens[_position - 1],
                $"expected the value of {name.Text}", "the value goes here", $"for example {name.Text}: '0.1.0';",
                "A name, a colon, and then... silence."));
        }

        private EnvValue Shell(Token name)
        {
            var open = Take(TokenKind.Open, "'(' after s");
            var strings = Strings();
            Take(TokenKind.Close, "')' after the line of s(...)");
            return strings.Count == 1
                ? new EnvValue.Shell(strings[0])
                : throw Fail(Output.ErrorCodes.EnvsShellOneLine, open, "s(...) takes one string, the whole line", "this s(...)",
                    $"write one line: {name.Text}: s(`echo $HOME`);", "One shell, one line. It is not a group chat.");
        }

        private EnvValue Program(Token name)
        {
            var open = Take(TokenKind.Open, "'('");
            var strings = Strings();
            Take(TokenKind.Close, "')' after the strings of a program");
            return strings.Count > 0
                ? new EnvValue.Program(strings)
                : throw Fail(Output.ErrorCodes.EnvsEmptyProgram, open, "a program names at least itself", "these parentheses are empty",
                    $"name the program first: {name.Text}: (\"rustc\" \"--version\");",
                    "Empty parentheses: a program that forgot its own name.");
        }

        private List<string> Strings()
        {
            var strings = new List<string>();
            while (Peek is { Kind: TokenKind.String } token)
            {
                _position++;
                strings.Add(token.Text);
            }

            return Peek is { Kind: TokenKind.Placeholder } placeholder
                ? throw Fail(Output.ErrorCodes.EnvsPlaceholder, placeholder, "a value of tazel_envs.txt takes no <N> or <*>",
                    "this placeholder", "write the argument itself, in quotes", "Nobody passes arguments to a file. It just sits there.")
                : strings;
        }

        private Token Take(TokenKind kind, string wanted)
        {
            if (Peek is { } token && token.Kind == kind)
            {
                _position++;
                return token;
            }

            var (code, label, help) = Missing(kind);
            if (_position > 0 && (Peek is null || kind is TokenKind.Semicolon or TokenKind.Colon))
            {
                throw new CustomCommandsException(Diagnostic.After(code, text, file, tokens[_position - 1], $"expected {wanted}",
                    label, help, Jokes.Missing(kind)));
            }

            throw Fail(code, Peek!, $"expected {wanted}", "not this", help, Jokes.Missing(kind));
        }

        /// <summary>The code, the label of the gap, and the help for something missing.</summary>
        private static (string Code, string Label, string Help) Missing(TokenKind kind) => kind switch
        {
            TokenKind.Semicolon => (Output.ErrorCodes.EnvsNoSemicolon, "a ';' goes here", "end every value with ';': example-version: '0.1.0';"),
            TokenKind.Colon => (Output.ErrorCodes.EnvsNoColon, "a ':' goes here", "a name and its value are joined by ':': example-version: '0.1.0';"),
            TokenKind.Close => (Output.ErrorCodes.EnvsNoClose, "a ')' goes here", "close it with ')'"),
            TokenKind.Open => (Output.ErrorCodes.EnvsNoOpen, "something goes here", "open it with '(': s(`date`)"),
            _ => (Output.ErrorCodes.EnvsUnexpected, "something goes here", "an entry is: name: value;"),
        };

        private CustomCommandsException Fail(string code, Token token, string message, string label, string help, string joke) =>
            new(Diagnostic.AtToken(code, text, file, token, message, label, help, joke));
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

    /// <summary>
    /// The values of the module at <paramref name="root"/>, or null when it has no <c>tazel_envs.txt</c>; its
    /// conditions see <c>target</c> as the platform built for (this machine's when null).
    /// </summary>
    public static ModuleEnvironment? Load(string root, string shown) => Load(root, shown, null);

    /// <summary><see cref="Load(string, string)"/>, its conditions read for <paramref name="target"/>, else this machine.</summary>
    public static ModuleEnvironment? Load(string root, string shown, Common.Platform? target)
    {
        // Join, not Combine: the file stays below the root whatever the name holds.
        var file = Path.Join(root, TazelEnvsFile.FileName);
        if (!File.Exists(file))
        {
            return null;
        }

        var context = Configuration.ConfigurationContext.ForThisMachine();
        context = context with { Target = Configuration.SystemFacts.Of(target ?? Driver.Host.Platform) };
        return new ModuleEnvironment(root, shown, TazelEnvsFile.Parse(File.ReadAllText(file), shown, file, context));
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
