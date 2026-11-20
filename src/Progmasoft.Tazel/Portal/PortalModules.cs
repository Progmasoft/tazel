// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Versioning;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Portal;

/// <summary>A dependency of a version in an index.</summary>
internal sealed record IndexDependency(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("test")] bool Test);

/// <summary>One version in an index.</summary>
internal sealed record IndexVersion(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("pushed")] DateTimeOffset Pushed,
    [property: JsonPropertyName("yanked")] bool Yanked,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<IndexDependency> Dependencies);

/// <summary><c>modules.tazel.net/$group/$module/index.json</c>: every version of one module.</summary>
internal sealed record ModuleIndex(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("versions")] IReadOnlyList<IndexVersion> Versions);

/// <summary>One module of <c>modules.tazel.net/index.json</c>, the list that search reads.</summary>
internal sealed record CatalogEntry(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("module")] string Module,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("latest")] string? Latest);

/// <summary><c>modules.tazel.net/index.json</c>.</summary>
internal sealed record Catalog([property: JsonPropertyName("modules")] IReadOnlyList<CatalogEntry> Modules);

/// <summary>
/// The client of Tazel Portal Modules, laid out as the Bazel Central Registry is: an index per module, and each version's
/// archive with its <c>.sha256sums</c> beside it. The address is <c>https://modules.tazel.net</c>, or
/// <c>TAZEL_PORTAL_MODULES_URL</c>.
/// </summary>
internal sealed class PortalModulesClient : IDisposable
{
    public const string DefaultAddress = "https://modules.tazel.net";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    private readonly HttpClient _http;

    private readonly string? _vendor;

    /// <summary>
    /// A client of the portal at <paramref name="address"/>; with <paramref name="vendor"/>, an index found there is read
    /// from there instead.
    /// </summary>
    public PortalModulesClient(Uri? address = null, HttpMessageHandler? handler = null, string? vendor = null)
    {
        _vendor = vendor;
        Address = address ?? new Uri(Environment.GetEnvironmentVariable("TAZEL_PORTAL_MODULES_URL") is { Length: > 0 } set
            ? set.TrimEnd('/')
            : DefaultAddress);
        if (Address.Scheme != Uri.UriSchemeHttps && !Address.IsLoopback)
        {
            throw new ProjectException($"Tazel Portal Modules is reached over https only, not {Address}");
        }

        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"tazel/{Cli.Commands.TazelVersion.ToString(3)}");
    }

    public Uri Address { get; }

    /// <summary>The address as text without a closing slash, for the paths below it.</summary>
    private string Base => Address.ToString().TrimEnd('/');

    /// <summary>The list of every module, for search.</summary>
    public async Task<Catalog> CatalogAsync(CancellationToken cancel) =>
        await GetJsonAsync<Catalog>("index.json", cancel).ConfigureAwait(false)
            ?? throw new ProjectException($"{Base}/index.json is empty");

    /// <summary>The index of <paramref name="group"/>:<paramref name="module"/>, or null when there is no such module.</summary>
    public async Task<ModuleIndex?> IndexAsync(string group, string module, CancellationToken cancel)
    {
        var text = await IndexTextAsync(group, module, cancel).ConfigureAwait(false);
        return text is null ? null : Parse<ModuleIndex>(text, $"{group}/{module}/index.json");
    }

    /// <summary>The text of an index: from vendor/ when it is there, else from the portal; null for no such module.</summary>
    public async Task<string?> IndexTextAsync(string group, string module, CancellationToken cancel)
    {
        var path = $"{group}/{module}/index.json";
        if (_vendor is not null && File.Exists(Path.Combine(_vendor, group, module, "index.json")))
        {
            return await File.ReadAllTextAsync(Path.Combine(_vendor, group, module, "index.json"), cancel).ConfigureAwait(false);
        }

        return await GetTextAsync(path, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the archive of a version into <paramref name="directory"/>, checks it against its
    /// <c>.sha256sums</c>, and returns its path and SHA-256.
    /// </summary>
    public async Task<(string Archive, string Sha256)> DownloadAsync(
        string group, string module, string version, string directory, CancellationToken cancel)
    {
        var name = $"{ModuleArchive.BaseName(group, module, version)}{ModuleArchive.Extension}";
        var path = $"{group}/{module}/{version}/{name}";
        var sums = await GetTextAsync(path + ".sha256sums", cancel).ConfigureAwait(false)
            ?? throw new ProjectException($"{Base}/{path}.sha256sums is not there");
        var expected = Sha256Sums.SumFor(sums, name, $"{path}.sha256sums")
            ?? throw new ProjectException($"{Base}/{path}.sha256sums does not name {name}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, name);
        var partial = file + ".partial";
        using (var response = await _http.GetAsync(new Uri($"{Base}/{path}"), HttpCompletionOption.ResponseHeadersRead, cancel)
                   .ConfigureAwait(false))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ProjectException($"{Base}/{path} answered {(int)response.StatusCode}");
            }

            var output = File.Create(partial);
            await using (output.ConfigureAwait(false))
            {
                await response.Content.CopyToAsync(output, cancel).ConfigureAwait(false);
            }
        }

        var actual = Sha256Sums.Sha256(partial);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            File.Delete(partial);
            throw new ProjectException($"{Base}/{path} has the SHA-256 {actual}, and its .sha256sums says {expected}");
        }

        File.Move(partial, file, overwrite: true);
        await File.WriteAllTextAsync(file + ".sha256sums", $"{actual}  {name}\n", cancel).ConfigureAwait(false);
        return (file, actual);
    }

    /// <summary>The highest version of <paramref name="index"/> by SemVer that is not yanked; a release before any pre-release.</summary>
    public static IndexVersion? Highest(ModuleIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var usable = index.Versions.Where(v => !v.Yanked && NuGetVersion.TryParseStrict(v.Version, out _))
            .Select(v => (Entry: v, Version: NuGetVersion.Parse(v.Version))).ToList();
        var releases = usable.Where(v => !v.Version.IsPrerelease).ToList();
        return (releases.Count > 0 ? releases : usable).OrderByDescending(v => v.Version).Select(v => v.Entry).FirstOrDefault();
    }

    public void Dispose() => _http.Dispose();

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancel)
        where T : class
    {
        var text = await GetTextAsync(path, cancel).ConfigureAwait(false);
        return text is null ? null : Parse<T>(text, path);
    }

    private T Parse<T>(string text, string path)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)
                ?? throw new ProjectException($"{Base}/{path} is empty");
        }
        catch (JsonException error)
        {
            throw new ProjectException($"{Base}/{path} is not what Tazel Portal Modules serves: {error.Message}", error);
        }
    }

    private async Task<string?> GetTextAsync(string path, CancellationToken cancel)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri($"{Base}/{path}"), cancel).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProjectException($"{Base}/{path} answered {(int)response.StatusCode}");
            }

            return await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new ProjectException($"Tazel Portal Modules at {Address} cannot be reached: {error.Message}", error);
        }
    }
}
