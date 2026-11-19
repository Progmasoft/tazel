// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using Progmasoft.Tazel.Label;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class LabelTests
{
    private static readonly PackageId App = new(null, "app");

    [Theory]
    [InlineData("//:x", null, "", "x")]
    [InlineData("//app:greeter", null, "app", "greeter")]
    [InlineData("//app/cli:main-1.0", null, "app/cli", "main-1.0")]
    [InlineData(":local", null, "app", "local")]
    [InlineData("@dep//:x", "dep", "", "x")]
    [InlineData("@dep-2//lib:y", "dep-2", "lib", "y")]
    public void ReadsTheFormsOfATargetLabel(string text, string? repository, string path, string name)
    {
        var label = Labels.Target(text, App);
        Assert.Equal(new TargetLabel(new PackageId(repository, path), name), label);
    }

    [Theory]
    [InlineData("//...", null, "")]
    [InlineData("//app/...", null, "app")]
    [InlineData("@dep//...", "dep", "")]
    public void ReadsPackagePatterns(string text, string? repository, string path)
    {
        var selection = Labels.Selection(text, PackageId.Root);
        Assert.Equal(new PackagePattern(new PackageId(repository, path)), selection);
    }

    [Theory]
    [InlineData("//app", "names no target")]
    [InlineData("//app/:x", "no / before the colon")]
    [InlineData("app:x", "is not a label")]
    [InlineData("//a/../b:x", "no . or .. parts")]
    [InlineData("//:bad name", "must be letters")]
    [InlineData("@dep:x", "has no //")]
    public void RefusesWhatIsNotALabel(string text, string reason)
    {
        var error = Assert.Throws<LabelException>(() => Labels.Selection(text, PackageId.Root));
        Assert.Contains(reason, error.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void APatternIsNotOneTarget() =>
        Assert.Throws<LabelException>(() => Labels.Target("//...", PackageId.Root));

    [Fact]
    public void APatternCoversItsPackageAndThoseBelow()
    {
        var pattern = new PackagePattern(new PackageId(null, "app"));
        Assert.True(pattern.Covers(new PackageId(null, "app")));
        Assert.True(pattern.Covers(new PackageId(null, "app/cli")));
        Assert.False(pattern.Covers(new PackageId(null, "application")));
        Assert.False(pattern.Covers(new PackageId("dep", "app")));
        Assert.True(new PackagePattern(PackageId.Root).Covers(new PackageId(null, "x/y")));
    }

    [Fact]
    public void LabelsPrintAsTheyAreWritten()
    {
        Assert.Equal("//app:greeter", Labels.Target("//app:greeter", PackageId.Root).ToString());
        Assert.Equal("@dep//:x", Labels.Target("@dep//:x", PackageId.Root).ToString());
        Assert.Equal("//app/...", Labels.Selection("//app/...", PackageId.Root).ToString());
        Assert.Equal("//...", Labels.Selection("//...", PackageId.Root).ToString());
    }
}
