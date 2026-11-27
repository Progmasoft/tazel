// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>Where a layer of configuration comes from, lowest first; the command line is above them all.</summary>
public enum LayerKind
{
    /// <summary><c>$TAZEL_HOME/User/</c>: the user's own settings.</summary>
    User,

    /// <summary>A directory above the one the command runs in.</summary>
    Parent,

    /// <summary><c>$TAZEL_HOME/Projects/&lt;key&gt;/</c>: this machine's settings of the module, outside it.</summary>
    Project,

    /// <summary>The directory the command runs in.</summary>
    Local,
}

/// <summary>One file of a layer.</summary>
/// <param name="Kind">The layer.</param>
/// <param name="File">Its full path.</param>
public sealed record Layer(LayerKind Kind, string File)
{
    /// <summary>The layer in words, for <c>tazel options get</c>.</summary>
    public string Description => Kind switch
    {
        LayerKind.User => "the user's",
        LayerKind.Parent => "a directory above",
        LayerKind.Project => "this machine's for the project",
        _ => "the current directory's",
    };
}

/// <summary>
/// The files of a kind that hold in a directory, lowest first: <c>$TAZEL_HOME/User/</c>, the directories above, the
/// farthest first, <c>$TAZEL_HOME/Projects/&lt;key&gt;/</c> of the module the directory is in, and the directory itself.
/// So the project's settings outrank every directory above, the module root too unless it is the directory itself.
/// A file counted in one layer is not counted again in another.
/// </summary>
public static class ConfigurationLayers
{
    /// <summary>The files named <paramref name="fileName"/> that apply in <paramref name="directory"/>, lowest first.</summary>
    public static IReadOnlyList<Layer> For(string fileName, string directory, bool withProject = true)
    {
        var full = Path.GetFullPath(directory);
        var candidates = new List<Layer> { new(LayerKind.User, Path.Combine(TazelHome.Directory(), "User", fileName)) };
        var above = new List<string>();
        for (var parent = Directory.GetParent(full); parent is not null; parent = parent.Parent)
        {
            above.Add(Path.Combine(parent.FullName, fileName));
        }

        candidates.AddRange(above.AsEnumerable().Reverse().Select(file => new Layer(LayerKind.Parent, file)));
        if (withProject && ModuleRoot(full) is { } root)
        {
            candidates.Add(new Layer(LayerKind.Project, Path.Combine(ProjectKey.Directory(root), fileName)));
        }

        candidates.Add(new Layer(LayerKind.Local, Path.Combine(full, fileName)));
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var layers = new List<Layer>();

        // The highest layer keeps a file two layers name.
        foreach (var layer in candidates.AsEnumerable().Reverse())
        {
            if (IsFile(layer.File) && seen.Add(ConfigurationResolver.Canonical(layer.File)))
            {
                layers.Add(layer);
            }
        }

        layers.Reverse();
        return layers;
    }

    /// <summary>The file of one layer for <paramref name="directory"/>, whether it exists or not.</summary>
    public static string FileOf(LayerKind kind, string fileName, string directory, bool create = false) => kind switch
    {
        LayerKind.User => Path.Combine(TazelHome.Directory(), "User", fileName),
        LayerKind.Project => Path.Combine(
            ProjectKey.Directory(ModuleRoot(Path.GetFullPath(directory))
                ?? throw new ConfigurationHomeException($"{Path.GetFullPath(directory)} is in no module, so it has no project settings"), create),
            fileName),
        _ => Path.Combine(Path.GetFullPath(directory), fileName),
    };

    // The nearest directory at or above with a MODULE.csx, or null.
    private static string? ModuleRoot(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "MODULE.csx")))
            {
                return current.FullName;
            }
        }

        return null;
    }

    // A file that cannot be looked at is an error, never a file that is not there, so no layer is skipped quietly.
    private static bool IsFile(string file)
    {
        try
        {
            return (File.GetAttributes(file) & FileAttributes.Directory) == 0;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationHomeException($"{file} could not be inspected: {error.Message}", error);
        }
    }
}
