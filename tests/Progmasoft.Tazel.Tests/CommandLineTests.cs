// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--platform", "macos-arm64")]
    [InlineData("--platform=macos-arm64")]
    [InlineData("--platform:macos-arm64")]
    public void ALongOptionTakesItsValueAfterSpaceEqualsOrColon(params string[] option)
    {
        var build = Assert.IsType<BuildCommand>(CommandLine.Parse(["build", .. option, "//..."]));
        Assert.Equal(Platform.MacOSArm64, build.Flags.Platform);
        Assert.Equal(["//..."], build.Labels);
    }

    [Fact]
    public void AListGoesAfterEqualsOrColonAndRepeatsKeepTheirOrder()
    {
        var build = Assert.IsType<BuildCommand>(CommandLine.Parse(
            ["build", "--compiler-options=-O2,-g", "--compiler-options", "-Wall", "--linker-options:-Wl\\,--as-needed"]));
        Assert.Equal(["-O2", "-g", "-Wall"], build.Flags.CompilerOptions);
        Assert.Equal(["-Wl,--as-needed"], build.Flags.LinkerOptions);
    }

    [Fact]
    public void AValueAfterWhitespaceIsOneValue()
    {
        var build = Assert.IsType<BuildCommand>(CommandLine.Parse(["build", "--linker-options", "-Wl,--as-needed"]));
        Assert.Equal(["-Wl,--as-needed"], build.Flags.LinkerOptions);
    }

    [Fact]
    public void OptionsMayStandAnywhereBeforeTheDashes()
    {
        var run = Assert.IsType<RunCommand>(CommandLine.Parse(
            ["--quiet", "run", "//:x", "--mode", "release", "--", "--mode", "y"]));
        Assert.Equal("//:x", run.Label);
        Assert.Equal(BuildMode.Release, run.Flags.Mode);
        Assert.Equal(["--mode", "y"], run.Arguments);
        Assert.True(run.Output.Quiet);
    }

    [Fact]
    public void CustomTakesEverythingAfterIt()
    {
        var custom = Assert.IsType<CustomCommandLine>(CommandLine.Parse(["--color", "never", "custom", "build", "with", "-e", "--x"]));
        Assert.Equal(["build", "with", "-e", "--x"], custom.Words);
        Assert.Equal(When.Never, custom.Output.Color);
    }

    [Theory]
    [InlineData("--color:awlyas", "did you mean --color:always?")]
    [InlineData("--color=nevr", "did you mean --color=never?")]
    [InlineData("--featur=lto", "did you mean --feature=lto?")]
    [InlineData("--expunge", "--expunge is not an option of tazel build")]
    [InlineData("--jobs=0", "--jobs takes a number from 1 to 4096")]
    [InlineData("-x", "-x is not an option")]
    [InlineData("--keep-going=yes", "--keep-going takes no value")]
    public void HintsKeepTheSpellingThatWasWritten(string option, string message)
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["build", option]));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspeltCommandGetsAHint()
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["biuld"]));
        Assert.Contains("did you mean tazel build?", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTakesOneTarget()
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["run", "//:a", "//:b"]));
        Assert.Contains("the program's own arguments go after --", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpComesFromNothingOrTheHelpOption()
    {
        Assert.IsType<HelpCommand>(CommandLine.Parse([]));
        Assert.Equal("test", Assert.IsType<HelpCommand>(CommandLine.Parse(["test", "-h"])).Topic);
        Assert.Equal("build", Assert.IsType<HelpCommand>(CommandLine.Parse(["help", "build"])).Topic);
    }

    [Fact]
    public void EveryCommandHasHelp()
    {
        foreach (var command in CommandLine.Commands)
        {
            Assert.NotEmpty(Help.Text(command));
        }
    }

    [Theory]
    [InlineData("a,b", new[] { "a", "b" })]
    [InlineData("a\\,b", new[] { "a,b" })]
    [InlineData("-Wl\\,-rpath\\,x,-g", new[] { "-Wl,-rpath,x", "-g" })]
    public void SplitsListsAtCommasThatAreNotEscaped(string text, string[] items) =>
        Assert.Equal(items, CommandLine.SplitList(text));

    [Fact]
    public void TheDistanceCountsSwapsOnce()
    {
        Assert.Equal(1, Suggestions.Distance("biuld", "build"));
        Assert.Equal(0, Suggestions.Distance("x", "x"));
        Assert.Null(Suggestions.Nearest("zzz", ["build", "test"]));
    }
}
