// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using LzmaNet;

namespace Progmasoft.Tazel.Archive;

/// <summary>
/// Writes a <c>.tar.xz</c> that is the same bytes for the same files: its entries are sorted, below one top directory,
/// owned by no one, dated at the epoch, and either <c>0644</c> or, for an executable file, <c>0755</c>; xz compresses
/// with one thread. The modules of Tazel and the plugins of the SDK are written by it.
/// </summary>
internal static class ReproducibleArchive
{
    private const UnixFileMode FileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode ExecutableMode = FileMode |
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private const UnixFileMode DirectoryMode = ExecutableMode | UnixFileMode.UserWrite;

    /// <summary>
    /// Writes <paramref name="files"/>, paths with <c>/</c> below <paramref name="root"/>, into <paramref name="archive"/>
    /// below the directory <paramref name="baseName"/>. The archive appears whole or not at all.
    /// </summary>
    public static void Write(string root, string baseName, string archive, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var directory = Path.GetDirectoryName(Path.GetFullPath(archive))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(archive)}.{Guid.NewGuid():N}.partial");
        try
        {
            using (var output = File.Create(temporary))
            using (var xz = new XzCompressStream(output, new XzCompressOptions { Threads = 1 }, leaveOpen: false))
            using (var tar = new TarWriter(xz, TarEntryFormat.Ustar, leaveOpen: false))
            {
                foreach (var (path, isDirectory) in PathsAndDirectories(files))
                {
                    var name = path.Length == 0 ? $"{baseName}/" : isDirectory ? $"{baseName}/{path}/" : $"{baseName}/{path}";
                    var entry = new UstarTarEntry(isDirectory ? TarEntryType.Directory : TarEntryType.RegularFile, name)
                    {
                        ModificationTime = DateTimeOffset.UnixEpoch,
                        Uid = 0,
                        Gid = 0,
                        UserName = "",
                        GroupName = "",
                    };
                    var source = Path.Combine(root, path);
                    entry.Mode = isDirectory ? DirectoryMode : IsExecutable(source) ? ExecutableMode : FileMode;
                    if (isDirectory)
                    {
                        tar.WriteEntry(entry);
                    }
                    else
                    {
                        using var data = File.OpenRead(source);
                        entry.DataStream = data;
                        tar.WriteEntry(entry);
                    }
                }
            }

            File.Move(temporary, archive, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    /// <summary>The SHA-256 of <paramref name="file"/> in lower-case hexadecimal.</summary>
    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>The line of a <c>.sha256sums</c> file for <paramref name="file"/>, as <c>sha256sum</c> writes it.</summary>
    public static string SumsLine(string file) => $"{Sha256(file)}  {Path.GetFileName(file)}\n";

    private static bool IsExecutable(string file) =>
        !OperatingSystem.IsWindows() && (File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;

    private static IEnumerable<(string Path, bool IsDirectory)> PathsAndDirectories(IReadOnlyList<string> files)
    {
        var directories = new SortedSet<string>(StringComparer.Ordinal) { "" };
        foreach (var file in files)
        {
            var parts = file.Split('/');
            for (var depth = 1; depth < parts.Length; depth++)
            {
                directories.Add(string.Join('/', parts.Take(depth)));
            }
        }

        return directories.Select(d => (d, true)).Concat(files.Select(f => (f, false)))
            .OrderBy(entry => entry.Item1, StringComparer.Ordinal);
    }
}
