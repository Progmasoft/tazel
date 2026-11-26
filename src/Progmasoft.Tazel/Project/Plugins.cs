// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using NuGet.Versioning;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Module.Prelude;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.PluginApi;

namespace Progmasoft.Tazel.Project;

/// <summary>What <c>tazel-plugin.json</c>, the manifest at the top of a plugin archive, says.</summary>
/// <param name="Group">The group of the plugin.</param>
/// <param name="Name">The name of the plugin.</param>
/// <param name="Version">Its SemVer version.</param>
/// <param name="PluginApi">The version of Progmasoft.Tazel.PluginApi it was built against.</param>
/// <param name="EntryAssembly">The file of the assembly that holds its <see cref="ITazelPlugin"/>.</param>
internal sealed record PluginManifest(string Group, string Name, string Version, string PluginApi, string EntryAssembly);

/// <summary>A plugin, loaded into a load context of its own and applied.</summary>
internal sealed class LoadedPlugin(PluginManifest manifest, string directory, AssemblyLoadContext context, List<Assembly> assemblies)
{
    public PluginManifest Manifest { get; } = manifest;

    public string Directory { get; } = directory;

    public AssemblyLoadContext Context { get; } = context;

    /// <summary>The plugin's own assemblies, every one loaded.</summary>
    public List<Assembly> Assemblies { get; } = assemblies;

    /// <summary>The namespaces BUILD.csx files see without a <c>using</c>.</summary>
    public List<string> Imports { get; } = [];

    public string Id => $"{Manifest.Group}.{Manifest.Name}";
}

/// <summary>
/// The plugins a MODULE.csx names. Each is a <c>.tazel.tar.xz</c> of the module with <c>tazel-plugin.json</c> at its top,
/// unpacked once below <c>.tazel/plugins</c>; its assemblies load into a load context of their own, so two plugins'
/// libraries never clash, while Progmasoft.Tazel.PluginApi and Progmasoft.Tazel.Dsl are Tazel's, so every plugin shares
/// their types with Tazel.
/// </summary>
internal static class Plugins
{
    public const string ManifestFile = "tazel-plugin.json";

    private static readonly string[] Shared = ["Progmasoft.Tazel.PluginApi", "Progmasoft.Tazel.Dsl"];

    /// <summary>The version of Progmasoft.Tazel.PluginApi this Tazel has.</summary>
    public static Version ApiVersion { get; } = typeof(ITazelPlugin).Assembly.GetName().Version ?? new Version(0, 1, 0);

    /// <summary>Loads and applies the plugins of <paramref name="module"/>.</summary>
    public static IReadOnlyList<LoadedPlugin> LoadAll(
        string mainRoot, LoadedModule module, BuildContext build, Action<string> status)
    {
        ArgumentNullException.ThrowIfNull(module);
        var place = module.Shown(ModuleFiles.ModuleFile);
        var loaded = new List<LoadedPlugin>();
        foreach (var reference in module.Globals.plugins?.Items ?? [])
        {
            if (reference.Path is null)
            {
                throw new ProjectException(
                    $"{place}: {reference.Group}.{reference.Name} is a plugin of Tazel Portal, and this Tazel fetches no " +
                    "plugins; use LocalPlugin.From with its archive");
            }

            var plugin = LoadOne(mainRoot, module, reference, place);
            if (loaded.Any(other => other.Id == plugin.Id))
            {
                throw new ProjectException($"{place}: the plugin {plugin.Id} is named twice");
            }

            Apply(plugin, module, build, status, place);
            loaded.Add(plugin);
        }

        return loaded;
    }

    /// <summary>The assemblies and imports that let the BUILD.csx files of a module use its plugins.</summary>
    public static ScriptEnvironment EnvironmentFor(IReadOnlyList<LoadedPlugin> plugins)
    {
        ArgumentNullException.ThrowIfNull(plugins);
        if (plugins.Count == 0)
        {
            return ScriptEnvironment.Default;
        }

        return ScriptEnvironment.With(plugins.SelectMany(plugin => plugin.Assemblies), plugins.SelectMany(plugin => plugin.Imports));
    }

