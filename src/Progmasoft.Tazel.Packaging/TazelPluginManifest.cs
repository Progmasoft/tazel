// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Progmasoft.Tazel.Packaging;

/// <summary>
/// The manifest of a plugin, <c>tazel-plugin.json</c> at the top of its archive: who the plugin is, the plugin API it
/// was built against and the assembly Tazel loads first.
/// </summary>
/// <param name="Group">The group, such as <c>com.example</c>.</param>
/// <param name="Name">The name.</param>
/// <param name="Version">The SemVer version.</param>
/// <param name="PluginApi">The version of Progmasoft.Tazel.PluginApi it was built against.</param>
/// <param name="EntryAssembly">The file name of the assembly that holds the plugin, at the top of the archive.</param>
public sealed record TazelPluginManifest(string Group, string Name, string Version, string PluginApi, string EntryAssembly)
{
    /// <summary>The name of the manifest file.</summary>
    public static string FileName { get; } = "tazel-plugin.json";

    private static readonly JsonSerializerOptions ReadOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowTrailingCommas = false };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The identity of the plugin's package.</summary>
    /// <exception cref="TazelPackageException">The group, name or version is not what it must be.</exception>
    public TazelPackageIdentity Identity => new(Group, Name, Version);

    /// <summary>Reads a manifest and checks it with <see cref="Check"/>.</summary>
    /// <param name="json">The text of <c>tazel-plugin.json</c>.</param>
    /// <param name="shown">How the manifest is named in a message.</param>
    /// <exception cref="TazelPackageException">The text is not a manifest, or the manifest is not valid.</exception>
    public static TazelPluginManifest Read(string json, string shown)
    {
        ArgumentNullException.ThrowIfNull(json);
        TazelPluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<TazelPluginManifest>(json, ReadOptions);
        }
        catch (JsonException error)
        {
            throw new TazelPackageException($"{shown}: {FileName} cannot be read: {error.Message}", error);
        }

        if (manifest is null)
        {
            throw new TazelPackageException($"{shown}: {FileName} is empty");
        }

        manifest.Check(shown);
        return manifest;
    }

    /// <summary>
    /// Checks the manifest: a group of names joined by dots, a name, a SemVer version, a plugin API version and an entry
    /// assembly that is a <c>.dll</c> at the top of the archive. Whether the plugin API matches a Tazel is Tazel's question.
    /// </summary>
    /// <exception cref="TazelPackageException">A field is not what it must be; the message says which.</exception>
    public void Check(string shown)
    {
        CheckIdentity(shown);
        CheckLoading(shown);
    }

    private void CheckIdentity(string shown)
    {
        if (string.IsNullOrEmpty(Group) || !TazelPackageIdentity.IsGroup(Group))
        {
            throw new TazelPackageException($"{shown}: the group '{Group}' of its manifest is not names joined by dots");
        }

        if (string.IsNullOrEmpty(Name) || !TazelPackageIdentity.IsName(Name))
        {
            throw new TazelPackageException($"{shown}: the name '{Name}' of its manifest is not a name");
        }

        if (!TazelPackageIdentity.IsSemVer(Version ?? ""))
        {
            throw new TazelPackageException($"{shown}: '{Version}' is not a SemVer version");
        }
    }

    private void CheckLoading(string shown)
    {
        if (!System.Version.TryParse(PluginApi ?? "", out _))
        {
            throw new TazelPackageException($"{shown}: '{PluginApi}' is not the version of a plugin API");
        }

        if (string.IsNullOrEmpty(EntryAssembly) || EntryAssembly.Contains('/', StringComparison.Ordinal) ||
            EntryAssembly.Contains('\\', StringComparison.Ordinal) || !EntryAssembly.EndsWith(".dll", StringComparison.Ordinal))
        {
            throw new TazelPackageException($"{shown}: the entry assembly '{EntryAssembly}' is not a .dll at the top");
        }
    }

    /// <summary>The manifest as <c>tazel-plugin.json</c> holds it: its keys sorted, indented, with a final newline.</summary>
    public string ToJson()
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["entryAssembly"] = EntryAssembly,
            ["group"] = Group,
            ["name"] = Name,
            ["pluginApi"] = PluginApi,
            ["version"] = Version,
        };
        return JsonSerializer.Serialize(fields, WriteOptions) + "\n";
    }
}
