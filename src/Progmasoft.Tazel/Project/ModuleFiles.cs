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

    /// <summary>The name of a package's project file, the only one: case-sensitive on every system.</summary>
    public const string BuildFile = "BUILD.csx";

    /// <summary>The root of the module <paramref name="directory"/> is in: the nearest directory with a MODULE.csx.</summary>
    public static string FindRoot(string directory) => RootOf(directory)
        ?? throw new ProjectException($"there is no {ModuleFile} in {Path.GetFullPath(directory)} or above it");

    /// <summary>The root of the module <paramref name="directory"/> is in, or null when it is in none.</summary>
    public static string? RootOf(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, ModuleFile)))
            {
                return current.FullName;
            }
        }

        return null;
    }

    /// <summary>The project file of the package at <paramref name="directory"/>, or null when it has none.</summary>
    public static string? BuildFileIn(string directory)
    {
        // The lookup is case-sensitive on every system, so build.csx is not a project file anywhere.
        var names = new HashSet<string>(
            Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal);
        if (names.Contains(BuildFile))
        {
            return Path.Combine(directory, BuildFile);
        }

        // A file of an earlier name would leave its targets out without a word; it is refused instead.
        if (CsxMigration.LegacyNames.FirstOrDefault(names.Contains) is { } legacy)
        {
            throw new ProjectException(
                $"{Path.Combine(directory, legacy)}: a project file is named BUILD.csx and nothing else; rename it, or let " +
                "tazel limited-time C# rename it and write its Depends as labels");
        }

        return null;
    }

    /// <summary>
    /// Every package of the module at <paramref name="root"/>, by its path: directories with a project file, less
    /// hidden ones, the directories of other modules and what its <c>.tazelignore</c> files ignore. An ignored
    /// directory is not entered.
    /// </summary>
    public static IReadOnlyList<(string Path, string File)> Packages(string root)
    {
        var found = new List<(string, string)>();
        var ignore = Ignore.IgnoreRules.For(root);
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
                var childPath = path.Length == 0 ? child.Name : $"{path}/{child.Name}";
                if (child.Name.StartsWith('.') || child.LinkTarget is not null ||
                    File.Exists(Path.Combine(child.FullName, ModuleFile)) || ignore.Ignored(childPath, directory: true))
                {
                    continue;
                }

                Visit(child.FullName, childPath);
            }
        }
    }

    /// <summary>
    /// The package at <paramref name="path"/> of the module at <paramref name="root"/>, whatever its .tazelignore files
    /// say, as an explicit label names it; null when there is none there: no project file, a hidden or linked
    /// directory on the way, or another module's.
    /// </summary>
    public static (string Path, string File)? PackageAt(string root, string path)
    {
        var directory = root;
        foreach (var part in path.Length == 0 ? [] : path.Split('/'))
        {
            if (part.Length == 0 || part.StartsWith('.'))
            {
                return null;
            }

            directory = Path.Combine(directory, part);
            if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null ||
                File.Exists(Path.Combine(directory, ModuleFile)))
            {
                return null;
            }
        }

        return BuildFileIn(directory) is { } file ? (path, file) : null;
    }
}
