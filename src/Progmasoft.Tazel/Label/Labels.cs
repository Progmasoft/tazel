// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Text.RegularExpressions;

namespace Progmasoft.Tazel.Label;

/// <summary>A label that cannot be read, with the reason.</summary>
public sealed class LabelException : Exception
{
    /// <summary>A label that cannot be read.</summary>
    public LabelException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public LabelException()
    {
    }

    /// <summary>A problem and its cause.</summary>
    public LabelException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A package: the directory of one <c>BUILD.csx</c>, relative to the root of its module, in this module
/// (<see cref="Repository"/> null) or a dependency (its name, alias or local module name).
/// </summary>
/// <param name="Repository">The dependency, or null for this module.</param>
/// <param name="Path">The directory, with <c>/</c> between its parts; empty for the root package.</param>
public sealed record PackageId(string? Repository, string Path)
{
    /// <summary>The root package of this module.</summary>
    public static PackageId Root { get; } = new(null, "");

    /// <summary><c>//dir</c> or <c>@name//dir</c>; the root package is <c>//</c>.</summary>
    public override string ToString() => $"{(Repository is null ? "" : "@" + Repository)}//{Path}";
}

/// <summary>What a command selects: one target, or every target of some packages.</summary>
public abstract record Selection;

/// <summary>One target: <c>//dir:name</c>, <c>//:name</c>, <c>@dep//dir:name</c>.</summary>
/// <param name="Package">Its package.</param>
/// <param name="Name">Its name.</param>
public sealed record TargetLabel(PackageId Package, string Name) : Selection
{
    /// <inheritdoc/>
    public override string ToString() => $"{Package}:{Name}";
}

/// <summary><c>//dir/...</c>: the package and every package below it; <c>//...</c> is the whole module.</summary>
/// <param name="Package">The package the pattern starts at.</param>
public sealed record PackagePattern(PackageId Package) : Selection
{
    /// <summary>Whether <paramref name="other"/> is this package or one below it.</summary>
    public bool Covers(PackageId other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Repository == Package.Repository &&
            (Package.Path.Length == 0 || other.Path == Package.Path ||
                other.Path.StartsWith(Package.Path + "/", StringComparison.Ordinal));
    }

    /// <inheritdoc/>
    public override string ToString() => Package.Path.Length == 0 ? $"{Package}..." : $"{Package}/...";
}

/// <summary>Reads labels as Bazel spells them; nothing else is a label.</summary>
public static partial class Labels
{
    /// <summary>A target label; <c>:name</c> is a target of <paramref name="current"/>. Patterns are refused.</summary>
    public static TargetLabel Target(string text, PackageId current) =>
        Selection(text, current) switch
        {
            TargetLabel label => label,
            _ => throw new LabelException($"'{text}' names packages, not one target; write //dir:name"),
        };

    /// <summary>A target label or a package pattern; <c>:name</c> is a target of <paramref name="current"/>.</summary>
    public static Selection Selection(string text, PackageId current)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);
        if (text.StartsWith(':'))
        {
            return new TargetLabel(current, Checked(text[1..], "target name", text));
        }

        string? repository = null;
        var rest = text;
        if (text.StartsWith('@'))
        {
            var slashes = text.IndexOf("//", StringComparison.Ordinal);
            if (slashes < 0)
            {
                throw new LabelException($"'{text}' has no //; write @name//:target");
            }

            repository = Checked(text[1..slashes], "dependency name", text);
            rest = text[slashes..];
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            throw new LabelException($"'{text}' is not a label; write //dir:name, //:name or :name");
        }

        var body = rest[2..];
        if (body == "...")
        {
            return new PackagePattern(new PackageId(repository, ""));
        }

        if (body.EndsWith("/...", StringComparison.Ordinal))
        {
            return new PackagePattern(new PackageId(repository, PackagePath(body[..^4], text)));
        }

        var colon = body.LastIndexOf(':');
        if (colon < 0)
        {
            var shown = body.TrimEnd('/');
            var last = shown[(shown.LastIndexOf('/') + 1)..];
            throw new LabelException($"'{text}' names no target; write //{shown}:{last} or //{shown}/...");
        }

        var directory = body[..colon];
        if (directory.EndsWith('/'))
        {
            throw new LabelException($"'{text}': no / before the colon; write //{directory.TrimEnd('/')}:...");
        }

        var path = directory.Length == 0 ? "" : PackagePath(directory, text);
        return new TargetLabel(new PackageId(repository, path), Checked(body[(colon + 1)..], "target name", text));
    }

    /// <summary>Checks a target or package name as a project file writes it.</summary>
    public static bool IsName(string name) => NamePattern().IsMatch(name);

    private static string PackagePath(string path, string text)
    {
        if (path.Length == 0)
        {
            throw new LabelException($"'{text}' has an empty package");
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment is "." or "..")
            {
                throw new LabelException($"'{text}': a package path has no . or .. parts");
            }

            Checked(segment, "package directory", text);
        }

        return path;
    }

    private static string Checked(string name, string what, string text)
    {
        if (!IsName(name))
        {
            throw new LabelException(
                $"'{text}': the {what} '{name}' must be letters, digits and _ + . -");
        }

        return name;
    }

    [GeneratedRegex("^[A-Za-z0-9_+.-]+$")]
    private static partial Regex NamePattern();
}
