// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>What a planned target makes, and what its dependents take from it.</summary>
internal sealed class TargetProducts
{
    /// <summary>The action that makes the target's main file: the archive, the link or the zig command.</summary>
    public BuildAction? Main { get; set; }

    /// <summary>The file dependents link: a static library, a shared library or its import library.</summary>
    public string? Library { get; set; }

    public bool IsShared { get; set; }

    public bool AlwaysLink { get; set; }

    /// <summary>The program, for executables and tests.</summary>
    public string? Executable { get; set; }

    /// <summary>The directories of the shared libraries a program loads, for rpath and PATH.</summary>
    public List<string> SharedLibraryDirectories { get; } = [];

    /// <summary>Include directories for this target's dependents.</summary>
    public List<string> PublicIncludes { get; } = [];

    /// <summary>Macros for this target's dependents.</summary>
    public List<string> PublicDefines { get; } = [];

    /// <summary>Headers dependents may include, for the inputs of their compilations.</summary>
    public List<string> PublicHeaders { get; } = [];

    /// <summary>Actions whose outputs dependents' compilations read, such as headers zig emits.</summary>
    public List<BuildAction> HeaderActions { get; } = [];

    /// <summary>Linker options, frameworks and libraries that go to whatever links this library.</summary>
    public List<string> LinkTail { get; } = [];
}

/// <summary>Turns targets into the actions that build them.</summary>
internal sealed partial class Planner
{
    private readonly Dictionary<ProjectTarget, TargetProducts> _planned = [];

    public Planner(Workspace workspace, BuildOptions options, ToolchainChooser toolchains)
    {
        Workspace = workspace;
        Options = options;
        Toolchains = toolchains;
        Layout = new OutputLayout(workspace.Main.Root, options);
        ActionEnvironment = Processes.ForActions();
    }

    public Workspace Workspace { get; }

    public BuildOptions Options { get; }

    public ToolchainChooser Toolchains { get; }

    public OutputLayout Layout { get; }

    public Dictionary<string, string> ActionEnvironment { get; }

    /// <summary>What <paramref name="target"/> makes, planning it and what it depends on once.</summary>
    public TargetProducts Plan(ProjectTarget target)
    {
        if (_planned.TryGetValue(target, out var known))
        {
            return known;
        }

        var products = target.Family switch
        {
            Family.Zig => PlanZig(target),
            Family.Plugin => PlanStep(target),
            _ => PlanCC(target),
        };
        _planned[target] = products;
        return products;
    }

    /// <summary>Checks the features the command line names against the toolchains the build used.</summary>
    public void CheckCommandLineFeatures(IEnumerable<CCToolchain> used)
    {
        var known = used.SelectMany(toolchain => toolchain.Features).Select(feature => feature.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in Options.FeaturesOn.Concat(Options.FeaturesOff))
        {
            if (!known.Contains(name))
            {
                var near = Cli.Suggestions.Nearest(name, known);
                throw new ProjectException(
                    $"no toolchain of this build has the feature '{name}'{(near is null ? "" : $"; did you mean '{near}'?")}");
            }
        }
    }

    /// <summary>The libraries a link takes for <paramref name="target"/>'s dependencies, each before what it needs.</summary>
    private List<(ProjectTarget Target, TargetProducts Products)> LinkOrder(ProjectTarget target)
    {
        var post = new List<(ProjectTarget Target, TargetProducts Products)>();
        var seen = new HashSet<(ProjectTarget, bool)>();
        foreach (var dependency in target.Dependencies)
        {
            Visit(dependency, belowShared: false);
        }

        // Reversed, each library comes before the libraries it needs; a library reached twice keeps its last place.
        post.Reverse();
        var placed = new HashSet<ProjectTarget>();
        return post.Where(entry => placed.Add(entry.Target)).ToList();

        void Visit(ProjectTarget library, bool belowShared)
        {
            if (!seen.Add((library, belowShared)))
            {
                return;
            }

            var products = Plan(library);
            // What a shared library links statically is inside it; only its shared libraries still have to be found.
            foreach (var next in library.Dependencies)
            {
                Visit(next, belowShared || products.IsShared);
            }

            if (!belowShared || products.IsShared)
            {
                post.Add((library, products));
            }
        }
    }

