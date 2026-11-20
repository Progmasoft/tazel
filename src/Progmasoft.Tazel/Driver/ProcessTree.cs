// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Progmasoft.Tazel.Driver;

/// <summary>
/// Stops a process Tazel started and the processes it started in turn, and nothing else.
/// </summary>
/// <remarks>
/// On Windows this is <see cref="Process.Kill(bool)"/> with the whole tree. Elsewhere Tazel finds the descendants
/// itself, from one listing of every process and its parent made before the first signal, and signals only those:
/// on macOS the whole-tree kill of .NET has ended the processes of the CI runner that ran Tazel's tests.
/// </remarks>
internal static class ProcessTree
{
    /// <summary>Stops <paramref name="process"/> and its descendants; one that has already ended is left alone.</summary>
    public static void Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (OperatingSystem.IsWindows())
        {
            KillQuietly(process, entireProcessTree: true);
            return;
        }

        int root;
        try
        {
            if (process.HasExited)
            {
                return;
            }

            root = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var descendants = Descendants(root, Parents());
        // The parent first, so that it starts nothing more; then what it had started.
        KillQuietly(process, entireProcessTree: false);
        foreach (var id in descendants)
        {
            try
            {
                using var child = Process.GetProcessById(id);
                KillQuietly(child, entireProcessTree: false);
            }
            catch (ArgumentException)
            {
                // It ended in the meantime.
            }
        }
    }

    /// <summary>
    /// The descendants of <paramref name="root"/> in <paramref name="parents"/>, children before their children. The
    /// listing gives each process its parent; Tazel itself, its ancestors and the first process are never among them.
    /// </summary>
    internal static List<int> Descendants(int root, IReadOnlyDictionary<int, int> parents)
    {
        ArgumentNullException.ThrowIfNull(parents);
        var children = new Dictionary<int, List<int>>();
        foreach (var (id, parent) in parents)
        {
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }

            list.Add(id);
        }

        var protectedIds = Protected(parents);
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
        {
            if (!children.TryGetValue(queue.Dequeue(), out var list))
            {
                continue;
            }

            foreach (var id in list)
            {
                if (id > 1 && !protectedIds.Contains(id) && seen.Add(id))
                {
                    found.Add(id);
                    queue.Enqueue(id);
                }
            }
        }

        return found;
    }

    /// <summary>Tazel's own process and its ancestors, which a stop never reaches.</summary>
    private static HashSet<int> Protected(IReadOnlyDictionary<int, int> parents)
    {
        var ids = new HashSet<int>();
        var id = Environment.ProcessId;
        while (id > 1 && ids.Add(id) && parents.TryGetValue(id, out var parent))
        {
            id = parent;
        }

        return ids;
    }

    /// <summary>Every process and its parent, from <c>ps</c>; empty when <c>ps</c> cannot be run.</summary>
    private static Dictionary<int, int> Parents()
    {
        var parents = new Dictionary<int, int>();
        var start = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-A", "-o", "pid=", "-o", "ppid=" })
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var ps = Process.Start(start);
            if (ps is null)
            {
                return parents;
            }

            var text = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                    int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent))
                {
                    parents[id] = parent;
                }
            }
        }
        catch (Win32Exception)
        {
            // No ps: only the process itself is stopped.
        }

        return parents;
    }

    private static void KillQuietly(Process process, bool entireProcessTree)
    {
        try
        {
            process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException)
        {
            // It ended by itself.
        }
        catch (Win32Exception)
        {
            // It ended, or it is not Tazel's to stop.
        }
    }
}
