// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;
using Progmasoft.Tazel.Project.Ignore;

namespace Progmasoft.Tazel.Cli;

/// <summary><c>tazel ignore create-from|validate|add|delete|del</c>.</summary>
internal sealed record IgnoreCommand(OutputOptions Output, string Action, string? Argument) : Command(Output);

/// <summary>
/// The commands of <c>.tazelignore</c>. <c>validate</c> reads; <c>create-from</c> makes the file of the current
/// directory, never over one; <c>add</c> and <c>delete</c> edit only that file, which must exist, keeping its other
/// lines, under a lock, writing beside it and moving the new text into place.
/// </summary>
internal sealed class IgnoreCommands(Reporter reporter, string workingDirectory)
{
    private string Here => Path.Combine(workingDirectory, IgnoreFile.FileName);

    public int Run(IgnoreCommand command) => command.Action switch
    {
        "create-from" => CreateFrom(command.Argument!),
        "validate" => Validate(),
        "add" => Add(command.Argument!),
        _ => Delete(command.Argument!),
    };

    /// <summary>
    /// A <c>.tazelignore</c> here with the rules of a gitignore file, which is read and left as it is. A line that
    /// starts with a word Tazel reads as a directive takes a backslash, so it means for Tazel what it meant for git.
    /// </summary>
    private int CreateFrom(string source)
    {
        var from = Path.GetFullPath(source, workingDirectory);
        string text;
        try
        {
            text = File.ReadAllText(from, new UTF8Encoding(false, throwOnInvalidBytes: true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new IgnoreException($"{source} cannot be read as a gitignore file: {error.Message}", error);
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new IgnoreException($"{source} holds a NUL character; a gitignore file is text");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => IgnoreFile.NeedsEscape(line) ? (line.StartsWith('!') ? "!\\" + line[1..] : "\\" + line) : line);
        var converted = string.Join(newline, lines);
        IgnoreFile.Read(Here, converted);
        try
        {
            using var stream = new FileStream(Here, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(new UTF8Encoding(false).GetBytes(converted));
        }
        catch (IOException error) when (File.Exists(Here))
        {
            throw new IgnoreException($"{IgnoreFile.FileName} is already here; Tazel does not overwrite it", error);
        }

        reporter.Success($"wrote {IgnoreFile.FileName} from {source}");
        return 0;
    }

    /// <summary>
    /// Reads every <c>.tazelignore</c> of the module, those of ignored directories too, with the files they include,
    /// and compiles every rule; nothing is matched or run.
    /// </summary>
    private int Validate()
    {
        var root = ModuleFiles.RootOf(workingDirectory) ?? workingDirectory;
        var files = new List<string>();
        Collect(new DirectoryInfo(root), files, top: true);
        if (files.Count == 0)
        {
            reporter.Plain($"no {IgnoreFile.FileName} in this module; there is nothing to check");
            return 0;
        }

        foreach (var file in files)
        {
            var rules = IgnoreFile.Read(file, File.ReadAllText(file));
            reporter.Plain($"valid: {Path.GetRelativePath(workingDirectory, file).Replace('\\', '/')} ({rules.Count} rule{(rules.Count == 1 ? "" : "s")})");
        }

        return 0;
    }

    // Every ignore file of the module below directory: hidden directories, links and other modules are not entered.
    private static void Collect(DirectoryInfo directory, List<string> files, bool top)
    {
        if (!top && File.Exists(Path.Combine(directory.FullName, ModuleFiles.ModuleFile)))
        {
            return;
        }

        var file = Path.Combine(directory.FullName, IgnoreFile.FileName);
        if (File.Exists(file))
        {
            files.Add(file);
        }

        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (!child.Name.StartsWith('.') && child.LinkTarget is null)
            {
                Collect(child, files, top: false);
            }
        }
    }

    /// <summary>Adds a rule at the end of the file here; a rule already there as written is left, and said so.</summary>
    private int Add(string pattern)
    {
        CheckRule(pattern);
        return Edit(lines =>
        {
            if (lines.Contains(pattern, StringComparer.Ordinal))
            {
                reporter.Plain($"'{pattern}' is already in {IgnoreFile.FileName}; nothing changed");
                return null;
            }

            lines.Add(pattern);
            reporter.Success($"added '{pattern}' to {IgnoreFile.FileName}");
            return lines;
        });
    }

    /// <summary>Deletes every line that is <paramref name="pattern"/> as written; no pattern is matched or run.</summary>
    private int Delete(string pattern) => Edit(lines =>
    {
        var removed = lines.RemoveAll(line => line == pattern);
        if (removed == 0)
        {
            throw new IgnoreException($"'{pattern}' is not a line of {IgnoreFile.FileName}; delete takes a line exactly as written");
        }

        reporter.Success($"deleted '{pattern}' from {IgnoreFile.FileName}{(removed > 1 ? $" ({removed} lines)" : "")}");
        return lines;
    });

    // A line add takes: one line, a rule and not a directive of includes, valid as the file would read it.
    private void CheckRule(string pattern)
    {
        if (pattern.Length == 0 || pattern.Contains('\n', StringComparison.Ordinal) || pattern.Contains('\r', StringComparison.Ordinal))
        {
            throw new IgnoreException("tazel ignore add takes one rule on one line");
        }

        if (pattern.StartsWith('#') || pattern.StartsWith("pragma:", StringComparison.Ordinal) ||
            pattern.StartsWith("import:", StringComparison.Ordinal) || pattern.StartsWith("include:", StringComparison.Ordinal))
        {
            throw new IgnoreException($"'{pattern}' is not a rule; tazel ignore add adds patterns and regex: rules, and edits no directive");
        }

        IgnoreFile.Read(Here, pattern);
    }

    // The file here, edited by change under a lock; change returns null for no change.
    private int Edit(Func<List<string>, List<string>?> change)
    {
        if (!File.Exists(Here))
        {
            throw new IgnoreException($"there is no {IgnoreFile.FileName} here; make one, or tazel ignore create-from a gitignore file");
        }

        using var held = Lock(Here);
        var text = File.ReadAllText(Here);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var endsWithNewline = lines.Count > 1 && lines[^1].Length == 0;
        if (endsWithNewline)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var changed = change(lines);
        if (changed is null)
        {
            return 0;
        }

        var updated = string.Join(newline, changed) + (endsWithNewline || text.Length == 0 ? newline : "");
        IgnoreFile.Read(Here, updated);
        var partial = $"{Here}.{Guid.NewGuid():N}.partial";
        File.WriteAllText(partial, updated, new UTF8Encoding(false));
        File.Move(partial, Here, overwrite: true);
        return 0;
    }

    private static FileStream Lock(string file)
    {
        var locks = Path.Combine(TazelHome.Directory(), "Locks");
        Directory.CreateDirectory(locks);
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file))))[..16];
        var path = Path.Combine(locks, $"ignore-{name}.lock");
        const int Attempts = 100;
        for (var attempt = 1; attempt < Attempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }

        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
}
