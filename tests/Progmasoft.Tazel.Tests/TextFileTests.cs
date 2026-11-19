// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Options;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class CustomCommandTests
{
    private const string Sample = """
        # comment
        <# comment #>
        build-no-cache: ("tazel" 'build' `--no-cache`);
        fetch:
            ("cargo" "fetch")
            ("tazel" "fetch");
        npm-dev-sh: s('npm dev');
        build no-cache: ("tazel" "build" "--no-cache");
        build with: ("tazel" "build" <1> <2>);
        all: ("echo" <*>);
        """;

    [Fact]
    public void ReadsTheCommandsInTheirOrder()
    {
        var commands = CustomParser.Parse(Sample);
        Assert.Equal(["build-no-cache", "fetch", "npm-dev-sh", "build no-cache", "build with", "all"],
            commands.Select(c => c.Name));
        Assert.Equal(2, commands[1].Steps.Count);
        Assert.Equal(new CustomStep.Shell("npm dev"), commands[2].Steps[0]);
        Assert.Equal(2, commands[4].Required);
        Assert.True(commands[5].TakesAll);
    }

    [Fact]
    public void TheLongestNameThatFitsWins()
    {
        var commands = CustomParser.Parse(Sample);
        var (command, arguments) = commands.Matching(["build", "with", "-e", "MyApp"])!.Value;
        Assert.Equal("build with", command.Name);
        Assert.Equal(["-e", "MyApp"], arguments);
        Assert.Equal(["tazel", "build", "-e", "MyApp"], ((CustomStep.Program)command.Steps[0]).Expanded(arguments));
    }

    [Fact]
    public void DashesEndTheName()
    {
        var commands = CustomParser.Parse(Sample);
        var (command, arguments) = commands.Matching(["all", "--", "with"])!.Value;
        Assert.Equal("all", command.Name);
        Assert.Equal(["with"], arguments);
    }

    [Theory]
    [InlineData("x: ();", "a command names at least a program")]
    [InlineData("x: s(<1>);", "s(...) takes no arguments")]
    [InlineData("x: s('a' 'b');", "s(...) takes one string")]
    [InlineData("x: (<1> 'a');", "starts with the name of its program")]
    [InlineData("x: ('a'); x: ('b');", "defined twice")]
    [InlineData("x: ('a' <0>);", "<0> is not an argument")]
    [InlineData("x: ('a);", "has no closing '")]
    [InlineData("<# x", "no closing #>")]
    [InlineData("x: ('a')", "expected ';' at the end of a command")]
    [InlineData("x -- y: ('a');", "cannot hold it")]
    public void RefusesWhatIsWrongWithItsPlace(string text, string reason)
    {
        var error = Assert.Throws<CustomCommandsException>(() => CustomParser.Parse(text));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.StartsWith("tazel_custom.txt:", error.Message, StringComparison.Ordinal);
    }
}

public sealed class TazelOptionsTests
{
    [Fact]
    public void ReadsEveryOption()
    {
        var options = TazelOptionsFile.Parse("""
            # comment
            sandboxing-with-microvm: true;
            microvm-network: false;
            microvm-memory: 2048;
            microvm-cpus: "auto";
            platform: macos-x86_64
            """);
        Assert.True(options.SandboxingWithMicrovm);
        Assert.False(options.MicrovmNetwork);
        Assert.Equal(new Allocation(2048), options.MicrovmMemory);
        Assert.Equal(Allocation.Auto, options.MicrovmCpus);
        Assert.Equal(Platform.MacOSX86_64, options.Platform);
    }

    [Theory]
    [InlineData("platform: linux-x64;", "is not a platform")]
    [InlineData("microvm-memory: 100;", "is outside 256 to 1048576 MiB")]
    [InlineData("microvm-cpus: 'some';", "the only string here is \"auto\"")]
    [InlineData("microvm-network: yes;", "expected true or false")]
    [InlineData("microvm-network: true; microvm-network: true;", "is set twice")]
    [InlineData("memory: 1;", "is not an option")]
    public void RefusesWhatIsWrong(string text, string reason)
    {
        var error = Assert.Throws<TazelOptionsException>(() => TazelOptionsFile.Parse(text));
        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASandboxAskedForAboveStaysAskedFor()
    {
        using var module = new TemporaryModule();
        module.Write("tazel_options.txt", "sandboxing-with-microvm: true;")
            .Write("inner/tazel_options.txt", "sandboxing-with-microvm: false; platform: linux-aarch64;");
        var (options, file) = TazelOptionsFile.Effective(Path.Combine(module.Root, "inner"));
        Assert.True(options.SandboxingWithMicrovm);
        Assert.Equal(Platform.LinuxAarch64, options.Platform);
        Assert.Equal(Path.Combine(module.Root, "tazel_options.txt"), file);
    }

    [Fact]
    public void EveryPlatformNameReadsBack()
    {
        foreach (var platform in Enum.GetValues<Platform>())
        {
            Assert.True(Platforms.TryParse(platform.Name(), out var read));
            Assert.Equal(platform, read);
        }
    }
}
