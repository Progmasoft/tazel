// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Versioning;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Module.Prelude;
using Progmasoft.Tazel.Label;

namespace Progmasoft.Tazel.Project;

/// <summary>What a module says about itself, checked.</summary>
internal sealed record ModuleInfo(string Group, string Name, NuGetVersion Version, ModuleDefinition Definition);

/// <summary>A module whose MODULE.csx ran: the main one, or a dependency by the name its labels use.</summary>
internal sealed class LoadedModule(
    string root, string? repository, ModuleGlobals globals, ModuleInfo info, Dsl.Envs.ModuleEnvironment? environment)
{
    /// <summary>The values of its <c>tazel_envs.txt</c>, or null when it has none.</summary>
    public Dsl.Envs.ModuleEnvironment? Environment { get; } = environment;

    public string Root { get; } = root;

    public string? Repository { get; } = repository;

    public ModuleGlobals Globals { get; } = globals;

    public ModuleInfo Info { get; } = info;

    /// <summary>How messages name a file of this module.</summary>
    public string Shown(string relative) => Repository is null ? relative : $"@{Repository}//{relative}";
}

/// <summary>A target, with what it depends on found.</summary>
internal sealed class ProjectTarget
{
    public required TargetLabel Label { get; init; }

    public required Family Family { get; init; }

    public required TargetKind Kind { get; init; }

    public required TargetSettings Settings { get; init; }

    /// <summary>Where it is declared, <c>file:line</c>.</summary>
    public required string Place { get; init; }

    public required string PackageDirectory { get; init; }

    public required LoadedModule Module { get; init; }

    /// <summary>The platforms it builds for.</summary>
    public required IReadOnlyList<Platform> Constraints { get; init; }

    public List<ProjectTarget> Dependencies { get; } = [];

    /// <summary>For Zig targets: the modules they import, by the name <c>@import</c> uses.</summary>
    public Dictionary<string, ProjectTarget> ZigImports { get; } = new(StringComparer.Ordinal);

    public bool IsManual => Settings.Tags.Contains("manual", StringComparer.Ordinal);

    public bool IsLibrary => Kind is TargetKind.StaticLibrary or TargetKind.SharedLibrary;

    public bool IsRunnable => Kind is TargetKind.Executable or TargetKind.Test;

    public bool BuildsFor(Platform platform) => Constraints.Count == 0 || Constraints.Contains(platform);

    public override string ToString() => Label.ToString();
}

/// <summary>A toolchain a project file declared.</summary>
internal sealed record ProjectToolchain(TargetLabel Label, Family Family, object Settings, string Place);

/// <summary>The modules of a command, their packages, targets and toolchains.</summary>
internal sealed class Workspace
{
    private Workspace(LoadedModule main)
    {
        Main = main;
    }

    public LoadedModule Main { get; }

    public Dictionary<string, LoadedModule> External { get; } = new(StringComparer.Ordinal);

    public Dictionary<TargetLabel, ProjectTarget> Targets { get; } = [];

    public Dictionary<TargetLabel, ProjectToolchain> Toolchains { get; } = [];

    /// <summary>The packages of every module.</summary>
    public List<PackageId> Packages { get; } = [];

    /// <summary>The toolchains the main module registers, in its order.</summary>
    public List<ProjectToolchain> Registered { get; } = [];

    /// <summary>Loads the module that <paramref name="directory"/> is in, with the modules it depends on.</summary>
    public static async Task<Workspace> LoadAsync(
        string directory, BuildContext build, Version tazelVersion, Action<string>? status = null)
    {
        var root = ModuleFiles.FindRoot(directory);
        var main = await LoadModuleAsync(root, null, tazelVersion, root, build.Platform).ConfigureAwait(false);
        var workspace = new Workspace(main) { _target = build.Platform };
        await workspace.LoadDependenciesAsync(main, tazelVersion).ConfigureAwait(false);
        await workspace.LoadPortalModulesAsync(tazelVersion, offline: false).ConfigureAwait(false);
        await workspace.LoadPackagesAsync(main, build, status ?? (_ => { })).ConfigureAwait(false);
        foreach (var module in workspace.External.Values)
        {
            await workspace.LoadPackagesAsync(module, build, status ?? (_ => { })).ConfigureAwait(false);
        }

        workspace.Resolve();
        return workspace;
    }

