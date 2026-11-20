// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class MarkerTests
{
    [Fact]
    public void AMarkerNamesTheDirectoryItWroteAndNothingElse()
    {
        using var state = new TemporaryModule();
        var destination = Path.Combine(state.Root, "external", "dep");
        Assert.Null(ModuleArchive.FromMarker(destination));
        Directory.CreateDirectory(Path.Combine(destination, "com.example.dep-1.0.0"));
        ModuleArchive.WriteMarker(destination, Path.Combine(destination, "com.example.dep-1.0.0"));
        Assert.Equal(Path.Combine(destination, "com.example.dep-1.0.0"), ModuleArchive.FromMarker(destination));
    }

    [Theory]
    [InlineData("../../outside")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("missing")]
    public void AnAlteredMarkerIsNotTrusted(string written)
    {
        using var state = new TemporaryModule();
        var destination = Path.Combine(state.Root, "external", "dep");
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(Path.Combine(state.Root, "outside"));
        File.WriteAllText(Path.Combine(destination, ".unpacked"), written);
        var error = Assert.Throws<ProjectException>(() => ModuleArchive.FromMarker(destination));
        Assert.Contains("is damaged", error.Message, StringComparison.Ordinal);
    }
}
