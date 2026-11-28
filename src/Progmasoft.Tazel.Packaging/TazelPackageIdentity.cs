// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Progmasoft.Tazel.Packaging;

/// <summary>A package that cannot be read, written or trusted; the message says why.</summary>
public sealed class TazelPackageException : Exception
{
    /// <summary>A problem of a package.</summary>
    public TazelPackageException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public TazelPackageException()
    {
    }

    /// <summary>A problem of a package, and what was found.</summary>
    public TazelPackageException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Who a package is: a group such as <c>com.example</c>, a name and a SemVer 2.0.0 version. Its archive is
/// <see cref="FileName"/>, <c>$group.$name-$version.tazel.tar.xz</c>.
/// </summary>
public sealed partial record TazelPackageIdentity
{
    /// <summary>A package, once its parts are checked.</summary>
    /// <exception cref="TazelPackageException">A part is not what it must be; the message says which.</exception>
    public TazelPackageIdentity(string group, string name, string version)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);
        if (!IsGroup(group))
        {
            throw new TazelPackageException($"the group '{group}' must be names joined by dots, such as com.example");
        }

        if (!IsName(name))
        {
            throw new TazelPackageException($"the name '{name}' must be letters, digits and _ + . -");
        }

        if (!IsSemVer(version))
        {
            throw new TazelPackageException($"'{version}' is not a SemVer version such as 1.2.0");
        }

        Group = group;
        Name = name;
        Version = version;
    }

    /// <summary>The group, such as <c>com.example</c>.</summary>
    public string Group { get; }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>The SemVer 2.0.0 version, such as <c>1.2.0</c> or <c>2.0.0-rc.1</c>.</summary>
    public string Version { get; }

    /// <summary><c>$group.$name-$version</c>: the name of the archive without its extension, and its top directory.</summary>
    public string BaseName => $"{Group}.{Name}-{Version}";

    /// <summary>The file name of the archive.</summary>
    public string FileName => BaseName + TazelPackageArchive.Extension;

    /// <summary>Whether <paramref name="name"/> is a name: letters, digits and <c>_ + . -</c>.</summary>
    public static bool IsName(string name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Whether <paramref name="group"/> is names of letters, digits, <c>_</c> and <c>-</c> joined by dots.</summary>
    public static bool IsGroup(string group) =>
        !string.IsNullOrEmpty(group) && group.Split('.').All(part =>
            part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    /// <summary>Whether <paramref name="version"/> is a SemVer 2.0.0 version, as the expression of semver.org says.</summary>
    public static bool IsSemVer(string version) => version is not null && SemVerPattern().IsMatch(version);

    /// <inheritdoc />
    public override string ToString() => BaseName;

    [GeneratedRegex("^[A-Za-z0-9_+.-]+$")]
    private static partial Regex NamePattern();

    // The expression semver.org gives for SemVer 2.0.0.
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex SemVerPattern();
}
