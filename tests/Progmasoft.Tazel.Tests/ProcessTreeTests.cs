// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class ProcessTreeTests
{
    [Fact]
    public void TheDescendantsAreTheChildrenAndTheirChildrenOnly()
    {
        // 10 started 11 and 12, 11 started 13; 20 is a sibling of 10 and 21 its child.
        var parents = new Dictionary<int, int> { [10] = 5, [11] = 10, [12] = 10, [13] = 11, [20] = 5, [21] = 20 };

        Assert.Equal([11, 12, 13], ProcessTree.Descendants(10, parents));
        Assert.Empty(ProcessTree.Descendants(13, parents));
    }

    [Fact]
    public void TazelItselfAndItsAncestorsAreNeverDescendants()
    {
        // A listing in which Tazel and its parent appear below the process to stop, as a confused one might.
        var self = Environment.ProcessId;
        var parents = new Dictionary<int, int> { [self] = 77, [77] = 50, [50] = 1, [60] = 77 };

        var found = ProcessTree.Descendants(77, parents);

        Assert.DoesNotContain(self, found);
        Assert.Contains(60, found);
        Assert.DoesNotContain(1, ProcessTree.Descendants(0, new Dictionary<int, int> { [1] = 0 }));
    }

    [Fact]
    public async Task StoppingAProcessStopsWhatItStartedAndNothingBeside()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the shell test needs /bin/sh");
        using var beside = Process.Start("/bin/sh", ["-c", "sleep 60"]);
        using var parent = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", "sleep 60 & sleep 60 & wait"])
        {
            RedirectStandardOutput = true,
        })!;
        var children = new List<int>();
        for (var tries = 0; children.Count < 2 && tries < 50; tries++)
        {
            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
            children = ChildrenOf(parent.Id);
        }

        Assert.Equal(2, children.Count);

        ProcessTree.Kill(parent);
        await parent.WaitForExitAsync(Xunit.TestContext.Current.CancellationToken);

        for (var tries = 0; tries < 50 && ChildrenAlive(children); tries++)
        {
            await Task.Delay(100, Xunit.TestContext.Current.CancellationToken);
        }

        Assert.False(ChildrenAlive(children));
        Assert.False(beside.HasExited);
        beside.Kill();
    }

    private static List<int> ChildrenOf(int id)
    {
        var start = new ProcessStartInfo("pgrep", ["-P", id.ToString(System.Globalization.CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true,
        };
        using var pgrep = Process.Start(start)!;
        var text = pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();
        var ids = new List<int>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ids.Add(int.Parse(line, System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    private static bool ChildrenAlive(List<int> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // It is gone.
            }
        }

        return false;
    }
}
