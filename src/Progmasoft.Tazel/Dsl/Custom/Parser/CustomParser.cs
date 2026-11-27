// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progmasoft.Tazel.Dsl.Custom.Lexer;

namespace Progmasoft.Tazel.Dsl.Custom.Parser;

/// <summary>Reads the commands of a <c>tazel_custom.txt</c>.</summary>
public static class CustomParser
{
    /// <summary>The commands of the text of a <c>tazel_custom.txt</c>, in the order they are written.</summary>
    public static IReadOnlyList<CustomCommand> Parse(string text) => Parse(text, CustomCommandFiles.FileName);

    /// <summary>
    /// The commands of <paramref name="text"/>; <paramref name="file"/> names it in messages. Its directives are
    /// resolved for <paramref name="context"/> (this machine, built for itself, by default), and a quoted include is
    /// found beside <paramref name="path"/> (<paramref name="file"/> from the working directory by default).
    /// </summary>
    public static IReadOnlyList<CustomCommand> Parse(string text, string file, string? path = null, Configuration.ConfigurationContext? context = null) =>
        new Reader(Configuration.ConfigurationResolver.Resolve(path ?? file, file, text, Configuration.ConfigurationFamily.Custom,
            context ?? Configuration.ConfigurationContext.ForThisMachine().ForItself()), file, text).Commands();

    private sealed class Reader(IReadOnlyList<Token> tokens, string file, string text)
    {
        private int _position;

        public IReadOnlyList<CustomCommand> Commands()
        {
            var commands = new Dictionary<string, CustomCommand>(StringComparer.Ordinal);
            var order = new List<CustomCommand>();
            while (Peek() is not null)
            {
                var name = Take(TokenKind.Name, "the name of a command");
                var words = new List<string> { name.Text };
                while (Peek()?.Kind == TokenKind.Name)
                {
                    words.Add(Take(TokenKind.Name, "a word").Text);
                }

                if (words.Contains(CustomCommandFiles.NameEnd))
                {
                    var end = words.IndexOf(CustomCommandFiles.NameEnd);
                    throw Fail(Output.ErrorCodes.CustomNameEnd, tokens[_position - words.Count + end],
                        $"'{CustomCommandFiles.NameEnd}' ends a name on the command line, so a name cannot hold it",
                        "this word", "pick another word for this part of the name",
                        $"'{CustomCommandFiles.NameEnd}' is the bouncer at the end of a name; it cannot be a guest too.");
                }

                if (words.Count > CustomCommandFiles.MaxArguments)
                {
                    throw Fail(Output.ErrorCodes.CustomNameTooLong, name, $"a name holds at most {CustomCommandFiles.MaxArguments} words", "this name",
                        $"shorten it to {CustomCommandFiles.MaxArguments} words or fewer", "That is not a name, that is a sentence.");
                }

                Take(TokenKind.Colon, "':' after the name of a command");
                var steps = new List<CustomStep>();
                do
                {
                    steps.Add(Step());
                }
                while (StartsAStep());

                // The last command may end the file without a ';'; between two commands it is needed.
                if (Peek() is not null)
                {
                    Take(TokenKind.Semicolon, "';' at the end of a command");
                }

                var joined = string.Join(' ', words);
                if (commands.ContainsKey(joined))
                {
                    throw Fail(Output.ErrorCodes.CustomTwice, name, $"the command '{joined}' is defined twice", "the second one",
                        "rename one of them, or put all their steps in one", "Two commands, one name: Tazel refuses to pick a favorite.");
                }

                var command = new CustomCommand(joined, steps);
                if (words.Count + command.Required > CustomCommandFiles.MaxArguments)
                {
                    throw Fail(
                        Output.ErrorCodes.CustomTooManyArguments,
                        name,
                        $"'{joined}' needs {command.Required} arguments, and its name leaves room for " +
                        $"{CustomCommandFiles.MaxArguments - words.Count}",
                        "this name",
                        "use fewer <N> in its steps, or fewer words in its name",
                        "The arguments do not all fit in the car.");
                }

                commands[joined] = command;
                order.Add(command);
            }

            return order;
        }

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private CustomCommandsException Fail(string code, Token token, string message, string label, string? help, string? joke) =>
            new(Diagnostic.AtToken(code, text, file, token, message, label, help, joke));

