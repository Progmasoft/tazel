// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class DiagnosticTests
{
    [Theory]
    [InlineData("envs", "x: '1' y: '2';")]
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
    [InlineData("custom", "build: (\"tazel\") test: (\"tazel\");")]
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
        Assert.NotNull(ErrorCodes.Find(diagnostic.Code));
        Assert.Equal(text.Split('\n')[diagnostic.Line - 1], diagnostic.SourceLine);
    }

    [Fact]
    public void AMissingSemicolonIsMarkedRightAfterTheValue()
    {
        var diagnostic = DiagnosticOf("envs", "example-version: '0.1.0'\nexample-name: 'tazel';\n")!;
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
            .Write("tazel_envs.txt", "example-version: '0.1.0'\nexample-name: 'tazel';\n");
        var (status, output) = await module.TazelAsync(".", "build", "//...", "--quiet");
        Assert.Equal(1, status);
        Assert.Contains("error[E0406]: expected ';' after the value of example-version", output, StringComparison.Ordinal);
        Assert.Contains("more: tazel explain E0406", output, StringComparison.Ordinal);
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
        Assert.Contains("error[E0100]: ", message, StringComparison.Ordinal);
        Assert.Contains(Jokes.ForCommandLine(message[(message.IndexOf("]: ", StringComparison.Ordinal) + 3)..].Trim()),
            output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("envs", "x: '1' y: '2';", "E0406")]
    [InlineData("envs", "x '1';", "E0407")]
    [InlineData("envs", "x: version;", "E0404")]
    [InlineData("envs", "x: \"open", "E0003")]
    [InlineData("custom", "build: (\"tazel\") test: (\"tazel\");", "E0305")]
    [InlineData("custom", "build: s(\"a\" \"b\");", "E0311")]
    [InlineData("options", "platform: linux-amd46;", "E0211")]
    [InlineData("options", "microvm-cpus: 99999;", "E0210")]
    public void EachProblemHasItsOwnCode(string reader, string text, string code) =>
        Assert.Equal(code, DiagnosticOf(reader, text)!.Code);

    [Theory]
    [InlineData("envs", "x: '1'")]
    [InlineData("envs", "x: '1'; y: s(\"echo 2\")")]
    [InlineData("custom", "build: (\"tazel\")")]
    [InlineData("custom", "build: (\"tazel\") s(\"echo done\"); test: (\"tazel\" \"test\")")]
    [InlineData("options", "platform: linux-amd64")]
    public void TheLastEntryMayEndTheFileWithoutItsSemicolon(string reader, string text)
    {
        var entries = reader switch
        {
            "envs" => TazelEnvsFile.Parse(text).Count,
            "custom" => CustomParser.Parse(text).Count,
            _ => TazelOptionsFile.Parse(text).Platform is null ? 0 : 1,
        };
        Assert.True(entries >= 1);
    }

    [Fact]
    public void EveryCodeIsWrittenOnceAndExplained()
    {
        var constants = typeof(ErrorCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!).ToList();
        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(constants.Order(StringComparer.Ordinal), ErrorCodes.Codes.Select(code => code.Code).Order(StringComparer.Ordinal));
        foreach (var code in ErrorCodes.Codes)
        {
            Assert.True(ErrorCodes.IsCode(code.Code), code.Code);
            Assert.False(string.IsNullOrWhiteSpace(code.Title), code.Code);
            Assert.EndsWith(".", code.Explanation, StringComparison.Ordinal);
            Assert.Equal(code.Wrong is null, code.Right is null);
        }
    }

    [Fact]
    public async Task ExplainSaysWhatACodeMeansShortAndLong()
    {
        using var module = new TemporaryModule();
        var (status, output) = await module.TazelAsync(".", "explain", "E0406");
        Assert.Equal(0, status);
        Assert.Contains("E0406: a value without its ';'", output, StringComparison.Ordinal);
        Assert.Contains("example-version: '0.1.0';", output, StringComparison.Ordinal);
        Assert.Contains("tazel explain E0406 --long", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "explain", "e0406", "--long");
        Assert.Equal(0, status);
        Assert.Contains("wrong", output, StringComparison.Ordinal);
        Assert.Contains("right", output, StringComparison.Ordinal);
        Assert.Contains("The other errors of tazel_envs.txt", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "explain" }, "takes one error code")]
    [InlineData(new[] { "explain", "406" }, "'406' is not an error code")]
    [InlineData(new[] { "explain", "E9999" }, "Tazel has no error E9999")]
    [InlineData(new[] { "explain", "E0407", "E0406" }, "takes one error code")]
    public void ExplainRefusesWhatIsNotACode(string[] arguments, string message)
    {
        var error = Assert.Throws<Progmasoft.Tazel.Cli.CommandLineException>(() => Progmasoft.Tazel.Cli.CommandLine.Parse(arguments));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ErrorCodes.OptionsNoSemicolon, "options")]
    [InlineData(ErrorCodes.CustomNoSemicolon, "custom")]
    [InlineData(ErrorCodes.EnvsNoSemicolon, "envs")]
    public void TheExamplesOfAMissingSemicolonAreWhatTheySay(string code, string reader)
    {
        var entry = ErrorCodes.Find(code)!;
        Assert.Equal(code, DiagnosticOf(reader, entry.Wrong!)?.Code);
        // The right text is read without a problem.
        _ = reader switch
        {
            "envs" => TazelEnvsFile.Parse(entry.Right!).Count,
            "custom" => CustomParser.Parse(entry.Right!).Count,
            _ => TazelOptionsFile.Parse(entry.Right!).Platform is null ? 0 : 1,
        };
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
