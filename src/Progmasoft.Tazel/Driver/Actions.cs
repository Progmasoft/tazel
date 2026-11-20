// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Progmasoft.Tazel.Driver;

/// <summary>
/// One action of a build: a command line with its environment, the files it reads and the files it writes, all relative
/// to the directory it runs in. Inputs the command finds by itself, such as headers, come from its depfile.
/// </summary>
internal sealed class BuildAction
{
    public required string Description { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    public required string Directory { get; init; }

    public required IReadOnlyList<string> Inputs { get; init; }

    public required IReadOnlyList<string> Outputs { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The Make-style dependency file the command writes, if it writes one.</summary>
    public string? Depfile { get; init; }

    /// <summary>What to say when the command succeeded but did not write one of its outputs.</summary>
    public IReadOnlyDictionary<string, string> MissingOutputHints { get; init; } = new Dictionary<string, string>();

    /// <summary>Work done in Tazel after the command succeeded, such as the links of a shared library.</summary>
    public Action? After { get; init; }

    /// <summary>Work that runs in Tazel instead of <see cref="Command"/>, such as a step of a plugin.</summary>
    public Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<ProcessResult>>? Work { get; init; }

    /// <summary>The actions whose outputs this one reads.</summary>
    public List<BuildAction> Prerequisites { get; } = [];

    /// <summary>The digest of what the action is apart from its files: command, directory and environment.</summary>
    public string Key
    {
        get
        {
            var text = new StringBuilder();
            foreach (var part in Command)
            {
                text.Append(part).Append('\0');
            }

            text.Append('\u0001').Append(Directory).Append('\u0001');
            foreach (var (name, value) in Environment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(name).Append('=').Append(value).Append('\0');
            }

            return Digests.Sha256(Encoding.UTF8.GetBytes(text.ToString()));
        }
    }

    public string Full(string path) => Path.GetFullPath(Path.Combine(Directory, path));
}

/// <summary>
/// The SHA-256 of files, kept while a file keeps its size and modification time, so a build reads a file once and a
/// file that is touched but not changed costs one more read.
/// </summary>
internal sealed class Digests
{
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, string Digest)> _known = new(StringComparer.Ordinal);

    /// <summary>The SHA-256 of <paramref name="bytes"/> as lower-case hexadecimal.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The digest of <paramref name="file"/>, or null when it is not a readable regular file.</summary>
    public string? Of(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists)
        {
            return null;
        }

        var (size, modified) = (info.Length, info.LastWriteTimeUtc);
        if (_known.TryGetValue(file, out var seen) && seen.Size == size && seen.Modified == modified)
        {
            return seen.Digest;
        }

        string digest;
        try
        {
            using var stream = File.OpenRead(file);
            digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        _known[file] = (size, modified, digest);
        return digest;
    }

    /// <summary>Forgets <paramref name="file"/>, after an action wrote it.</summary>
    public void Forget(string file) => _known.TryRemove(file, out _);
}

/// <summary>
/// Action-level incremental builds. Each action keeps a record beside its first output: its key, and the digest of
/// every input it read and every output it wrote. An action is current while its key is the same, every input has the
/// digest it had, and every output is still what it wrote. Inputs are compared by content, so touching a file reruns
/// nothing, and an output rebuilt to the same bytes reruns none of the actions that read it.
/// </summary>
internal sealed class ActionCache(Digests digests, bool enabled = true)
{
    /// <summary>Whether <paramref name="action"/> would write what it wrote last time; never, when the cache is off.</summary>
    public bool IsCurrent(BuildAction action)
    {
        if (!enabled)
        {
            return false;
        }

        var record = Read(RecordOf(action));
        if (record is null || record.Value.Key != action.Key)
        {
            return false;
        }

        var files = record.Value.Files;
        return files.All(entry => digests.Of(entry.Key) == entry.Value) &&
            action.Outputs.All(output => files.ContainsKey(action.Full(output))) &&
            action.Inputs.All(input => files.ContainsKey(action.Full(input)));
    }

    /// <summary>Records <paramref name="action"/> after it succeeded.</summary>
    public void Record(BuildAction action)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var output in action.Outputs)
        {
            digests.Forget(action.Full(output));
        }

        var discovered = action.Depfile is { } depfile && File.Exists(action.Full(depfile))
            ? Depfiles.Inputs(File.ReadAllText(action.Full(depfile)))
            : [];
        foreach (var file in action.Inputs.Concat(discovered).Concat(action.Outputs).Select(action.Full))
        {
            files[file] = digests.Of(file) ?? throw new IOException($"{file} was not written or cannot be read");
        }

        var text = new StringBuilder().Append("key ").Append(action.Key).Append('\n');
        foreach (var (file, digest) in files)
        {
            text.Append("file ").Append(digest).Append(' ').Append(file).Append('\n');
        }

        File.WriteAllText(RecordOf(action), text.ToString());
    }

    /// <summary>Forgets <paramref name="action"/>, so that a failed or stopped run is redone.</summary>
    public void Forget(BuildAction action) => File.Delete(RecordOf(action));

    private static string RecordOf(BuildAction action) => action.Full(action.Outputs[0]) + ".action";

    private static (string Key, Dictionary<string, string> Files)? Read(string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        if (lines.Length == 0 || !lines[0].StartsWith("key ", StringComparison.Ordinal))
        {
            return null;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("file ", StringComparison.Ordinal) || line.Length < 5 + 64 + 2)
            {
                return null;
            }

            files[line[(5 + 64 + 1)..]] = line[5..(5 + 64)];
        }

        return (lines[0][4..], files);
    }
}

/// <summary>Make-style dependency files, as compilers write them with <c>-MD -MF</c>.</summary>
internal static class Depfiles
{
    /// <summary>
    /// The inputs a depfile names: the target, a colon, then the inputs, with <c>\ </c> for a space and a backslash at
    /// the end of a line to go on.
    /// </summary>
    public static IReadOnlyList<string> Inputs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var colon = text.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0)
        {
            colon = text.IndexOf(":\n", StringComparison.Ordinal);
        }

        if (colon < 0)
        {
            return [];
        }

        var body = text[(colon + 1)..];
        var inputs = new List<string>();
        var current = new StringBuilder();
        var index = 0;
        while (index < body.Length)
        {
            var rest = body.AsSpan(index);
            var step = 1;
            if (rest.StartsWith("\\ "))
            {
                current.Append(' ');
                step = 2;
            }
            else if (rest.StartsWith("\\\n"))
            {
                Flush();
                step = 2;
            }
            else if (rest.StartsWith("\\\r\n"))
            {
                Flush();
                step = 3;
            }
            else if (rest.StartsWith("$$"))
            {
                current.Append('$');
                step = 2;
            }
            else if (char.IsWhiteSpace(rest[0]))
            {
                Flush();
            }
            else
            {
                current.Append(rest[0]);
            }

            index += step;
        }

        Flush();
        return inputs;

        void Flush()
        {
            if (current.Length > 0)
            {
                inputs.Add(current.ToString());
            }

            current.Clear();
        }
    }
}