    /// <summary>Runs only the MODULE.csx of the module <paramref name="directory"/> is in.</summary>
    public static Task<LoadedModule> LoadMainModuleAsync(string directory, Version tazelVersion)
    {
        var root = ModuleFiles.FindRoot(directory);
        return LoadModuleAsync(root, null, tazelVersion, root);
    }

    // The platform the modules are loaded for: what the conditions of their tazel_envs.txt see.
    private Platform? _target;

    private static async Task<LoadedModule> LoadModuleAsync(
        string root, string? repository, Version tazelVersion, string mainRoot, Platform? target = null)
    {
        var file = Path.Combine(root, ModuleFiles.ModuleFile);
        var shown = repository is null ? ModuleFiles.ModuleFile : $"@{repository}//{ModuleFiles.ModuleFile}";
        var globals = new ModuleGlobals();
        var environment = EnvironmentOf(root, repository, target);
        using (Dsl.Environment.EnvironmentValues.Enter(environment))
        {
            await ScriptHost.RunAsync(await File.ReadAllTextAsync(file).ConfigureAwait(false), shown, globals, mainRoot)
                .ConfigureAwait(false);
        }

        var definition = globals.module?.Definition
            ?? throw new ProjectException($"{shown} sets no module: module = Module.Define(new() {{ ... }});");
        var version = Checked(definition, shown, tazelVersion);
        return new LoadedModule(root, repository, globals, new ModuleInfo(definition.Group, definition.Name, version, definition),
            environment);
    }

    /// <summary>The values of the module's tazel_envs.txt, or null when it has none.</summary>
    private static Dsl.Envs.ModuleEnvironment? EnvironmentOf(string root, string? repository, Platform? target)
    {
        var shown = repository is null ? Dsl.Envs.TazelEnvsFile.FileName : $"@{repository}//{Dsl.Envs.TazelEnvsFile.FileName}";
        try
        {
            return Dsl.Envs.ModuleEnvironment.Load(root, shown, target, inherit: repository is null);
        }
        catch (Dsl.Custom.CustomCommandsException error)
        {
            throw new ProjectException(error.Message, error);
        }
    }

    /// <summary>The version of a module, once its name, group, version and Tazel compatibility are checked.</summary>
    private static NuGetVersion Checked(ModuleDefinition definition, string shown, Version tazelVersion)
    {
        if (!Labels.IsName(definition.Name))
        {
            throw new ProjectException($"{shown}: the module name '{definition.Name}' must be letters, digits and _ + . -");
        }

        if (!IsGroup(definition.Group))
        {
            throw new ProjectException(
                $"{shown}: the group '{definition.Group}' must be names joined by dots, such as com.example");
        }

        if (!NuGetVersion.TryParseStrict(definition.Version, out var version))
        {
            throw new ProjectException($"{shown}: '{definition.Version}' is not a SemVer version such as 1.2.0");
        }

        if (definition.Compatibility is { } compatibility && !compatibility.Admits(tazelVersion))
        {
            throw new ProjectException(
                $"{shown} builds with Tazel {compatibility}; this is Tazel {tazelVersion.ToString(3)}");
        }

        return version;
    }

