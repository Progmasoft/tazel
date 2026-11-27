// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Progmasoft.Tazel.Packaging.Spdx;

/// <summary>An expression that is not an SPDX license expression.</summary>
public sealed class SpdxExpressionException : FormatException
{
    /// <summary>A malformed expression, and why.</summary>
    /// <param name="reason">What is wrong, in a sentence that fits after a colon.</param>
    /// <param name="position">The index of the character where the problem is.</param>
    public SpdxExpressionException(string reason, int position)
        : base($"{reason} (at character {position + 1})")
    {
        Reason = reason;
        Position = position;
    }

    /// <summary>Nothing to say.</summary>
    public SpdxExpressionException()
    {
        Reason = "";
    }

    /// <summary>A malformed expression.</summary>
    public SpdxExpressionException(string message)
        : base(message)
    {
        Reason = message;
    }

    /// <summary>A malformed expression, and what was found.</summary>
    public SpdxExpressionException(string message, Exception inner)
        : base(message, inner)
    {
        Reason = message;
    }

    /// <summary>What is wrong, without the position.</summary>
    public string Reason { get; }

    /// <summary>The index of the character where the problem is.</summary>
    public int Position { get; }
}

/// <summary>
/// Reads SPDX license expressions (SPDX 2.3, Annex D):
/// <c>or = and {OR and}</c>, <c>and = with {AND with}</c>, <c>with = simple [WITH exception] | "(" or ")"</c>, where
/// a simple expression is a license identifier, optionally followed by <c>+</c>, or a <c>LicenseRef-</c> reference.
/// AND binds tighter than OR, WITH tighter than both. The operators are read in any case. It reads the form only: whether
/// an identifier is on the SPDX License List is <see cref="SpdxLicenseList.Check"/>'s question.
/// </summary>
public static class SpdxExpressionParser
{
    /// <summary>The deepest nesting of parentheses it reads; deeper ones are refused, so no input can exhaust the stack.</summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The most licenses one expression may name. The tree of a longer one would be deep enough that walking it, to write
    /// or compare it, could exhaust the stack.
    /// </summary>
    public const int MaxLicenses = 1000;

    /// <summary>The expression <paramref name="text"/> writes.</summary>
    /// <exception cref="SpdxExpressionException">The text is not an SPDX license expression.</exception>
    public static SpdxExpression Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var reader = new Reader(Tokens(text), text.Length);
        if (reader.AtEnd)
        {
            throw new SpdxExpressionException("the expression is empty", 0);
        }

        var expression = reader.Or(0);
        if (!reader.AtEnd)
        {
            var token = reader.Current;
            throw token.Kind == Kind.Close
                ? new SpdxExpressionException("a ')' closes no '('", token.Position)
                : new SpdxExpressionException($"'{token.Text}' cannot follow; join licenses with AND or OR", token.Position);
        }

