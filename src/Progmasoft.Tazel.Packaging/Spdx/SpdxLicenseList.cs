// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Progmasoft.Tazel.Packaging.Spdx;

/// <summary>A license or an exception of the SPDX License List.</summary>
/// <param name="Id">The identifier as the list writes it, such as <c>Apache-2.0</c>.</param>
/// <param name="Name">The full name.</param>
/// <param name="Deprecated">Whether the list keeps it only for old documents.</param>
public sealed record SpdxListEntry(string Id, string Name, bool Deprecated);

/// <summary>What <see cref="SpdxLicenseList.Check"/> found wrong with one identifier of an expression.</summary>
public enum SpdxProblemKind
{
    /// <summary>The license is not on the list.</summary>
    UnknownLicense,

    /// <summary>The exception is not on the list.</summary>
    UnknownException,

    /// <summary>The license is on the list but deprecated.</summary>
    DeprecatedLicense,

    /// <summary>The exception is on the list but deprecated.</summary>
    DeprecatedException,
}

/// <summary>A problem with one identifier of an expression.</summary>
/// <param name="Kind">What is wrong.</param>
/// <param name="Id">The identifier as written.</param>
/// <param name="Suggestions">
/// For an unknown identifier, up to three of the list that are close to it; for a deprecated one, what replaces it, such
/// as <c>GPL-2.0-only</c> and <c>GPL-2.0-or-later</c> for <c>GPL-2.0</c>, or only <c>-or-later</c> when it was written
/// with <c>+</c>. May be empty.
/// </param>
public sealed record SpdxProblem(SpdxProblemKind Kind, string Id, IReadOnlyList<string> Suggestions);

/// <summary>
/// The SPDX License List that ships with this package (<c>licenses.json</c> and <c>exceptions.json</c> of
/// spdx/license-list-data, unmodified). Identifiers match in any case, as the SPDX specification says.
/// </summary>
public sealed class SpdxLicenseList
{
    private static readonly Lazy<SpdxLicenseList> Shipped = new(Load);

    private readonly Dictionary<string, SpdxListEntry> _licenses;
    private readonly Dictionary<string, SpdxListEntry> _exceptions;

    private SpdxLicenseList(string version, Dictionary<string, SpdxListEntry> licenses, Dictionary<string, SpdxListEntry> exceptions)
    {
        Version = version;
        _licenses = licenses;
        _exceptions = exceptions;
    }

    /// <summary>The list that ships with this package.</summary>
    public static SpdxLicenseList Current => Shipped.Value;

    /// <summary>The version of the list, such as <c>3.29.0</c>.</summary>
    public string Version { get; }

    /// <summary>Every license of the list.</summary>
    public IReadOnlyCollection<SpdxListEntry> Licenses => _licenses.Values;

    /// <summary>Every exception of the list.</summary>
    public IReadOnlyCollection<SpdxListEntry> Exceptions => _exceptions.Values;

    /// <summary>The license <paramref name="id"/>, in any case, or null.</summary>
    public SpdxListEntry? License(string id) => _licenses.GetValueOrDefault(id);

    /// <summary>The exception <paramref name="id"/>, in any case, or null.</summary>
    public SpdxListEntry? Exception(string id) => _exceptions.GetValueOrDefault(id);

    /// <summary>
    /// The identifiers of <paramref name="expression"/> that are not on the list or are deprecated, left to right. A
    /// <see cref="SpdxLicenseReference"/> names a license of its author's own and is never a problem.
    /// </summary>
    public IReadOnlyList<SpdxProblem> Check(SpdxExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var problems = new List<SpdxProblem>();
        var pending = new Stack<SpdxExpression>();
        pending.Push(expression);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case SpdxOr or:
                    pending.Push(or.Right);
                    pending.Push(or.Left);
                    break;
                case SpdxAnd and:
                    pending.Push(and.Right);
                    pending.Push(and.Left);
                    break;
                case SpdxWith with:
                    pending.Push(new ExceptionMark(with.Exception));
                    pending.Push(with.License);
                    break;
                case SpdxLicense license:
                    CheckLicense(license, problems);
                    break;
                case ExceptionMark mark:
                    CheckException(mark.Id, problems);
                    break;
                default:
                    // A LicenseRef- names a license of its author's own, which no list holds.
                    break;
            }
        }

        return problems;
    }

    private void CheckLicense(SpdxLicense license, List<SpdxProblem> problems)
    {
        if (License(license.Id) is not { } entry)
        {
            problems.Add(new SpdxProblem(SpdxProblemKind.UnknownLicense, license.Id, Closest(license.Id, _licenses)));
        }
        else if (entry.Deprecated)
        {
            var forms = license.OrLater ? new[] { "-or-later" } : ["-only", "-or-later"];
            var replacements = forms.Select(form => License(entry.Id + form)).OfType<SpdxListEntry>()
                .Where(replacement => !replacement.Deprecated).Select(replacement => replacement.Id).ToList();
            problems.Add(new SpdxProblem(SpdxProblemKind.DeprecatedLicense, license.ToString(), replacements));
        }
    }

    private void CheckException(string id, List<SpdxProblem> problems)
    {
        if (Exception(id) is not { } entry)
        {
            problems.Add(new SpdxProblem(SpdxProblemKind.UnknownException, id, Closest(id, _exceptions)));
        }
        else if (entry.Deprecated)
        {
            problems.Add(new SpdxProblem(SpdxProblemKind.DeprecatedException, id, []));
        }
    }

    /// <summary>A stand-in on the stack of <see cref="Check"/> for the exception of a WITH.</summary>
    private sealed record ExceptionMark(string Id) : SpdxExpression
    {
        private protected override int Precedence => 3;
    }

    /// <summary>Up to three identifiers of the table that are not deprecated and close to <paramref name="id"/>.</summary>
    private static List<string> Closest(string id, Dictionary<string, SpdxListEntry> table)
    {
        var upper = id.ToUpperInvariant();
        var limit = Math.Max(2, id.Length / 4);
        return table.Values.Where(entry => !entry.Deprecated)
            .Select(entry => (entry.Id, Distance: Distance(upper, entry.Id.ToUpperInvariant(), limit)))
            .Where(candidate => candidate.Distance <= limit)
            .OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Take(3).Select(candidate => candidate.Id).ToList();
    }

    /// <summary>The edit distance of two strings, or more than <paramref name="limit"/> as soon as it must be.</summary>
    private static int Distance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static SpdxLicenseList Load()
    {
        using var licenses = JsonDocument.Parse(Resource("spdx.licenses.json"));
        using var exceptions = JsonDocument.Parse(Resource("spdx.exceptions.json"));
        var version = licenses.RootElement.GetProperty("licenseListVersion").GetString() ?? "";
        return new SpdxLicenseList(version, Table(licenses, "licenses", "licenseId"),
            Table(exceptions, "exceptions", "licenseExceptionId"));
    }

    private static Dictionary<string, SpdxListEntry> Table(JsonDocument document, string list, string idProperty)
    {
        var table = new Dictionary<string, SpdxListEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.GetProperty(list).EnumerateArray())
        {
            var id = item.GetProperty(idProperty).GetString() ?? "";
            table[id] = new SpdxListEntry(id, item.GetProperty("name").GetString() ?? id,
                item.GetProperty("isDeprecatedLicenseId").GetBoolean());
        }

        return table;
    }

    private static Stream Resource(string name) =>
        typeof(SpdxLicenseList).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"the SPDX License List ({name}) is missing from Progmasoft.Tazel.Packaging");
}
