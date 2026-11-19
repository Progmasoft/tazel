// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Build.Prelude;

/// <summary>What Objective-C targets have beyond C and C++ ones.</summary>
public interface IObjectiveCSettings
{
    /// <summary>Files compiled without automatic reference counting; every other file has it.</summary>
    Files NonAutomaticReferenceCountingSources { get; }

    /// <summary>Whether Clang modules are on (<c>-fmodules</c>).</summary>
    bool EnableClangModules { get; }

    /// <summary>The name of the module this target is (<c>-fmodule-name</c>).</summary>
    string? ModuleName { get; }

    /// <summary>The module map of this target.</summary>
    string? ModuleMap { get; }
}

/// <summary>How an Objective-C test runs.</summary>
public enum ObjectiveCTestRunner
{
    /// <summary>The test is an XCTest bundle that <c>xctest</c> runs.</summary>
    XCTest,

    /// <summary>The test is an executable judged by its exit status.</summary>
    Executable,
}

/// <summary>A macOS application bundle (<c>.app</c>).</summary>
public sealed class Bundle
{
    /// <summary>The bundle identifier, such as <c>com.example.greeter</c>.</summary>
    public string Identifier { get; init; } = "";

    /// <summary>The <c>Info.plist</c> of the bundle.</summary>
    public string? InfoPlist { get; init; }

    /// <summary>Files for <c>Contents/Resources</c>.</summary>
    public Files Resources { get; init; } = Files.None;

    /// <summary>The identity that signs the bundle; default: not signed.</summary>
    public string? CodeSigningIdentity { get; init; }
}

/// <summary>An Objective-C static library.</summary>
public sealed class ObjectiveCStaticLibrarySettings : CCStaticLibrarySettings, IObjectiveCSettings
{
    /// <inheritdoc/>
    public Files NonAutomaticReferenceCountingSources { get; init; } = Files.None;

    /// <inheritdoc/>
    public bool EnableClangModules { get; init; }

    /// <inheritdoc/>
    public string? ModuleName { get; init; }

    /// <inheritdoc/>
    public string? ModuleMap { get; init; }
}

/// <summary>An Objective-C shared library.</summary>
public sealed class ObjectiveCSharedLibrarySettings : CCSharedLibrarySettings, IObjectiveCSettings
{
    /// <inheritdoc/>
    public Files NonAutomaticReferenceCountingSources { get; init; } = Files.None;

    /// <inheritdoc/>
    public bool EnableClangModules { get; init; }

    /// <inheritdoc/>
    public string? ModuleName { get; init; }

    /// <inheritdoc/>
    public string? ModuleMap { get; init; }
}

/// <summary>An Objective-C executable, a plain program or an application bundle.</summary>
public sealed class ObjectiveCExecutableSettings : CCExecutableSettings, IObjectiveCSettings
{
    /// <inheritdoc/>
    public Files NonAutomaticReferenceCountingSources { get; init; } = Files.None;

    /// <inheritdoc/>
    public bool EnableClangModules { get; init; }

    /// <inheritdoc/>
    public string? ModuleName { get; init; }

    /// <inheritdoc/>
    public string? ModuleMap { get; init; }

    /// <summary>The application bundle; default: a plain program.</summary>
    public Bundle? Bundle { get; init; }
}

/// <summary>An Objective-C test.</summary>
public sealed class ObjectiveCTestSettings : CCTestSettings, IObjectiveCSettings
{
    /// <inheritdoc/>
    public Files NonAutomaticReferenceCountingSources { get; init; } = Files.None;

    /// <inheritdoc/>
    public bool EnableClangModules { get; init; }

    /// <inheritdoc/>
    public string? ModuleName { get; init; }

    /// <inheritdoc/>
    public string? ModuleMap { get; init; }

    /// <summary>How the test runs; default: <see cref="ObjectiveCTestRunner.Executable"/>.</summary>
    public ObjectiveCTestRunner TestRunner { get; init; } = ObjectiveCTestRunner.Executable;
}

/// <summary>The Objective-C targets have these defaults beyond those of C and C++.</summary>
internal static class ObjectiveCDefaults
{
    /// <summary>Where an Objective-C target builds when it names no platforms: the SDK frameworks are Apple's.</summary>
    public static IReadOnlyList<Platform> Platforms { get; } = [Platform.MacOSArm64, Platform.MacOSX86_64];
}
