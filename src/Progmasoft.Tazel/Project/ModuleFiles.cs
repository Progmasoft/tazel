// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Progmasoft.Tazel.Project;

/// <summary>Where the project files of a module are.</summary>
internal static class ModuleFiles
{
    /// <summary>The file at the root of a module.</summary>
    public const string ModuleFile = "MODULE.csx";

    /// <summary>The directory below the root where Tazel keeps what it makes.</summary>
    public const string StateDirectory = ".tazel";

    /// <summary>The names a package's project file may have, in the order they are looked for; the first wins.</summary>
    public static IReadOnlyList<string> BuildFileNames { get; } =
        ["BUILD.csx", "BUILD.tzl", "BUILD.tazel", "BUILD", "Tazel.build"];

    /// <summary>The root of the module <paramref name="directory"/> is in: the nearest directory with a MODULE.csx.</summary>
    public static string FindRoot(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ModuleFile)))
            {
                return current.FullName;
            }
        }

        throw new ProjectException($"there is no {ModuleFile} in {Path.GetFullPath(directory)} or above it");
    }

    /// <summary>The project file of the package at <paramref name="directory"/>, or null when it has none.</summary>
    public static string? BuildFileIn(string directory)
    {
        // The lookup is case-sensitive on every system, so build.csx is not a project file anywhere.
        var names = new HashSet<string>(
            Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal);
        return BuildFileNames.FirstOrDefault(names.Contains) is { } name ? Path.Combine(directory, name) : null;
    }

    /// <summary>
    /// Every package of the module at <paramref name="root"/>, by its path: directories with a project file, less
    /// hidden ones and the directories of other modules.
    /// </summary>
    public static IReadOnlyList<(string Path, string File)> Packages(string root)
    {
        var found = new List<(string, string)>();
        Visit(root, "");
        return found.OrderBy(entry => entry.Item1, StringComparer.Ordinal).ToList();

        void Visit(string directory, string path)
        {
            if (BuildFileIn(directory) is { } file)
            {
                found.Add((path, file));
            }

            foreach (var child in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (child.Name.StartsWith('.') || child.LinkTarget is not null ||
                    File.Exists(Path.Combine(child.FullName, ModuleFile)))
                {
                    continue;
                }

                Visit(child.FullName, path.Length == 0 ? child.Name : $"{path}/{child.Name}");
            }
        }
    }
}
