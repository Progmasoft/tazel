// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>
/// The key of a project in <c>$TAZEL_HOME/Projects/</c>: the algorithm of Agenceland's <c>projectKey</c>, applied to the
/// canonical root of the module (the directory of its <c>MODULE.csx</c>), so every directory of a module shares it.
/// </summary>
public static class ProjectKey
{
    /// <summary>The most characters of the readable part; with the hash the key stays well below 255 bytes.</summary>
    public const int MaxReadableLength = 200;

    /// <summary>The name of the file that keeps the canonical root of a project's directory.</summary>
    public const string IdentityFile = "identity.txt";

    /// <summary>
    /// The key of <paramref name="canonicalPath"/>: each UTF-16 unit outside A-Z, a-z and 0-9 becomes <c>-</c>; on
    /// Unix the last part is joined to the rest by <c>--</c>; the readable part keeps at most its last 200 characters,
    /// and <c>-</c> and the first 16 hexadecimal digits of the SHA-256 of the whole path in UTF-8 follow.
    /// </summary>
    public static string Of(string canonicalPath, bool windows)
    {
        ArgumentNullException.ThrowIfNull(canonicalPath);
        var readable = Encode(canonicalPath);
        if (!windows)
        {
            var last = canonicalPath.LastIndexOf('/');
            if (last > 0)
            {
                readable = $"{Encode(canonicalPath[..last])}--{Encode(canonicalPath[(last + 1)..])}";
            }
        }

        if (readable.Length > MaxReadableLength)
        {
            readable = readable[^MaxReadableLength..];
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)))[..16];
        return $"{readable}-{hash}";
    }

    private static string Encode(string text)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (var unit in text)
        {
            encoded.Append(char.IsAsciiLetterOrDigit(unit) ? unit : '-');
        }

        return encoded.ToString();
    }

    /// <summary>
    /// <c>$TAZEL_HOME/Projects/&lt;key&gt;/</c> of the module rooted at <paramref name="moduleRoot"/>. A directory there that
    /// keeps another root in its identity file belongs to another project whose key is the same, and is refused; so is
    /// one with no identity file. With <paramref name="create"/>, a missing directory is made with its identity.
    /// </summary>
    public static string Directory(string moduleRoot, bool create = false)
    {
        var canonical = ConfigurationResolver.Canonical(moduleRoot);
        var directory = Path.Combine(TazelHome.Directory(), "Projects", Of(canonical, OperatingSystem.IsWindows()));
        var identity = Path.Combine(directory, IdentityFile);
        if (System.IO.Directory.Exists(directory))
        {
            var kept = File.Exists(identity) ? File.ReadAllText(identity).TrimEnd('\n', '\r') : null;
            if (kept != canonical)
            {
                throw new ConfigurationHomeException(kept is null
                    ? $"{directory} has no {IdentityFile}, so Tazel cannot tell whose it is; remove it or write {canonical} in it"
                    : $"{directory} belongs to {kept}, whose key is the same as {canonical}'s; move one of them");
            }
        }
        else if (create)
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(identity, canonical + "\n");
        }

        return directory;
    }
}
