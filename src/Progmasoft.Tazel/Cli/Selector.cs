// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Cli;

/// <summary>Which targets a command line names.</summary>
internal sealed class Selector(Workspace workspace, Platform platform)
{
    private readonly Dictionary<ProjectTarget, string?> _incompatible = [];

    /// <summary>How many targets the patterns left out because they do not build for the platform.</summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// The targets of <paramref name="labels"/>, from the package of <paramref name="directory"/>; those that
    /// <paramref name="wanted"/> refuses are left out of patterns and are an error when named.
    /// </summary>
    public List<ProjectTarget> Select(
        IReadOnlyList<string> labels, string directory, Func<ProjectTarget, bool> wanted, string kind)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(wanted);
        var current = CurrentPackage(directory);
        var selected = new List<ProjectTarget>();
        if (labels.Count == 0)
        {
            if (current is null)
            {
                throw new ProjectException("the current directory is in no package; name targets with labels such as //...");
            }

            AddPattern(selected, target => target.Label.Package == current, wanted);
            return selected;
        }

        foreach (var text in labels)
        {
            Selection selection;
            try
            {
                selection = Labels.Selection(text, current ?? PackageId.Root);
            }
            catch (LabelException error)
            {
                throw new ProjectException(error.Message, error);
            }

            if (selection is TargetLabel label)
            {
                var target = Named(label, wanted, kind);
                if (!selected.Contains(target))
                {
                    selected.Add(target);
                }
            }
            else if (selection is PackagePattern pattern)
            {
                bool Covers(PackageId package) => pattern.Covers(package) && !workspace.IgnoredPackages.Contains(package);
                if (!workspace.Packages.Any(Covers))
                {
                    throw new ProjectException($"{pattern} names no package");
                }

                AddPattern(selected, target => Covers(target.Label.Package), wanted);
            }
        }

        return selected;
    }

    /// <summary>The target <paramref name="label"/> names, which must be there, wanted and buildable for the platform.</summary>
    private ProjectTarget Named(TargetLabel label, Func<ProjectTarget, bool> wanted, string kind)
    {
        if (!workspace.Targets.TryGetValue(label, out var target))
        {
            var package = workspace.Packages.Contains(label.Package)
                ? $"its package has no target '{label.Name}'"
                : $"there is no package {label.Package}";
            throw new ProjectException($"{label} is not a target: {package}");
        }

        if (!wanted(target))
        {
            throw new ProjectException($"{label} is not {kind}");
        }

        return Incompatibility(target) is { } reason
            ? throw new ProjectException($"{label} does not build for {platform.Name()}: {reason}")
            : target;
    }

    /// <summary>Why <paramref name="target"/> does not build for the platform, or null when it does.</summary>
    public string? Incompatibility(ProjectTarget target)
    {
        if (_incompatible.TryGetValue(target, out var known))
        {
            return known;
        }

        string? reason = null;
        if (!target.BuildsFor(platform))
        {
            reason = $"its TargetConstraints are {string.Join(", ", target.Constraints.Select(p => p.Name()))}" +
                (target.Family == Family.ObjectiveC && target.Settings.TargetConstraints.Count == 0
                    ? " (the default of Objective-C targets)"
                    : "");
        }
        else
        {
            foreach (var dependency in target.Dependencies.Concat(target.ZigImports.Values))
            {
                if (Incompatibility(dependency) is not null)
                {
                    reason = $"it depends on {dependency.Label}, which does not";
                    break;
                }
            }
        }

        _incompatible[target] = reason;
        return reason;
    }

    private void AddPattern(List<ProjectTarget> selected, Func<ProjectTarget, bool> covered, Func<ProjectTarget, bool> wanted)
    {
        foreach (var target in workspace.Targets.Values.Where(covered).OrderBy(t => t.Label.ToString(), StringComparer.Ordinal))
        {
            if (target.IsManual || !wanted(target) || selected.Contains(target))
            {
                continue;
            }

            if (Incompatibility(target) is not null)
            {
                Skipped++;
                continue;
            }

            selected.Add(target);
        }
    }

    private PackageId? CurrentPackage(string directory)
    {
        var relative = FileSets.ToRoot(System.IO.Path.GetFullPath(directory), workspace.Main.Root);
        var path = relative == "." ? "" : relative;
        while (true)
        {
            var id = new PackageId(null, path);
            if (workspace.Packages.Contains(id))
            {
                return id;
            }

            if (path.Length == 0)
            {
                return null;
            }

            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
    }
}
