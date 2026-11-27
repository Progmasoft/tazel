// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Tethys.SPDX.ExpressionParser;

namespace Progmasoft.Tazel.Project;

/// <summary>A license of a module that is not a valid SPDX expression of the license list Tazel carries.</summary>
public sealed class LicenseException : Exception
{
    /// <summary>A license that cannot stand.</summary>
    public LicenseException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public LicenseException()
    {
    }

    /// <summary>A license that cannot stand, and why.</summary>
    public LicenseException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The SPDX License List that ships with Tazel (<c>spdx/licenses.json</c> and <c>spdx/exceptions.json</c> of the
/// repository, embedded) and the check of a module's <c>License</c> against it. Identifiers match without regard to
/// case, as the SPDX specification says; <c>LicenseRef-</c> and <c>DocumentRef-</c> name licenses of one's own.
/// </summary>
internal static class SpdxLicenses
{
    private sealed record Entry(string Id, bool Deprecated);

    private static readonly Lazy<(string Version, Dictionary<string, Entry> Licenses, Dictionary<string, Entry> Exceptions)>
        List = new(Load);

    /// <summary>The version of the SPDX License List, such as 3.29.0.</summary>
    public static string Version => List.Value.Version;

    /// <summary>Whether <paramref name="id"/> is a license of the list, in any case.</summary>
    public static bool IsLicense(string id) => List.Value.Licenses.ContainsKey(id);

    /// <summary>Whether <paramref name="id"/> is an exception of the list, in any case.</summary>
    public static bool IsException(string id) => List.Value.Exceptions.ContainsKey(id);

    /// <summary>
    /// Checks a license expression: its grammar (<c>AND</c>, <c>OR</c>, <c>WITH</c>, parentheses, <c>+</c>), that
    /// every license and exception is on the list and that none is deprecated.
    /// </summary>
    /// <exception cref="LicenseException">The expression cannot stand; the message says why and what to write.</exception>
    public static void Check(string expression, string shown)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new LicenseException($"{shown}: License is empty; write an SPDX expression such as MIT, or leave it out");
        }

        var (version, licenses, exceptions) = List.Value;
        var afterWith = false;
        foreach (var word in Words(expression))
        {
            if (word is "(" or ")" || IsOperator(word, "AND") || IsOperator(word, "OR"))
            {
                afterWith = false;
                continue;
            }

            if (IsOperator(word, "WITH"))
            {
                afterWith = true;
                continue;
            }

            if (!afterWith && IsReference(word))
            {
                continue;
            }

            var id = afterWith ? word : word.TrimEnd('+');
            var table = afterWith ? exceptions : licenses;
            var kind = afterWith ? "exception" : "license";
            if (!table.TryGetValue(id, out var entry))
            {
                throw new LicenseException(
                    $"{shown}: '{id}' is no {kind} of the SPDX License List {version}{Suggestions(id, table, afterWith)}");
            }

            if (entry.Deprecated)
            {
                throw new LicenseException(
                    $"{shown}: '{entry.Id}' is deprecated in the SPDX License List {version}{Replacements(entry.Id, licenses, orLater: !afterWith && word.EndsWith('+'))}");
            }

            afterWith = false;
        }

        // Tethys.SPDX 2.2.0 recurses without end, and the process dies of a stack overflow that cannot be caught, on an
        // expression that ends in AND or OR; only an expression whose form is checked here reaches it.
        Grammar.Check(Words(expression).ToList(), expression, shown);
        try
        {
            _ = SpdxExpressionParser.Parse(expression, id => IsLicense(id) || IsException(id) || IsReference(id), IsException);
        }
        catch (SpdxExpressionException error)
        {
            throw new LicenseException(
                $"{shown}: '{expression}' is not an SPDX expression: licenses joined by AND and OR, an exception after " +
                "WITH, parentheses around a group", error);
        }
        catch (ArgumentException error)
        {
            throw new LicenseException($"{shown}: '{expression}' is not an SPDX expression", error);
        }
    }

    /// <summary>
    /// The form of an expression, as SPDX writes it: <c>expression = term {(AND | OR) term}</c> and
    /// <c>term = "(" expression ")" | license [WITH exception]</c>.
    /// </summary>
    private sealed class Grammar
    {
        private readonly List<string> _words;
        private readonly string _expression;
        private readonly string _shown;
        private int _at;

        private Grammar(List<string> words, string expression, string shown)
        {
            _words = words;
            _expression = expression;
            _shown = shown;
        }

