// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class EnvsTests
{
    private const string Sample = """
        # comment
        <# comment #>

        example-version: '0.1.0'
        example-output: ("rustc" "--help")
        shell-expension: s(`$HOME`)
        """;

    [Fact]
    public void ReadsTheThreeKindsOfValue()
    {
        var entries = TazelEnvsFile.Parse(Sample);
        Assert.Equal(["example-version", "example-output", "shell-expension"], entries.Select(e => e.Name));
        Assert.Equal(new EnvValue.Text("0.1.0"), entries[0].Value);
        Assert.Equal(["rustc", "--help"], Assert.IsType<EnvValue.Program>(entries[1].Value).Arguments);
        Assert.Equal(new EnvValue.Shell("$HOME"), entries[2].Value);
        Assert.Equal(6, entries[2].Line);
    }

    [Fact]
    public void ValuesFollowOneAnotherWithoutSemicolons() =>
        Assert.Equal(2, TazelEnvsFile.Parse("a: 'x' b: \"y\"").Count);

    [Theory]
    [InlineData("a: 'x'\na: 'y'", "'a' is set twice")]
    [InlineData("a: 'x';", "has no ';' after it")]
    [InlineData("a 'x'", "expected ':' after a")]
    [InlineData("a: ()", "a program names at least itself")]
    [InlineData("a: s('x' 'y')", "s(...) takes one string")]
    [InlineData("a: ('echo' <1>)", "takes no <N> or <*>")]
    [InlineData("a: b", "expected the value of a")]
    [InlineData("a: 'x", "has no closing '")]
    public void RefusesWhatIsWrongWithItsPlace(string text, string message)
    {
        var error = Assert.Throws<CustomCommandsException>(() => TazelEnvsFile.Parse(text));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("tazel_envs.txt:", error.Message, StringComparison.Ordinal);
    }

    private const string Build = """
        using System;
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        using Progmasoft.Tazel.Dsl.Environment;
        var version = GetEnvironment.From("example-version") ?? "none";
        var shell = GetEnvironment.From("from-shell") ?? "none";
        var program = GetEnvironment.From("from-program") ?? "none";
        var missing = GetEnvironment.From("missing") is null ? "null" : "set";
        var notAVariable = Environment.GetEnvironmentVariable("example-version") is null ? "not-a-variable" : "a-variable";
        CC.StaticLibrary($"lib-{version}-{shell}-{(program == "none" ? "none" : "ran")}-{missing}-{notAVariable}", new());
        """;

    [Fact]
    public async Task ProjectFilesReadTheValuesWithGetEnvironment()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", Build)
            .Write("tazel_envs.txt", """
                example-version: '0.1.0'
                from-shell: s('echo shell-said')
                from-program: ("dotnet" "--version")
                """);
        var workspace = await module.LoadAsync();
        var name = workspace.Targets.Keys.Single().Name;
        Assert.Equal("lib-0.1.0-shell-said-ran-null-not-a-variable", name);
    }

    [Fact]
    public async Task WithoutTheFileEveryValueIsNull()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Build);
        var workspace = await module.LoadAsync();
        Assert.Equal("lib-none-none-none-null-not-a-variable", workspace.Targets.Keys.Single().Name);
    }

    [Fact]
    public async Task AProgramThatFailsStopsTheFileThatAsked()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Environment;\nvar x = GetEnvironment.From(\"broken\");")
            .Write("tazel_envs.txt", "broken: s('exit 3')\n");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("tazel_envs.txt:1: broken ran", error.Message, StringComparison.Ordinal);
        Assert.Contains("failed with 3", error.Message, StringComparison.Ordinal);
    }
}
