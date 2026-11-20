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
        var resolution = new Resolution(client);
        foreach (var (group, module, text, alias, place) in direct)
        {
            resolution.AddDirect(group, module, text, alias, place);
        }

        await resolution.RunAsync(cancel).ConfigureAwait(false);
        return resolution.Result();
    }

    /// <summary>One resolution: each module at the highest version anything asks for, followed through the indexes.</summary>
    private sealed class Resolution(PortalModulesClient client)
    {
        private readonly Dictionary<string, NuGetVersion> _wanted = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _why = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ModuleIndex> _indexes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NuGetVersion> _done = new(StringComparer.Ordinal);
        private readonly Queue<string> _queue = new();

        public void AddDirect(string group, string module, string text, string? alias, string place)
        {
            if (!NuGetVersion.TryParseStrict(text, out var version))
            {
                throw new ProjectException($"{place}: '{text}' is not a SemVer version");
            }

            Portal.PortalNames.Check(group, module, place);
            var key = $"{group}:{module}";
            var repository = alias ?? module;
            if (_names.TryGetValue(key, out var other) && other != repository)
            {
                throw new ProjectException($"{place}: {key} is named twice, as @{other} and @{repository}");
            }

            _names[key] = repository;
            Ask(key, version, place);
        }

        public async Task RunAsync(CancellationToken cancel)
        {
            while (_queue.Count > 0)
            {
                var key = _queue.Dequeue();
                var version = _wanted[key];
                if (_done.TryGetValue(key, out var seen) && seen == version)
                {
                    continue;
                }

                _done[key] = version;
                var entry = await EntryAsync(key, version, cancel).ConfigureAwait(false);
                foreach (var dependency in entry.Dependencies.Where(d => !d.Test))
                {
                    Follow(key, version, dependency);
                }
            }
        }

        public List<ResolvedModule> Result()
        {
            var resolved = _wanted.Select(entry =>
            {
                var parts = entry.Key.Split(':');
                return new ResolvedModule(_names[entry.Key], parts[0], parts[1], entry.Value);
            }).OrderBy(module => module.Key, StringComparer.Ordinal).ToList();
            var clash = resolved.GroupBy(module => module.Repository).FirstOrDefault(group => group.Count() > 1);
            return clash is null
                ? resolved
                : throw new ProjectException(
                    $"{string.Join(" and ", clash.Select(m => m.Key))} would both be @{clash.Key}; give one of them an Alias");
        }

        /// <summary>The entry of <paramref name="version"/> in the index of <paramref name="key"/>, which must not be yanked.</summary>
        private async Task<IndexVersion> EntryAsync(string key, NuGetVersion version, CancellationToken cancel)
        {
            if (!_indexes.TryGetValue(key, out var index))
            {
                var parts = key.Split(':');
                index = await client.IndexAsync(parts[0], parts[1], cancel).ConfigureAwait(false)
                    ?? throw new ProjectException($"{_why[key]}: Tazel Portal Modules has no module {key}");
                _indexes[key] = index;
            }

            var entry = index.Versions.FirstOrDefault(v => NuGetVersion.TryParseStrict(v.Version, out var listed) && listed == version)
                ?? throw new ProjectException(
                    $"{_why[key]}: {key} has no version {version}; it has {string.Join(", ", index.Versions.Select(v => v.Version))}");
            return entry.Yanked
                ? throw new ProjectException($"{_why[key]}: {key} {version} is yanked, so it takes part in no resolution")
                : entry;
        }

        /// <summary>A dependency an index names, whose names and version are checked, as the server sent them.</summary>
        private void Follow(string key, NuGetVersion version, IndexDependency dependency)
        {
            if (!NuGetVersion.TryParseStrict(dependency.Version, out var needed))
            {
                throw new ProjectException($"the index of {key} names {dependency.Group}:{dependency.Module} at '{dependency.Version}'");
            }

            Portal.PortalNames.Check(dependency.Group, dependency.Module, $"the index of {key}");
            var next = $"{dependency.Group}:{dependency.Module}";
            _names.TryAdd(next, dependency.Module);
            Ask(next, needed, $"{key} {version}");
        }

        private void Ask(string key, NuGetVersion version, string place)
        {
            if (!_wanted.TryGetValue(key, out var current) || version > current)
            {
                _wanted[key] = version;
                _why[key] = place;
                _queue.Enqueue(key);
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
        if (ModuleArchive.FromMarker(destination) is { } unpacked)
        {
            return unpacked;
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        var root = ModuleArchive.Unpack(archive, destination);
        ModuleArchive.WriteMarker(destination, root);
        return root;
    }
}
