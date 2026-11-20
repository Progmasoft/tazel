// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Build.Prelude;

/// <summary>C and C++ targets; the language of each file comes from its extension.</summary>
public static class CC
{
    /// <summary>Declares a static library.</summary>
    public static Target StaticLibrary(
        string name, CCStaticLibrarySettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.CC, TargetKind.StaticLibrary, name, settings, file, line);

    /// <summary>Declares a shared library.</summary>
    public static Target SharedLibrary(
        string name, CCSharedLibrarySettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.CC, TargetKind.SharedLibrary, name, settings, file, line);

    /// <summary>Declares an executable.</summary>
    public static Target Executable(
        string name, CCExecutableSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.CC, TargetKind.Executable, name, settings, file, line);

    /// <summary>Declares a test.</summary>
    public static Target Test(
        string name, CCTestSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.CC, TargetKind.Test, name, settings, file, line);

    /// <summary>Turns a toolchain feature on for a target.</summary>
    public static FeatureToggle Enable(string name) => new(name, true);

    /// <summary>Turns a toolchain feature off for a target.</summary>
    public static FeatureToggle Disable(string name) => new(name, false);
}

/// <summary>Objective-C and Objective-C++ targets, with C and C++ files beside them.</summary>
public static class ObjectiveC
{
    /// <summary>Declares a static library.</summary>
    public static Target StaticLibrary(
        string name, ObjectiveCStaticLibrarySettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.ObjectiveC, TargetKind.StaticLibrary, name, settings, file, line);

    /// <summary>Declares a shared library.</summary>
    public static Target SharedLibrary(
        string name, ObjectiveCSharedLibrarySettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.ObjectiveC, TargetKind.SharedLibrary, name, settings, file, line);

    /// <summary>Declares an executable.</summary>
    public static Target Executable(
        string name, ObjectiveCExecutableSettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.ObjectiveC, TargetKind.Executable, name, settings, file, line);

    /// <summary>Declares a test.</summary>
    public static Target Test(
        string name, ObjectiveCTestSettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.ObjectiveC, TargetKind.Test, name, settings, file, line);

    /// <summary>Turns a toolchain feature on for a target.</summary>
    public static FeatureToggle Enable(string name) => new(name, true);

    /// <summary>Turns a toolchain feature off for a target.</summary>
    public static FeatureToggle Disable(string name) => new(name, false);
}

/// <summary>Zig targets.</summary>
public static class Zig
{
    /// <summary>Declares a module, which other Zig targets import.</summary>
    public static Target Module(
        string name, ZigModuleSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.Zig, TargetKind.Module, name, settings, file, line);

    /// <summary>Declares a static library.</summary>
    public static Target StaticLibrary(
        string name, ZigStaticLibrarySettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.Zig, TargetKind.StaticLibrary, name, settings, file, line);

    /// <summary>Declares a shared library.</summary>
    public static Target SharedLibrary(
        string name, ZigSharedLibrarySettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.Zig, TargetKind.SharedLibrary, name, settings, file, line);

    /// <summary>Declares an executable.</summary>
    public static Target Executable(
        string name, ZigExecutableSettings settings, [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.Zig, TargetKind.Executable, name, settings, file, line);

    /// <summary>Declares a test, which <c>zig test</c> builds.</summary>
    public static Target Test(
        string name, ZigTestSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.Declare(Family.Zig, TargetKind.Test, name, settings, file, line);
}

/// <summary>What a project file can ask about the build it is part of.</summary>
public sealed class BuildContext
{
    internal BuildContext(BuildMode mode, Platform platform)
    {
        Mode = mode;
        Platform = platform;
    }

    /// <summary>Whether the build is a debug or a release build.</summary>
    public BuildMode Mode { get; }

    /// <summary>The platform the command builds for.</summary>
    public Platform Platform { get; }

    /// <summary>The operating system of <see cref="Platform"/>.</summary>
    public OS OS => Platform.GetOS();

    /// <summary>The architecture of <see cref="Platform"/>.</summary>
    public Arch Arch => Platform.GetArch();
}

/// <summary>What a <c>BUILD.csx</c> sees beyond its imports.</summary>
public sealed class BuildGlobals
{
    internal BuildGlobals(BuildContext build) => Build = build;

    /// <summary>The build this file is part of.</summary>
    public BuildContext Build { get; }
}
