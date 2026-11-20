// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using Progmasoft.Tazel.Dsl.Custom.Lexer;

namespace Progmasoft.Tazel.Dsl.Custom;

/// <summary>
/// A problem in a file the user wrote, with all it takes to show it well: where it is, the line it is on, what to put
/// under the mark, how to fix it, and a joke to soften the blow.
/// </summary>
/// <param name="File">The file, as it is shown.</param>
/// <param name="Line">The line, from 1.</param>
/// <param name="Column">The column, from 1.</param>
/// <param name="Width">How many characters the mark covers, at least 1.</param>
/// <param name="SourceLine">The text of the line, without its line ending; empty when unknown.</param>
/// <param name="Message">What is wrong, in one sentence.</param>
/// <param name="Label">What the mark under the line says.</param>
/// <param name="Help">How to fix it, ideally with the fixed text.</param>
/// <param name="Joke">A light line about it.</param>
public sealed record Diagnostic(
    string File, int Line, int Column, int Width, string SourceLine, string Message, string Label, string? Help, string? Joke)
{
    /// <summary>The message with its place, as one plain line: <c>file:line:column: message</c>.</summary>
    public string Plain => $"{File}:{Line}:{Column}: {Message}";

    /// <summary>A problem at <paramref name="line"/>:<paramref name="column"/> of <paramref name="text"/>.</summary>
    public static Diagnostic At(
        string text, string file, int line, int column, int width, string message, string label, string? help = null,
        string? joke = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Diagnostic(file, line, column, Math.Max(1, width), LineOf(text, line), message, label, help, joke);
    }

    /// <summary>A problem at <paramref name="token"/>, marking all of it, quotes too.</summary>
    public static Diagnostic AtToken(
        string text, string file, Token token, string message, string label, string? help = null, string? joke = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var width = token.Kind switch
        {
            TokenKind.String => token.Text.Length + 2,
            TokenKind.Placeholder => token.Text.Length + 2,
            _ => token.Text.Length,
        };
        return At(text, file, token.Line, token.Column, width, message, label, help, joke);
    }

    /// <summary>A problem just after <paramref name="token"/>, where something is missing.</summary>
    public static Diagnostic After(
        string text, string file, Token token, string message, string label, string? help = null, string? joke = null)
    {
        ArgumentNullException.ThrowIfNull(token);
        var marked = AtToken(text, file, token, message, label, help, joke);
        // A string may span lines; then the mark goes to the end of the line it starts on.
        var column = Math.Min(token.Column + marked.Width, marked.SourceLine.Length + 1);
        return marked with { Column = column, Width = 1 };
    }

    private static string LineOf(string text, int line)
    {
        var start = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0)
            {
                return "";
            }

            start = next + 1;
        }

        var end = text.IndexOf('\n', start);
        return (end < 0 ? text[start..] : text[start..end]).TrimEnd('\r').Replace('\t', ' ');
    }
}
