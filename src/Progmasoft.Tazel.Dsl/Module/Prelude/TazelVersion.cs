// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;

namespace Progmasoft.Tazel.Dsl.Module.Prelude;

/// <summary>How a <see cref="TazelVersion"/> compares.</summary>
public enum VersionComparison
{
    /// <summary>Only this version.</summary>
    Exactly,

    /// <summary>This version or a later one.</summary>
    AtLeast,

    /// <summary>A later version.</summary>
    GreaterThan,

    /// <summary>This version or an earlier one.</summary>
    AtMost,

    /// <summary>An earlier version.</summary>
    LessThan,

    /// <summary>From the lower version to the upper one, both included.</summary>
    Between,
}

/// <summary>The Tazel versions a module builds with; PATCH may be left out and is then 0.</summary>
public sealed class TazelVersion
{
    private TazelVersion(VersionComparison comparison, Version lower, Version? upper)
    {
        Comparison = comparison;
        Lower = lower;
        Upper = upper;
    }

    /// <summary>How the versions compare.</summary>
    public VersionComparison Comparison { get; }

    /// <summary>The version, or the lower end of <see cref="VersionComparison.Between"/>.</summary>
    public Version Lower { get; }

    /// <summary>The upper end of <see cref="VersionComparison.Between"/>.</summary>
    public Version? Upper { get; }

    /// <summary>Only MAJOR.MINOR.PATCH.</summary>
    public static TazelVersion Exactly(int major, int minor) => Exactly(major, minor, 0);

    /// <inheritdoc cref="Exactly(int, int)"/>
    public static TazelVersion Exactly(int major, int minor, int patch) =>
        new(VersionComparison.Exactly, Make(major, minor, patch), null);

    /// <summary>MAJOR.MINOR.PATCH or later.</summary>
    public static TazelVersion AtLeast(int major, int minor) => AtLeast(major, minor, 0);

    /// <inheritdoc cref="AtLeast(int, int)"/>
    public static TazelVersion AtLeast(int major, int minor, int patch) =>
        new(VersionComparison.AtLeast, Make(major, minor, patch), null);

    /// <summary>Later than MAJOR.MINOR.PATCH.</summary>
    public static TazelVersion GreaterThan(int major, int minor) => GreaterThan(major, minor, 0);

    /// <inheritdoc cref="GreaterThan(int, int)"/>
    public static TazelVersion GreaterThan(int major, int minor, int patch) =>
        new(VersionComparison.GreaterThan, Make(major, minor, patch), null);

    /// <summary>MAJOR.MINOR.PATCH or earlier.</summary>
    public static TazelVersion AtMost(int major, int minor) => AtMost(major, minor, 0);

    /// <inheritdoc cref="AtMost(int, int)"/>
    public static TazelVersion AtMost(int major, int minor, int patch) =>
        new(VersionComparison.AtMost, Make(major, minor, patch), null);

    /// <summary>Earlier than MAJOR.MINOR.PATCH.</summary>
    public static TazelVersion LessThan(int major, int minor) => LessThan(major, minor, 0);

    /// <inheritdoc cref="LessThan(int, int)"/>
    public static TazelVersion LessThan(int major, int minor, int patch) =>
        new(VersionComparison.LessThan, Make(major, minor, patch), null);

    /// <summary>From MAJOR.MINOR to MAJOR.MINOR, both included.</summary>
    public static TazelVersion Between(int major, int minor, int upperMajor, int upperMinor) =>
        Between(major, minor, 0, upperMajor, upperMinor, 0);

    /// <summary>From MAJOR.MINOR.PATCH to MAJOR.MINOR.PATCH, both included.</summary>
    public static TazelVersion Between(int major, int minor, int patch, int upperMajor, int upperMinor, int upperPatch)
    {
        var lower = Make(major, minor, patch);
        var upper = Make(upperMajor, upperMinor, upperPatch);
        if (upper < lower)
        {
            throw new DslException($"TazelVersion.Between: {upper} comes before {lower}");
        }

        return new TazelVersion(VersionComparison.Between, lower, upper);
    }

    /// <summary>Whether <paramref name="version"/> is one of these versions.</summary>
    public bool Admits(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var compared = Plain(version).CompareTo(Lower);
        return Comparison switch
        {
            VersionComparison.Exactly => compared == 0,
            VersionComparison.AtLeast => compared >= 0,
            VersionComparison.GreaterThan => compared > 0,
            VersionComparison.AtMost => compared <= 0,
            VersionComparison.LessThan => compared < 0,
            _ => compared >= 0 && Plain(version).CompareTo(Upper) <= 0,
        };
    }

    /// <inheritdoc/>
    public override string ToString() => Comparison switch
    {
        VersionComparison.Exactly => $"exactly {Lower}",
        VersionComparison.AtLeast => $"{Lower} or later",
        VersionComparison.GreaterThan => $"later than {Lower}",
        VersionComparison.AtMost => $"{Lower} or earlier",
        VersionComparison.LessThan => $"earlier than {Lower}",
        _ => $"{Lower} to {Upper}",
    };

    private static Version Plain(Version version) =>
        new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    private static Version Make(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new DslException($"a Tazel version has no negative parts: {major}.{minor}.{patch}");
        }

        return new Version(major, minor, patch);
    }
}
