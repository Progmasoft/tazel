// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Progmasoft.Tazel.Archive;

namespace Progmasoft.Tazel.PluginSdk;

/// <summary>
/// Makes <c>$group.$name-$version.tazel.tar.xz</c> and its <c>.sha256sums</c> from the output of a plugin: its
/// assemblies and those of its references, with <c>tazel-plugin.json</c>. The plugin API and the DSL are left out,
/// whatever brought them, as Tazel has its own.
/// </summary>
public sealed class PackTazelPlugin : Task
{
    private static readonly string[] Shared = ["Progmasoft.Tazel.PluginApi.dll", "Progmasoft.Tazel.Dsl.dll"];

    /// <summary>The output directory of the build.</summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <summary>The file of the plugin's own assembly.</summary>
    [Required]
    public string EntryAssembly { get; set; } = "";

    /// <summary>The group of the plugin.</summary>
    [Required]
    public string Group { get; set; } = "";

    /// <summary>The name of the plugin.</summary>
    [Required]
    public string Name { get; set; } = "";

    /// <summary>The version of the plugin.</summary>
    [Required]
    public string Version { get; set; } = "";

    /// <summary>The version of the plugin API the plugin was built against.</summary>
    [Required]
    public string PluginApiVersion { get; set; } = "";

    /// <summary>Where the archive goes.</summary>
    [Required]
    public string PackageOutputPath { get; set; } = "";

    /// <summary>The archive that was written.</summary>
    [Output]
    public string Archive { get; set; } = "";

    /// <inheritdoc/>
    public override bool Execute()
    {
        var files = Directory.EnumerateFiles(OutputDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).OfType<string>()
            .Where(file => !Shared.Contains(file, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (!files.Contains(EntryAssembly, StringComparer.Ordinal))
        {
            Log.LogError($"{EntryAssembly} is not in {OutputDirectory}");
            return false;
        }

        var staging = Path.Combine(Path.GetTempPath(), "tazel-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                File.Copy(Path.Combine(OutputDirectory, file), Path.Combine(staging, file));
            }

            var manifest = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["entryAssembly"] = EntryAssembly,
                ["group"] = Group,
                ["name"] = Name,
                ["pluginApi"] = PluginApiVersion,
                ["version"] = Version,
            };
            File.WriteAllText(Path.Combine(staging, "tazel-plugin.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            files.Add("tazel-plugin.json");
            files.Sort(StringComparer.Ordinal);

            var baseName = $"{Group}.{Name}-{Version}";
            Archive = Path.Combine(PackageOutputPath, baseName + ".tazel.tar.xz");
            ReproducibleArchive.Write(staging, baseName, Archive, files);
            File.WriteAllText(Archive + ".sha256sums", ReproducibleArchive.SumsLine(Archive));
            Log.LogMessage(MessageImportance.High, $"Tazel plugin: {Archive}");
            return true;
        }
        catch (IOException error)
        {
            Log.LogError($"The plugin archive could not be written: {error.Message}");
            return false;
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }
}
