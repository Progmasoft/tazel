// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.PluginApi.Steps;

/// <summary>
/// A build step: work of a plugin that reads <see cref="Inputs"/> and writes <see cref="Outputs"/> into its output
/// directory. It runs again only when its inputs, its outputs or its <see cref="Key"/> changed. A C-family or Zig target
/// that depends on it runs after it and finds headers in its output directory.
/// </summary>
public sealed class StepSettings : TargetSettings
{
    /// <summary>The files the step reads, below the project file.</summary>
    public Files Inputs { get; init; } = Files.None;

    /// <summary>The files the step writes, relative to its output directory.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>What else changes what the step makes, such as its options; a different key runs it again.</summary>
    public string Key { get; init; } = "";

    /// <summary>The work of the step.</summary>
    public Func<IStepContext, CancellationToken, Task>? Run { get; init; }
}

/// <summary>Declares build steps; the methods of a plugin call it from the BUILD.csx files that use them.</summary>
public static class Step
{
    /// <summary>Declares the step <paramref name="name"/> in the BUILD.csx that is running.</summary>
    public static Target Declare(
        string name, StepSettings settings, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Run is null)
        {
            throw new DslException($"{Declarations.Current.Place(line)}: the step '{name}' needs its Run");
        }

        return Declarations.Declare(Family.Plugin, TargetKind.Step, name, settings, file, line);
    }
}

/// <summary>Where a step runs and what it can ask of Tazel.</summary>
public interface IStepContext
{
    /// <summary>The label of the step.</summary>
    string Label { get; }

    /// <summary>The root of the module of the step.</summary>
    string ModuleRoot { get; }

    /// <summary>The directory of the project file that declared the step.</summary>
    string PackageDirectory { get; }

    /// <summary>The directory the step writes its outputs to; it exists when the step runs.</summary>
    string OutputDirectory { get; }

    /// <summary>The full paths of the inputs.</summary>
    IReadOnlyList<string> Inputs { get; }

    /// <summary>The program or library each target the step depends on made, by its label, as a full path.</summary>
    IReadOnlyDictionary<string, string> Dependencies { get; }

    /// <summary>The platform the build is for.</summary>
    Platform Platform { get; }

    /// <summary>Whether the build is a debug or a release build.</summary>
    BuildMode Mode { get; }

    /// <summary>The full path of a program on PATH, or null.</summary>
    string? FindProgram(string name);

    /// <summary>
    /// Runs <paramref name="program"/> directly, without a shell, with the environment of the actions of the build, in
    /// <paramref name="workingDirectory"/> or the root of the module; the result has its exit status and output.
    /// </summary>
    Task<StepRun> RunAsync(
        string program, IReadOnlyList<string> arguments, string? workingDirectory, CancellationToken cancellationToken);

    /// <summary>Runs <paramref name="program"/> in the root of the module; see the overload with every parameter.</summary>
    Task<StepRun> RunAsync(string program, IReadOnlyList<string> arguments) =>
        RunAsync(program, arguments, null, CancellationToken.None);

    /// <summary>Runs <paramref name="program"/> in <paramref name="workingDirectory"/>; see the overload with every parameter.</summary>
    Task<StepRun> RunAsync(string program, IReadOnlyList<string> arguments, string? workingDirectory) =>
        RunAsync(program, arguments, workingDirectory, CancellationToken.None);

    /// <summary>Adds a line to what the step prints, shown with the step when the build reports it.</summary>
    void Log(string message);
}

/// <summary>What a program a step ran did.</summary>
/// <param name="ExitCode">Its exit status.</param>
/// <param name="Output">What it printed, its errors among it.</param>
public sealed record StepRun(int ExitCode, string Output);