        private Token Take(TokenKind kind, string wanted)
        {
            var token = Peek();
            if (token?.Kind == kind)
            {
                _position++;
                return token;
            }

            var previous = _position > 0 ? tokens[_position - 1] : null;
            var code = kind switch
            {
                TokenKind.Semicolon => Output.ErrorCodes.CustomNoSemicolon,
                TokenKind.Colon => Output.ErrorCodes.CustomNoColon,
                TokenKind.Open => Output.ErrorCodes.CustomNoOpen,
                TokenKind.Close => Output.ErrorCodes.CustomNoClose,
                _ => Output.ErrorCodes.CustomUnexpected,
            };
            var help = kind switch
            {
                TokenKind.Semicolon => "end every command with ';': build: (\"tazel\" \"build\" \"//...\");",
                TokenKind.Colon => "a name and its steps are joined by ':': build: (\"tazel\" \"build\");",
                TokenKind.Open => "a step is (\"program\" \"argument\") or s(\"a shell line\")",
                TokenKind.Close => "close the step with ')'",
                _ => "a command is: name: (\"program\" \"argument\");",
            };
            // Missing at the end of the file, or a ';' or ':' that belongs right after the last thing: mark the gap.
            if (previous is not null && (token is null || kind is TokenKind.Semicolon or TokenKind.Colon))
            {
                throw new CustomCommandsException(Diagnostic.After(code, text, file, previous, $"expected {wanted}",
                    $"{Describe(kind)} goes here", help, Jokes.Missing(kind)));
            }

            if (token is null)
            {
                throw new CustomCommandsException(Diagnostic.At(code, text, file, new Place(1, 1, 1), $"expected {wanted}",
                    "the file is empty here", help, Jokes.Missing(kind)));
            }

            throw Fail(code, token, $"expected {wanted}", $"{Describe(kind)} goes here, not this", help, Jokes.Missing(kind));
        }

        private static string Describe(TokenKind kind) => kind switch
        {
            TokenKind.Semicolon => "a ';'",
            TokenKind.Colon => "a ':'",
            TokenKind.Open => "a '('",
            TokenKind.Close => "a ')'",
            TokenKind.String => "a string",
            _ => "a name",
        };

        private List<Token> Values()
        {
            var values = new List<Token>();
            while (Peek() is { Kind: TokenKind.String or TokenKind.Placeholder } token)
            {
                values.Add(Take(token.Kind, "a string"));
            }

            return values;
        }

        private static CustomArgument ArgumentOf(Token token) =>
            token.Kind == TokenKind.String ? new CustomArgument.Text(token.Text)
            : token.Text == "*" ? new CustomArgument.All()
            : new CustomArgument.Position(int.Parse(token.Text, CultureInfo.InvariantCulture));

        /// <summary>Whether a step comes next: a '(', or the s of an s(...).</summary>
        private bool StartsAStep() =>
            Peek() is { Kind: TokenKind.Open } ||
            (Peek() is { Kind: TokenKind.Name, Text: "s" } && _position + 1 < tokens.Count && tokens[_position + 1].Kind == TokenKind.Open);

        private CustomStep Step()
        {
            var shell = Peek() is { Kind: TokenKind.Name, Text: "s" };
            if (shell)
            {
                _position++;
            }

            var open = Take(TokenKind.Open, "'(' of a command");
            var values = Values();
            Take(TokenKind.Close, "')' after the strings of a command");
            if (shell)
            {
                var placeholder = values.FirstOrDefault(value => value.Kind == TokenKind.Placeholder);
                if (placeholder is not null)
                {
                    throw Fail(Output.ErrorCodes.CustomShellArguments, placeholder, "s(...) takes no arguments; (...) passes them", "this one",
                        "pass arguments with (\"program\" <1>), or write the whole line in s(...)",
                        "The shell has its own ideas; Tazel does not hand it arguments too.");
                }

                if (values.Count != 1)
                {
                    throw Fail(Output.ErrorCodes.CustomShellOneLine, open, "s(...) takes one string, the whole command line", "this s(...)",
                        "write one line: s(\"npm run dev\")", "One shell, one line. It is not a group chat.");
                }

                return new CustomStep.Shell(values[0].Text);
            }

            if (values.Count == 0)
            {
                throw Fail(Output.ErrorCodes.CustomEmpty, open, "a command names at least a program", "these parentheses are empty",
                    "name the program first: (\"npm\" \"run\" \"dev\")", "Empty parentheses: a command with nothing to say.");
            }

            if (values[0].Kind != TokenKind.String)
            {
                throw Fail(Output.ErrorCodes.CustomNoProgram, values[0], "a command starts with the name of its program", "this is not a program",
                    "put the program first: (\"echo\" <1>)", "Tazel does not run surprises; it wants to know the program.");
            }

            return new CustomStep.Program(values.Select(ArgumentOf).ToList());
        }
    }
}
