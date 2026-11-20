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
    public static void Write(string root, string baseName, string archive, IReadOnlyList<string> files) =>
        ReproducibleArchive.Write(root, baseName, archive, files);

    /// <summary>An unpacked archive has one top directory, and the file it must hold in it.</summary>
    private static void CheckUnpacked(string work, [System.Diagnostics.CodeAnalysis.NotNull] string? top, string requiredFile, string shown)
    {
        if (top is null)
        {
            throw new ProjectException($"{shown} is empty");
        }

        if (!System.IO.File.Exists(Path.Combine(work, top, requiredFile)))
        {
            throw new ProjectException($"{shown} has no {top}/{requiredFile}");
        }
    }

    /// <summary>The parts of the path of an entry, which must stay inside the archive.</summary>
    private static string[] PartsOf(TarEntry entry, string shown)
    {
        var name = entry.Name.TrimEnd('/');
        var parts = name.Split('/');
        return name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) ||
            parts.Any(part => part is "" or "." or "..")
            ? throw new ProjectException($"{shown}: '{entry.Name}' is not a path inside the archive")
            : parts;
    }

    /// <summary>Writes a directory or a file of the archive; anything else is refused. Returns the bytes written.</summary>
    private static long Extract(TarEntry entry, string target, string shown)
    {
        if (entry.EntryType == TarEntryType.Directory)
        {
            Directory.CreateDirectory(target);
            return 0;
        }

        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
        {
            throw new ProjectException($"{shown}: '{entry.Name}' is a {entry.EntryType}; an archive holds only files");
        }

        // The size is checked before the bytes are written, so a huge entry is refused unwritten.
        if (entry.Length > MaxUnpackedBytes)
        {
            throw new ProjectException($"{shown} unpacks to more than {MaxUnpackedBytes} bytes");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: false);
        return entry.Length;
    }

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="destination"/>, which must not exist, and returns the
    /// directory of the module in it. Every entry must be below one top directory; links, devices, absolute paths and
    /// <c>..</c> are refused, as are archives over <see cref="MaxUnpackedBytes"/> or <see cref="MaxEntries"/>.
    /// </summary>
    public static string Unpack(string archive, string destination) => Unpack(archive, destination, ModuleFiles.ModuleFile);

    /// <inheritdoc cref="Unpack(string, string)"/>
    public static string Unpack(string archive, string destination, string requiredFile)
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

                    var parts = PartsOf(entry, shown);
                    top ??= parts[0];
                    if (parts[0] != top)
                    {
                        throw new ProjectException($"{shown}: '{entry.Name}' is not below {top}/");
                    }

                    total += Extract(entry, Path.Combine(work, Path.Combine(parts)), shown);
                    if (total > MaxUnpackedBytes)
                    {
                        throw new ProjectException($"{shown} unpacks to more than {MaxUnpackedBytes} bytes");
                    }
                }
            }

            CheckUnpacked(work, top, requiredFile, shown);
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

}

/// <summary>The SHA-256 of an archive and the <c>.sha256sums</c> file beside it, as <c>sha256sum</c> writes it.</summary>
internal static class Sha256Sums
{
    /// <summary>The SHA-256 of <paramref name="file"/> in lower-case hexadecimal.</summary>
    public static string Sha256(string file) => ReproducibleArchive.Sha256(file);

    /// <summary>The line of a <c>.sha256sums</c> file for <paramref name="file"/>.</summary>
    public static string SumsLine(string file) => ReproducibleArchive.SumsLine(file);

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
