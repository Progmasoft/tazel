// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Output;

namespace Progmasoft.Tazel.Project.Ignore;

/// <summary>What is wrong with a <c>.tazelignore</c>, with where it is when it has a place.</summary>
public sealed class IgnoreException : Exception
{
    /// <summary>A problem with no place in a file.</summary>
    public IgnoreException(string message)
        : base(message)
    {
    }

    /// <summary>A problem at a line of a file.</summary>
    public IgnoreException(Diagnostic diagnostic)
        : base(diagnostic?.Plain)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>Nothing to say.</summary>
    public IgnoreException()
    {
    }

    /// <summary>What is wrong, and why.</summary>
    public IgnoreException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Where the problem is, when it is in a file.</summary>
    public Diagnostic? Diagnostic { get; }
}

/// <summary>One rule: a gitignore pattern or a <c>regex:</c>, compiled, with whether it ignores or keeps.</summary>
/// <param name="Text">The line as written, for messages.</param>
/// <param name="Pattern">What a path relative to the rule's directory must match, whole.</param>
/// <param name="Negated">Whether it keeps what an earlier rule ignored (<c>!</c>).</param>
/// <param name="DirectoryOnly">Whether it matches directories only (a trailing <c>/</c>).</param>
/// <param name="Place">The file and line it was read from.</param>
internal sealed record IgnoreRule(string Text, Regex Pattern, bool Negated, bool DirectoryOnly, string Place);

/// <summary>
/// The rules of one <c>.tazelignore</c>, with the files it includes and imports in their places. Lines are gitignore
/// rules, except these, which no gitignore line can be without a backslash before it:
/// <c>pragma:once</c>, <c>import:&lt;file&gt;</c>, <c>import:"file"</c>, <c>include:…</c>, <c>regex:…</c> and
/// <c>!regex:…</c>. A rule that means a name starting with one of these words is written <c>\pragma:…</c>, as gitignore
/// itself reads it.
/// </summary>
internal static class IgnoreFile
{
    /// <summary>The name of the file.</summary>
    public static string FileName { get; } = ".tazelignore";

    /// <summary>How long one <c>regex:</c> may take on one path.</summary>
    public static TimeSpan RegexTimeout { get; } = TimeSpan.FromSeconds(1);

    private static readonly string[] Reserved = ["pragma:", "import:", "include:", "regex:", "!regex:"];

    /// <summary>Whether a gitignore line starts with a word Tazel reads as a directive, so it needs a backslash.</summary>
    public static bool NeedsEscape(string line) => Reserved.Any(word => line.StartsWith(word, StringComparison.Ordinal));

    /// <summary>
    /// The rules of <paramref name="file"/>, read from <paramref name="text"/>, with includes followed; a problem is an
    /// <see cref="IgnoreException"/> at its line.
    /// </summary>
    public static IReadOnlyList<IgnoreRule> Read(string file, string text) => new Reader().Root(file, text);

    private sealed class Reader
    {
        private readonly List<IgnoreRule> _rules = [];
        private readonly HashSet<string> _once = new(StringComparer.Ordinal);
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly List<string> _chain = [];

        public IReadOnlyList<IgnoreRule> Root(string file, string text)
        {
            Read(Path.GetFullPath(file), text);
            return _rules;
        }

        private void Read(string file, string text)
        {
            var canonical = ConfigurationResolver.Canonical(file);
            _chain.Add(canonical);
            _loaded.Add(canonical);
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            var first = true;
            for (var number = 1; number <= lines.Length; number++)
            {
                var line = lines[number - 1];
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line == "pragma:once")
                {
                    if (!first)
                    {
                        throw Fail(file, text, number, "pragma:once must come first in its file", "move it above every rule");
                    }

                    _once.Add(canonical);
                }
                else if (line.StartsWith("pragma:", StringComparison.Ordinal))
                {
                    throw Fail(file, text, number, $"'{line}' is not a pragma; pragma:once is the only one",
                        $"for a name that starts with pragma:, write \\{line}");
                }
                else if (line.StartsWith("import:", StringComparison.Ordinal) || line.StartsWith("include:", StringComparison.Ordinal))
                {
                    Include(file, text, number, line);
                }
                else
                {
                    _rules.Add(Rule(file, text, number, line));
                }

                first = false;
            }

