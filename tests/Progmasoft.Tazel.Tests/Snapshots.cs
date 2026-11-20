// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Snapshooter;
using Snapshooter.Xunit3;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>
/// Snapshots with Snapshooter, in <c>__snapshots__/</c> beside the test. A difference fails with the lines that differ
/// and where the new snapshot is; a missing snapshot fails under <c>SNAPSHOOTER_STRICT_MODE=true</c>, as in CI, and
/// is written otherwise, for a review before it is committed.
/// </summary>
internal static class Snapshots
{
    private const int ContextLines = 2;
    private const int MaxShownDifferences = 40;

    /// <summary>Compares <paramref name="value"/> with the snapshot of the calling test.</summary>
    public static void Match(object value, [CallerFilePath] string source = "", [CallerMemberName] string test = "")
    {
        var folder = Path.Combine(Path.GetDirectoryName(source)!, "__snapshots__");
        var file = $"{Path.GetFileNameWithoutExtension(source)}.{test}.snap";
        var stored = Path.Combine(folder, file);
        var mismatch = Path.Combine(folder, "__mismatch__", file);
        var strict = string.Equals(Environment.GetEnvironmentVariable("SNAPSHOOTER_STRICT_MODE"), "true",
            StringComparison.OrdinalIgnoreCase);
        if (strict && !File.Exists(stored))
        {
            Assert.Fail(
                $"There is no snapshot {Shown(stored)}, and SNAPSHOOTER_STRICT_MODE=true does not let a test write one.\n" +
                "Run the test once without SNAPSHOOTER_STRICT_MODE, read the snapshot it writes, and commit it.");
        }

        // A mismatch of an earlier run says nothing about this one.
        if (File.Exists(mismatch))
        {
            File.Delete(mismatch);
        }
        try
        {
            // Snapshooter puts __snapshots__ below the folder it is given.
            Snapshot.Match(value, new SnapshotFullName(file, Path.GetDirectoryName(source)!));
        }
        catch (Exception) when (File.Exists(mismatch) && File.Exists(stored))
        {
            Assert.Fail(Describe(stored, mismatch));
        }
    }

    /// <summary>The lines that differ between the stored snapshot and the new one, with a few lines around each.</summary>
    internal static string Describe(string stored, string mismatch)
    {
        var expected = File.ReadAllText(stored).ReplaceLineEndings("\n").Split('\n');
        var actual = File.ReadAllText(mismatch).ReplaceLineEndings("\n").Split('\n');
        var text = new StringBuilder()
            .Append(CultureInfoInvariant($"The snapshot {Shown(stored)} differs from what the test made.\n"))
            .Append("- is the stored snapshot, + is what the test made:\n\n");
        var shown = 0;
        foreach (var (start, end) in Hunks(Diff(expected, actual)))
        {
            foreach (var (kind, line) in Diff(expected, actual).Skip(start).Take(end - start))
            {
                text.Append(kind).Append(' ').Append(line).Append('\n');
            }

            text.Append("...\n");
            if (++shown == MaxShownDifferences)
            {
                break;
            }
        }

        return text
            .Append(CultureInfoInvariant($"\nThe new snapshot is {Shown(mismatch)}. When it is right, move it over the stored one and commit it.\n"))
            .ToString();
    }

    /// <summary>A line diff by the longest common subsequence: ' ' for kept, '-' for stored only, '+' for new only.</summary>
    /// <summary>The length of the longest common run of lines from each pair of positions on.</summary>
    private static int[,] CommonLengths(string[] expected, string[] actual)
    {
        var lengths = new int[expected.Length + 1, actual.Length + 1];
        for (var i = expected.Length - 1; i >= 0; i--)
        {
            for (var j = actual.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = expected[i] == actual[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        return lengths;
    }

    private static List<(char Kind, string Line)> Diff(string[] expected, string[] actual)
    {
        var lengths = CommonLengths(expected, actual);
        var result = new List<(char, string)>();
        int a = 0, b = 0;
        while (a < expected.Length || b < actual.Length)
        {
            if (a < expected.Length && b < actual.Length && expected[a] == actual[b])
            {
                result.Add((' ', expected[a++]));
                b++;
            }
            else if (b < actual.Length && (a == expected.Length || lengths[a, b + 1] > lengths[a + 1, b]))
            {
                result.Add(('+', actual[b++]));
            }
            else
            {
                result.Add(('-', expected[a++]));
            }
        }

        return result;
    }

    /// <summary>The ranges of the diff to show: each changed line with <see cref="ContextLines"/> around it, joined.</summary>
    private static IEnumerable<(int Start, int End)> Hunks(List<(char Kind, string Line)> diff)
    {
        var changed = diff.Select((entry, index) => (entry.Kind, index)).Where(entry => entry.Kind != ' ')
            .Select(entry => entry.index).ToList();
        var i = 0;
        while (i < changed.Count)
        {
            var start = Math.Max(0, changed[i] - ContextLines);
            var end = Math.Min(diff.Count, changed[i] + ContextLines + 1);
            while (++i < changed.Count && changed[i] - ContextLines <= end)
            {
                end = Math.Min(diff.Count, changed[i] + ContextLines + 1);
            }

            yield return (start, end);
        }
    }

    // Paths from the root of the repository, the directory with Tazel.slnx, as a reader finds them.
    private static string Shown(string path)
    {
        var root = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Tazel.slnx")))
        {
            root = root.Parent;
        }

        return (root is null ? path : Path.GetRelativePath(root.FullName, path)).Replace('\\', '/');
    }

    private static string CultureInfoInvariant(FormattableString text) =>
        text.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
