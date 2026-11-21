// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Module.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Each way a text file, a version range or a command line can be wrong, and what Tazel says.</summary>
public sealed class TextFileErrorTests
{
    [Theory]
    [InlineData("platform linux-amd64;", "E0205")]
    [InlineData("build: linux-amd64;", "E0201")]
    [InlineData("platform: linux-amd64; platform: linux-amd64;", "E0202")]
    [InlineData("platform: plan9-amd64;", "E0211")]
    [InlineData("platform: linux-amd64 microvm-cpus: 2;", "E0204")]
    [InlineData("microvm-memory: ;", "E0203")]
    [InlineData("microvm-memory: 0128;", "E0209")]
    [InlineData("microvm-cpus: 'two';", "E0208")]
    [InlineData("microvm-cpus: 999;", "E0210")]
    [InlineData("microvm-network: maybe;", "E0207")]
    [InlineData("(platform: linux-amd64;", "E0206")]
    [InlineData("'unclosed", "E0003")]
    public void AnOptionsFileThatIsWrongSaysWhereWithItsCode(string text, string code)
    {
        var error = Assert.Throws<TazelOptionsException>(() => TazelOptionsFile.Parse(text));
        Assert.True(error.Diagnostic is not null ? error.Diagnostic.Code == code : error.Message.Contains(code, StringComparison.Ordinal),
            $"{error.Diagnostic?.Code}: {error.Message}");
    }

    [Fact]
    public void TheLastOptionMayEndTheFileWithoutItsSemicolon()
    {
        Assert.Equal(Platform.LinuxAmd64, TazelOptionsFile.Parse("platform: linux-amd64").Platform);
    }

    [Theory]
    [InlineData("microvm-memory:", "microvm-memory: 2048;")]
    [InlineData("microvm-cpus:", "microvm-cpus: 2;")]
    [InlineData("platform:", "platform: linux-amd64;")]
    [InlineData("microvm-network:", "microvm-network: true;")]
    public void AMissingValueIsHelpedWithAnExampleOfItsOption(string text, string example)
    {
        var error = Assert.Throws<TazelOptionsException>(() => TazelOptionsFile.Parse(text));
        Assert.Contains(example, error.Diagnostic!.Help, StringComparison.Ordinal);
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void AnOptionsFileThatCannotBeReadIsAnErrorAndNotAMissingFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Environment.UserName == "root", "needs Unix permissions that apply");
        using var module = new TemporaryModule().Write("sub/tazel_options.txt", "platform: linux-amd64;\n");
        var directory = Path.Combine(module.Root, "sub");
        File.SetUnixFileMode(directory, UnixFileMode.UserWrite);
        try
        {
            var error = Assert.Throws<TazelOptionsException>(() => TazelOptionsFile.Find(directory));
            Assert.Contains("could not be inspected", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var file = Path.Combine(directory, "tazel_options.txt");
        File.SetUnixFileMode(file, UnixFileMode.UserWrite);
        try
        {
            var error = Assert.Throws<TazelOptionsException>(() => TazelOptionsFile.Effective(directory));
            Assert.Contains("could not be read", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("build ('tazel' 'build');", "E0306")]
    [InlineData("build: 'tazel' 'build');", "E0307")]
    [InlineData("build: ('tazel' 'build';", "E0308")]
    [InlineData("build: ('tazel' 'build') test: ('tazel');", "E0305")]
    [InlineData("build: ;", "E0307")]
    [InlineData("('x');", "E0309")]
    [InlineData("build: (", "E0308")]
    public void ACustomFileThatIsWrongSaysWhereWithItsCode(string text, string code)
    {
        var error = Assert.Throws<CustomCommandsException>(() => CustomParser.Parse(text));
        Assert.Equal(code, error.Diagnostic!.Code);
    }

    [Fact]
    public void ACustomNameHoldsAtMostTheWordsACommandLineTakes()
    {
        var max = CustomCommandFiles.MaxArguments;
        var tooLong = string.Join(' ', Enumerable.Range(0, max + 1).Select(i => $"w{i}"));
        Assert.Equal("E0302",
            Assert.Throws<CustomCommandsException>(() => CustomParser.Parse($"{tooLong}: ('true');")).Diagnostic!.Code);

        var full = string.Join(' ', Enumerable.Range(0, max - 1).Select(i => $"w{i}"));
        Assert.Equal("E0304",
            Assert.Throws<CustomCommandsException>(() => CustomParser.Parse($"{full}: ('echo' <2>);")).Diagnostic!.Code);
    }

    [Fact]
    public void EveryVersionRangeSaysWhatItAllowsAndDescribesItself()
    {
        var version = new Version(1, 4, 2);
        (TazelVersion Range, bool Allows, string Says)[] cases =
        [
            (TazelVersion.Exactly(1, 4, 2), true, "exactly 1.4.2"),
            (TazelVersion.Exactly(1, 4), false, "exactly 1.4.0"),
            (TazelVersion.GreaterThan(1, 4), true, "later than 1.4.0"),
            (TazelVersion.GreaterThan(1, 4, 2), false, "later than 1.4.2"),
            (TazelVersion.AtMost(1, 4), false, "1.4.0 or earlier"),
            (TazelVersion.AtMost(1, 4, 2), true, "1.4.2 or earlier"),
            (TazelVersion.LessThan(1, 5), true, "earlier than 1.5.0"),
            (TazelVersion.LessThan(1, 4, 2), false, "earlier than 1.4.2"),
            (TazelVersion.Between(1, 0, 2, 0), true, "1.0.0 to 2.0.0"),
            (TazelVersion.Between(1, 4, 3, 2, 0, 0), false, "1.4.3 to 2.0.0"),
        ];
        foreach (var (range, allows, says) in cases)
        {
            Assert.Equal(allows, range.Admits(version));
            Assert.Equal(says, range.ToString());
        }

        Assert.Throws<DslException>(() => TazelVersion.Between(2, 0, 1, 0));
        Assert.Throws<DslException>(() => TazelVersion.Exactly(1, -1));
    }

    [Theory]
    [InlineData(new[] { "dotnet", "restart-daemon" }, null)]
    [InlineData(new[] { "dotnet", "start-deamon" }, "did you mean start-daemon?")]
    [InlineData(new[] { "dotnet" }, "takes start-daemon, restart-daemon or stop-daemon")]
    [InlineData(new[] { "build", "--feature=" }, "--feature has an empty value")]
    [InlineData(new[] { "build", "--", "x" }, "takes nothing after --")]
    [InlineData(new[] { "portal-modules", "info", "a", "b" }, "takes one module")]
    public void TheCommandLineSaysWhatItTakes(string[] arguments, string? said)
    {
        if (said is null)
        {
            Assert.Equal("restart-daemon", Assert.IsType<DaemonCommand>(CommandLine.Parse(arguments)).Action);
            return;
        }

        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(arguments));
        Assert.Contains(said, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColorProgressAndModeTakeEachOfTheirValues()
    {
        var always = CommandLine.Parse(["build", "--color=always", "--progress=auto", "--mode=debug"]);
        Assert.Equal(When.Always, always.Output.Color);
        Assert.Equal(When.Auto, always.Output.Progress);
        Assert.Equal(BuildMode.Debug, Assert.IsType<BuildCommand>(always).Flags.Mode);
        Assert.IsType<VersionCommand>(CommandLine.Parse(["version"]));
    }
}
