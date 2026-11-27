// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Progmasoft.Tazel.Project.Ignore;

/// <summary>
/// What the <c>.tazelignore</c> files of a module ignore. A file applies to its directory and below; for a path, the
/// rules of every file from the module root down to the path's directory are read in that order, and the last rule
/// that matches decides, <c>!</c> keeping what an earlier one ignored. A path inside an ignored directory is ignored
/// whatever a rule says of it, as in gitignore. Directories whose names start with a dot, <c>.tazel</c> among them, are
/// left out by Tazel before any rule, and no rule brings them back.
/// </summary>
internal sealed class IgnoreRules
{
    private readonly string _root;
    private readonly Dictionary<string, IReadOnlyList<(string Scope, IgnoreRule Rule)>> _rulesOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _directories = new(StringComparer.Ordinal);

    private IgnoreRules(string root) => _root = Path.GetFullPath(root);

    /// <summary>The rules of the module rooted at <paramref name="root"/>; files are read as paths need them.</summary>
    public static IgnoreRules For(string root) => new(root);

    /// <summary>Whether <paramref name="relative"/>, a path below the module root with <c>/</c> between its parts, is ignored.</summary>
    public bool Ignored(string relative, bool directory)
    {
        ArgumentNullException.ThrowIfNull(relative);
        var path = relative.Replace('\\', '/').Trim('/');
        if (path.Length == 0)
        {
            return false;
        }

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        if (parent.Length > 0 && IgnoredDirectory(parent))
        {
            return true;
        }

        return directory ? IgnoredDirectory(path) : Decide(path, parent, directory: false);
    }

    private bool IgnoredDirectory(string path)
    {
        if (_directories.TryGetValue(path, out var known))
        {
            return known;
        }

        var slash = path.LastIndexOf('/');
        var parent = slash < 0 ? "" : path[..slash];
        var ignored = (parent.Length > 0 && IgnoredDirectory(parent)) || Decide(path, parent, directory: true);
        _directories[path] = ignored;
        return ignored;
    }

    // The last matching rule of the files from the root down to parent decides.
    private bool Decide(string path, string parent, bool directory)
    {
        var ignored = false;
        foreach (var (scope, rule) in RulesDownTo(parent))
        {
            if (rule.DirectoryOnly && !directory)
            {
                continue;
            }

            var local = scope.Length == 0 ? path : path[(scope.Length + 1)..];
            bool matches;
            try
            {
                matches = rule.Pattern.IsMatch(local);
            }
            catch (RegexMatchTimeoutException error)
            {
                throw new IgnoreException($"{rule.Place}: '{rule.Text}' took more than {IgnoreFile.RegexTimeout.TotalSeconds:0} s on {path}", error);
            }

            if (matches)
            {
                ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    // The rules of the files of the root and of each directory down to directory, shallowest first.
    private IReadOnlyList<(string Scope, IgnoreRule Rule)> RulesDownTo(string directory)
    {
        if (_rulesOf.TryGetValue(directory, out var known))
        {
            return known;
        }

        var slash = directory.LastIndexOf('/');
        var above = directory.Length == 0 ? [] : RulesDownTo(slash < 0 ? "" : directory[..slash]);
        var rules = new List<(string, IgnoreRule)>(above);
        var file = Path.Combine(_root, directory.Replace('/', Path.DirectorySeparatorChar), IgnoreFile.FileName);
        if (File.Exists(file))
        {
            foreach (var rule in IgnoreFile.Read(file, File.ReadAllText(file)))
            {
                rules.Add((directory, rule));
            }
        }

        _rulesOf[directory] = rules;
        return rules;
    }
}
