// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Portal;

namespace Progmasoft.Tazel.Project;

/// <summary>A module of Tazel Portal Modules a build uses, at the version the resolution chose.</summary>
/// <param name="Repository">The name its labels use: the alias, or the module's name.</param>
/// <param name="Group">Its group.</param>
/// <param name="Module">Its name.</param>
/// <param name="Version">The version chosen.</param>
internal sealed record ResolvedModule(string Repository, string Group, string Module, NuGetVersion Version)
{
    public string Key => $"{Group}:{Module}";

    public string BaseName => ModuleArchive.BaseName(Group, Module, Version.ToNormalizedString());
}

/// <summary>
/// Resolves the modules of Tazel Portal Modules a build needs, as Bazel's minimal version selection does: each module at
/// the highest version anything asks for. Their dependencies come from the indexes, so no archive is downloaded to
/// resolve. A yanked version takes part in no resolution.
/// </summary>
internal static class PortalDependencies
{
    /// <summary>The directory below the main module whose archives a build takes before it downloads.</summary>
    public const string VendorDirectory = "vendor";

    public static async Task<IReadOnlyList<ResolvedModule>> ResolveAsync(
        IReadOnlyList<(string Group, string Module, string Version, string? Alias, string Place)> direct,
        PortalModulesClient client, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(direct);
        var wanted = new Dictionary<string, NuGetVersion>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var why = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var (group, module, text, alias, place) in direct)
        {
            if (!NuGetVersion.TryParseStrict(text, out var version))
            {
                throw new ProjectException($"{place}: '{text}' is not a SemVer version");
            }

            var key = $"{group}:{module}";
            var repository = alias ?? module;
            if (names.TryGetValue(key, out var other) && other != repository)
            {
                throw new ProjectException($"{place}: {key} is named twice, as @{other} and @{repository}");
            }

            names[key] = repository;
            Ask(key, version, place);
        }

        var indexes = new Dictionary<string, ModuleIndex>(StringComparer.Ordinal);
        var done = new Dictionary<string, NuGetVersion>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            var version = wanted[key];
            if (done.TryGetValue(key, out var seen) && seen == version)
            {
                continue;
            }

            done[key] = version;
            if (!indexes.TryGetValue(key, out var index))
            {
                var parts = key.Split(':');
                index = await client.IndexAsync(parts[0], parts[1], cancel).ConfigureAwait(false)
                    ?? throw new ProjectException($"{why[key]}: Tazel Portal Modules has no module {key}");
                indexes[key] = index;
            }

            var entry = index.Versions.FirstOrDefault(v =>
                NuGetVersion.TryParseStrict(v.Version, out var listed) && listed == version)
                ?? throw new ProjectException(
                    $"{why[key]}: {key} has no version {version}; it has {string.Join(", ", index.Versions.Select(v => v.Version))}");
            if (entry.Yanked)
            {
                throw new ProjectException($"{why[key]}: {key} {version} is yanked, so it takes part in no resolution");
            }

            foreach (var dependency in entry.Dependencies.Where(d => !d.Test))
            {
                if (!NuGetVersion.TryParseStrict(dependency.Version, out var needed))
                {
                    throw new ProjectException($"the index of {key} names {dependency.Group}:{dependency.Module} at '{dependency.Version}'");
                }

                var next = $"{dependency.Group}:{dependency.Module}";
                names.TryAdd(next, dependency.Module);
                Ask(next, needed, $"{key} {version}");
            }
        }

        var resolved = wanted.Select(entry =>
        {
            var parts = entry.Key.Split(':');
            return new ResolvedModule(names[entry.Key], parts[0], parts[1], entry.Value);
        }).OrderBy(module => module.Key, StringComparer.Ordinal).ToList();
        var clash = resolved.GroupBy(module => module.Repository).FirstOrDefault(group => group.Count() > 1);
        if (clash is not null)
        {
            throw new ProjectException(
                $"{string.Join(" and ", clash.Select(m => m.Key))} would both be @{clash.Key}; give one of them an Alias");
        }

        return resolved;

        void Ask(string key, NuGetVersion version, string place)
        {
            if (!wanted.TryGetValue(key, out var current) || version > current)
            {
                wanted[key] = version;
                why[key] = place;
                queue.Enqueue(key);
            }
        }
    }

    /// <summary>
    /// The root of <paramref name="module"/>, unpacked once below the state directory: from <c>vendor/</c> when its
    /// archive is there, checked against its <c>.sha256sums</c>, or downloaded.
    /// </summary>
    public static async Task<string> FetchAsync(
        string mainRoot, ResolvedModule module, PortalModulesClient? client, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(module);
        var name = module.BaseName + ModuleArchive.Extension;
        var vendored = Path.Combine(mainRoot, VendorDirectory, name);
        string archive;
        string sha;
        if (File.Exists(vendored))
        {
            var sums = vendored + ".sha256sums";
            var expected = File.Exists(sums)
                ? Sha256Sums.SumFor(await File.ReadAllTextAsync(sums, cancel).ConfigureAwait(false), name, $"vendor/{name}.sha256sums")
                : null;
            sha = Sha256Sums.Sha256(vendored);
            if (expected is null || !string.Equals(expected, sha, StringComparison.Ordinal))
            {
                throw new ProjectException($"vendor/{name} does not match its .sha256sums; run tazel vendor again");
            }

            archive = vendored;
        }
        else
        {
            if (client is null)
            {
                throw new ProjectException($"{module.Key} {module.Version} is not in vendor/ and nothing may be downloaded");
            }

            (archive, sha) = await client.DownloadAsync(module.Group, module.Module, module.Version.ToNormalizedString(),
                Path.Combine(mainRoot, ModuleFiles.StateDirectory, "downloads"), cancel).ConfigureAwait(false);
        }

        var destination = Path.Combine(mainRoot, ModuleFiles.StateDirectory, "external", $"{module.BaseName}-{sha[..16]}");
        var marker = Path.Combine(destination, ".unpacked");
        if (File.Exists(marker))
        {
            return Path.Combine(destination, (await File.ReadAllTextAsync(marker, cancel).ConfigureAwait(false)).Trim());
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        var root = ModuleArchive.Unpack(archive, destination);
        await File.WriteAllTextAsync(marker, Path.GetFileName(root), cancel).ConfigureAwait(false);
        return root;
    }
}
