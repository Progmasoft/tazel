// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl;

/// <summary>What a project file wrote wrong, with the line it wrote it on.</summary>
public sealed class DslException : Exception
{
    /// <summary>A problem without a place.</summary>
    public DslException(string message)
        : base(message)
    {
    }

    /// <summary>A problem and its cause.</summary>
    public DslException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>Nothing to say.</summary>
    public DslException()
    {
    }
}

/// <summary>The languages a target is written in.</summary>
internal enum Family
{
    CC,
    ObjectiveC,
    Zig,
}

/// <summary>What a target makes.</summary>
internal enum TargetKind
{
    StaticLibrary,
    SharedLibrary,
    Executable,
    Test,
    Module,
}

/// <summary>A target as a project file declared it, before anything is checked against other files.</summary>
internal sealed record TargetDeclaration(
    Family Family, TargetKind Kind, string Name, string Label, object Settings, string File, int Line);

/// <summary>A toolchain as a project file declared it.</summary>
internal sealed record ToolchainDeclaration(
    Family Family, string Name, string Label, object Settings, string File, int Line);

/// <summary>What one project file declares while it runs.</summary>
internal sealed class DeclarationScope(string packagePath, string file)
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);

    public string PackagePath { get; } = packagePath;

    public string File { get; } = file;

    public List<TargetDeclaration> Targets { get; } = [];

    public List<ToolchainDeclaration> Toolchains { get; } = [];

    public string LabelOf(string name) => PackagePath.Length == 0 ? $"//:{name}" : $"//{PackagePath}:{name}";

    public string Claim(string name, int line)
    {
        Names.Check(name, "target name", Place(line));
        if (!_names.Add(name))
        {
            throw new DslException($"{Place(line)}: '{name}' is declared twice in this file");
        }

        return LabelOf(name);
    }

    public string Place(int line) => $"{File}:{line}";
}

/// <summary>Checks the names a project file gives.</summary>
internal static partial class Names
{
    public static void Check(string name, string what, string place)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Pattern().IsMatch(name))
        {
            throw new DslException(
                $"{place}: the {what} '{name}' must be letters, digits and _ + . -");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_+.-]+$")]
    private static partial Regex Pattern();
}

/// <summary>The project file that is running, for the declarations it makes.</summary>
internal static class Declarations
{
    private static readonly AsyncLocal<DeclarationScope?> CurrentScope = new();

    public static DeclarationScope Current =>
        CurrentScope.Value ?? throw new DslException("targets and toolchains are declared in a BUILD.csx");

    /// <summary>Makes <paramref name="scope"/> the file that is running until the result is disposed.</summary>
    public static IDisposable Enter(DeclarationScope scope)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return new Restore(() => CurrentScope.Value = previous);
    }

    public static Target Declare(Family family, TargetKind kind, string name, object settings, string file, int line)
    {
        var scope = Current;
        if (settings is null)
        {
            throw new DslException($"{scope.Place(line)}: '{name}' needs its settings: new() {{ ... }}");
        }

        var label = scope.Claim(name, line);
        scope.Targets.Add(new TargetDeclaration(family, kind, name, label, settings, scope.File, line));
        return new Target(label);
    }

    public static void DeclareToolchain(Family family, string name, object settings, string file, int line)
    {
        var scope = Current;
        if (settings is null)
        {
            throw new DslException($"{scope.Place(line)}: the toolchain '{name}' needs its settings");
        }

        var label = scope.Claim(name, line);
        scope.Toolchains.Add(new ToolchainDeclaration(family, name, label, settings, scope.File, line));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
