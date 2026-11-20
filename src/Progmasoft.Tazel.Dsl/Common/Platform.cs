// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Progmasoft.Tazel.Dsl.Common;

/// <summary>
/// A platform Tazel builds for. Each member follows its name on the command line
/// (<c>--platform linux-amd64</c> is <see cref="LinuxAmd64"/>).
/// </summary>
public enum Platform
{
    /// <summary><c>linux-amd64</c>.</summary>
    LinuxAmd64,

    /// <summary><c>linux-aarch64</c>.</summary>
    LinuxAarch64,

    /// <summary><c>windows-x64</c>.</summary>
    WindowsX64,

    /// <summary><c>windows-arm64</c>.</summary>
    WindowsArm64,

    /// <summary><c>macos-x86_64</c>.</summary>
    MacOSX86_64,

    /// <summary><c>macos-arm64</c>.</summary>
    MacOSArm64,

    /// <summary><c>freebsd-amd64</c>.</summary>
    FreeBSDAmd64,

    /// <summary><c>freebsd-aarch64</c>.</summary>
    FreeBSDAarch64,

    /// <summary><c>openbsd-amd64</c>.</summary>
    OpenBSDAmd64,

    /// <summary><c>openbsd-aarch64</c>.</summary>
    OpenBSDAarch64,

    /// <summary><c>dragonfly-bsd-amd64</c>; DragonFly BSD runs on x86-64 only.</summary>
    DragonFlyBSDAmd64,
}

/// <summary>An operating system, as conditions in a project file see it.</summary>
public enum OS
{
    /// <summary>Linux.</summary>
    Linux,

    /// <summary>Windows.</summary>
    Windows,

    /// <summary>macOS.</summary>
    MacOS,

    /// <summary>FreeBSD.</summary>
    FreeBSD,

    /// <summary>OpenBSD.</summary>
    OpenBSD,

    /// <summary>DragonFly BSD.</summary>
    DragonFlyBSD,
}

/// <summary>A processor architecture.</summary>
public enum Arch
{
    /// <summary>x86-64.</summary>
    X64,

    /// <summary>64-bit ARM.</summary>
    Arm64,
}

/// <summary>How a build compiles: for debugging, or optimized.</summary>
public enum BuildMode
{
    /// <summary>Not optimized, with debug information.</summary>
    Debug,

    /// <summary>Optimized, without assertions.</summary>
    Release,
}

/// <summary>The names, systems and architectures of the platforms.</summary>
public static class Platforms
{
    private static readonly (Platform Platform, string Name, OS OS, Arch Arch)[] All =
    [
        (Platform.LinuxAmd64, "linux-amd64", OS.Linux, Arch.X64),
        (Platform.LinuxAarch64, "linux-aarch64", OS.Linux, Arch.Arm64),
        (Platform.WindowsX64, "windows-x64", OS.Windows, Arch.X64),
        (Platform.WindowsArm64, "windows-arm64", OS.Windows, Arch.Arm64),
        (Platform.MacOSX86_64, "macos-x86_64", OS.MacOS, Arch.X64),
        (Platform.MacOSArm64, "macos-arm64", OS.MacOS, Arch.Arm64),
        (Platform.FreeBSDAmd64, "freebsd-amd64", OS.FreeBSD, Arch.X64),
        (Platform.FreeBSDAarch64, "freebsd-aarch64", OS.FreeBSD, Arch.Arm64),
        (Platform.OpenBSDAmd64, "openbsd-amd64", OS.OpenBSD, Arch.X64),
        (Platform.OpenBSDAarch64, "openbsd-aarch64", OS.OpenBSD, Arch.Arm64),
        (Platform.DragonFlyBSDAmd64, "dragonfly-bsd-amd64", OS.DragonFlyBSD, Arch.X64),
    ];

    /// <summary>Every platform name, in the order of the enum.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.ConvertAll(All, entry => entry.Name);

    /// <summary>The name of <paramref name="platform"/> on the command line.</summary>
    public static string Name(this Platform platform) => Find(platform).Name;

    /// <summary>The operating system of <paramref name="platform"/>.</summary>
    public static OS GetOS(this Platform platform) => Find(platform).OS;

    /// <summary>The architecture of <paramref name="platform"/>.</summary>
    public static Arch GetArch(this Platform platform) => Find(platform).Arch;

    /// <summary>Whether <paramref name="name"/> is a platform name, and which.</summary>
    public static bool TryParse(string name, out Platform platform)
    {
        foreach (var entry in All)
        {
            if (string.Equals(entry.Name, name, StringComparison.Ordinal))
            {
                platform = entry.Platform;
                return true;
            }
        }

        platform = default;
        return false;
    }

    private static (Platform Platform, string Name, OS OS, Arch Arch) Find(Platform platform)
    {
        foreach (var entry in All)
        {
            if (entry.Platform == platform)
            {
                return entry;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(platform), platform, "not a platform");
    }
}
