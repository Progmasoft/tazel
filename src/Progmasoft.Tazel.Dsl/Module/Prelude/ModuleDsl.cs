// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Module.Prelude;

/// <summary>What a module says about itself.</summary>
public sealed class ModuleDefinition
{
    /// <summary>The group, as in <c>com.example</c>.</summary>
    public string Group { get; init; } = "";

    /// <summary>The name of the module.</summary>
    public string Name { get; init; } = "";

    /// <summary>The SemVer version.</summary>
    public string Version { get; init; } = "";

    /// <summary>One sentence about the module.</summary>
    public string? Description { get; init; }

    /// <summary>Who wrote it.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>The SPDX expression of its license.</summary>
    public string? License { get; init; }

    /// <summary>Its README; default: README.md.</summary>
    public string Readme { get; init; } = "README.md";

    /// <summary>The Tazel versions it builds with; default: any.</summary>
    public TazelVersion? Compatibility { get; init; }
}

/// <summary>The module of a <c>MODULE.csx</c>.</summary>
public sealed class Module
{
    private Module(ModuleDefinition definition) => Definition = definition;

    /// <summary>What the module says about itself.</summary>
    public ModuleDefinition Definition { get; }

    /// <summary>The module of this file.</summary>
    public static Module Define(ModuleDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new Module(definition);
    }

    /// <summary>A dependency on a module of Tazel Portal Modules.</summary>
    public static Dependency From(ModuleCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        return new Dependency(coordinates, null);
    }
}

/// <summary>A module of Tazel Portal Modules, by group, name and version.</summary>
public sealed class ModuleCoordinates
{
    /// <summary>The group.</summary>
    public string Group { get; init; } = "";

    /// <summary>The name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The version.</summary>
    public string Version { get; init; } = "";

    /// <summary>The name its labels use instead of <see cref="Name"/>.</summary>
    public string? Alias { get; init; }
}

/// <summary>A module in an archive of this module.</summary>
public sealed class LocalModuleSettings
{
    /// <summary>The name its labels use: <c>@name//:x</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>The archive, a <c>.tazel.tar.xz</c> file.</summary>
    public string Include { get; init; } = "";

    /// <summary>The SHA-256 of the archive.</summary>
    public string? Sha256 { get; init; }

    /// <summary>A <c>.sha256sums</c> file that names the archive.</summary>
    public string? Sha256Sums { get; init; }
}

/// <summary>A module in an archive of this module.</summary>
public static class LocalModule
{
    /// <summary>A dependency on an archive.</summary>
    public static Dependency From(LocalModuleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Dependency(null, settings);
    }
}

/// <summary>A module this module depends on.</summary>
public sealed class Dependency
{
    internal Dependency(ModuleCoordinates? portal, LocalModuleSettings? local)
    {
        Portal = portal;
        Local = local;
    }

    /// <summary>The module of Tazel Portal Modules, if it is one.</summary>
    public ModuleCoordinates? Portal { get; }

    /// <summary>The archive, if it is one.</summary>
    public LocalModuleSettings? Local { get; }
}

/// <summary>The modules a module depends on.</summary>
public sealed class Dependencies
{
    private Dependencies(IReadOnlyList<Dependency> items) => Items = items;

    /// <summary>The dependencies, in their order.</summary>
    public IReadOnlyList<Dependency> Items { get; }

    /// <summary>The dependencies of this module.</summary>
    public static Dependencies Define(params Dependency[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new Dependencies(items);
    }
}

/// <summary>Whether and how the module goes to Tazel Portal Modules.</summary>
public sealed class PortalModulesSettings
{
    /// <summary>Whether <c>tazel push</c> may push it; default: false.</summary>
    public bool Push { get; init; }

    /// <summary>Files left out of its archive.</summary>
    public Files Exclude { get; init; } = Files.None;
}

/// <summary>Whether and how the module goes to Tazel Portal Modules.</summary>
public sealed class PortalModules
{
    private PortalModules(PortalModulesSettings settings) => Settings = settings;

    /// <summary>The settings.</summary>
    public PortalModulesSettings Settings { get; }

    /// <summary>The settings of this module.</summary>
    public static PortalModules Define(PortalModulesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PortalModules(settings);
    }
}

/// <summary>A remote cache or remote execution service.</summary>
public sealed class RemoteSettings
{
    /// <summary>Whether results are read from it; default: true.</summary>
    public bool Read { get; init; } = true;

    /// <summary>Whether results are written to it; default: false.</summary>
    public bool Write { get; init; }

    /// <summary>The token that signs in.</summary>
    public string? Token { get; init; }

    /// <summary>The key that checks what is read.</summary>
    public string? PublicKey { get; init; }

    /// <summary>The key that signs what is written.</summary>
    public string? SigningKey { get; init; }
}

/// <summary>A service at a URL.</summary>
public sealed class Remote
{
    private Remote(Uri url, RemoteSettings settings)
    {
        Url = url;
        Settings = settings;
    }

