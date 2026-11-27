// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Cli;

/// <summary><c>tazel cache validate [--remote]</c> and <c>tazel cache repair [--remote]</c>.</summary>
internal sealed record CacheCheckCommand(OutputOptions Output, bool Repair, bool Remote) : Command(Output);

/// <summary>What the local caches hold, by kind.</summary>
internal sealed record CacheCensus(int Valid, int Stale, IReadOnlyList<(string File, string Reason)> Broken, IReadOnlyList<string> Leftovers);

/// <summary>
/// Checks the caches of a module. An action record is valid when every file it names has the digest it recorded;
/// stale when an input changed, which only means the next build runs it; broken when it cannot be read, or the
/// output it is named after is missing or changed while everything else is as recorded. A test result is broken
/// without its log or with a key that is not a digest. A compiled project file is broken when its metadata does not
/// read; an empty one is the image of a file of usings only. Files a write left half done are leftovers.
/// </summary>
internal sealed class CacheChecks(string root)
{
    private readonly string _state = Path.Combine(root, ModuleFiles.StateDirectory);

    /// <summary>The action records below <c>.tazel</c>.</summary>
    public CacheCensus Actions()
    {
        var (valid, stale) = (0, 0);
        var broken = new List<(string, string)>();
        foreach (var record in Files("*.action"))
        {
            switch (Classify(record, out var reason))
            {
                case RecordState.Valid:
                    valid++;
                    break;
                case RecordState.Stale:
                    stale++;
                    break;
                default:
                    broken.Add((record, reason!));
                    break;
            }
        }

        return new CacheCensus(valid, stale, broken, Files("*.partial").ToList());
    }

    /// <summary>The recorded passes of tests.</summary>
    public CacheCensus Tests()
    {
        var logs = Path.Combine(_state, "testlogs");
        var passed = Path.Combine(logs, ".passed");
        if (!Directory.Exists(passed))
        {
            return new CacheCensus(0, 0, [], []);
        }

        // A pass is kept under the log directory's path with / as _ (a test of the root package with a _ before it);
        // the log must be there.
        var logged = Directory.EnumerateFiles(logs, "test.log", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(logs, Path.GetDirectoryName(file)!).Replace('\\', '/'))
            .SelectMany(directory => new[] { directory.Replace('/', '_'), "_" + directory.Replace('/', '_') })
            .ToHashSet(StringComparer.Ordinal);
        var (valid, broken) = (0, new List<(string, string)>());
        foreach (var file in Directory.EnumerateFiles(passed).Order(StringComparer.Ordinal))
        {
            var key = File.ReadAllText(file);
            if (!IsDigest(key))
            {
                broken.Add((file, "its key is not a SHA-256 digest"));
            }
            else if (!logged.Contains(Path.GetFileName(file)))
            {
                broken.Add((file, "the log of the run it records is not there"));
            }
            else
            {
                valid++;
            }
        }

        return new CacheCensus(valid, 0, broken, []);
    }

    /// <summary>The compiled project files in <c>.tazel/scripts</c>.</summary>
    public CacheCensus Scripts()
    {
        var directory = Path.Combine(_state, ScriptCache.Directory);
        if (!Directory.Exists(directory))
        {
            return new CacheCensus(0, 0, [], []);
        }

        var (valid, broken) = (0, new List<(string, string)>());
        foreach (var file in Directory.EnumerateFiles(directory, "*.dll").Order(StringComparer.Ordinal))
        {
            if (!IsDigest(Path.GetFileNameWithoutExtension(file)))
            {
                broken.Add((file, "its name is not the SHA-256 digest of a project file"));
            }
            else if (!ReadsAsAssembly(file))
            {
                broken.Add((file, "it is not an assembly whose metadata reads"));
            }
            else
            {
                valid++;
            }
        }

        return new CacheCensus(valid, 0, broken, Directory.EnumerateFiles(directory, "*.partial").ToList());
    }

    private IEnumerable<string> Files(string pattern) => Directory.Exists(_state)
        ? Directory.EnumerateFiles(_state, pattern, SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}{ScriptCache.Directory}{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
        : [];

    private enum RecordState
    {
        Valid,
        Stale,
        Broken,
    }

    private static RecordState Classify(string record, out string? reason)
    {
        reason = null;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(record);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reason = $"it cannot be read: {error.Message}";
            return RecordState.Broken;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (lines.Length == 0 || !lines[0].StartsWith("key ", StringComparison.Ordinal) || !IsDigest(lines[0][4..]))
        {
            reason = "it does not start with the key of its action";
            return RecordState.Broken;
        }

        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length != 3 || parts[0] != "file" || !IsDigest(parts[1]) || parts[2].Length == 0)
            {
                reason = $"'{line}' is not a line of a record";
                return RecordState.Broken;
            }

            files[parts[2]] = parts[1];
        }

        var output = record[..^".action".Length];
        if (!files.TryGetValue(output, out var recorded))
        {
            reason = "it does not name the output it is kept beside";
            return RecordState.Broken;
        }

        var changed = files.Where(entry => DigestOf(entry.Key) != entry.Value).Select(entry => entry.Key).ToList();
        if (changed.Count == 0)
        {
            return RecordState.Valid;
        }

        if (changed is [var only] && only == output)
        {
            reason = DigestOf(output) is null
                ? $"{output} is gone while its inputs are as recorded"
                : $"{output} changed while its inputs are as recorded (it was {recorded[..12]}…)";
            return RecordState.Broken;
        }

        return RecordState.Stale;
    }

    private static string? DigestOf(string file)
    {
        try
        {
            return File.Exists(file) ? Digests.Sha256(File.ReadAllBytes(file)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDigest(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigitLower);

    // Read, never loaded: the metadata of an assembly, or nothing at all for a file of usings only.
    private static bool ReadsAsAssembly(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.Length == 0)
            {
                return true;
            }

            using var reader = new PEReader(stream);
            return reader.HasMetadata && System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader).IsAssembly;
        }
        catch (Exception error) when (error is BadImageFormatException or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>The local part of <c>tazel cache validate</c> and <c>tazel cache repair</c>.</summary>
internal sealed class CacheCommands(Reporter reporter, string workingDirectory)
{
    /// <summary>Says what the local caches hold; with <paramref name="repair"/>, removes what is broken. The broken left.</summary>
    public int RunLocal(bool repair)
    {
        var root = ModuleFiles.FindRoot(workingDirectory);
        var checks = new CacheChecks(root);
        var broken = 0;
        foreach (var (name, census) in new[] { ("action records", checks.Actions()), ("test results", checks.Tests()), ("compiled project files", checks.Scripts()) })
        {
            reporter.Plain($"{name}: {census.Valid} valid, {census.Stale} stale, {census.Broken.Count} broken" +
                (census.Leftovers.Count > 0 ? $", {census.Leftovers.Count} left half written" : ""));
            foreach (var (file, reason) in census.Broken)
            {
                if (repair)
                {
                    File.Delete(file);
                    reporter.Plain($"removed {Shown(file)}: {reason}");
                }
                else
                {
                    reporter.Error(ErrorCodes.CacheBroken, $"{Shown(file)}: {reason}", "tazel cache repair removes it, and the next build redoes it");
                    broken++;
                }
            }

            if (repair)
            {
                foreach (var leftover in census.Leftovers)
                {
                    File.Delete(leftover);
                }
            }
        }

        return broken;
    }

    private string Shown(string file)
    {
        var relative = Path.GetRelativePath(workingDirectory, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative.Replace('\\', '/');
    }
}
