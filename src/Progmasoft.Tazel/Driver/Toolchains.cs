// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>A program and the arguments that always come first, such as <c>zig cc</c>.</summary>
/// <param name="Program">The program, a full path.</param>
/// <param name="Prefix">The arguments before any other.</param>
internal sealed record Tool(string Program, IReadOnlyList<string> Prefix)
{
    public IEnumerable<string> Command => Prefix.Prepend(Program);

    public override string ToString() => string.Join(' ', Command);
}

/// <summary>The machine Tazel runs on, and programs on its PATH.</summary>
internal static class Host
{
    /// <summary>The platform of this machine.</summary>
    public static Platform Platform { get; } = Detect();

    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>The full path of <paramref name="name"/>: as written when it has a directory, else from PATH.</summary>
    public static string? Which(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        var extensions = IsWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var extension in extensions.Prepend(""))
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate) && (IsWindows || IsExecutable(candidate)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool IsExecutable(string file) =>
        !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;

    private static Platform Detect()
    {
        var arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
        {
            return arm ? Platform.WindowsArm64 : Platform.WindowsX64;
        }

        if (OperatingSystem.IsMacOS())
        {
            return arm ? Platform.MacOSArm64 : Platform.MacOSX86_64;
        }

        if (OperatingSystem.IsFreeBSD())
        {
            return arm ? Platform.FreeBSDAarch64 : Platform.FreeBSDAmd64;
        }

        var description = RuntimeInformation.OSDescription;
        if (description.Contains("OpenBSD", StringComparison.OrdinalIgnoreCase))
        {
            return arm ? Platform.OpenBSDAarch64 : Platform.OpenBSDAmd64;
        }

        if (description.Contains("DragonFly", StringComparison.OrdinalIgnoreCase))
        {
            return Platform.DragonFlyBSDAmd64;
        }

        return arm ? Platform.LinuxAarch64 : Platform.LinuxAmd64;
    }
}

/// <summary>The C-family tools of a build and the features targets may turn on.</summary>
internal sealed class CCToolchain(string name, Tool c, Tool cpp, Tool linker, Tool archiver, IReadOnlyList<Feature> features)
{
    public string Name { get; } = name;

    public Tool C { get; } = c;

    public Tool Cpp { get; } = cpp;

    public Tool Linker { get; } = linker;

    public Tool Archiver { get; } = archiver;

    public IReadOnlyList<Feature> Features { get; } = features;

    /// <summary>Whether the archiver is Apple's libtool, which takes <c>-static -o</c> instead of <c>rcs</c>.</summary>
    public bool ArchiverIsLibtool =>
        Path.GetFileNameWithoutExtension(Archiver.Program).Equals("libtool", StringComparison.Ordinal);
}

/// <summary>The zig of a build.</summary>
internal sealed record ZigToolchain(string Name, Tool Zig);

/// <summary>Chooses the toolchain of each family for a platform.</summary>
internal sealed class ToolchainChooser(Workspace workspace, Platform platform)
{
    private readonly Dictionary<Family, object> _chosen = [];

    public Platform Platform { get; } = platform;

    /// <summary>The C-family toolchains this build chose.</summary>
    public IEnumerable<CCToolchain> UsedCC => _chosen.Values.OfType<CCToolchain>();

    /// <summary>
    /// The first toolchain the module registers for <paramref name="family"/> that runs here and builds for the platform;
    /// without one, this machine's own tools, and only when the platform is this machine's.
    /// </summary>
    public CCToolchain CC(Family family)
    {
        if (_chosen.TryGetValue(family, out var known))
        {
            return (CCToolchain)known;
        }

        var registered = Find(family);
        CCToolchain chosen;
        if (registered is not null)
        {
            var settings = (CCToolchainSettings)registered.Settings;
            var tools = settings.Tools;
            var c = Required(tools.CCompiler, "CCompiler", registered);
            var cpp = Required(tools.CppCompiler ?? tools.CCompiler, "CppCompiler", registered);
            var linker = tools.Linker is null ? cpp : Required(tools.Linker, "Linker", registered);
            var archiver = Required(tools.Archiver ?? "ar", "Archiver", registered);
            chosen = new CCToolchain(registered.Label.ToString(), c, cpp, linker, archiver, settings.Features);
        }
        else
        {
            chosen = HostCC(family);
        }

        _chosen[family] = chosen;
        return chosen;
    }