    private static bool IsGroup(string group) =>
        group.Length > 0 && group.Split('.').All(part =>
            part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));

    private async Task LoadDependenciesAsync(LoadedModule module, Version tazelVersion)
    {
        foreach (var dependency in module.Globals.dependencies?.Items ?? [])
        {
            if (dependency.Portal is { } portal)
            {
                // Modules of Tazel Portal are resolved together, once every local module is loaded.
                PortalRequests.Add((portal.Group, portal.Name, portal.Version, portal.Alias, module.Shown(ModuleFiles.ModuleFile)));
                continue;
            }

            var local = dependency.Local!;
            var place = module.Shown(ModuleFiles.ModuleFile);
            if (!Labels.IsName(local.Name))
            {
                throw new ProjectException($"{place}: the local module name '{local.Name}' must be letters, digits and _ + . -");
            }

            if (External.ContainsKey(local.Name))
            {
                throw new ProjectException($"{place}: two dependencies are named '{local.Name}'");
            }

            var unpacked = LocalModules.Unpack(Main.Root, module.Root, local, place);
            var loaded = await LoadModuleAsync(unpacked, local.Name, tazelVersion, Main.Root, _target).ConfigureAwait(false);
            External[local.Name] = loaded;
            await LoadDependenciesAsync(loaded, tazelVersion).ConfigureAwait(false);
        }
    }

    /// <summary>The modules of Tazel Portal Modules the project files ask for, before resolution.</summary>
    private List<(string Group, string Module, string Version, string? Alias, string Place)> PortalRequests { get; } = [];

    /// <summary>The modules of Tazel Portal Modules the build uses, at the versions the resolution chose.</summary>
    public List<ResolvedModule> PortalModules { get; } = [];

    /// <summary>Resolves the modules of Tazel Portal Modules, then fetches and loads each.</summary>
    private async Task LoadPortalModulesAsync(Version tazelVersion, bool offline)
    {
        if (PortalRequests.Count == 0)
        {
            return;
        }

        using var client = new Portal.PortalModulesClient(vendor: System.IO.Path.Combine(Main.Root, PortalDependencies.VendorDirectory));
        var resolved = await PortalDependencies.ResolveAsync(PortalRequests, client, System.Threading.CancellationToken.None)
            .ConfigureAwait(false);
        foreach (var module in resolved)
        {
            if (External.ContainsKey(module.Repository))
            {
                throw new ProjectException($"{module.Key} would be @{module.Repository}, the name of a local module; give it an Alias");
            }

            var root = await PortalDependencies.FetchAsync(Main.Root, module, offline ? null : client,
                System.Threading.CancellationToken.None).ConfigureAwait(false);
            External[module.Repository] = await LoadModuleAsync(root, module.Repository, tazelVersion, Main.Root, _target).ConfigureAwait(false);
            PortalModules.Add(module);
        }
    }

    /// <summary>The modules of Tazel Portal Modules that <paramref name="directory"/>'s module needs, resolved but not fetched.</summary>
    public static async Task<(LoadedModule Main, IReadOnlyList<ResolvedModule> Modules)> ResolvePortalAsync(
        string directory, Version tazelVersion, Portal.PortalModulesClient client)
    {
        var main = await LoadMainModuleAsync(directory, tazelVersion).ConfigureAwait(false);
        var workspace = new Workspace(main);
        await workspace.LoadDependenciesAsync(main, tazelVersion).ConfigureAwait(false);
        if (workspace.PortalRequests.Count == 0)
        {
            return (main, []);
        }

        return (main, await PortalDependencies.ResolveAsync(workspace.PortalRequests, client, System.Threading.CancellationToken.None)
            .ConfigureAwait(false));
    }

    /// <summary>The plugins each module loaded, by its repository (null for the main module).</summary>
    public Dictionary<string, IReadOnlyList<LoadedPlugin>> PluginsOf { get; } = new(StringComparer.Ordinal);

    private async Task LoadPackagesAsync(LoadedModule module, BuildContext build, Action<string> status)
    {
        var plugins = Plugins.LoadAll(Main.Root, module, build, status);
        PluginsOf[module.Repository ?? ""] = plugins;
        var environment = Plugins.EnvironmentFor(plugins);
        var packages = ModuleFiles.Packages(module.Root);
        var scopes = await Task.WhenAll(packages.Select(package => Task.Run(async () =>
        {
            var shown = module.Shown(FileSets.ToRoot(package.File, module.Root));
            var scope = new DeclarationScope(package.Path, shown);
            var code = await File.ReadAllTextAsync(package.File).ConfigureAwait(false);
            using (Declarations.Enter(scope))
            using (Dsl.Environment.EnvironmentValues.Enter(module.Environment))
            {
                await ScriptHost.RunAsync(code, shown, new BuildGlobals(build), Main.Root, environment).ConfigureAwait(false);
            }

            return (Package: package, Scope: scope);
        }))).ConfigureAwait(false);

        foreach (var (package, scope) in scopes)
        {
            var id = new PackageId(module.Repository, package.Path);
            Packages.Add(id);
            var directory = Path.GetDirectoryName(package.File)!;
            foreach (var declared in scope.Targets)
            {
                var label = new TargetLabel(id, declared.Name);
                var settings = (TargetSettings)declared.Settings;
                var constraints = settings.TargetConstraints.Count == 0 && declared.Family == Family.ObjectiveC
                    ? ObjectiveCDefaults.Platforms
                    : settings.TargetConstraints;
                Targets[label] = new ProjectTarget
                {
                    Label = label,
                    Family = declared.Family,
                    Kind = declared.Kind,
                    Settings = settings,
                    Place = $"{declared.File}:{declared.Line}",
                    PackageDirectory = directory,
                    Module = module,
                    Constraints = constraints,
                };
            }

            foreach (var declared in scope.Toolchains)
            {
                var label = new TargetLabel(id, declared.Name);
                Toolchains[label] = new ProjectToolchain(label, declared.Family, declared.Settings, $"{declared.File}:{declared.Line}");
            }
        }
    }

    private void Resolve()
    {
        foreach (var target in Targets.Values)
        {
            TargetChecks.Check(target);
            foreach (var reference in target.Settings.Depends.Items)
            {
                var found = Find(reference.Label, target);
                // A step may need a program the build makes; what compiles and links takes libraries and steps.
                if (found.Kind is TargetKind.Executable or TargetKind.Test && target.Family != Family.Plugin)
                {
                    throw new ProjectException($"{target.Place}: {found.Label} is a {Describe(found)}; only libraries are linked");
                }

                if (found.Kind == TargetKind.Module)
                {
                    throw new ProjectException($"{target.Place}: {found.Label} is a Zig module; name it in ModuleImports");
                }

                target.Dependencies.Add(found);
            }

            if (target.Settings is ZigModuleSettings zig)
            {
                foreach (var (name, labelText) in zig.ModuleImports)
                {
                    var found = Find(labelText, target);
                    if (found.Kind != TargetKind.Module)
                    {
                        throw new ProjectException($"{target.Place}: ModuleImports[\"{name}\"] names {found.Label}, which is not a Zig module");
                    }

                    target.ZigImports[name] = found;
                }
            }
        }

        CheckCycles();
        foreach (var labelText in Main.Globals.toolchains?.Labels ?? [])
        {
            TargetLabel label;
            try
            {
                label = Labels.Target(labelText, PackageId.Root);
            }
            catch (LabelException error)
            {
                throw new ProjectException($"{ModuleFiles.ModuleFile}: {error.Message}", error);
            }

            Registered.Add(Toolchains.TryGetValue(label, out var toolchain)
                ? toolchain
                : throw new ProjectException($"{ModuleFiles.ModuleFile}: no toolchain {label} is declared"));
        }
    }

    private ProjectTarget Find(string text, ProjectTarget from)
    {
        TargetLabel label;
        try
        {
            label = Labels.Target(text, from.Label.Package);
        }
        catch (LabelException error)
        {
            throw new ProjectException($"{from.Place}: {error.Message}", error);
        }

        if (label.Package.Repository is null && from.Module.Repository is not null)
        {
            label = label with { Package = label.Package with { Repository = from.Module.Repository } };
        }

        if (!Targets.TryGetValue(label, out var found))
        {
            var hint = Suggest(label);
            throw new ProjectException($"{from.Place}: {from.Label} depends on {label}, which is not declared{hint}");
        }

        var visible = found.Settings.Visibility switch
        {
            Visibility.Public => true,
            Visibility.Module => found.Module == from.Module,
            _ => found.Label.Package == from.Label.Package,
        };
        if (!visible)
        {
            throw new ProjectException(
                $"{from.Place}: {found.Label} is {found.Settings.Visibility.ToString().ToLowerInvariant()}, " +
                $"so {from.Label} may not depend on it; set Visibility on {found.Label}");
        }

        return found;
    }

    private string Suggest(TargetLabel label)
    {
        var near = Targets.Keys.Where(k => k.Package == label.Package)
            .Select(k => k.Name).OrderBy(n => Cli.Suggestions.Distance(n, label.Name)).FirstOrDefault();
        return near is not null && Cli.Suggestions.Distance(near, label.Name) <= 2 ? $"; did you mean :{near}?" : "";
    }

    private static string Describe(ProjectTarget target) => target.Kind switch
    {
        TargetKind.Executable => "executable",
        TargetKind.Test => "test",
        _ => target.Kind.ToString(),
    };

    private void CheckCycles()
    {
        var state = new Dictionary<ProjectTarget, int>();
        var path = new List<ProjectTarget>();
        foreach (var target in Targets.Values.OrderBy(t => t.Label.ToString(), StringComparer.Ordinal))
        {
            Visit(target);
        }

        void Visit(ProjectTarget target)
        {
            if (state.TryGetValue(target, out var seen))
            {
                if (seen == 1)
                {
                    var start = path.IndexOf(target);
                    var cycle = string.Join(" -> ", path.Skip(start).Append(target).Select(t => t.Label));
                    throw new ProjectException($"{target.Place}: the targets depend on each other: {cycle}");
                }

                return;
            }

            state[target] = 1;
            path.Add(target);
            foreach (var next in target.Dependencies.Concat(target.ZigImports.Values))
            {
                Visit(next);
            }

            path.RemoveAt(path.Count - 1);
            state[target] = 2;
        }
    }
}