    private static LoadedPlugin LoadOne(string mainRoot, LoadedModule module, PluginReference reference, string place)
    {
        var path = reference.Path!;
        if (path.Length == 0 || Path.IsPathRooted(path) || !path.EndsWith(ModuleArchive.Extension, StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: '{path}' is not a {ModuleArchive.Extension} of this module");
        }

        var archive = Path.Combine(module.Root, path);
        if (!File.Exists(archive) || new FileInfo(archive).LinkTarget is not null)
        {
            throw new ProjectException($"{place}: the plugin '{path}' is not a file of this module");
        }

        var directory = Unpacked(mainRoot, archive);
        var manifest = ReadManifest(Path.Combine(directory, ManifestFile), path);
        CheckManifest(manifest, path);
        var context = new PluginLoadContext(manifest.Group + "." + manifest.Name, directory);
        // Tazel's own copy of a shared assembly is the one every plugin uses; the entry assembly comes first.
        var assemblies = Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal)
            .Where(file => !Shared.Contains(Path.GetFileNameWithoutExtension(file), StringComparer.Ordinal))
            .Select(file => (File: Path.GetFileName(file), Assembly: LoadAssembly(context, file, path)))
            .OrderBy(loaded => loaded.File == manifest.EntryAssembly ? 0 : 1)
            .ToList();
        if (assemblies.Count == 0 || assemblies[0].File != manifest.EntryAssembly)
        {
            throw new ProjectException($"{path}: its manifest names the entry assembly {manifest.EntryAssembly}, which it does not hold");
        }

        return new LoadedPlugin(manifest, directory, context, assemblies.Select(loaded => loaded.Assembly).ToList());
    }