    /// <summary>The zig of the build, chosen as <see cref="CC"/> chooses.</summary>
    public ZigToolchain Zig()
    {
        if (_chosen.TryGetValue(Family.Zig, out var known))
        {
            return (ZigToolchain)known;
        }

        var registered = Find(Family.Zig);
        ZigToolchain chosen;
        if (registered is not null)
        {
            var settings = (ZigToolchainSettings)registered.Settings;
            chosen = new ZigToolchain(registered.Label.ToString(), Required(settings.Tools.Zig ?? "zig", "Zig", registered));
        }
        else
        {
            RequireHost(Family.Zig);
            var zig = Host.Which("zig") ?? throw new ProjectException(
                "no zig on PATH; install Zig or register a Zig toolchain in MODULE.csx");
            chosen = new ZigToolchain("zig on PATH", new Tool(zig, []));
        }

        _chosen[Family.Zig] = chosen;
        return chosen;
    }

    private ProjectToolchain? Find(Family family) =>
        workspace.Registered.FirstOrDefault(toolchain =>
            toolchain.Family == family && toolchain.Settings switch
            {
                CCToolchainSettings cc => cc.ExecutionPlatform == Host.Platform && cc.TargetPlatform == Platform,
                ZigToolchainSettings zig => zig.ExecutionPlatform == Host.Platform && zig.TargetPlatform == Platform,
                _ => false,
            });

    private static Tool Required(string? name, string what, ProjectToolchain toolchain)
    {
        if (name is null)
        {
            throw new ProjectException($"{toolchain.Place}: the toolchain {toolchain.Label} has no {what}");
        }

        var found = Host.Which(name)
            ?? throw new ProjectException($"{toolchain.Place}: the {what} '{name}' of {toolchain.Label} is not found");
        return new Tool(found, []);
    }

    private void RequireHost(Family family)
    {
        if (Platform != Host.Platform)
        {
            throw new ProjectException(
                $"no {FamilyName(family)} toolchain of MODULE.csx runs on {Host.Platform.Name()} and builds for " +
                $"{Platform.Name()}; this machine's own tools build only for {Host.Platform.Name()}");
        }
    }

    private CCToolchain HostCC(Family family)
    {
        RequireHost(family);
        if (Host.Which("clang") is { } clang)
        {
            var clangxx = Host.Which("clang++") ?? clang;
            var archiver = Host.Which("llvm-ar") ?? Host.Which("ar");
            if (archiver is not null)
            {
                return new CCToolchain("clang on PATH", new Tool(clang, []), new Tool(clangxx, []), new Tool(clangxx, []),
                    new Tool(archiver, []), []);
            }
        }

        if (family == Family.CC && Host.Which("gcc") is { } gcc && Host.Which("g++") is { } gxx &&
            (Host.Which("gcc-ar") ?? Host.Which("ar")) is { } ar)
        {
            return new CCToolchain("gcc on PATH", new Tool(gcc, []), new Tool(gxx, []), new Tool(gxx, []), new Tool(ar, []), []);
        }

        if (family == Family.CC && Host.Which("zig") is { } zig)
        {
            return new CCToolchain("zig cc on PATH", new Tool(zig, ["cc"]), new Tool(zig, ["c++"]), new Tool(zig, ["c++"]),
                new Tool(zig, ["ar"]), []);
        }

        throw new ProjectException(
            $"no {FamilyName(family)} compiler on PATH (clang{(family == Family.CC ? ", gcc or zig" : "")}); " +
            "install one or register a toolchain in MODULE.csx");
    }

    private static string FamilyName(Family family) => family switch
    {
        Family.CC => "C and C++",
        Family.ObjectiveC => "Objective-C",
        _ => "Zig",
    };
}
