// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using LzmaNet;

namespace Progmasoft.Tazel.Packaging;

/// <summary>
/// The archive of a Tazel package, <c>$group.$name-$version.tazel.tar.xz</c>: the files of the package below one
/// directory of that name less the extension. Modules and plugins are both written this way.
/// </summary>
/// <remarks>
/// An archive is the same bytes for the same files: entries are sorted, owned by no one, dated at the epoch, and either
/// <c>0644</c> or, for an executable file, <c>0755</c>; xz compresses with one thread. Unpacking refuses links, devices,
/// absolute paths, <c>..</c>, entries outside the one top directory, and archives over <see cref="MaxUnpackedBytes"/> or
/// <see cref="MaxEntries"/>.
/// </remarks>
public static class TazelPackageArchive
{
    /// <summary>The extension of an archive.</summary>
    public const string Extension = ".tazel.tar.xz";

    /// <summary>The most an archive may unpack to; a larger one is refused before it fills the disk.</summary>
    public const long MaxUnpackedBytes = 1L << 30;

    /// <summary>The most entries an archive may hold.</summary>
    public const int MaxEntries = 100_000;

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
        ArgumentNullException.ThrowIfNull(archive);
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

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="destination"/>, which must not exist, and returns the top
    /// directory in it, which must hold <paramref name="requiredFile"/>.
    /// </summary>
    /// <exception cref="TazelPackageException">The archive cannot be unpacked or is not a package; the message says why.</exception>
    public static string Unpack(string archive, string destination, string requiredFile)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(requiredFile);
        var shown = Path.GetFileName(archive);
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, $".unpack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            string? top = null;
            long total = 0;
            var entries = 0;
            using (var input = File.OpenRead(archive))
            using (var xz = new XzDecompressStream(input, new XzDecompressOptions(), leaveOpen: false))
            using (var tar = new TarReader(xz))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    if (++entries > MaxEntries)
                    {
                        throw new TazelPackageException($"{shown} holds more than {MaxEntries} entries");
                    }

                    var parts = PartsOf(entry, shown);
                    top ??= parts[0];
                    if (parts[0] != top)
                    {
                        throw new TazelPackageException($"{shown}: '{entry.Name}' is not below {top}/");
                    }

                    total += Extract(entry, Path.Combine(work, Path.Combine(parts)), shown);
                    if (total > MaxUnpackedBytes)
                    {
                        throw new TazelPackageException($"{shown} unpacks to more than {MaxUnpackedBytes} bytes");
                    }
                }
            }

            CheckUnpacked(work, top, requiredFile, shown);
            Directory.Move(work, destination);
            return Path.Combine(destination, top);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            throw new TazelPackageException($"{shown} could not be unpacked: {error.Message}", error);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static void CheckUnpacked(string work, [NotNull] string? top, string requiredFile, string shown)
    {
        if (top is null)
        {
            throw new TazelPackageException($"{shown} is empty");
        }

        if (!File.Exists(Path.Combine(work, top, requiredFile)))
        {
            throw new TazelPackageException($"{shown} has no {top}/{requiredFile}");
        }
    }

    private static string[] PartsOf(TarEntry entry, string shown)
    {
        var name = entry.Name.TrimEnd('/');
        var parts = name.Split('/');
        return name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) ||
            parts.Any(part => part is "" or "." or "..")
            ? throw new TazelPackageException($"{shown}: '{entry.Name}' is not a path inside the archive")
            : parts;
    }

    private static long Extract(TarEntry entry, string target, string shown)
    {
        if (entry.EntryType == TarEntryType.Directory)
        {
            Directory.CreateDirectory(target);
            return 0;
        }

        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw new TazelPackageException($"{shown}: '{entry.Name}' is a {entry.EntryType}; an archive holds only files");
        }

        // The size is checked before the bytes are written, so a huge entry is refused unwritten.
        if (entry.Length > MaxUnpackedBytes)
        {
            throw new TazelPackageException($"{shown} unpacks to more than {MaxUnpackedBytes} bytes");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: false);
        return entry.Length;
    }

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
