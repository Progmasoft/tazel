// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using Progmasoft.Tazel.Packaging.Spdx;

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
/// The check of a module's <c>License</c>: Progmasoft.Tazel.Packaging reads the expression and holds the SPDX License
/// List; this turns what it finds into the messages of <c>error[E0509]</c>.
/// </summary>
internal static class SpdxLicenses
{
    /// <summary>The version of the SPDX License List, such as 3.29.0.</summary>
    public static string Version => SpdxLicenseList.Current.Version;

    /// <summary>
    /// Checks a license expression: its form (<c>AND</c>, <c>OR</c>, <c>WITH</c>, parentheses, <c>+</c>), that every
    /// license and exception is on the list and that none is deprecated.
    /// </summary>
    /// <exception cref="LicenseException">The expression cannot stand; the message says why and what to write.</exception>
    public static void Check(string expression, string shown)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new LicenseException($"{shown}: License is empty; write an SPDX expression such as MIT, or leave it out");
        }

        if (!SpdxExpressionParser.TryParse(expression, out var parsed, out var error))
        {
            throw new LicenseException(
                $"{shown}: '{expression}' is not an SPDX expression: {error.Reason}; licenses are joined by AND and OR, " +
                "an exception follows WITH, and parentheses group them", error);
        }

        var list = SpdxLicenseList.Current;
        foreach (var problem in list.Check(parsed))
        {
            throw new LicenseException($"{shown}: {Describe(problem, list.Version)}");
        }
    }

    private static string Describe(SpdxProblem problem, string version)
    {
        var suggested = string.Join(", ", problem.Suggestions);
        return problem.Kind switch
        {
            SpdxProblemKind.UnknownLicense => $"'{problem.Id}' is no license of the SPDX License List {version}" +
                (problem.Suggestions.Count > 0 ? $"; did you mean {suggested}?" : "; a license of your own is written LicenseRef-<name>"),
            SpdxProblemKind.UnknownException => $"'{problem.Id}' is no exception of the SPDX License List {version}" +
                (problem.Suggestions.Count > 0
                    ? $"; did you mean {suggested}?"
                    : "; the exceptions are listed at https://spdx.org/licenses/exceptions-index.html"),
            SpdxProblemKind.DeprecatedLicense => $"'{problem.Id}' is deprecated in the SPDX License List {version}" +
                (problem.Suggestions.Count > 0
                    ? $"; write {string.Join(" or ", problem.Suggestions)}"
                    : "; https://spdx.org/licenses/ names the identifier that replaces it"),
            _ => $"'{problem.Id}' is a deprecated exception in the SPDX License List {version}; " +
                "https://spdx.org/licenses/exceptions-index.html names the one that replaces it",
        };
    }
}