            _chain.RemoveAt(_chain.Count - 1);
        }

        private void Include(string file, string text, int number, string line)
        {
            var import = line.StartsWith("import:", StringComparison.Ordinal);
            var argument = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..];
            var (path, global) = argument switch
            {
                ['<', .. var inner, '>'] when inner.Length > 0 => (new string(inner), true),
                ['"', .. var inner, '"'] when inner.Length > 0 => (new string(inner), false),
                _ => throw Fail(file, text, number, $"'{line}' needs one path, in quotes or in angle brackets",
                    "write include:\"file\" for a file beside this one, or include:<file> for one of TAZEL_HOME's Include/Ignore"),
            };
            if (ConfigurationResolver.Refusal(path, global) is { } refusal)
            {
                throw Fail(file, text, number, $"'{path}' {refusal}", null);
            }

            var target = global ? Global(file, text, number, path) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path));
            if (!File.Exists(target))
            {
                throw Fail(file, text, number, $"{path} is not a file{(global ? " of TAZEL_HOME's Include/Ignore" : " beside this one")}", null);
            }

            var canonical = ConfigurationResolver.Canonical(target);
            if (_once.Contains(canonical) || (import && _loaded.Contains(canonical)))
            {
                return;
            }

            if (_chain.Contains(canonical))
            {
                throw Fail(file, text, number, $"{path} includes itself: {string.Join(" -> ", _chain.Select(Path.GetFileName))} -> {path}",
                    "read it once with import:, or begin it with pragma:once");
            }

            if (_chain.Count >= ConfigurationParser.MaxDepth || _loaded.Count >= ConfigurationResolver.MaxFiles)
            {
                throw Fail(file, text, number, $"includes nest more than {ConfigurationParser.MaxDepth} deep, or read more than {ConfigurationResolver.MaxFiles} files", null);
            }

            if (import)
            {
                _once.Add(canonical);
            }

            Read(target, File.ReadAllText(target));
        }

        // A file below TAZEL_HOME/Include/Ignore, which no link may leave.
        private static string Global(string file, string text, int number, string path)
        {
            string root;
            try
            {
                root = ConfigurationResolver.Canonical(ConfigurationResolver.IncludeRoot(ConfigurationFamily.Ignore));
            }
            catch (ConfigurationHomeException error)
            {
                throw Fail(file, text, number, error.Message, null);
            }

            var full = Path.GetFullPath(Path.Combine(root, path));
            if (File.Exists(full) && !ConfigurationResolver.Canonical(full).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw Fail(file, text, number, $"'{path}' leaves {root} through a link", null);
            }

            return full;
        }
    }

    // One rule line: regex:, !regex:, or a gitignore pattern.
    private static IgnoreRule Rule(string file, string text, int number, string line)
    {
        var place = $"{file}:{number}";
        foreach (var (prefix, negated) in new[] { ("regex:", false), ("!regex:", true) })
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var expression = line[prefix.Length..];
                try
                {
                    return new IgnoreRule(line, new Regex(expression, RegexOptions.CultureInvariant, RegexTimeout), negated, false, place);
                }
                catch (ArgumentException error)
                {
                    throw Fail(file, text, number, $"'{expression}' is not a regular expression: {error.Message}", "fix the expression; it is .NET's Regex");
                }
            }
        }

        var glob = Gitignore.Parse(line);
        return glob is null
            ? new IgnoreRule(line, new Regex("(?!)", RegexOptions.None, RegexTimeout), false, false, place)
            : new IgnoreRule(line, glob.Value.Pattern, glob.Value.Negated, glob.Value.DirectoryOnly, place);
    }

    private static IgnoreException Fail(string file, string text, int number, string message, string? help) =>
        new(Diagnostic.At(ErrorCodes.IgnoreInvalid, text, file, new Place(number, 1, Math.Max(1, LineLength(text, number))), message,
            "this line", help, "Tazel ignores many things, but not a line it cannot read."));

    private static int LineLength(string text, int number) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ElementAtOrDefault(number - 1)?.Length ?? 1;
}