        public static void Check(List<string> words, string expression, string shown)
        {
            var grammar = new Grammar(words, expression, shown);
            grammar.Expression();
            if (grammar._at < words.Count)
            {
                grammar.Fail(words[grammar._at] == ")" ? "a ')' closes no '('" : $"'{words[grammar._at]}' cannot follow");
            }
        }

        private void Expression()
        {
            Term();
            while (_at < _words.Count && (IsOperator(_words[_at], "AND") || IsOperator(_words[_at], "OR")))
            {
                var joiner = _words[_at++];
                if (_at == _words.Count)
                {
                    Fail($"it ends with {joiner.ToUpperInvariant()}; a license must follow it");
                }

                Term();
            }
        }

        private void Term()
        {
            if (_at == _words.Count)
            {
                Fail("a license is missing");
            }

            var word = _words[_at++];
            if (word == "(")
            {
                Expression();
                if (_at == _words.Count || _words[_at] != ")")
                {
                    Fail("a '(' is not closed");
                }

                _at++;
                return;
            }

            if (word == ")" || IsOperator(word, "AND") || IsOperator(word, "OR") || IsOperator(word, "WITH"))
            {
                Fail($"'{word}' stands where a license should");
            }

            if (_at < _words.Count && IsOperator(_words[_at], "WITH"))
            {
                _at++;
                if (_at == _words.Count || _words[_at] is "(" or ")")
                {
                    Fail("an exception must follow WITH");
                }

                _at++;
            }
        }

        private void Fail(string why) =>
            throw new LicenseException(
                $"{_shown}: '{_expression}' is not an SPDX expression: {why}; licenses are joined by AND and OR, an " +
                "exception follows WITH, and parentheses group them");
    }

    /// <summary>The words of an expression: parentheses apart, the rest split at white space.</summary>
    private static IEnumerable<string> Words(string expression) =>
        expression.Replace("(", " ( ", StringComparison.Ordinal).Replace(")", " ) ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static bool IsOperator(string word, string name) => string.Equals(word, name, StringComparison.OrdinalIgnoreCase);

    private static bool IsReference(string word) =>
        word.StartsWith("LicenseRef-", StringComparison.OrdinalIgnoreCase) ||
        word.StartsWith("DocumentRef-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Up to three identifiers of the list close to a misspelt one.</summary>
    private static string Suggestions(string id, Dictionary<string, Entry> table, bool exception)
    {
        var close = table.Values.Where(entry => !entry.Deprecated)
            .Select(entry => (entry.Id, Distance: Distance(id.ToUpperInvariant(), entry.Id.ToUpperInvariant())))
            .Where(candidate => candidate.Distance <= Math.Max(2, id.Length / 4))
            .OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Take(3).Select(candidate => candidate.Id).ToList();
        if (close.Count > 0)
        {
            return $"; did you mean {string.Join(", ", close)}?";
        }

        return exception
            ? "; the exceptions are listed at https://spdx.org/licenses/exceptions-index.html"
            : "; a license of your own is written LicenseRef-<name>";
    }

    /// <summary>What to write instead of a deprecated license: its -only and -or-later forms when the list has them.</summary>
    private static string Replacements(string id, Dictionary<string, Entry> licenses, bool orLater)
    {
        var stem = id.EndsWith('+') ? id[..^1] : id;
        var forms = (orLater ? new[] { stem + "-or-later" } : new[] { stem + "-only", stem + "-or-later" }).Where(licenses.ContainsKey).ToList();
        return forms.Count > 0
            ? $"; write {string.Join(" or ", forms.Select(form => licenses[form].Id))}"
            : "; https://spdx.org/licenses/ names the identifier that replaces it";
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            previous = current;
        }

        return previous[b.Length];
    }

    private static (string, Dictionary<string, Entry>, Dictionary<string, Entry>) Load()
    {
        using var licensesJson = JsonDocument.Parse(Resource("spdx.licenses.json"));
        using var exceptionsJson = JsonDocument.Parse(Resource("spdx.exceptions.json"));
        var version = licensesJson.RootElement.GetProperty("licenseListVersion").GetString() ?? "";
        return (version, Table(licensesJson, "licenses", "licenseId"), Table(exceptionsJson, "exceptions", "licenseExceptionId"));
    }

    private static Dictionary<string, Entry> Table(JsonDocument document, string list, string idProperty)
    {
        var table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.GetProperty(list).EnumerateArray())
        {
            var id = item.GetProperty(idProperty).GetString() ?? "";
            table[id] = new Entry(id, item.GetProperty("isDeprecatedLicenseId").GetBoolean());
        }

        return table;
    }

    private static Stream Resource(string name) =>
        typeof(SpdxLicenses).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"the SPDX License List ({name}) is missing from Tazel");
}
