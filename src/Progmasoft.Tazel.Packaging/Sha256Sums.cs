// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Progmasoft.Tazel.Packaging;

/// <summary>The SHA-256 of a file and the <c>.sha256sums</c> file beside it, as <c>sha256sum</c> writes it.</summary>
public static class Sha256Sums
{
    /// <summary>The SHA-256 of <paramref name="file"/> in lower-case hexadecimal.</summary>
    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The line of a <c>.sha256sums</c> file for <paramref name="file"/>: the SHA-256, two spaces and the name.</summary>
    public static string Line(string file) => $"{Sha256(file)}  {Path.GetFileName(file)}\n";

    /// <summary>
    /// The SHA-256 that the <c>.sha256sums</c> text <paramref name="sums"/> gives for <paramref name="name"/>, or null.
    /// Lines are <c>&lt;sha256&gt;  &lt;name&gt;</c> or <c>&lt;sha256&gt; *&lt;name&gt;</c>; blank lines are skipped.
    /// </summary>
    /// <param name="sums">The text of the file.</param>
    /// <param name="name">The name to look up.</param>
    /// <param name="shown">How the file is named in a message.</param>
    /// <exception cref="TazelPackageException">A line is not a line of sha256sum, or the name is listed twice.</exception>
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
                throw new TazelPackageException($"{shown}:{number}: not a line of sha256sum: '{line}'");
            }

            if (line[66..] == name)
            {
                if (found is not null)
                {
                    throw new TazelPackageException($"{shown} lists {name} twice");
                }

                found = line[..64].ToLowerInvariant();
            }
        }

        return found;
    }
}