/// <summary>gitignore patterns, compiled to regular expressions that match a path relative to the file's directory.</summary>
internal static class Gitignore
{
    /// <summary>
    /// The pattern of <paramref name="line"/>, or null for a line that is no rule (only spaces, or a lone <c>!</c>).
    /// Trailing spaces go unless a backslash keeps them; <c>!</c> negates, <c>\!</c> and <c>\#</c> are literal; a
    /// trailing <c>/</c> matches directories only; a <c>/</c> elsewhere anchors the pattern to the file's directory,
    /// and without one it matches at any depth. <c>*</c>, <c>?</c> and <c>[...]</c> stop at <c>/</c>; <c>**</c> as a
    /// whole part matches any number of directories.
    /// </summary>
    public static (Regex Pattern, bool Negated, bool DirectoryOnly)? Parse(string line)
    {
        var pattern = TrimTrailingSpaces(line);
        var negated = pattern.StartsWith('!');
        if (negated)
        {
            pattern = pattern[1..];
        }

        var directoryOnly = pattern.EndsWith('/') && !pattern.EndsWith("\\/", StringComparison.Ordinal);
        if (directoryOnly)
        {
            pattern = pattern.TrimEnd('/');
        }

        if (pattern.Length == 0)
        {
            return null;
        }

        var anchored = pattern.Contains('/', StringComparison.Ordinal);
        pattern = pattern.TrimStart('/');
        var body = Body(pattern);
        var expression = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";
        return (new Regex(expression, RegexOptions.CultureInvariant, IgnoreFile.RegexTimeout), negated, directoryOnly);
    }

    private static string TrimTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && !(end >= 2 && line[end - 2] == '\\'))
        {
            end--;
        }

        return line[..end];
    }

    // The pattern as a regular expression, part by part.
    private static string Body(string pattern)
    {
        var parts = pattern.Split('/');
        var result = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            var last = i == parts.Length - 1;
            if (parts[i] == "**")
            {
                // Leading or middle: any directories, none too; trailing: everything below.
                result.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }

            result.Append(Part(parts[i]));
            if (!last)
            {
                result.Append('/');
            }
        }

        return result.ToString();
    }

    private static string Part(string part)
    {
        var result = new StringBuilder();
        for (var i = 0; i < part.Length; i++)
        {
            var letter = part[i];
            switch (letter)
            {
                case '\\' when i + 1 < part.Length:
                    result.Append(Regex.Escape(part[++i].ToString()));
                    break;
                case '*':
                    while (i + 1 < part.Length && part[i + 1] == '*')
                    {
                        i++;
                    }

                    result.Append("[^/]*");
                    break;
                case '?':
                    result.Append("[^/]");
                    break;
                case '[' when Class(part, i) is { } found:
                    result.Append(found.Expression);
                    i = found.End;
                    break;
                default:
                    result.Append(Regex.Escape(letter.ToString()));
                    break;
            }
        }

        return result.ToString();
    }

    // [abc], [a-z], [!x] or [^x] from start; null when it has no closing ], so the [ is literal.
    private static (string Expression, int End)? Class(string part, int start)
    {
        var i = start + 1;
        var negated = i < part.Length && part[i] is '!' or '^';
        if (negated)
        {
            i++;
        }

        var members = new StringBuilder();
        var firstMember = true;
        for (; i < part.Length; i++)
        {
            var letter = part[i];
            if (letter == ']' && !firstMember)
            {
                return ($"(?![/])[{(negated ? "^" : "")}{members}]", i);
            }

            if (letter == '\\' && i + 1 < part.Length)
            {
                letter = part[++i];
                members.Append('\\').Append(letter);
            }
            else
            {
                members.Append(letter is '-' ? "-" : letter is '[' or '^' or ']' ? "\\" + letter : letter);
            }

            firstMember = false;
        }

        return null;
    }
}
