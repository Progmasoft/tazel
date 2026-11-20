// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class SnapshotTests
{
    [Fact]
    public void ADifferenceShowsTheLinesThatDifferWithTheirNeighbours()
    {
        using var module = new TemporaryModule();
        var lines = string.Join('\n', Array.ConvertAll(new int[20], _ => "same"));
        module.Write("stored.snap", $"first\n{lines}\nold line\nlast\n").Write("new.snap", $"first\n{lines}\nnew line\nlast\n");
        var message = Snapshots.Describe(Path.Combine(module.Root, "stored.snap"), Path.Combine(module.Root, "new.snap"));
        Assert.Contains("- old line\n+ new line\n", message, StringComparison.Ordinal);
        Assert.Contains("  same\n  same\n- old line", message, StringComparison.Ordinal);
        Assert.Contains("+ new line\n  last\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("first", message, StringComparison.Ordinal);
        Assert.Contains("new.snap. When it is right, move it over the stored one", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAddedLineIsShownAlone()
    {
        using var module = new TemporaryModule();
        module.Write("stored.snap", "a\nb\n").Write("new.snap", "a\nadded\nb\n");
        var message = Snapshots.Describe(Path.Combine(module.Root, "stored.snap"), Path.Combine(module.Root, "new.snap"));
        Assert.Contains("  a\n+ added\n  b\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("- a\n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("- b\n", message, StringComparison.Ordinal);
    }
}