        return expression;
    }

    /// <summary>Reads <paramref name="text"/>, or says why it cannot.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out SpdxExpression? expression,
        [NotNullWhen(false)] out SpdxExpressionException? error)
    {
        try
        {
            expression = Parse(text);
            error = null;
            return true;
        }
        catch (SpdxExpressionException problem)
        {
            expression = null;
            error = problem;
            return false;
        }
    }

    private enum Kind
    {
        Word,
        Open,
        Close,
    }

    private readonly record struct Token(Kind Kind, string Text, int Position);

    private static List<Token> Tokens(string text)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsWhiteSpace(character))
            {
                index++;
            }
            else if (character is '(' or ')')
            {
                tokens.Add(new Token(character == '(' ? Kind.Open : Kind.Close, character.ToString(), index));
                index++;
            }
            else if (IsWordCharacter(character))
            {
                var start = index;
                while (index < text.Length && IsWordCharacter(text[index]))
                {
                    index++;
                }

                tokens.Add(new Token(Kind.Word, text[start..index], start));
            }
            else
            {
                throw new SpdxExpressionException(
                    $"'{character}' cannot be part of an SPDX expression; identifiers are letters, digits, '.' and '-'", index);
            }
        }

        return tokens;
    }

    private static bool IsWordCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or ':';

    private static bool IsIdString(string text) =>
        text.Length > 0 && text.AsSpan().IndexOfAnyExcept(IdCharacters) < 0;

    private static readonly System.Buffers.SearchValues<char> IdCharacters =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-");

    private static bool IsOperator(Token token, string name) =>
        token.Kind == Kind.Word && string.Equals(token.Text, name, StringComparison.OrdinalIgnoreCase);

    private sealed class Reader(List<Token> tokens, int length)
    {
        private int _at;
        private int _licenses;

        public bool AtEnd => _at == tokens.Count;

        public Token Current => tokens[_at];

        public SpdxExpression Or(int depth)
        {
            var expression = And(depth);
            while (!AtEnd && IsOperator(Current, "OR"))
            {
                Operator("OR");
                expression = new SpdxOr(expression, And(depth));
            }

            return expression;
        }

        private SpdxExpression And(int depth)
        {
            var expression = With(depth);
            while (!AtEnd && IsOperator(Current, "AND"))
            {
                Operator("AND");
                expression = new SpdxAnd(expression, With(depth));
            }

            return expression;
        }

        private void Operator(string name)
        {
            var position = Current.Position;
            _at++;
            if (AtEnd)
            {
                throw new SpdxExpressionException($"it ends with {name}; a license must follow it", position);
            }
        }

        private SpdxExpression With(int depth)
        {
            if (AtEnd)
            {
                throw new SpdxExpressionException("a license is missing", length);
            }

            var token = tokens[_at++];
            if (token.Kind == Kind.Open)
            {
                if (depth + 1 > MaxDepth)
                {
                    throw new SpdxExpressionException($"parentheses nest deeper than {MaxDepth}", token.Position);
                }

                var inner = Or(depth + 1);
                if (AtEnd || Current.Kind != Kind.Close)
                {
                    throw new SpdxExpressionException("a '(' is not closed", token.Position);
                }

                _at++;
                return inner;
            }

            if (token.Kind == Kind.Close || IsOperator(token, "AND") || IsOperator(token, "OR") || IsOperator(token, "WITH"))
            {
                throw new SpdxExpressionException($"'{token.Text}' stands where a license should", token.Position);
            }

            if (++_licenses > MaxLicenses)
            {
                throw new SpdxExpressionException($"it names more than {MaxLicenses} licenses", token.Position);
            }

            var simple = Simple(token);
            if (AtEnd || !IsOperator(Current, "WITH"))
            {
                return simple;
            }

            var with = Current.Position;
            _at++;
            if (AtEnd || Current.Kind != Kind.Word || IsOperator(Current, "AND") || IsOperator(Current, "OR") ||
                IsOperator(Current, "WITH"))
            {
                throw new SpdxExpressionException("an exception must follow WITH", with);
            }

            var exception = tokens[_at++];
            if (!IsIdString(exception.Text))
            {
                throw new SpdxExpressionException(
                    $"'{exception.Text}' is no exception identifier; it is letters, digits, '.' and '-'", exception.Position);
            }

            return new SpdxWith(simple, exception.Text);
        }

        private static SpdxExpression Simple(Token token)
        {
            var text = token.Text;
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0 || text.StartsWith("LicenseRef-", StringComparison.OrdinalIgnoreCase))
            {
                var document = colon >= 0 ? text[..colon] : null;
                var license = colon >= 0 ? text[(colon + 1)..] : text;
                if ((document is not null && !Reference(document, "DocumentRef-")) || !Reference(license, "LicenseRef-"))
                {
                    throw new SpdxExpressionException(
                        $"'{text}' is no license reference; write LicenseRef-<name> or DocumentRef-<name>:LicenseRef-<name>",
                        token.Position);
                }

                return new SpdxLicenseReference(document, license);
            }

            var orLater = text.EndsWith('+');
            var id = orLater ? text[..^1] : text;
            if (!IsIdString(id))
            {
                throw new SpdxExpressionException(
                    $"'{text}' is no license identifier; it is letters, digits, '.' and '-', with '+' only at the end",
                    token.Position);
            }

            return new SpdxLicense(id, orLater);
        }

        private static bool Reference(string text, string prefix) =>
            text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsIdString(text[prefix.Length..]);
    }
}
