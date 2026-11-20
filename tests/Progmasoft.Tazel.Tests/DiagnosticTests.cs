// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Dsl.Options;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class DiagnosticTests
{
    [Theory]
    [InlineData("envs", "x: '1'")]
    [InlineData("envs", "x '1';")]
    [InlineData("envs", "x: '1';\nx: '2';")]
    [InlineData("envs", "x: version;")]
    [InlineData("envs", "x:")]
    [InlineData("envs", "x: ();")]
    [InlineData("envs", "x: s('a' 'b');")]
    [InlineData("envs", "x: ('echo' <1>);")]
    [InlineData("envs", "x: \"open")]
    [InlineData("envs", "<# open")]
    [InlineData("envs", "x: @;")]
    [InlineData("custom", "build (\"tazel\");")]
    [InlineData("custom", "build: (\"tazel\")")]
    [InlineData("custom", "build: ();")]
    [InlineData("custom", "build: s(\"a\" \"b\");")]
    [InlineData("custom", "build: (<1>);")]
    [InlineData("custom", "build: (\"a\" <0>);")]
    [InlineData("custom", "a: (\"x\");\na: (\"y\");")]
    [InlineData("options", "platform: linux-amd46;")]
    [InlineData("options", "plaform: linux-amd64;")]
    [InlineData("options", "microvm-cpus: 99999;")]
    [InlineData("options", "microvm-network: maybe;")]
    [InlineData("options", "microvm-memory: \"lots\";")]
    [InlineData("options", "platform linux-amd64;")]
    [InlineData("options", "platform:")]
    public void EveryProblemIsShownWithItsPlaceHelpAndAJoke(string reader, string text)
    {
        var diagnostic = DiagnosticOf(reader, text);
        Assert.NotNull(diagnostic);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Label));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Help));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Joke));
        Assert.InRange(diagnostic.Column, 1, diagnostic.SourceLine.Length + 1);
        Assert.Equal(text.Split('\n')[diagnostic.Line - 1], diagnostic.SourceLine);
    }

    [Fact]
    public void AMissingSemicolonIsMarkedRightAfterTheValue()
    {
        var diagnostic = DiagnosticOf("envs", "example-version: '0.1.0'\n")!;
        Assert.Equal((1, 25), (diagnostic.Line, diagnostic.Column));
        Assert.Equal("a ';' goes here", diagnostic.Label);
        Assert.Contains("example-version: '0.1.0';", diagnostic.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspelledPlatformGetsASuggestion() =>
        Assert.Equal("did you mean linux-amd64?", DiagnosticOf("options", "platform: linux-amd46;")!.Help);

    [Fact]
    public void TheSameProblemAlwaysGetsTheSameJoke() =>
        Assert.Equal(Jokes.ForProject("//:x is not a target"), Jokes.ForProject("//:x is not a target"));

    [Fact]
    public async Task TheErrorIsDrawnWithTheLineAMarkHelpAndAJoke()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("tazel_envs.txt", "example-version: '0.1.0'\n");
        var (status, output) = await module.TazelAsync(".", "build", "//...", "--quiet");
        Assert.Equal(1, status);
        Assert.Contains("error: expected ';' after the value of example-version", output, StringComparison.Ordinal);
        Assert.Contains("tazel_envs.txt:1:25", output, StringComparison.Ordinal);
        Assert.Contains("1 | example-version: '0.1.0'", output.Replace('│', '|'), StringComparison.Ordinal);
        Assert.Contains("a ';' goes here", output, StringComparison.Ordinal);
        Assert.Contains("help: end every value with ';'", output, StringComparison.Ordinal);
        Assert.Contains("Semicolons are small, but they hold this whole file together.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandLineErrorSaysWhereTheHelpIs()
    {
        using var module = new TemporaryModule();
        var (status, written) = await module.TazelAsync(".", "build", "--colour", "never");
        // The command line is read before --color is, so the colors stay; they are not what this test is about.
        var output = System.Text.RegularExpressions.Regex.Replace(written, "\u001b\\[[0-9;]*m", "");
        Assert.Equal(2, status);
        Assert.Contains("help: tazel help build --long says what build takes", output, StringComparison.Ordinal);
        var message = output.Split('\n')[0];
        Assert.Contains(Jokes.ForCommandLine(message[(message.IndexOf("error: ", StringComparison.Ordinal) + 7)..].Trim()),
            output, StringComparison.Ordinal);
    }

    private static Diagnostic? DiagnosticOf(string reader, string text)
    {
        try
        {
            switch (reader)
            {
                case "envs":
                    TazelEnvsFile.Parse(text);
                    break;
                case "custom":
                    CustomParser.Parse(text);
                    break;
                default:
                    TazelOptionsFile.Parse(text);
                    break;
            }
        }
        catch (CustomCommandsException error)
        {
            return error.Diagnostic;
        }
        catch (TazelOptionsException error)
        {
            return error.Diagnostic;
        }

        Assert.Fail($"'{text}' was read without a problem");
        return null;
    }
}
