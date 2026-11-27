// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>How a command builds, from its command line and the module.</summary>
internal sealed record BuildOptions
{
    public BuildMode Mode { get; init; } = BuildMode.Debug;

    public Platform Platform { get; init; } = Host.Platform;

    public int Jobs { get; init; } = Environment.ProcessorCount;

    public bool KeepGoing { get; init; }

    public IReadOnlyList<string> FeaturesOn { get; init; } = [];

    public IReadOnlyList<string> FeaturesOff { get; init; } = [];

    public IReadOnlyList<string> CompilerOptions { get; init; } = [];

    public IReadOnlyList<string> CCompilerOptions { get; init; } = [];

    public IReadOnlyList<string> CppCompilerOptions { get; init; } = [];

    public IReadOnlyList<string> LinkerOptions { get; init; } = [];

    public IReadOnlyList<string> ZigCompilerOptions { get; init; } = [];

    /// <summary>Whether C-family code is built with LLVM source-based coverage, for <c>tazel coverage</c>.</summary>
    public bool Coverage { get; init; }

    /// <summary>What the build may take from its caches: <c>--recompile</c> and <c>--no-cache</c>.</summary>
    public CachePolicy CachePolicy { get; init; } = CachePolicy.Normal;
}

/// <summary>Where a build writes, below the state directory of the main module.</summary>
internal sealed class OutputLayout(string mainRoot, BuildOptions options)
{
    /// <summary>The root of the main module, where every action runs.</summary>
    public string MainRoot { get; } = mainRoot;

    /// <summary>The outputs of this platform and mode, relative to <see cref="MainRoot"/>.</summary>
    public string OutRoot { get; } =
        $"{ModuleFiles.StateDirectory}/out/{options.Platform.Name()}-{options.Mode.ToString().ToLowerInvariant()}" +
        (options.Coverage ? "-coverage" : "");

    /// <summary>Where <c>tazel coverage</c> writes its report, relative to <see cref="MainRoot"/>.</summary>
    public string CoverageRoot { get; } = $"{ModuleFiles.StateDirectory}/coverage";

    /// <summary>Where the tests write their logs, relative to <see cref="MainRoot"/>.</summary>
    public string TestLogs { get; } = $"{ModuleFiles.StateDirectory}/testlogs";

    /// <summary>zig's own cache, relative to <see cref="MainRoot"/>.</summary>
    public string ZigCache { get; } = $"{ModuleFiles.StateDirectory}/zig-cache";

    /// <summary>The output directory of the package of <paramref name="target"/>, relative to <see cref="MainRoot"/>.</summary>
    public string PackageOut(ProjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var package = target.Label.Package;
        var prefix = package.Repository is null ? OutRoot : $"{OutRoot}/external/{package.Repository}";
        return package.Path.Length == 0 ? prefix : $"{prefix}/{package.Path}";
    }

    /// <summary>The full path of <paramref name="relative"/>.</summary>
    public string Full(string relative) => Path.GetFullPath(Path.Combine(MainRoot, relative));
}
