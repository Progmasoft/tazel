// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Packaging;
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
    public const string Extension = TazelPackageArchive.Extension;

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
        TazelPackageArchive.Write(root, baseName, archive, files);

    /// <summary>
    /// The directory a <c>.unpacked</c> marker below <paramref name="destination"/> names, or null when there is no
    /// marker. The marker holds one directory name that Unpack made; anything else, such as a path that leads out of the
    /// destination, means the state was altered, and the unpacked files are not trusted.
    /// </summary>
    public static string? FromMarker(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var marker = Path.Combine(destination, ".unpacked");
        if (!System.IO.File.Exists(marker))
        {
            return null;
        }

        var name = System.IO.File.ReadAllText(marker).Trim();
        var directory = Path.Combine(destination, Path.GetFileName(name));
        return name.Length > 0 && name == Path.GetFileName(name) && name is not ("." or "..") && Directory.Exists(directory)
            ? directory
            : throw new ProjectException($"{destination} is damaged; remove it and run the command again");
    }

    /// <summary>Writes the marker that names the unpacked directory of <paramref name="destination"/>.</summary>
    public static void WriteMarker(string destination, string directory) =>
        System.IO.File.WriteAllText(Path.Combine(destination, ".unpacked"), Path.GetFileName(directory));

    /// <summary>
    /// Unpacks <paramref name="archive"/> into <paramref name="destination"/>, which must not exist, and returns the
    /// directory of the module in it. Every entry must be below one top directory; links, devices, absolute paths and
    /// <c>..</c> are refused, as are archives over <see cref="TazelPackageArchive.MaxUnpackedBytes"/> or <see cref="TazelPackageArchive.MaxEntries"/>.
    /// </summary>
    public static string Unpack(string archive, string destination) => Unpack(archive, destination, ModuleFiles.ModuleFile);

    /// <inheritdoc cref="Unpack(string, string)"/>
    public static string Unpack(string archive, string destination, string requiredFile)
    {
        try
        {
            return TazelPackageArchive.Unpack(archive, destination, requiredFile);
        }
        catch (TazelPackageException error)
        {
            throw new ProjectException(error.Message, error);
        }
    }
}

/// <summary>The SHA-256 of an archive and the <c>.sha256sums</c> file beside it, as <c>sha256sum</c> writes it.</summary>
internal static class Sha256Sums
{
    /// <summary>The SHA-256 of <paramref name="file"/> in lower-case hexadecimal.</summary>
    public static string Sha256(string file) => Packaging.Sha256Sums.Sha256(file);

    /// <summary>The line of a <c>.sha256sums</c> file for <paramref name="file"/>.</summary>
    public static string SumsLine(string file) => Packaging.Sha256Sums.Line(file);

    /// <summary>
    /// The SHA-256 that the <c>.sha256sums</c> text <paramref name="sums"/> gives for <paramref name="name"/>. Lines are
    /// <c>&lt;sha256&gt;  &lt;name&gt;</c> or <c>&lt;sha256&gt; *&lt;name&gt;</c>; blank lines are skipped and any other
    /// line is an error.
    /// </summary>
    public static string? SumFor(string sums, string name, string shown)
    {
        try
        {
            return Packaging.Sha256Sums.SumFor(sums, name, shown);
        }
        catch (TazelPackageException error)
        {
            throw new ProjectException(error.Message, error);
        }
    }
}
