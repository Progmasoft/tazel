// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;

/// <summary>The actions of a C-family build, as Bazel names them, for the flags of a feature.</summary>
public enum CCAction
{
    /// <summary>Compiling a .c file.</summary>
    CCompile,

    /// <summary>Compiling a .cpp, .cc or .cxx file.</summary>
    CppCompile,

    /// <summary>Compiling a .m file.</summary>
    ObjectiveCCompile,

    /// <summary>Compiling a .mm file.</summary>
    ObjectiveCppCompile,

    /// <summary>Assembling a .s or .S file.</summary>
    Assemble,

    /// <summary>Linking an executable.</summary>
    CppLinkExecutable,

    /// <summary>Linking a shared library.</summary>
    CppLinkSharedLibrary,

    /// <summary>Making a static library with the archiver.</summary>
    CppLinkStaticLibrary,
}

/// <summary>Flags that go to the tool of some actions.</summary>
public sealed class FlagSet
{
    /// <summary>The actions the flags go to.</summary>
    public IReadOnlyList<CCAction> Actions { get; init; } = [];

    /// <summary>The flags, as written.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
}

/// <summary>What a feature does and whether it is on by default.</summary>
public sealed class FeatureSettings
{
    /// <summary>Whether the feature is on unless a target or the command line turns it off.</summary>
    public bool Enabled { get; init; }

    /// <summary>The flags of the feature.</summary>
    public IReadOnlyList<FlagSet> FlagSets { get; init; } = [];
}

/// <summary>A named set of flags of a toolchain.</summary>
public sealed class Feature
{
    internal Feature(string name, FeatureSettings settings)
    {
        Name = name;
        Settings = settings;
    }

    /// <summary>The name targets and <c>--feature</c> use.</summary>
    public string Name { get; }

    /// <summary>What the feature does.</summary>
    public FeatureSettings Settings { get; }
}

/// <summary>The programs of a C-family toolchain; a bare name is looked up on PATH.</summary>
public sealed class CCTools
{
    /// <summary>The C compiler, for .c and .m files.</summary>
    public string? CCompiler { get; init; }

    /// <summary>The C++ compiler, for .cpp and .mm files.</summary>
    public string? CppCompiler { get; init; }

    /// <summary>The program that links; default: the C++ compiler.</summary>
    public string? Linker { get; init; }

    /// <summary>The archiver of static libraries.</summary>
    public string? Archiver { get; init; }
}

/// <summary>A C-family toolchain: the platform it runs on, the one it builds for, its tools and features.</summary>
public sealed class CCToolchainSettings
{
    /// <summary>The platform the tools run on.</summary>
    public Platform ExecutionPlatform { get; init; }

    /// <summary>The platform the tools build for.</summary>
    public Platform TargetPlatform { get; init; }

    /// <summary>The tools.</summary>
    public CCTools Tools { get; init; } = new();

    /// <summary>The features targets may turn on and off.</summary>
    public IReadOnlyList<Feature> Features { get; init; } = [];
}

/// <summary>The programs of a Zig toolchain.</summary>
public sealed class ZigTools
{
    /// <summary>The zig program; a bare name is looked up on PATH.</summary>
    public string? Zig { get; init; }
}

/// <summary>A Zig toolchain.</summary>
public sealed class ZigToolchainSettings
{
    /// <summary>The platform zig runs on.</summary>
    public Platform ExecutionPlatform { get; init; }

    /// <summary>The platform zig builds for.</summary>
    public Platform TargetPlatform { get; init; }

    /// <summary>The tools.</summary>
    public ZigTools Tools { get; init; } = new();
}

/// <summary>C and C++ toolchains and their features.</summary>
public static class CC
{
    /// <summary>A feature, for the <c>Features</c> of toolchains.</summary>
    public static Feature Feature(string name, FeatureSettings settings)
    {
        Names.Check(name, "feature name", "a feature");
        return new Feature(name, settings);
    }

    /// <summary>Declares a toolchain; MODULE.csx registers it by its label.</summary>
    public static void Toolchain(
        string name, CCToolchainSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.DeclareToolchain(Family.CC, name, settings, file, line);
}

/// <summary>Objective-C toolchains.</summary>
public static class ObjectiveC
{
    /// <summary>Declares a toolchain; MODULE.csx registers it by its label.</summary>
    public static void Toolchain(
        string name, CCToolchainSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.DeclareToolchain(Family.ObjectiveC, name, settings, file, line);
}

/// <summary>Zig toolchains.</summary>
public static class Zig
{
    /// <summary>Declares a toolchain; MODULE.csx registers it by its label.</summary>
    public static void Toolchain(
        string name, ZigToolchainSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        Declarations.DeclareToolchain(Family.Zig, name, settings, file, line);
}
