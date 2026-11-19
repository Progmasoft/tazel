// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Dsl.Module.Prelude;

namespace Progmasoft.Tazel.Project;

/// <summary>The archives a module names with <c>LocalModule.From</c>, checked and unpacked.</summary>
internal static class LocalModules
{
    /// <summary>
    /// Checks the archive of <paramref name="local"/> against its SHA-256 and unpacks it once below the state directory of
    /// <paramref name="mainRoot"/>; returns the root of the module in it.
    /// </summary>
    public static string Unpack(string mainRoot, string moduleRoot, LocalModuleSettings local, string place)
    {
        if (local.Include.Length == 0 || Path.IsPathRooted(local.Include) || local.Include.Contains('*', StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: Include names one archive by its path in the module, without a glob");
        }

        if (!local.Include.EndsWith(ModuleArchive.Extension, StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: '{local.Include}' is not a {ModuleArchive.Extension} archive");
        }

        var archive = Path.Combine(moduleRoot, local.Include);
        if (!File.Exists(archive))
        {
            throw new ProjectException($"{place}: '{local.Include}' does not exist");
        }

        var expected = Expected(moduleRoot, local, place);
        var actual = Sha256Sums.Sha256(archive);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: '{local.Include}' has the SHA-256 {actual}, not {expected}");
        }

        var destination = Path.Combine(mainRoot, ModuleFiles.StateDirectory, "external", $"{local.Name}-{actual[..16]}");
        var marker = Path.Combine(destination, ".unpacked");
        if (File.Exists(marker))
        {
            return File.ReadAllText(marker).Trim() is { Length: > 0 } top
                ? Path.Combine(destination, top)
                : throw new ProjectException($"{destination} is damaged; remove it");
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        var root = ModuleArchive.Unpack(archive, destination);
        File.WriteAllText(marker, Path.GetFileName(root));
        return root;
    }

    private static string Expected(string moduleRoot, LocalModuleSettings local, string place)
    {
        if ((local.Sha256 is null) == (local.Sha256Sums is null))
        {
            throw new ProjectException($"{place}: '{local.Name}' needs Sha256 or Sha256Sums, one of them");
        }

        if (local.Sha256 is { } sha)
        {
            if (sha.Length != 64 || !Uri.IsHexDigit(sha[0]) || !IsHex(sha))
            {
                throw new ProjectException($"{place}: '{sha}' is not a SHA-256 of 64 hexadecimal digits");
            }

            return sha.ToLowerInvariant();
        }

        var sums = Path.Combine(moduleRoot, local.Sha256Sums!);
        if (!File.Exists(sums))
        {
            throw new ProjectException($"{place}: '{local.Sha256Sums}' does not exist");
        }

        return Sha256Sums.SumFor(File.ReadAllText(sums), Path.GetFileName(local.Include), local.Sha256Sums!)
            ?? throw new ProjectException($"{place}: '{local.Sha256Sums}' does not name {Path.GetFileName(local.Include)}");
    }

    private static bool IsHex(string text)
    {
        foreach (var letter in text)
        {
            if (!Uri.IsHexDigit(letter))
            {
                return false;
            }
        }

        return true;
    }
}
