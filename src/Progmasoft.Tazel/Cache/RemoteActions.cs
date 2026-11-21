// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;

namespace Progmasoft.Tazel.Cache;

/// <summary>
/// Takes the results of actions from a remote cache, and gives it the results of actions run here.
/// </summary>
/// <remarks>
/// <para>
/// The key of an action is the SHA-256 of what decides its outputs on any machine: the platform Tazel runs on, the
/// command, the digest of the program it runs, the environment without the variables that only name this user's
/// directories, and the path and digest of each input it declares. The directory it runs in is not part of it, so two
/// checkouts of a module share their results.
/// </para>
/// <para>
/// The inputs a compiler finds by itself, such as headers, are known only after it ran. Tazel stores them with the
/// result, each with its digest, as a file of the result named <see cref="DiscoveredInputs"/>; a result is taken only
/// when every one of them has here the digest it had there. Each file is checked against its SHA-256 and size before it
/// is put in place, and a result whose paths would leave the action's directory is refused.
/// </para>
/// <para>
/// A cache that cannot be reached or answers with an error is said once, and the build goes on without it.
/// </para>
/// </remarks>
internal sealed class RemoteActions(RemoteCache cache, bool read, bool write, Digests digests, Action<string> warn)
{
    /// <summary>The name, in a result, of the list of the inputs the action found by itself.</summary>
    public const string DiscoveredInputs = "@tazel/discovered-inputs";

    private static readonly HashSet<string> UserDirectories = new(StringComparer.Ordinal) { "HOME", "TMPDIR", "TMP", "TEMP" };

    private int _broken;

    /// <summary>Whether results of actions run here are given to the cache.</summary>
    public bool Writes => write && Volatile.Read(ref _broken) == 0;

    private bool Reads => read && Volatile.Read(ref _broken) == 0;

