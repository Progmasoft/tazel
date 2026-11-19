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
    public static IReadOnlyList<CustomCommand> Parse(string text, string file = CustomCommandFiles.FileName) =>
        new Reader(new CustomLexer(text, file).Tokens(), file).Commands();

    private sealed class Reader(IReadOnlyList<Token> tokens, string file)
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
                    throw Fail(name, $"'{CustomCommandFiles.NameEnd}' ends a name on the command line, so a name cannot hold it");
                }

                if (words.Count > CustomCommandFiles.MaxArguments)
                {
                    throw Fail(name, $"a name holds at most {CustomCommandFiles.MaxArguments} words");
                }

                Take(TokenKind.Colon, "':' after the name of a command");
                var steps = new List<CustomStep>();
                do
                {
                    steps.Add(Step());
                }
                while (Peek() is { Kind: not TokenKind.Semicolon });

                Take(TokenKind.Semicolon, "';' at the end of a command");
                var joined = string.Join(' ', words);
                if (commands.ContainsKey(joined))
                {
                    throw Fail(name, $"the command '{joined}' is defined twice");
                }

                var command = new CustomCommand(joined, steps);
                if (words.Count + command.Required > CustomCommandFiles.MaxArguments)
                {
                    throw Fail(
                        name,
                        $"'{joined}' needs {command.Required} arguments, and its name leaves room for " +
                        $"{CustomCommandFiles.MaxArguments - words.Count}");
                }

                commands[joined] = command;
                order.Add(command);
            }

            return order;
        }

        private Token? Peek() => _position < tokens.Count ? tokens[_position] : null;

        private CustomCommandsException Fail(Token? token, string message) =>
            new($"{file}:{(token is null ? "" : $"{token.Line}:{token.Column}: ")}{message}");

        private Token Take(TokenKind kind, string wanted)
        {
            var token = Peek();
            if (token?.Kind != kind)
            {
                throw Fail(token ?? tokens.LastOrDefault(), $"expected {wanted}");
            }

            _position++;
            return token;
        }

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
                    throw Fail(placeholder, "s(...) takes no arguments; (...) passes them");
                }

                if (values.Count != 1)
                {
                    throw Fail(open, "s(...) takes one string, the whole command line: s(\"npm dev\")");
                }

                return new CustomStep.Shell(values[0].Text);
            }

            if (values.Count == 0)
            {
                throw Fail(open, "a command names at least a program");
            }

            if (values[0].Kind != TokenKind.String)
            {
                throw Fail(values[0], "a command starts with the name of its program");
            }

            return new CustomStep.Program(values.Select(ArgumentOf).ToList());
        }
    }
}
