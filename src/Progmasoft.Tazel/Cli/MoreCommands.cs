// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Portal;
using Progmasoft.Tazel.Project;
using Spectre.Console;

namespace Progmasoft.Tazel.Cli;

/// <summary>The queries, fetching and vendoring, and Tazel Portal Modules.</summary>
internal sealed partial class Commands
{
    private async Task<int> QueryAsync(QueryCommand query, CancellationToken cancel)
    {
        RefuseWithoutSandbox();
        var (options, _) = TazelOptionsFile.Effective(workingDirectory);
        var platform = query.Flags.Platform ?? options.Platform ?? Host.Platform;
        var mode = query.Flags.Mode ?? BuildMode.Debug;
        var workspace = await Workspace.LoadAsync(workingDirectory, new BuildContext(mode, platform), TazelVersion,
            reporter.Status).ConfigureAwait(false);
        var current = CurrentPackage(workspace);
        var targets = new Query(workspace, current).Evaluate(query.Expression);
        var configuration = $"{platform.Name()}-{mode.ToString().ToLowerInvariant()}";
        switch (query.Kind)
        {
            case QueryKind.Query:
                foreach (var target in targets)
                {
                    reporter.Plain(query.Format == "label_kind" ? $"{Query.KindOf(target)} {target.Label}" : target.Label.ToString());
                }

                return 0;
            case QueryKind.CQuery:
                var selector = new Selector(workspace, platform);
                foreach (var target in targets.Where(target => selector.Incompatibility(target) is null))
                {
                    var label = $"{target.Label} ({configuration})";
                    reporter.Plain(query.Format == "label_kind" ? $"{Query.KindOf(target)} {label}" : label);
                }

                return 0;
            default:
                var compatible = new Selector(workspace, platform);
                var buildOptions = new BuildOptions
                {
                    Mode = mode,
                    Platform = platform,
                    FeaturesOn = query.Flags.FeaturesOn,
                    FeaturesOff = query.Flags.FeaturesOff,
                    CompilerOptions = query.Flags.CompilerOptions,
                    CCompilerOptions = query.Flags.CCompilerOptions,
                    CppCompilerOptions = query.Flags.CppCompilerOptions,
                    LinkerOptions = query.Flags.LinkerOptions,
                    ZigCompilerOptions = query.Flags.ZigCompilerOptions,
                };
                var planner = new Planner(workspace, buildOptions, new ToolchainChooser(workspace, platform));
                var seen = new HashSet<BuildAction>();
                foreach (var target in targets.Where(target => compatible.Incompatibility(target) is null))
                {
                    if (planner.Plan(target).Main is not { } main)
                    {
                        continue;
                    }

                    foreach (var action in Owned(main, target, planner).Where(seen.Add))
                    {
                        reporter.Plain($"action '{action.Description}'\n  Target: {target.Label} ({configuration})\n" +
                            $"  Command: {(action.Work is null ? Reporter.Quote(action.Command) : "(in Tazel) " + string.Join(' ', action.Command))}\n" +
                            $"  Inputs: [{string.Join(", ", action.Inputs)}]\n  Outputs: [{string.Join(", ", action.Outputs)}]\n");
                    }
                }

                return 0;
        }
    }

    /// <summary>The actions of <paramref name="target"/> itself, not those of what it depends on, in the order they run.</summary>
    private static List<BuildAction> Owned(BuildAction main, ProjectTarget target, Planner planner)
    {
        var others = new HashSet<BuildAction>(target.Dependencies.Concat(target.ZigImports.Values)
            .Select(dependency => planner.Plan(dependency)).SelectMany(products => products.HeaderActions.Append(products.Main))
            .OfType<BuildAction>());
        var order = new List<BuildAction>();
        Visit(main);
        return order;

        void Visit(BuildAction action)
        {
            if (others.Contains(action) || order.Contains(action))
            {
                return;
            }

            foreach (var prerequisite in action.Prerequisites)
            {
                Visit(prerequisite);
            }

            order.Add(action);
        }
    }

    private PackageId CurrentPackage(Workspace workspace)
    {
        var relative = FileSets.ToRoot(Path.GetFullPath(workingDirectory), workspace.Main.Root);
        var path = relative == "." ? "" : relative;
        while (path.Length > 0 && !workspace.Packages.Contains(new PackageId(null, path)))
        {
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }

        return new PackageId(null, path);
    }

    private async Task<int> FetchAsync()
    {
        RefuseWithoutSandbox();
        var (options, _) = TazelOptionsFile.Effective(workingDirectory);
        var workspace = await Workspace.LoadAsync(workingDirectory,
            new BuildContext(BuildMode.Debug, options.Platform ?? Host.Platform), TazelVersion, reporter.Status).ConfigureAwait(false);
        var plugins = workspace.PluginsOf.Values.Sum(list => list.Count);
        reporter.Success($"fetched {workspace.External.Count} module{(workspace.External.Count == 1 ? "" : "s")} " +
            $"({workspace.PortalModules.Count} of Tazel Portal Modules) and {plugins} plugin{(plugins == 1 ? "" : "s")}");
        return 0;
    }

