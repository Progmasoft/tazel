// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.Cache;

/// <summary>
/// The HTTP side of a remote cache, as Bazel's HTTP caching protocol lays it out and bazel-remote serves it: action
/// results at <c>/ac/&lt;sha256&gt;</c> and file contents at <c>/cas/&lt;sha256&gt;</c>, read with GET and HEAD and written
/// with PUT.
/// </summary>
internal sealed class RemoteCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    /// <summary>
    /// The cache at <paramref name="address"/>. A <paramref name="token"/> that starts with <c>Basic </c> or
    /// <c>Bearer </c> is sent as it is in <c>Authorization</c>; any other is sent as a bearer token.
    /// </summary>
    public RemoteCache(Uri address, string? token, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.Scheme != Uri.UriSchemeHttps && !address.IsLoopback)
        {
            throw new ArgumentException($"a remote cache is reached over https only, not {address}", nameof(address));
        }

        Address = address;
        _base = address.ToString().TrimEnd('/');
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"tazel/{Cli.Commands.TazelVersion.ToString(3)}");
        if (token is { Length: > 0 })
        {
            _http.DefaultRequestHeaders.Authorization = token.StartsWith("Basic ", StringComparison.Ordinal) ||
                token.StartsWith("Bearer ", StringComparison.Ordinal)
                    ? AuthenticationHeaderValue.Parse(token)
                    : new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public Uri Address { get; }

    /// <summary>The action result stored under <paramref name="key"/>, or null when there is none.</summary>
    public Task<byte[]?> GetActionAsync(string key, CancellationToken cancel) => GetAsync($"ac/{key}", cancel);

    /// <summary>Stores <paramref name="message"/> as the action result of <paramref name="key"/>.</summary>
    public Task PutActionAsync(string key, byte[] message, CancellationToken cancel) =>
        PutAsync($"ac/{key}", new ByteArrayContent(message), cancel);

    /// <summary>Whether the cache holds the content <paramref name="hash"/>.</summary>
    public async Task<bool> HasContentAsync(string hash, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, $"{_base}/cas/{hash}");
        using var response = await _http.SendAsync(request, cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        Check(response, "HEAD", $"cas/{hash}");
        return true;
    }

    /// <summary>Writes the content <paramref name="hash"/> to <paramref name="destination"/>; false when there is none.</summary>
    public async Task<bool> GetContentAsync(string hash, Stream destination, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var response = await _http.GetAsync($"{_base}/cas/{hash}", HttpCompletionOption.ResponseHeadersRead, cancel)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        Check(response, "GET", $"cas/{hash}");
        await response.Content.CopyToAsync(destination, cancel).ConfigureAwait(false);
        return true;
    }

    /// <summary>Stores the file <paramref name="path"/> as the content <paramref name="hash"/>.</summary>
    public async Task PutContentAsync(string hash, string path, CancellationToken cancel)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            await PutAsync($"cas/{hash}", new StreamContent(stream), cancel).ConfigureAwait(false);
        }
    }

    /// <summary>Stores <paramref name="bytes"/> as the content <paramref name="hash"/>.</summary>
    public Task PutContentAsync(string hash, byte[] bytes, CancellationToken cancel) =>
        PutAsync($"cas/{hash}", new ByteArrayContent(bytes), cancel);

    /// <summary>The content <paramref name="hash"/>, or null when there is none; for small contents only.</summary>
    public Task<byte[]?> GetContentAsync(string hash, CancellationToken cancel) => GetAsync($"cas/{hash}", cancel);

    public void Dispose() => _http.Dispose();

    private async Task<byte[]?> GetAsync(string path, CancellationToken cancel)
    {
        using var response = await _http.GetAsync($"{_base}/{path}", cancel).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        Check(response, "GET", path);
        return await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
    }

    private async Task PutAsync(string path, HttpContent content, CancellationToken cancel)
    {
        using (content)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.PutAsync($"{_base}/{path}", content, cancel).ConfigureAwait(false);
            Check(response, "PUT", path);
        }
    }

    private void Check(HttpResponseMessage response, string method, string path)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteCacheException(
                $"{method} {Address.Host}/{path} answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }
}

/// <summary>A remote cache answered with an error or could not be reached.</summary>
public sealed class RemoteCacheException(string message) : Exception(message);