    /// <summary>The key of <paramref name="action"/>, or null when one of its declared inputs cannot be read.</summary>
    public string? KeyOf(BuildAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var text = new StringBuilder("tazel action 1\n")
            .Append(RuntimeInformation.OSDescription.Split(' ')[0]).Append('-').Append(RuntimeInformation.OSArchitecture)
            .Append('\n');
        foreach (var part in action.Command)
        {
            text.Append(part).Append('\0');
        }

        text.Append('\u0001').Append(Path.IsPathFullyQualified(action.Command[0]) ? digests.Of(action.Command[0]) : "")
            .Append('\u0001');
        foreach (var (name, value) in action.Environment.Where(entry => !UserDirectories.Contains(entry.Key))
                     .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        text.Append('\u0001');
        foreach (var input in action.Inputs.Order(StringComparer.Ordinal))
        {
            if (digests.Of(action.Full(input)) is not { } digest)
            {
                return null;
            }

            text.Append(Portable(input)).Append('\0').Append(digest).Append('\0');
        }

        return Digests.Sha256(Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>
    /// Puts the outputs of <paramref name="action"/> in place from the cache; false when the cache has no result for it
    /// that holds here.
    /// </summary>
    public async Task<bool> FetchAsync(BuildAction action, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Reads || action.Work is not null || KeyOf(action) is not { } key)
        {
            return false;
        }

        try
        {
            return await FetchAsync(action, key, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheTrouble(error, cancel))
        {
            Broken(error);
            return false;
        }
    }

    /// <summary>Gives the cache the outputs of <paramref name="action"/>, which just succeeded here.</summary>
    public async Task StoreAsync(BuildAction action, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Writes || action.Work is not null || KeyOf(action) is not { } key)
        {
            return;
        }

        try
        {
            await StoreAsync(action, key, cancel).ConfigureAwait(false);
        }
        catch (Exception error) when (IsCacheTrouble(error, cancel))
        {
            Broken(error);
        }
    }

    private async Task<bool> FetchAsync(BuildAction action, string key, CancellationToken cancel)
    {
        if (await cache.GetActionAsync(key, cancel).ConfigureAwait(false) is not { } message ||
            ActionResultMessage.Decode(message) is not { } files)
        {
            return false;
        }

        var byPath = new Dictionary<string, OutputFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (file.Path != DiscoveredInputs && !IsInside(file.Path))
            {
                return false;
            }

            byPath[file.Path] = file;
        }

        var wanted = Wanted(action).Select(Portable).ToList();
        if (!wanted.All(byPath.ContainsKey) || !byPath.TryGetValue(DiscoveredInputs, out var list) ||
            !await DiscoveredInputsHoldAsync(action, list, cancel).ConfigureAwait(false))
        {
            return false;
        }

        foreach (var path in wanted)
        {
            if (!await DownloadAsync(action, byPath[path], cancel).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether every input the action found by itself there has here the digest it had there.</summary>
    private async Task<bool> DiscoveredInputsHoldAsync(BuildAction action, OutputFile list, CancellationToken cancel)
    {
        if (list.Size > 16 * 1024 * 1024 ||
            await cache.GetContentAsync(list.Hash, cancel).ConfigureAwait(false) is not { } bytes ||
            Digests.Sha256(bytes) != list.Hash)
        {
            return false;
        }

        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 66 || line[64] != ' ' || digests.Of(action.Full(line[65..])) != line[..64])
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> DownloadAsync(BuildAction action, OutputFile file, CancellationToken cancel)
    {
        var destination = action.Full(file.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".remote";
        try
        {
            var stream = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                if (!await cache.GetContentAsync(file.Hash, stream, cancel).ConfigureAwait(false) || stream.Length != file.Size)
                {
                    return false;
                }

                stream.Position = 0;
                if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancel).ConfigureAwait(false)) != file.Hash)
                {
                    return false;
                }
            }

            if (file.IsExecutable && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(partial, File.GetUnixFileMode(partial) | UnixFileMode.UserExecute |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            File.Move(partial, destination, overwrite: true);
            digests.Forget(destination);
            return true;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    private async Task StoreAsync(BuildAction action, string key, CancellationToken cancel)
    {
        var files = new List<OutputFile>();
        foreach (var output in Wanted(action))
        {
            var full = action.Full(output);
            digests.Forget(full);
            if (digests.Of(full) is not { } hash)
            {
                return;
            }

            var executable = !OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(full) & UnixFileMode.UserExecute) != 0;
            files.Add(new OutputFile(Portable(output), hash, new FileInfo(full).Length, executable));
            if (!await cache.HasContentAsync(hash, cancel).ConfigureAwait(false))
            {
                await cache.PutContentAsync(hash, full, cancel).ConfigureAwait(false);
            }
        }

        var list = new StringBuilder();
        var found = action.Depfile is { } depfile && File.Exists(action.Full(depfile))
            ? Depfiles.Inputs(await File.ReadAllTextAsync(action.Full(depfile), cancel).ConfigureAwait(false))
            : [];
        foreach (var input in found.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (digests.Of(action.Full(input)) is not { } digest)
            {
                return;
            }

            list.Append(digest).Append(' ').Append(input).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(list.ToString());
        var listHash = Digests.Sha256(bytes);
        await cache.PutContentAsync(listHash, bytes, cancel).ConfigureAwait(false);
        files.Add(new OutputFile(DiscoveredInputs, listHash, bytes.Length, IsExecutable: false));
        await cache.PutActionAsync(key, ActionResultMessage.Encode(files), cancel).ConfigureAwait(false);
    }

    /// <summary>The files of a result: the outputs, and the depfile, which the local cache reads after a hit.</summary>
    private static IEnumerable<string> Wanted(BuildAction action) =>
        action.Depfile is { } depfile ? action.Outputs.Append(depfile).Distinct(StringComparer.Ordinal) : action.Outputs;

    /// <summary>A path of an action as a result stores it: relative, with <c>/</c>.</summary>
    private static string Portable(string path) => path.Replace('\\', '/');

    /// <summary>Whether a path of a result stays below the action's directory.</summary>
    private static bool IsInside(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.Contains('\\', StringComparison.Ordinal) &&
        !path.Contains(':', StringComparison.Ordinal) &&
        path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    private static bool IsCacheTrouble(Exception error, CancellationToken cancel) =>
        error is HttpRequestException or RemoteCacheException or IOException or UnauthorizedAccessException ||
        (error is OperationCanceledException && !cancel.IsCancellationRequested);

    private void Broken(Exception error)
    {
        if (Interlocked.Exchange(ref _broken, 1) == 0)
        {
            warn($"the remote cache at {cache.Address.Host} cannot be used ({error.Message}); building without it");
        }
    }
}