    /// <summary>Where the service is.</summary>
    public Uri Url { get; }

    /// <summary>How it is used.</summary>
    public RemoteSettings Settings { get; }

    /// <summary>The service at <paramref name="url"/>.</summary>
    public static Remote From(string url, RemoteSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new DslException($"'{url}' is not an https URL");
        }

        return new Remote(parsed, settings);
    }
}

/// <summary>The local and remote cache of results.</summary>
public sealed class CacheSettings
{
    /// <summary>Whether results are kept on this machine; default: true.</summary>
    public bool Local { get; init; } = true;

    /// <summary>How many results are kept per target; default: 8.</summary>
    public int Keep { get; init; } = 8;

    /// <summary>A remote cache; default: none.</summary>
    public Remote? Remote { get; init; }
}

/// <summary>The cache of a module.</summary>
public sealed class Cache
{
    private Cache(CacheSettings settings) => Settings = settings;

    /// <summary>The settings.</summary>
    public CacheSettings Settings { get; }

    /// <summary>The cache of this module.</summary>
    public static Cache Define(CacheSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Cache(settings);
    }
}

/// <summary>How a build runs.</summary>
public sealed class ExecutionSettings
{
    /// <summary>How many actions run at once; default: the processors.</summary>
    public int? Jobs { get; init; }

    /// <summary>How much memory the actions may use together; default: no limit.</summary>
    public ByteSize? Memory { get; init; }

    /// <summary>Whether a build goes on after an action failed; default: false.</summary>
    public bool KeepGoing { get; init; }

    /// <summary>A remote execution service; default: none.</summary>
    public Remote? Remote { get; init; }
}

/// <summary>How the builds of a module run.</summary>
public sealed class Execution
{
    private Execution(ExecutionSettings settings) => Settings = settings;

    /// <summary>The settings.</summary>
    public ExecutionSettings Settings { get; }

    /// <summary>How the builds of this module run.</summary>
    public static Execution Define(ExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new Execution(settings);
    }
}

/// <summary>A plugin, from Tazel Portal or from an archive of this module.</summary>
public sealed class PluginReference
{
    internal PluginReference(string? group, string? name, string? version, string? path)
    {
        Group = group;
        Name = name;
        Version = version;
        Path = path;
    }

    /// <summary>The group of a plugin of Tazel Portal.</summary>
    public string? Group { get; }

    /// <summary>The name of a plugin of Tazel Portal.</summary>
    public string? Name { get; }

    /// <summary>The version; none takes the highest by SemVer.</summary>
    public string? Version { get; }

    /// <summary>The archive of a local plugin.</summary>
    public string? Path { get; }
}

/// <summary>A plugin of Tazel Portal.</summary>
public static class Plugin
{
    /// <summary>The plugin <paramref name="group"/>.<paramref name="name"/>, at a version or the highest.</summary>
    public static PluginReference From(string group, string name, string? version = null) =>
        new(group, name, version, null);
}

/// <summary>A plugin in an archive of this module.</summary>
public static class LocalPlugin
{
    /// <summary>The plugin in the <c>.tazel.tar.xz</c> at <paramref name="path"/>.</summary>
    public static PluginReference From(string path) => new(null, null, null, path);
}

/// <summary>The plugins of a module.</summary>
public sealed class Plugins
{
    private Plugins(IReadOnlyList<PluginReference> items) => Items = items;

    /// <summary>The plugins, in their order.</summary>
    public IReadOnlyList<PluginReference> Items { get; }

    /// <summary>The plugins of this module.</summary>
    public static Plugins Define(params PluginReference[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new Plugins(items);
    }
}

/// <summary>The toolchains a module registers, in the order they are tried.</summary>
public sealed class Toolchains
{
    private Toolchains(IReadOnlyList<string> labels) => Labels = labels;

    /// <summary>The labels of the toolchains.</summary>
    public IReadOnlyList<string> Labels { get; }

    /// <summary>Registers toolchains by label.</summary>
    public static Toolchains Define(params string[] labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        return new Toolchains(labels);
    }
}

/// <summary>What a <c>MODULE.csx</c> sets.</summary>
public sealed class ModuleGlobals
{
    /// <summary>The module.</summary>
    public Module? module { get; set; }

    /// <summary>The modules it depends on.</summary>
    public Dependencies? dependencies { get; set; }

    /// <summary>Its settings for Tazel Portal Modules.</summary>
    public PortalModules? portalModules { get; set; }

    /// <summary>Its cache.</summary>
    public Cache? cache { get; set; }

    /// <summary>How its builds run.</summary>
    public Execution? execution { get; set; }

    /// <summary>Its plugins.</summary>
    public Plugins? plugins { get; set; }

    /// <summary>Its toolchains.</summary>
    public Toolchains? toolchains { get; set; }
}
