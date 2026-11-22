// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>How labels and the working directory choose targets.</summary>
public sealed class SelectorTests
{
    private const string Prelude = "using Progmasoft.Tazel.Dsl.Build.Prelude;\nusing Progmasoft.Tazel.Dsl.Common;\n";

    private static readonly string Other = Host.Platform == Dsl.Common.Platform.LinuxAmd64 ? "MacOSArm64" : "LinuxAmd64";

    /// <summary>A module whose root has no package: lib has two libraries, one built only for another platform, and app
    /// a program that depends on that one.</summary>
    private static TemporaryModule Module() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile)
        .Write("lib/BUILD.csx", Prelude + $$"""
            CC.StaticLibrary("util", new() { Sources = ["u.c"], Visibility = Visibility.Public });
            CC.StaticLibrary("elsewhere", new() { Sources = ["u.c"], Visibility = Visibility.Public, TargetConstraints = [Platform.{{Other}}] });
            """)
        .Write("lib/u.c", "")
        .Write("lib/sub/notes.txt", "")
        .Write("app/BUILD.csx", Prelude + """
            CC.Executable("app", new() { Sources = ["main.c"], Depends = ["//lib:elsewhere"] });
            """)
        .Write("app/main.c", "");

    private static string[] Names(System.Collections.Generic.IEnumerable<ProjectTarget> targets) =>
        [.. targets.Select(target => target.Label.ToString())];

    [Fact]
    public async Task WithoutLabelsTheNearestPackageAboveTheDirectoryIsChosen()
    {
        using var module = Module();
        var selector = new Selector(await module.LoadAsync(), Host.Platform);
        var chosen = selector.Select([], Path.Combine(module.Root, "lib", "sub"), _ => true, "a target");
        Assert.Equal(["//lib:util"], Names(chosen));
        Assert.Equal(1, selector.Skipped);
    }

    [Fact]
    public async Task WithoutLabelsADirectoryInNoPackageIsAnError()
    {
        using var module = Module();
        var selector = new Selector(await module.LoadAsync(), Host.Platform);
        var error = Assert.Throws<ProjectException>(() => selector.Select([], module.Root, _ => true, "a target"));
        Assert.Contains("the current directory is in no package", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//lib:a:b", "")]
    [InlineData("//nothing/...", "//nothing/... names no package")]
    [InlineData("//lib:missing", "its package has no target 'missing'")]
    [InlineData("//elsewhere:x", "there is no package //elsewhere")]
    [InlineData("//app:app", "it depends on //lib:elsewhere, which does not")]
    public async Task ANamedTargetThatCannotBeBuiltIsAnError(string label, string message)
    {
        using var module = Module();
        var selector = new Selector(await module.LoadAsync(), Host.Platform);
        var error = Assert.Throws<ProjectException>(() => selector.Select([label], module.Root, _ => true, "a target"));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APatternLeavesOutAndCountsWhatDoesNotBuildAndANamedTargetIsChosenOnce()
    {
        using var module = Module();
        var selector = new Selector(await module.LoadAsync(), Host.Platform);
        var chosen = selector.Select(["//...", "//lib:util"], module.Root, _ => true, "a target");
        Assert.Equal(["//lib:util"], Names(chosen));
        Assert.Equal(2, selector.Skipped);
        // A dependency's reason is kept: asking again gives the same answer.
        var workspace = await module.LoadAsync();
        var again = new Selector(workspace, Host.Platform);
        var app = workspace.Targets.Values.Single(target => target.Label.Name == "app");
        Assert.Equal(again.Incompatibility(app), again.Incompatibility(app));
    }
}
