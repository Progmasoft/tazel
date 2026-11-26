// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class MessageFormatTests
{
    // Every kind of message, once.
    private static string Everything(MessageFormat format, bool quiet = false)
    {
        var output = new StringWriter();
        using (var reporter = new Reporter(format, output, When.Always, quiet, When.Always))
        {
            reporter.Status("loading");
            reporter.ActionDone(1, 2, "Compiling a.c", "a.c:1: warning: x\n");
            reporter.ActionDone(2, 2, "Linking app", "");
            reporter.ActionFailed("Compiling b.c", ["cc", "-c", "b c.c"], "b.c:1: error: y\n");
            reporter.Error("E0500", "no package //nope", "check the label", "a joke");
            reporter.Diagnostic(new Diagnostic("E0406", "tazel_envs.txt", 2, 5, 3, "a: 'b' c", "a value without its ';'",
                "here", "add ';'", "another joke"));
            reporter.Warning("careful");
            reporter.Success("built 2 targets");
            reporter.TestSummary([new TestOutcome("//:t", TestStatus.Passed, TimeSpan.FromSeconds(1.25), ""),
                new TestOutcome("//:u", TestStatus.TimedOut, TimeSpan.FromSeconds(3), "")]);
            reporter.TestLog("//:u", "line one\nline two\n");
            reporter.Plain("//:a\n//:b");
            reporter.Strong("com.example:json  latest 1.0.0");
            reporter.Table(["Version", "Pushed"], [["1.0.0", "2026-11-26"], ["0.9.0 yanked", "2026-11-20"]]);
        }

        return output.ToString().ReplaceLineEndings("\n");
    }

    [Theory]
    [InlineData("--message-format=jsonl", nameof(MessageFormat.Jsonl))]
    [InlineData("--message-format:xml", nameof(MessageFormat.Xml))]
    [InlineData("--message-format script-friendly-stdio", nameof(MessageFormat.ScriptFriendlyStdio))]
    [InlineData("--message-format=xml --message-format=rich-stdio", nameof(MessageFormat.RichStdio))]
    [InlineData("", nameof(MessageFormat.RichStdio))]
    public void TheCommandLineChoosesTheFormat(string options, string expected)
    {
        var format = Enum.Parse<MessageFormat>(expected);
        string[] arguments = ["version", .. options.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        Assert.Equal(format, CommandLine.Parse(arguments).Output.Format);
        Assert.Equal(format, CommandLine.FormatOf(arguments));
    }

    [Fact]
    public void JsonIsNotAFormatAndHintsJsonl()
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["version", "--message-format=json"]));
        Assert.Contains("did you mean --message-format=jsonl", error.Message, StringComparison.Ordinal);
        Assert.Equal(MessageFormat.RichStdio, CommandLine.FormatOf(["version", "--message-format=json"]));
        Assert.Equal(MessageFormat.RichStdio, CommandLine.FormatOf(["run", "//:a", "--", "--message-format=jsonl"]));
    }

    [Fact]
    public void ScriptFriendlyLinesStartWithAFixedWord()
    {
        Assert.Equal("""
            INFO loading
            DONE Compiling a.c
              a.c:1: warning: x
            DONE Linking app
            FAILED Compiling b.c
              command: cc -c 'b c.c'
              b.c:1: error: y
            error[E0500]: no package //nope
              help: check the label
            error[E0406]: tazel_envs.txt:2:5: a value without its ';'
              here: here
              help: add ';'
            warning: careful
            SUCCESS built 2 targets
            PASSED //:t 1.3 s
            TIMED-OUT //:u 3.0 s
            TESTS 1 passed, 1 failed, 2 in all
            LOG //:u
              line one
              line two
            //:a
            //:b
            com.example:json  latest 1.0.0
            Version	Pushed
            1.0.0	2026-11-26
            0.9.0 yanked	2026-11-20

            """, Everything(MessageFormat.ScriptFriendlyStdio));
    }

    [Fact]
    public void EveryJsonlLineIsAnObjectWithItsKind()
    {
        var lines = Everything(MessageFormat.Jsonl).TrimEnd('\n').Split('\n');
        var kinds = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()).ToList();
        Assert.Equal(["status", "action", "action", "action-failed", "error", "diagnostic", "warning", "success", "test",
            "test", "test-summary", "test-log", "output", "output", "table"], kinds);
        var diagnostic = JsonDocument.Parse(lines[5]).RootElement;
        Assert.Equal(2, diagnostic.GetProperty("line").GetInt32());
        Assert.Equal(5, diagnostic.GetProperty("column").GetInt32());
        Assert.Equal("tazel_envs.txt", diagnostic.GetProperty("file").GetString());
        // An action with no output has no output field; a list stays a list.
        Assert.False(JsonDocument.Parse(lines[2]).RootElement.TryGetProperty("output", out _));
        Assert.Equal(["cc", "-c", "b c.c"],
            JsonDocument.Parse(lines[3]).RootElement.GetProperty("command").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1.25, JsonDocument.Parse(lines[8]).RootElement.GetProperty("seconds").GetDouble());
        Assert.Equal("timed-out", JsonDocument.Parse(lines[9]).RootElement.GetProperty("status").GetString());
        Assert.Equal("0.9.0 yanked", JsonDocument.Parse(lines[14]).RootElement.GetProperty("rows")[1][0].GetString());
    }

    [Fact]
    public void JsonlKeepsTextAsItIs()
    {
        var output = new StringWriter();
        using (var reporter = new Reporter(MessageFormat.Jsonl, output, When.Never, false, When.Never))
        {
            reporter.Warning("soufflé <b> & \"x\"");
        }

        Assert.Contains("soufflé <b> & \\\"x\\\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void XmlIsOneDocumentClosedAtTheEnd()
    {
        var document = XDocument.Parse(Everything(MessageFormat.Xml));
        Assert.Equal("tazel", document.Root!.Name.LocalName);
        Assert.Equal(15, document.Root.Elements().Count());
        var failed = document.Root.Element("action-failed")!;
        Assert.Equal(["cc", "-c", "b c.c"], failed.Element("command")!.Elements("item").Select(e => e.Value));
        Assert.Equal("1.25", document.Root.Element("test")!.Element("seconds")!.Value);
        Assert.Equal("0.9.0 yanked", document.Root.Element("table")!.Element("rows")!.Elements("row").Last().Elements("cell").First().Value);
    }

    [Fact]
    public void XmlReplacesControlCharactersItCannotHold()
    {
        var output = new StringWriter();
        using (var reporter = new Reporter(MessageFormat.Xml, output, When.Never, false, When.Never))
        {
            reporter.ActionDone(1, 1, "x", "bell\a escape\u001b[0m\n");
        }

        var text = XDocument.Parse(output.ToString()).Root!.Element("action")!.Element("output")!.Value;
        Assert.Equal("bell� escape�[0m\n", text);
    }

    [Fact]
    public void QuietDropsStatusInEveryFormat()
    {
        foreach (var format in new[] { MessageFormat.ScriptFriendlyStdio, MessageFormat.Jsonl, MessageFormat.Xml })
        {
            var text = Everything(format, quiet: true);
            Assert.DoesNotContain("loading", text, StringComparison.Ordinal);
            Assert.DoesNotContain("built 2 targets", text, StringComparison.Ordinal);
            Assert.Contains("careful", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RichStdioIsWhatTazelPrintedBefore()
    {
        var text = Everything(MessageFormat.RichStdio);
        // Colours, the counter, and the picture of a problem.
        Assert.Contains("\u001b[", text, StringComparison.Ordinal);
        var plain = System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", "");
        Assert.Contains("[1/2] Compiling a.c", plain, StringComparison.Ordinal);
        Assert.Contains("╭─[ tazel_envs.txt:2:5 ]", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("\"kind\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandLineThatCannotBeTakenIsReportedInItsFormat()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile);
        var (status, output) = await module.TazelAsync("", "build", "--nope", "--message-format=jsonl");
        Assert.Equal(2, status);
        var error = JsonDocument.Parse(output.Trim()).RootElement;
        Assert.Equal("E0100", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExplainWritesItsExplanation()
    {
        using var module = new TemporaryModule();
        var (status, output) = await module.TazelAsync("", "explain", "E0406", "--long", "--message-format=xml");
        Assert.Equal(0, status);
        var explanation = XDocument.Parse(output).Root!.Element("explanation")!;
        Assert.Equal("E0406", explanation.Element("code")!.Value);
        Assert.NotNull(explanation.Element("wrong"));
        Assert.NotEmpty(explanation.Element("related")!.Elements("item"));
    }
}
