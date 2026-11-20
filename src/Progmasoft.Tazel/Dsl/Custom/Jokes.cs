// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Custom.Lexer;

namespace Progmasoft.Tazel.Dsl.Custom;

/// <summary>
/// The light lines of Tazel's errors. The same problem always gets the same line, so a test or a log reads the same
/// every time; which line a message gets depends only on its text.
/// </summary>
internal static class Jokes
{
    private static readonly string[] Project =
    [
        "Tazel read your project files very carefully and found exactly one thing to complain about.",
        "Builds are like soufflés: one wrong ingredient and nothing rises.",
        "Tazel stopped here rather than guess. Guessing is how bugs are born.",
        "Nothing was built, so at least nothing was broken.",
        "Every great build starts with a small fix. This is yours.",
    ];

    private static readonly string[] CommandLine =
    [
        "Tazel takes command lines literally; it is that kind of friend.",
        "Close! Computers just do not do 'close'.",
        "Typing is hard. Tazel would know; it has no fingers.",
        "Tazel wanted to say yes. The parser said no.",
    ];

    /// <summary>The line for something missing where <paramref name="kind"/> belongs.</summary>
    public static string Missing(TokenKind kind) => kind switch
    {
        TokenKind.Semicolon => "Semicolons are small, but they hold this whole file together.",
        TokenKind.Colon => "A name without its ':' is a name with nothing to say.",
        TokenKind.Open => "Every good step opens with a '('.",
        TokenKind.Close => "What opens must close. Parentheses are very strict about that.",
        TokenKind.String => "Tazel expected words in quotes and got the silent treatment.",
        _ => "Tazel expected a name here and found a stranger.",
    };

    /// <summary>The line for an error of a project file, a label or a build.</summary>
    public static string ForProject(string message) => Pick(Project, message);

    /// <summary>The line for a command line Tazel cannot take.</summary>
    public static string ForCommandLine(string message) => Pick(CommandLine, message);

    // string.GetHashCode differs between runs; this does not.
    private static string Pick(IReadOnlyList<string> lines, string message)
    {
        var hash = 0u;
        foreach (var letter in message)
        {
            hash = (hash * 31) + letter;
        }

        return lines[(int)(hash % (uint)lines.Count)];
    }
}
