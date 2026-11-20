// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Build.Prelude;

/// <summary>How zig optimizes, as its <c>-O</c> names it.</summary>
public enum ZigOptimization
{
    /// <summary>No optimization, every safety check.</summary>
    Debug,

    /// <summary>Optimized, with safety checks.</summary>
    ReleaseSafe,

    /// <summary>Optimized for speed, without safety checks.</summary>
    ReleaseFast,

    /// <summary>Optimized for size, without safety checks.</summary>
    ReleaseSmall,
}

/// <summary>A Zig module: a root file other Zig targets import by a name.</summary>
public class ZigModuleSettings : TargetSettings
{
    /// <summary>The root file of the module.</summary>
    public string RootSourceFile { get; init; } = "";

    /// <summary>
    /// The files the root imports, for the cache; default: every <c>.zig</c> file beside the root file and below it.
    /// </summary>
    public Files Sources { get; init; } = Files.None;

    /// <summary>The modules this one imports: the name <c>@import</c> uses, and the label of a Zig module.</summary>
    public Dictionary<string, string> ModuleImports { get; init; } = new();

    /// <summary>How zig optimizes; default: Debug in a debug build, ReleaseSafe in a release build.</summary>
    public ZigOptimization? OptimizationMode { get; init; }
}

/// <summary>What every Zig target that zig compiles has.</summary>
public abstract class ZigCompiledSettings : ZigModuleSettings
{
    /// <summary>C and C++ files zig compiles into the target.</summary>
    public Files CSources { get; init; } = Files.None;

    /// <summary>Options for every file of <see cref="CSources"/>.</summary>
    public IReadOnlyList<string> CompilerOptions { get; init; } = [];

    /// <summary>Options for the .c files of <see cref="CSources"/>.</summary>
    public IReadOnlyList<string> CCompilerOptions { get; init; } = [];

    /// <summary>Options for the .cpp files of <see cref="CSources"/>.</summary>
    public IReadOnlyList<string> CppCompilerOptions { get; init; } = [];

    /// <summary>Options for zig itself, as written.</summary>
    public IReadOnlyList<string> ZigCompilerOptions { get; init; } = [];

    /// <summary>Options for the linker.</summary>
    public IReadOnlyList<string> LinkerOptions { get; init; } = [];

    /// <summary>Whether the C library is linked (<c>-lc</c>).</summary>
    public bool LinkCStandardLibrary { get; init; }

    /// <summary>Whether the code is position independent (<c>-fPIC</c>).</summary>
    public bool PositionIndependentCode { get; init; }

    /// <summary>Whether symbols are stripped (<c>-fstrip</c>).</summary>
    public bool StripSymbols { get; init; }

    /// <summary>Whether link-time optimization is on (<c>-flto</c>).</summary>
    public bool LinkTimeOptimization { get; init; }
}

/// <summary>A Zig static library.</summary>
public sealed class ZigStaticLibrarySettings : ZigCompiledSettings
{
    /// <summary>The C header zig emits (<c>-femit-h</c>); default: none.</summary>
    public string? EmitHeader { get; init; }
}

/// <summary>A Zig shared library.</summary>
public sealed class ZigSharedLibrarySettings : ZigCompiledSettings
{
    /// <summary>The version of the library; default: none.</summary>
    public Version? LibraryVersion { get; init; }

    /// <summary>The C header zig emits (<c>-femit-h</c>); default: none.</summary>
    public string? EmitHeader { get; init; }
}

/// <summary>A Zig executable.</summary>
public class ZigExecutableSettings : ZigCompiledSettings, IRunSettings
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <inheritdoc/>
    public Files RuntimeData { get; init; } = Files.None;

    /// <inheritdoc/>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();
}

/// <summary>A Zig test, which <c>zig test</c> builds.</summary>
public sealed class ZigTestSettings : ZigExecutableSettings, ITestSettings
{
    /// <summary>Runs only the tests whose names hold this text (<c>--test-filter</c>).</summary>
    public string? TestFilter { get; init; }

    /// <inheritdoc/>
    public IReadOnlyList<string> InheritedEnvironmentVariables { get; init; } = [];

    /// <inheritdoc/>
    public TestSize TestSize { get; init; } = TestSize.Medium;

    /// <inheritdoc/>
    public TimeSpan? Timeout { get; init; }

    /// <inheritdoc/>
    public bool Flaky { get; init; }

    /// <inheritdoc/>
    public int ShardCount { get; init; } = 1;

    /// <inheritdoc/>
    public bool RunLocally { get; init; }
}
