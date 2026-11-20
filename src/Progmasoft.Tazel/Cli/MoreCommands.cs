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
    private async Task<int> QueryAsync(QueryCommand query)
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

    /// <summary>What an error code means; with --long, why, and the wrong and right text side by side.</summary>
    private void Explain(ExplainCommand command)
    {
        var entry = ErrorCodes.Find(command.Code)!;
        var fancy = reporter.Console.Profile.Capabilities.Unicode;
        reporter.Console.MarkupLine(
            $"[red bold]{(fancy ? "✖ " : "")}{Markup.Escape(entry.Code)}[/]: [bold]{Markup.Escape(entry.Title)}[/]  [grey]({Markup.Escape(entry.Where)})[/]");
        if (command.Long)
        {
            ExplainLong(entry, fancy);
        }
        else
        {
            ExplainShort(entry, fancy);
        }
    }

    /// <summary>The first sentence of the explanation and the fixed text.</summary>
    private void ExplainShort(ErrorCode entry, bool fancy)
    {
        var console = reporter.Console;
        var first = entry.Explanation.IndexOf(". ", StringComparison.Ordinal);
        console.WriteLine(first < 0 ? entry.Explanation : entry.Explanation[..(first + 1)]);
        if (entry.Right is not null)
        {
            console.MarkupLine($"[green]{(fancy ? "✔ " : "")}for example:[/] {Markup.Escape(entry.Right.Replace("\n", " ", StringComparison.Ordinal))}");
        }

        console.MarkupLine($"[grey]More: tazel explain {entry.Code} --long[/]");
    }

    /// <summary>The whole explanation, the wrong and right text side by side, and the other codes of the same file.</summary>
    private void ExplainLong(ErrorCode entry, bool fancy)
    {
        var console = reporter.Console;
        console.WriteLine();
        console.WriteLine(entry.Explanation);
        if (entry.Wrong is not null && entry.Right is not null)
        {
            console.WriteLine();
            console.Write(new Columns(Example(entry.Wrong, "wrong", "✖", Color.Red, fancy), Example(entry.Right, "right", "✔", Color.Green, fancy))
            {
                Expand = false,
            });
        }

        var related = ErrorCodes.Codes.Where(other => other.Where == entry.Where && other.Code != entry.Code).Select(other => other.Code).ToList();
        if (related.Count > 0)
        {
            console.WriteLine();
            console.MarkupLine($"[grey]The other errors of {Markup.Escape(entry.Where)}: {string.Join(", ", related)}[/]");
        }
    }

    private static Panel Example(string text, string title, string mark, Color color, bool fancy) =>
        new Panel(new Text(text))
        {
            Header = new PanelHeader(fancy ? $" {mark} {title} " : $" {title} "),
            BorderStyle = new Style(color),
            Border = fancy ? BoxBorder.Rounded : BoxBorder.Ascii,
        };

    /// <summary>
    /// Runs the steps of a custom command inside a MicroVM that sees the module, or the directory of the command when
    /// it is in no module, and the host's /usr read-only; a shell step goes to the guest's /bin/sh.
    /// </summary>
    private async Task<int> CustomInMicroVMAsync(
        Dsl.Custom.CustomCommand command, IReadOnlyList<string> arguments, TazelOptions options, string sandboxFile,
        CancellationToken cancel)
    {
        if (options.MicroVMNetwork)
        {
            throw new MicroVM.MicroVMException(
                $"{sandboxFile} asks for microvm-network, and this Tazel's MicroVM has no network, so nothing ran; " +
                "set microvm-network: false;");
        }

        if (!OperatingSystem.IsLinux())
        {
            throw new MicroVM.MicroVMException(
                $"{sandboxFile} asks for sandboxing-with-microvm, and this Tazel starts its MicroVM on Linux only");
        }

        string shared;
        try
        {
            shared = ModuleFiles.FindRoot(workingDirectory);
        }
        catch (ProjectException)
        {
            shared = Path.GetFullPath(workingDirectory);
        }

        var files = await MicroVM.MicroVMFiles.FindAsync(TazelVersion, reporter.Status, cancel).ConfigureAwait(false);
        var size = new MicroVM.MicroVMSize(
            options.MicroVMMemory.Amount ?? 1024, options.MicroVMCpus.Amount ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
        reporter.Status($"starting the MicroVM ({size.MemoryMiB} MiB, {size.Processors} processor{(size.Processors == 1 ? "" : "s")})");
        var sandbox = await MicroVM.Sandbox.StartAsync(files, [new MicroVM.SharedDirectory(shared, ReadOnly: false)], size, cancel)
            .ConfigureAwait(false);
        await using (sandbox.ConfigureAwait(false))
        {
            var output = Console.OpenStandardOutput();
            var errors = Console.OpenStandardError();
            foreach (var step in command.Steps)
            {
                var started = step switch
                {
                    Dsl.Custom.CustomStep.Program program => program.Expanded(arguments),
                    Dsl.Custom.CustomStep.Shell shell => shell.Started(windows: false),
                    _ => throw new InvalidOperationException("a step of no kind"),
                };
                var status = await sandbox.RunAsync(started, Path.GetFullPath(workingDirectory), output, errors, cancel)
                    .ConfigureAwait(false);
                if (status != 0)
                {
                    return status;
                }
            }
        }

        return 0;
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
            return await SearchAsync(client, command, group, name, cancel).ConfigureAwait(false);
        }

        group ??= await GroupOfAsync(client, name, cancel).ConfigureAwait(false);
        var index = await client.IndexAsync(group, name, cancel).ConfigureAwait(false)
            ?? throw new ProjectException($"Tazel Portal Modules has no module {group}:{name}");
        ShowModule(index);
        return 0;
    }

    private async Task<int> SearchAsync(
        PortalModulesClient client, PortalModulesCommand command, string? group, string name, CancellationToken cancel)
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

    /// <summary>The group of a module named without one, which must be the only module of that name.</summary>
    private static async Task<string> GroupOfAsync(PortalModulesClient client, string name, CancellationToken cancel)
    {
        var catalog = await client.CatalogAsync(cancel).ConfigureAwait(false);
        var groups = catalog.Modules.Where(entry => entry.Module == name).Select(entry => entry.Group).Distinct().Order().ToList();
        return groups.Count switch
        {
            0 => throw new ProjectException($"Tazel Portal Modules has no module {name}"),
            1 => groups[0],
            _ => throw new CommandLineException($"{name} is a module of {string.Join(", ", groups)}; write the group: group:{name}"),
        };
    }

    /// <summary>A module's latest version and a table of its versions, newest first.</summary>
    private void ShowModule(ModuleIndex index)
    {
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
    }
}
