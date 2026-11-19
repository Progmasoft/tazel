// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Build.Prelude;

/// <summary>What every target has: what it depends on, who may depend on it, and where it builds.</summary>
public abstract class TargetSettings
{
    /// <summary>The targets this one depends on, by label or by the value their declaration returned.</summary>
    public TargetReferences Depends { get; init; } = TargetReferences.None;

    /// <summary>Who may depend on this target; default: <see cref="Visibility.Private"/>.</summary>
    public Visibility Visibility { get; init; } = Visibility.Private;

    /// <summary>
    /// The platforms this target builds for; default: all. Elsewhere <c>//...</c> skips it, and naming it is an error.
    /// </summary>
    public IReadOnlyList<Platform> TargetConstraints { get; init; } = [];

    /// <summary>Words for this target; <c>manual</c> keeps it out of <c>//...</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

/// <summary>A toolchain feature a target turns on or off.</summary>
public sealed class FeatureToggle
{
    internal FeatureToggle(string name, bool enabled)
    {
        Name = name;
        Enabled = enabled;
    }

    /// <summary>The name of the feature.</summary>
    public string Name { get; }

    /// <summary>Whether it is turned on.</summary>
    public bool Enabled { get; }
}

/// <summary>How an executable links the libraries it depends on.</summary>
public enum LinkingMode
{
    /// <summary>Static libraries where a dependency is one; the default.</summary>
    PreferStatic,

    /// <summary>The same, and system libraries as shared libraries.</summary>
    PreferShared,

    /// <summary>Everything static, the C library included (<c>-static</c>).</summary>
    FullyStatic,
}

/// <summary>What C, C++, Objective-C and Objective-C++ targets compile and how.</summary>
public abstract class CCSettings : TargetSettings
{
    /// <summary>The files to compile; the language of each comes from its extension.</summary>
    public Files Sources { get; init; } = Files.None;

    /// <summary>Headers this target and its dependents include.</summary>
    public Files Headers { get; init; } = Files.None;

    /// <summary>Headers that are included but never compiled alone.</summary>
    public Files TextualHeaders { get; init; } = Files.None;

    /// <summary>Directories of this target's headers, for it and its dependents.</summary>
    public IReadOnlyList<string> IncludeDirectories { get; init; } = [];

    /// <summary>Options for every file.</summary>
    public IReadOnlyList<string> CompilerOptions { get; init; } = [];

    /// <summary>Options for .c and .m files.</summary>
    public IReadOnlyList<string> CCompilerOptions { get; init; } = [];

    /// <summary>Options for .cpp, .cc, .cxx and .mm files.</summary>
    public IReadOnlyList<string> CppCompilerOptions { get; init; } = [];

    /// <summary>Options for the link; a library's go to the links of what depends on it.</summary>
    public IReadOnlyList<string> LinkerOptions { get; init; } = [];

    /// <summary>Macros for this target and its dependents.</summary>
    public IReadOnlyList<string> PreprocessorDefinitions { get; init; } = [];

    /// <summary>Macros for this target alone.</summary>
    public IReadOnlyList<string> LocalPreprocessorDefinitions { get; init; } = [];

    /// <summary>Toolchain features this target turns on or off.</summary>
    public IReadOnlyList<FeatureToggle> Features { get; init; } = [];

    /// <summary>Frameworks of the SDK to link (<c>-framework</c>).</summary>
    public IReadOnlyList<string> SdkFrameworks { get; init; } = [];

    /// <summary>Frameworks of the SDK to link weakly (<c>-weak_framework</c>).</summary>
    public IReadOnlyList<string> WeakSdkFrameworks { get; init; } = [];

    /// <summary>Libraries of the SDK or the system to link (<c>-l</c>).</summary>
    public IReadOnlyList<string> SdkDynamicLibraries { get; init; } = [];
}

/// <summary>A static library.</summary>
public class CCStaticLibrarySettings : CCSettings
{
    /// <summary>Whether every object goes into what links it, used or not.</summary>
    public bool AlwaysLink { get; init; }
}

/// <summary>A shared library.</summary>
public class CCSharedLibrarySettings : CCSettings
{
    /// <summary>The version of the library, as in <c>libx.so.1.2.0</c>; default: none.</summary>
    public Version? LibraryVersion { get; init; }

    /// <summary>More files for the linker, such as linker scripts.</summary>
    public Files AdditionalLinkerInputs { get; init; } = Files.None;
}

/// <summary>What a program gets when Tazel runs it.</summary>
public interface IRunSettings
{
    /// <summary>The arguments <c>tazel run</c> and <c>tazel test</c> pass.</summary>
    IReadOnlyList<string> Arguments { get; }

    /// <summary>Files the program reads; they sit in its runfiles directory, where it runs.</summary>
    Files RuntimeData { get; }

    /// <summary>The environment of the program.</summary>
    Dictionary<string, string> EnvironmentVariables { get; }
}

/// <summary>How a test runs.</summary>
public interface ITestSettings : IRunSettings
{
    /// <summary>Variables of Tazel's environment the test sees too.</summary>
    IReadOnlyList<string> InheritedEnvironmentVariables { get; }

    /// <summary>How much the test may take.</summary>
    TestSize TestSize { get; }

    /// <summary>The time the test may take; default: from <see cref="TestSize"/>.</summary>
    TimeSpan? Timeout { get; }

    /// <summary>Whether a failure is retried, up to three times.</summary>
    bool Flaky { get; }

    /// <summary>How many processes share the test (<c>TEST_TOTAL_SHARDS</c>); default: 1.</summary>
    int ShardCount { get; }

    /// <summary>Whether the test runs on this machine even when execution is remote.</summary>
    bool RunLocally { get; }
}

/// <summary>An executable.</summary>
public class CCExecutableSettings : CCSettings, IRunSettings
{
    /// <summary>How the executable links its libraries.</summary>
    public LinkingMode LinkingMode { get; init; } = LinkingMode.PreferStatic;

    /// <inheritdoc/>
    public Files RuntimeData { get; init; } = Files.None;

    /// <inheritdoc/>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <inheritdoc/>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();

    /// <summary>Whether the label and the module version are compiled in as macros.</summary>
    public bool StampBuildInformation { get; init; }

    /// <summary>More files for the linker.</summary>
    public Files AdditionalLinkerInputs { get; init; } = Files.None;
}

/// <summary>A test: an executable Tazel runs and judges by its exit status.</summary>
public class CCTestSettings : CCExecutableSettings, ITestSettings
{
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
