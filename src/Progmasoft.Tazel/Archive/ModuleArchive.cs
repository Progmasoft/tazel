// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LzmaNet;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Archive;

/// <summary>
/// The archive of a module, <c>$group.$module-$version.tazel.tar.xz</c>: the files of the module below one directory
/// of that name less the extension. It is reproducible: entries are sorted, owned by no one, dated at the epoch, and
/// either <c>0644</c> or, for an executable file, <c>0755</c>.
/// </summary>
internal static class ModuleArchive
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

    /// <summary><c>$group.$module-$version</c>, the name of an archive without its extension.</summary>
    public static string BaseName(string group, string module, string version) => $"{group}.{module}-{version}";

    /// <summary>
    /// The files of the module at <paramref name="root"/> that go into its archive: every regular file below it, less what
    /// starts with a dot (<c>.tazel</c>, <c>.git</c>), the directories of other modules and what
    /// <paramref name="excluded"/> matches.
    /// </summary>
    public static IReadOnlyList<string> Files(string root, Func<string, bool> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        var found = new List<string>();
        Visit(new DirectoryInfo(root), "");
        found.Sort(StringComparer.Ordinal);
        return found;

        void Visit(DirectoryInfo directory, string prefix)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry.Name.StartsWith('.'))
                {
                    continue;
                }

                var relative = prefix + entry.Name;
                if (entry.LinkTarget is not null)
                {
                    throw new ProjectException($"{relative} is a symbolic link; an archive holds only files");
                }

                if (entry is DirectoryInfo child)
                {
                    if (!System.IO.File.Exists(Path.Combine(child.FullName, ModuleFiles.ModuleFile)))
                    {
                        Visit(child, relative + "/");
                    }
                }
                else if (!excluded(relative))
                {
                    found.Add(relative);
                }
            }
        }
    }

    /// <summary>Writes <paramref name="files"/> of the module at <paramref name="root"/> below <paramref name="baseName"/>.</summary>
    public static void Write(string root, string baseName, string archive, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var directory = Path.GetDirectoryName(Path.GetFullPath(archive))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(archive)}.{Guid.NewGuid():N}.partial");
        try
        {
            using (var output = System.IO.File.Create(temporary))
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
                        using var data = System.IO.File.OpenRead(source);
                        entry.DataStream = data;
                        tar.WriteEntry(entry);
                    }
                }
            }

            System.IO.File.Move(temporary, archive, overwrite: true);
        }
        finally
        {
            System.IO.File.Delete(temporary);
        }
    }

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="destination"/>, which must not exist, and returns the
    /// directory of the module in it. Every entry must be below one top directory; links, devices, absolute paths and
    /// <c>..</c> are refused, as are archives over <see cref="MaxUnpackedBytes"/> or <see cref="MaxEntries"/>.
    /// </summary>
    public static string Unpack(string archive, string destination)
    {
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
            using (var input = System.IO.File.OpenRead(archive))
            using (var xz = new XzDecompressStream(input, new XzDecompressOptions(), leaveOpen: false))
            using (var tar = new TarReader(xz))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    if (++entries > MaxEntries)
                    {
                        throw new ProjectException($"{shown} holds more than {MaxEntries} entries");
                    }

                    var name = entry.Name.TrimEnd('/');
                    var parts = name.Split('/');
                    if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) ||
                        parts.Any(part => part is "" or "." or ".."))
                    {
                        throw new ProjectException($"{shown}: '{entry.Name}' is not a path inside the archive");
                    }

                    top ??= parts[0];
                    if (parts[0] != top)
                    {
                        throw new ProjectException($"{shown}: '{entry.Name}' is not below {top}/");
                    }

                    var target = Path.Combine(work, Path.Combine(parts));
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory:
                            Directory.CreateDirectory(target);
                            break;
                        case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                            total += entry.Length;
                            if (total > MaxUnpackedBytes)
                            {
                                throw new ProjectException($"{shown} unpacks to more than {MaxUnpackedBytes} bytes");
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            entry.ExtractToFile(target, overwrite: false);
                            break;
                        default:
                            throw new ProjectException($"{shown}: '{entry.Name}' is a {entry.EntryType}; an archive holds only files");
                    }
                }
            }

            if (top is null)
            {
                throw new ProjectException($"{shown} is empty");
            }

            if (!System.IO.File.Exists(Path.Combine(work, top, ModuleFiles.ModuleFile)))
            {
                throw new ProjectException($"{shown} has no {top}/{ModuleFiles.ModuleFile}");
            }

            Directory.Move(work, destination);
            return Path.Combine(destination, top);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or FormatException)
        {
            throw new ProjectException($"{shown} could not be unpacked: {error.Message}", error);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static bool IsExecutable(string file) =>
        !OperatingSystem.IsWindows() &&
        (System.IO.File.GetUnixFileMode(file) & UnixFileMode.UserExecute) != 0;

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

/// <summary>The SHA-256 of an archive and the <c>.sha256sums</c> file beside it, as <c>sha256sum</c> writes it.</summary>
internal static class Sha256Sums
{
    /// <summary>The SHA-256 of <paramref name="file"/> in lower-case hexadecimal.</summary>
    public static string Sha256(string file)
    {
        using var stream = System.IO.File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The line of a <c>.sha256sums</c> file for <paramref name="file"/>.</summary>
    public static string SumsLine(string file) => $"{Sha256(file)}  {Path.GetFileName(file)}\n";

    /// <summary>
    /// The SHA-256 that the <c>.sha256sums</c> text <paramref name="sums"/> gives for <paramref name="name"/>. Lines are
    /// <c>&lt;sha256&gt;  &lt;name&gt;</c> or <c>&lt;sha256&gt; *&lt;name&gt;</c>; blank lines are skipped and any other
    /// line is an error.
    /// </summary>
    public static string? SumFor(string sums, string name, string shown)
    {
        ArgumentNullException.ThrowIfNull(sums);
        string? found = null;
        var number = 0;
        foreach (var raw in sums.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0)
            {
                continue;
            }

            if (line.Length < 67 || !line[..64].All(Uri.IsHexDigit) || line[64] != ' ' || line[65] is not (' ' or '*'))
            {
                throw new ProjectException($"{shown}:{number}: not a line of sha256sum: '{line}'");
            }

            if (line[66..] == name)
            {
                if (found is not null)
                {
                    throw new ProjectException($"{shown} lists {name} twice");
                }

                found = line[..64].ToLowerInvariant();
            }
        }

        return found;
    }
}