    /// <summary>The directory of the plugin's archive, unpacked once below the state directory by its SHA-256.</summary>
    private static string Unpacked(string mainRoot, string archive)
    {
        var sha = Sha256Sums.Sha256(archive);
        var destination = Path.Combine(mainRoot, ModuleFiles.StateDirectory, "plugins", sha[..32]);
        if (ModuleArchive.FromMarker(destination) is { } unpacked)
        {
            return unpacked;
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        var directory = ModuleArchive.Unpack(archive, destination, ManifestFile);
        ModuleArchive.WriteMarker(destination, directory);
        return directory;
    }

    private static Assembly LoadAssembly(PluginLoadContext context, string file, string shown)
    {
        try
        {
            return context.LoadFromAssemblyPath(Path.GetFullPath(file));
        }
        catch (BadImageFormatException error)
        {
            throw new ProjectException($"{shown}: {Path.GetFileName(file)} is not a .NET assembly: {error.Message}", error);
        }
    }

    private static PluginManifest ReadManifest(string file, string shown)
    {
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowTrailingCommas = false })
                ?? throw new ProjectException($"{shown}: {ManifestFile} is empty");
        }
        catch (JsonException error)
        {
            throw new ProjectException($"{shown}: {ManifestFile} cannot be read: {error.Message}", error);
        }
    }

    private static void CheckManifest(PluginManifest manifest, string shown)
    {
        CheckNames(manifest, shown);
        CheckApi(manifest, shown);
        var entry = manifest.EntryAssembly;
        if (string.IsNullOrEmpty(entry) || entry.Contains('/', StringComparison.Ordinal) || entry.Contains('\\', StringComparison.Ordinal) ||
            !entry.EndsWith(".dll", StringComparison.Ordinal))
        {
            throw new ProjectException($"{shown}: the entry assembly '{entry}' is not a .dll at the top");
        }
    }

    private static void CheckNames(PluginManifest manifest, string shown)
    {
        if (string.IsNullOrEmpty(manifest.Group) || manifest.Group.Split('.').Any(part => part.Length == 0 || !Labels.IsName(part)))
        {
            throw new ProjectException($"{shown}: the group '{manifest.Group}' of its manifest is not names joined by dots");
        }

        if (string.IsNullOrEmpty(manifest.Name) || !Labels.IsName(manifest.Name))
        {
            throw new ProjectException($"{shown}: the name '{manifest.Name}' of its manifest is not a name");
        }

        if (!NuGetVersion.TryParseStrict(manifest.Version ?? "", out _))
        {
            throw new ProjectException($"{shown}: '{manifest.Version}' is not a SemVer version");
        }
    }

    /// <summary>
    /// The plugin API a plugin was built against must be this Tazel's: before 1.0.0 a new MINOR may change the API, so
    /// the MAJOR and MINOR must be the same; from 1.0.0 the MAJOR, and a MINOR no newer.
    /// </summary>
    private static void CheckApi(PluginManifest manifest, string shown)
    {
        if (!Version.TryParse(manifest.PluginApi ?? "", out var api))
        {
            throw new ProjectException($"{shown}: '{manifest.PluginApi}' is not the version of a plugin API");
        }

        var compatible = ApiVersion.Major == 0
            ? api.Major == 0 && api.Minor == ApiVersion.Minor
            : api.Major == ApiVersion.Major && api.Minor <= ApiVersion.Minor;
        if (!compatible)
        {
            throw new ProjectException(
                $"{shown} was built against Progmasoft.Tazel.PluginApi {api.ToString(3)}, and this Tazel has " +
                $"{ApiVersion.ToString(3)}");
        }
    }

    private static void Apply(LoadedPlugin plugin, LoadedModule module, BuildContext build, Action<string> status, string place)
    {
        var entry = plugin.Assemblies[0];
        Type[] types;
        try
        {
            types = entry.GetExportedTypes();
        }
        catch (Exception error) when (error is ReflectionTypeLoadException or FileNotFoundException or FileLoadException)
        {
            throw new ProjectException($"{place}: the plugin {plugin.Id} cannot be loaded: {error.Message}", error);
        }

        var candidates = types.Where(type => typeof(ITazelPlugin).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })
            .ToList();
        if (candidates.Count != 1)
        {
            throw new ProjectException(
                $"{place}: the entry assembly of {plugin.Id} has {candidates.Count} public classes that implement ITazelPlugin; it needs one");
        }

        if (candidates[0].GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ProjectException($"{place}: {candidates[0].FullName} of {plugin.Id} has no public constructor without parameters");
        }

        try
        {
            var instance = (ITazelPlugin)Activator.CreateInstance(candidates[0])!;
            instance.Apply(new Context(plugin, module, build, status));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw Failed(plugin, place, error.InnerException);
        }
        catch (PluginException error)
        {
            throw new ProjectException($"{place}: the plugin {plugin.Id}: {error.Message}", error);
        }
        catch (Exception error) when (error is not ProjectException and not OutOfMemoryException)
        {
            throw Failed(plugin, place, error);
        }
    }

    private static ProjectException Failed(LoadedPlugin plugin, string place, Exception error) =>
        new($"{place}: the plugin {plugin.Id} failed when it was applied: {error.GetType().Name}: {error.Message}", error);

    private sealed class Context(LoadedPlugin plugin, LoadedModule module, BuildContext build, Action<string> status) : IPluginContext
    {
        public string Group => plugin.Manifest.Group;

        public string Name => plugin.Manifest.Name;

        public string Version => plugin.Manifest.Version;

        public Platform Platform => build.Platform;

        public BuildMode Mode => build.Mode;

        public string ModuleRoot => module.Root;

        public void Import(string @namespace)
        {
            if (string.IsNullOrWhiteSpace(@namespace) || @namespace.Split('.').Any(part => part.Length == 0))
            {
                throw new PluginException($"'{@namespace}' is not a namespace");
            }

            if (!plugin.Imports.Contains(@namespace, StringComparer.Ordinal))
            {
                plugin.Imports.Add(@namespace);
            }
        }

        public void Status(string message) => status($"{plugin.Id}: {message}");
    }

    /// <summary>The load context of one plugin: its own assemblies from its directory, Tazel's shared ones from Tazel.</summary>
    private sealed class PluginLoadContext(string name, string directory) : AssemblyLoadContext(name)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null || Shared.Contains(assemblyName.Name, StringComparer.Ordinal))
            {
                return null;
            }

            var file = Path.Combine(directory, assemblyName.Name + ".dll");
            return File.Exists(file) ? LoadFromAssemblyPath(Path.GetFullPath(file)) : null;
        }
    }
}
