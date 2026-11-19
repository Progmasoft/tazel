// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Progmasoft.Tazel.Cli;

/// <summary>"Did you mean" hints for what was misspelt.</summary>
internal static class Suggestions
{
    /// <summary>The edits between two words: insertions, deletions, substitutions and swaps of neighbours.</summary>
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>The nearest of <paramref name="known"/> to <paramref name="written"/>, when it is near enough.</summary>
    public static string? Nearest(string written, IEnumerable<string> known)
    {
        var best = known.Select(k => (Word: k, Distance: Distance(written, k)))
            .OrderBy(entry => entry.Distance).ThenBy(entry => entry.Word, StringComparer.Ordinal).FirstOrDefault();
        var limit = Math.Max(1, written.Length / 3);
        return best.Word is not null && best.Distance <= limit ? best.Word : null;
    }
}
