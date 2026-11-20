// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Threading;

namespace Progmasoft.Tazel.Dsl.Environment;

/// <summary>
/// The values of <c>tazel_envs.txt</c>, beside the MODULE.csx of the module the project file belongs to. A value is not
/// an environment variable: it reaches no shell, no program Tazel runs and not Tazel itself; only this class reads it.
/// </summary>
public static class GetEnvironment
{
    /// <summary>The value named <paramref name="name"/>, or null when <c>tazel_envs.txt</c> has no such name.</summary>
    public static string? From(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return EnvironmentValues.Current?.Get(name);
    }
}

/// <summary>Where the values of the running project file come from; Tazel sets it while the file runs.</summary>
internal interface IEnvironmentValues
{
    string? Get(string name);
}

/// <summary>The values of the project file that is running.</summary>
internal static class EnvironmentValues
{
    private static readonly AsyncLocal<IEnvironmentValues?> CurrentValues = new();

    public static IEnvironmentValues? Current => CurrentValues.Value;

    /// <summary>Makes <paramref name="values"/> those of the running file until the result is disposed.</summary>
    public static IDisposable Enter(IEnvironmentValues? values)
    {
        var previous = CurrentValues.Value;
        CurrentValues.Value = values;
        return new Restore(() => CurrentValues.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