    /// <summary>A library on the link line; one linked whole is wrapped so that none of its objects is dropped.</summary>
    private static string[] LibraryArguments(string library, bool whole, OS os) =>
        !whole ? [library]
        : os == OS.MacOS ? [$"-Wl,-force_load,{library}"]
        : ["-Wl,--whole-archive", library, "-Wl,--no-whole-archive"];

    /// <summary>Where a program finds a shared library at run time, relative to itself; Windows has no run path.</summary>
    private static string[] RunPath(string outputDirectory, string directory, OS os)
    {
        var relative = Path.GetRelativePath(outputDirectory, directory).Replace('\\', '/');
        var suffix = relative == "." ? "" : "/" + relative;
        return os switch
        {
            OS.MacOS => [$"-Wl,-rpath,@loader_path{suffix}"],
            OS.Windows => [],
            _ => [$"-Wl,-rpath,$ORIGIN{suffix}"],
        };
    }

    /// <summary>The linker arguments for the libraries of <paramref name="target"/>, and the actions that make them.</summary>
    private (List<string> Arguments, List<string> Inputs, List<BuildAction> Actions, List<string> SharedDirectories)
        LinkLibraries(ProjectTarget target, string outputDirectory, bool forZig = false)
    {
        var arguments = new List<string>();
        var inputs = new List<string>();
        var actions = new List<BuildAction>();
        var sharedDirectories = new List<string>();
        var tail = new List<string>();
        var os = Options.Platform.GetOS();
        foreach (var (_, products) in LinkOrder(target))
        {
            if (products.Main is { } main)
            {
                actions.Add(main);
            }

            if (products.Library is { } library)
            {
                inputs.Add(library);
                arguments.AddRange(LibraryArguments(library, products.AlwaysLink && !products.IsShared && !forZig, os));
                var directory = Path.GetDirectoryName(library)!.Replace('\\', '/');
                if (products.IsShared && !sharedDirectories.Contains(directory))
                {
                    sharedDirectories.Add(directory);
                }
            }

            tail.AddRange(products.LinkTail);
        }

        if (!forZig)
        {
            arguments.AddRange(sharedDirectories.SelectMany(directory => RunPath(outputDirectory, directory, os)));
        }

        arguments.AddRange(tail);
        return (arguments, inputs, actions, sharedDirectories);
    }

    private static string Sanitized(string text) => new(text.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>The file names of libraries and programs on the platform of the build.</summary>
    private string StaticLibraryName(string name) =>
        Options.Platform.GetOS() == OS.Windows ? $"{name}.lib" : $"lib{name}.a";

    private string ExecutableName(string name) => Options.Platform.GetOS() == OS.Windows ? $"{name}.exe" : name;

    /// <summary>The file a shared library is written to, the file its dependents link, and its soname.</summary>
    private (string File, string LinkWith, string? Soname) SharedLibraryNames(string directory, string name, Version? version)
    {
        switch (Options.Platform.GetOS())
        {
            case OS.Windows:
                return ($"{directory}/{name}.dll", $"{directory}/{name}.lib", null);
            case OS.MacOS:
                var mac = version is null ? $"lib{name}.dylib" : $"lib{name}.{version.Major}.dylib";
                return ($"{directory}/{mac}", $"{directory}/{mac}", mac);
            default:
                if (version is null)
                {
                    return ($"{directory}/lib{name}.so", $"{directory}/lib{name}.so", $"lib{name}.so");
                }

                var full = $"lib{name}.so.{version.ToString(Math.Min(3, FieldCount(version)))}";
                return ($"{directory}/{full}", $"{directory}/{full}", $"lib{name}.so.{version.Major}");
        }
    }

    private static int FieldCount(Version version) => version.Build >= 0 ? 3 : version.Minor >= 0 ? 2 : 1;

    /// <summary>Makes <c>libx.so.1</c> and <c>libx.so</c> point at <c>libx.so.1.2.0</c>.</summary>
    private Action? SonameLinks(string file, string? soname, string name)
    {
        if (Options.Platform.GetOS() is OS.Windows or OS.MacOS || soname is null || Path.GetFileName(file) == soname)
        {
            return null;
        }

        var directory = Layout.Full(Path.GetDirectoryName(file)!);
        var target = Path.GetFileName(file);
        return () =>
        {
            foreach (var link in new[] { soname, $"lib{name}.so" })
            {
                var path = Path.Combine(directory, link);
                File.Delete(path);
                File.CreateSymbolicLink(path, target);
            }
        };
    }
}