    private async Task<int> VendorAsync(CancellationToken cancel)
    {
        RefuseWithoutSandbox();
        using var client = new PortalModulesClient();
        var (main, modules) = await Workspace.ResolvePortalAsync(workingDirectory, TazelVersion, client).ConfigureAwait(false);
        var vendor = Path.Combine(main.Root, PortalDependencies.VendorDirectory);
        foreach (var module in modules)
        {
            // The index too, so that a build with vendor/ resolves without the network.
            var index = await client.IndexTextAsync(module.Group, module.Module, cancel).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(vendor, module.Group, module.Module));
            await File.WriteAllTextAsync(Path.Combine(vendor, module.Group, module.Module, "index.json"), index, cancel)
                .ConfigureAwait(false);
            var name = module.BaseName + ModuleArchive.Extension;
            if (File.Exists(Path.Combine(vendor, name)))
            {
                continue;
            }

            await client.DownloadAsync(module.Group, module.Module, module.Version.ToNormalizedString(), vendor, cancel)
                .ConfigureAwait(false);
            reporter.Status($"vendored {module.Key} {module.Version}");
        }

        reporter.Success(modules.Count == 0
            ? "the module depends on no module of Tazel Portal Modules; its local modules and plugins are in it already"
            : $"vendor/ holds the {modules.Count} module{(modules.Count == 1 ? "" : "s")} of Tazel Portal Modules the module needs");
        return 0;
    }

    private async Task<int> PortalModulesAsync(PortalModulesCommand command, CancellationToken cancel)
    {
        using var client = new PortalModulesClient();
        var colon = command.Query.IndexOf(':', StringComparison.Ordinal);
        var group = colon < 0 ? null : command.Query[..colon];
        var name = colon < 0 ? command.Query : command.Query[(colon + 1)..];
        if (command.Action == PortalAction.Search)
        {
            var catalog = await client.CatalogAsync(cancel).ConfigureAwait(false);
            var found = catalog.Modules.Where(entry => command.Exact
                    ? entry.Group == group && entry.Module == name
                    : $"{entry.Group}:{entry.Module}".Contains(command.Query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.Group, StringComparer.Ordinal).ThenBy(entry => entry.Module, StringComparer.Ordinal).ToList();
            if (found.Count == 0)
            {
                reporter.Warning($"Tazel Portal Modules has no module that matches {command.Query}");
                return 1;
            }

            var table = new Table().Border(TableBorder.Rounded).AddColumn("Module").AddColumn("Latest").AddColumn("Description");
            foreach (var entry in found)
            {
                table.AddRow(Markup.Escape($"{entry.Group}:{entry.Module}"), Markup.Escape(entry.Latest ?? ""),
                    Markup.Escape(entry.Description ?? ""));
            }

            reporter.Console.Write(table);
            return 0;
        }

        if (group is null)
        {
            // Without a group, the name must be one module's alone.
            var catalog = await client.CatalogAsync(cancel).ConfigureAwait(false);
            var groups = catalog.Modules.Where(entry => entry.Module == name).Select(entry => entry.Group).Distinct().Order().ToList();
            group = groups.Count switch
            {
                0 => throw new ProjectException($"Tazel Portal Modules has no module {name}"),
                1 => groups[0],
                _ => throw new CommandLineException(
                    $"{name} is a module of {string.Join(", ", groups)}; write the group: group:{name}"),
            };
        }

        var index = await client.IndexAsync(group, name, cancel).ConfigureAwait(false)
            ?? throw new ProjectException($"Tazel Portal Modules has no module {group}:{name}");
        var highest = PortalModulesClient.Highest(index);
        reporter.Console.MarkupLine($"[bold]{Markup.Escape($"{index.Group}:{index.Module}")}[/]" +
            (highest is null ? "" : $"  latest {Markup.Escape(highest.Version)}"));
        var versions = new Table().Border(TableBorder.Rounded).AddColumn("Version").AddColumn("Pushed").AddColumn("Dependencies");
        foreach (var version in index.Versions.Reverse())
        {
            var dependencies = string.Join(", ", version.Dependencies.Select(d => $"{d.Group}:{d.Module} {d.Version}{(d.Test ? " (test)" : "")}"));
            versions.AddRow(Markup.Escape(version.Version) + (version.Yanked ? " [red]yanked[/]" : ""),
                version.Pushed.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Markup.Escape(dependencies));
        }

        reporter.Console.Write(versions);
        return 0;
    }
}
