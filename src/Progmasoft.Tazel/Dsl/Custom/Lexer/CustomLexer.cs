// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progmasoft.Tazel.Dsl.Custom.Lexer;

/// <summary>The kinds of <see cref="Token"/>.</summary>
public enum TokenKind
{
    /// <summary>A word.</summary>
    Name,

    /// <summary>A string in double quotes, single quotes or backticks.</summary>
    String,

    /// <summary><c>&lt;N&gt;</c> or <c>&lt;*&gt;</c>; its text is what stands between the brackets.</summary>
    Placeholder,

    /// <summary><c>:</c>.</summary>
    Colon,

    /// <summary><c>;</c>.</summary>
    Semicolon,

    /// <summary><c>(</c>.</summary>
    Open,

    /// <summary><c>)</c>.</summary>
    Close,
}

/// <summary>One word of the file, with where it starts.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Text">Its text; for a string, what stands between the quotes.</param>
/// <param name="Line">Its line, from 1.</param>
/// <param name="Column">Its column, from 1.</param>
public sealed record Token(TokenKind Kind, string Text, int Line, int Column);

/// <summary>
/// Splits the text of <c>tazel_custom.txt</c> or <c>tazel_options.txt</c>: comments go, and whitespace of any kind
/// only separates. <c>#</c> starts a comment to the end of the line, <c>&lt;# ... #&gt;</c> a comment of any length.
/// </summary>
/// <param name="text">The text.</param>
/// <param name="file">The file, for messages.</param>
public sealed class CustomLexer(string text, string file)
{
    private int _position;
    private int _line = 1;
    private int _column = 1;

    /// <summary>Every token of the text, in order; a character that starts no token is an error.</summary>
    public IReadOnlyList<Token> Tokens()
    {
        var tokens = new List<Token>();
        while (_position < text.Length)
        {
            var letter = text[_position];
            if (char.IsWhiteSpace(letter))
            {
                Advance();
            }
            else if (At("<#"))
            {
                SkipBlockComment();
            }
            else if (letter == '#')
            {
                while (_position < text.Length && text[_position] != '\n')
                {
                    Advance();
                }
            }
            else if (letter is '"' or '\'' or '`')
            {
                tokens.Add(QuotedString());
            }
            else if (letter == '<')
            {
                tokens.Add(Placeholder());
            }
            else if (IsNameLetter(letter))
            {
                tokens.Add(Name());
            }
            else
            {
                var kind = letter switch
                {
                    ':' => TokenKind.Colon,
                    ';' => TokenKind.Semicolon,
                    '(' => TokenKind.Open,
                    ')' => TokenKind.Close,
                    _ => throw Fail(Output.ErrorCodes.CharacterHasNoMeaning, $"'{letter}' has no meaning here", _line, _column, 1, "this one",
                        "this file knows names, strings in ' \" or `, ( ) : ; and <N>; put other text in quotes",
                        $"Tazel squinted at '{letter}' for a long time. It squinted back."),
                };
                tokens.Add(new Token(kind, letter.ToString(), _line, _column));
                Advance();
            }
        }

        return tokens;
    }

    private static bool IsNameLetter(char letter) => char.IsLetterOrDigit(letter) || letter is '-' or '_' or '.';

    private bool At(string what) => string.CompareOrdinal(text, _position, what, 0, what.Length) == 0;

    private CustomCommandsException Fail(
        string code, string message, int line, int column, int width, string label, string help, string joke) =>
        new(Diagnostic.At(code, text, file, new Place(line, column, width), message, label, help, joke));

    private char Advance()
    {
        var letter = text[_position++];
        if (letter == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        return letter;
    }

    private void SkipBlockComment()
    {
        var (line, column) = (_line, _column);
        Advance();
        Advance();
        while (_position < text.Length)
        {
            if (At("#>"))
            {
                Advance();
                Advance();
                return;
            }

            Advance();
        }

        throw Fail(Output.ErrorCodes.CommentNotClosed, "this comment has no closing #>", line, column, 2, "the comment opens here", "close it with #>",
            "This comment is still talking. Somebody has to say #>.");
    }

    // A string has no escapes: it ends at the next quote of its own kind, whatever comes between.
    private Token QuotedString()
    {
        var (line, column) = (_line, _column);
        var quote = Advance();
        var begin = _position;
        while (_position < text.Length && text[_position] != quote)
        {
            Advance();
        }

        if (_position == text.Length)
        {
            throw Fail(Output.ErrorCodes.StringNotClosed, $"this string has no closing {quote}", line, column, 1, "the string starts here",
                $"end it with another {quote}", "This string ran off with the rest of the file.");
        }

        var value = text[begin.._position];
        Advance();
        return new Token(TokenKind.String, value, line, column);
    }

    private Token Placeholder()
    {
        var (line, column) = (_line, _column);
        Advance();
        var begin = _position;
        while (_position < text.Length && text[_position] != '>' && !char.IsWhiteSpace(text[_position]))
        {
            Advance();
        }

        if (_position == text.Length || text[_position] != '>')
        {
            throw Fail(Output.ErrorCodes.PlaceholderNotClosed, "expected <N> or <*>", line, column, Math.Max(1, _position - begin + 1), "a placeholder starts here",
                "write <1> for the first argument, or <*> for all of them", "'<' walked in alone; its '>' never showed up.");
        }

        var value = text[begin.._position];
        Advance();
        if (!IsPlaceholder(value))
        {
            throw Fail(
                Output.ErrorCodes.PlaceholderOutOfRange,
                $"<{value}> is not an argument: <N> runs from 1 to {CustomCommandFiles.MaxArguments}, " +
                "and <*> is all of them",
                line,
                column,
                value.Length + 2,
                "this one",
                $"use <1> to <{CustomCommandFiles.MaxArguments}>, or <*>",
                value == "0" ? "Tazel counts from 1, like a person, not from 0, like a computer."
                    : "That is a lot of arguments. Tazel would rather not argue that much.");
        }

        return new Token(TokenKind.Placeholder, value, line, column);
    }

    /// <summary>Whether <paramref name="value"/> is <c>*</c> or a number from 1 to the most arguments, with no zero in front.</summary>
    private static bool IsPlaceholder(string value) =>
        value == "*" ||
        (value.Length is > 0 and <= 3 && value[0] != '0' && IsDigits(value) &&
            int.Parse(value, CultureInfo.InvariantCulture) <= CustomCommandFiles.MaxArguments);

    private static bool IsDigits(string value)
    {
        foreach (var letter in value)
        {
            if (letter is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private Token Name()
    {
        var (line, column) = (_line, _column);
        var begin = _position;
        while (_position < text.Length && IsNameLetter(text[_position]))
        {
            Advance();
        }

        return new Token(TokenKind.Name, text[begin.._position], line, column);
    }
}
