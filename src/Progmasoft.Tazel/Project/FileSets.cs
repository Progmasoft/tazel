// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Project;

/// <summary>
/// Turns the files a project file names into paths relative to the root of the main module, which is where every
/// command of a build runs. A glob does not reach into a subpackage, a directory with a project file of its own.
/// </summary>
internal static class FileSets
{
    /// <summary>The files of <paramref name="files"/>, in the package at <paramref name="packageDirectory"/>.</summary>
    public static IReadOnlyList<string> Resolve(Files files, string packageDirectory, string mainRoot, string place)
    {
        ArgumentNullException.ThrowIfNull(files);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in files.Items)
        {
            IEnumerable<string> found = item.Path is { } path
                ? [OnePath(path, packageDirectory, place)]
                : Expand(item.Glob!, packageDirectory, place);
            foreach (var relative in found)
            {
                var shown = ToRoot(Path.Combine(packageDirectory, relative), mainRoot);
                if (seen.Add(shown))
                {
                    result.Add(shown);
                }
            }
        }

        return result;
    }

    /// <summary>One path of a package, checked to stay inside it, relative to the main module root.</summary>
    public static string PathIn(string path, string packageDirectory, string mainRoot, string place, bool mustExist)
    {
        var relative = Checked(path, place);
        var full = Path.Combine(packageDirectory, relative);
        if (mustExist && !File.Exists(full) && !Directory.Exists(full))
        {
            throw new ProjectException($"{place}: '{path}' does not exist");
        }

        return ToRoot(full, mainRoot);
    }

    /// <summary><paramref name="full"/> relative to <paramref name="mainRoot"/>, with <c>/</c> between its parts.</summary>
    public static string ToRoot(string full, string mainRoot) =>
        Path.GetRelativePath(mainRoot, full).Replace('\\', '/');

    /// <summary>Whether <paramref name="relative"/>, with <c>/</c> between its parts, matches <paramref name="pattern"/>.</summary>
    public static bool Matches(string pattern, string relative) =>
        MatchParts(pattern.Split('/'), 0, relative.Split('/'), 0);

    private static string OnePath(string path, string packageDirectory, string place)
    {
        var relative = Checked(path, place);
        if (!File.Exists(Path.Combine(packageDirectory, relative)))
        {
            throw new ProjectException($"{place}: '{path}' does not exist");
        }

        return relative;
    }

    private static string Checked(string path, string place)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains('\\', StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: '{path}' must be a path below the project file, with / between its parts");
        }

        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or "..")
            {
                throw new ProjectException($"{place}: '{path}' must stay below the project file; it has no . or .. parts");
            }
        }

        return path;
    }

    private static List<string> Expand(Glob glob, string packageDirectory, string place)
    {
        Checked(glob.Pattern, place);
        var all = new List<string>();
        // A package is in a module; outside one, its own directory is where ignore files start.
        var root = ModuleFiles.RootOf(packageDirectory) ?? packageDirectory;
        var package = Path.GetRelativePath(root, packageDirectory).Replace('\\', '/');
        Walk(packageDirectory, "", all, top: true, Ignore.IgnoreRules.For(root), package == "." ? "" : package + "/");
        return all
            .Where(file => Matches(glob.Pattern, file) && !glob.Exclude.Any(pattern => Matches(pattern, file)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // The files of a package below directory, less hidden ones, other packages and modules, and what is ignored.
    private static void Walk(string directory, string prefix, List<string> found, bool top, Ignore.IgnoreRules ignore, string package)
    {
        if (!top && ModuleFiles.BuildFileIn(directory) is not null)
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Name.StartsWith('.') || entry.LinkTarget is not null ||
                ignore.Ignored(package + prefix + entry.Name, entry is DirectoryInfo))
            {
                continue;
            }

            if (entry is DirectoryInfo)
            {
                if (!File.Exists(Path.Combine(entry.FullName, ModuleFiles.ModuleFile)))
                {
                    Walk(entry.FullName, prefix + entry.Name + "/", found, top: false, ignore, package);
                }
            }
            else
            {
                found.Add(prefix + entry.Name);
            }
        }
    }

    private static bool MatchParts(string[] pattern, int p, string[] path, int i)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == "**")
            {
                for (var skip = i; skip <= path.Length; skip++)
                {
                    if (MatchParts(pattern, p + 1, path, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (i >= path.Length || !MatchName(pattern[p], 0, path[i], 0))
            {
                return false;
            }

            p++;
            i++;
        }

        return i == path.Length;
    }

    private static bool MatchName(string pattern, int p, string name, int n)
    {
        while (p < pattern.Length)
        {
            switch (pattern[p])
            {
                case '*':
                    for (var skip = n; skip <= name.Length; skip++)
                    {
                        if (MatchName(pattern, p + 1, name, skip))
                        {
                            return true;
                        }
                    }

                    return false;
                case '?':
                    if (n >= name.Length)
                    {
                        return false;
                    }

                    break;
                default:
                    if (n >= name.Length || name[n] != pattern[p])
                    {
                        return false;
                    }

                    break;
            }

            p++;
            n++;
        }

        return n == name.Length;
    }
}
