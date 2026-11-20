// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Spectre.Console;

namespace Progmasoft.Tazel.Output;

/// <summary>When colors or the progress counter are shown.</summary>
internal enum When
{
    /// <summary>When the output is a terminal.</summary>
    Auto,

    /// <summary>Always.</summary>
    Always,

    /// <summary>Never.</summary>
    Never,
}

/// <summary>How a test ended.</summary>
internal enum TestStatus
{
    Passed,
    Failed,
    TimedOut,
    Flaky,
    NotBuilt,
}

/// <summary>One line of the summary of <c>tazel test</c>.</summary>
internal sealed record TestOutcome(string Label, TestStatus Status, TimeSpan Duration, string Log);

/// <summary>Everything Tazel prints, through Spectre.Console; it is safe to call from the jobs of a build.</summary>
internal sealed class Reporter
{
    private readonly object _lock = new();
    private readonly bool _quiet;
    private readonly bool _counter;

    public Reporter(IAnsiConsole console, bool quiet, When progress)
    {
        Console = console;
        _quiet = quiet;
        _counter = progress == When.Always || (progress == When.Auto && !quiet && console.Profile.Capabilities.Interactive);
    }

    public IAnsiConsole Console { get; }

    /// <summary>A console with the colors that <paramref name="color"/> asks for.</summary>
    public static IAnsiConsole CreateConsole(When color, System.IO.TextWriter? output = null)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = color switch
            {
                When.Always => AnsiSupport.Yes,
                When.Never => AnsiSupport.No,
                _ => AnsiSupport.Detect,
            },
            ColorSystem = color == When.Never ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = output is null ? null : new AnsiConsoleOutput(output),
            // The enrichers of CI services turn ANSI on by themselves; --color decides here, and auto detects alone.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = color == When.Auto },
        });
        if (color != When.Auto)
        {
            console.Profile.Capabilities.Ansi = color == When.Always;
        }

        // Lines that go to a file or a pipe are not wrapped: a command line stays one line.
        if (output is not null || System.Console.IsOutputRedirected)
        {
            console.Profile.Width = 4096;
        }

        return console;
    }

    /// <summary>A status line, such as what is being loaded; <c>--quiet</c> drops it.</summary>
    public void Status(string text)
    {
        if (!_quiet)
        {
            Write($"[blue]INFO[/] {Markup.Escape(text)}");
        }
    }

    /// <summary>An action finished; its output, when it printed any, follows it.</summary>
    public void ActionDone(int done, int total, string description, string output)
    {
        if (_quiet && output.Length == 0)
        {
            return;
        }

        var counter = _counter ? $"[grey][[{done}/{total}]][/] " : "";
        lock (_lock)
        {
            if (!_quiet || output.Length > 0)
            {
                Console.MarkupLine($"{counter}{Markup.Escape(description)}");
            }

            if (output.Length > 0)
            {
                Console.WriteLine(output.TrimEnd('\n'));
            }
        }
    }

    /// <summary>An action failed, with its command line and what it printed.</summary>
    public void ActionFailed(string description, IReadOnlyList<string> command, string output)
    {
        lock (_lock)
        {
            Console.MarkupLine($"[red bold]FAILED[/] {Markup.Escape(description)}");
            Console.MarkupLine($"[grey]{Markup.Escape(Quote(command))}[/]");
            if (output.Length > 0)
            {
                Console.WriteLine(output.TrimEnd('\n'));
            }
        }
    }

    /// <summary>What went wrong, ending the command.</summary>
    public void Error(string message)
    {
        lock (_lock)
        {
            Console.MarkupLine($"[red bold]error[/]: {Markup.Escape(message)}");
        }
    }

    /// <summary>Something the user should know that does not stop the command.</summary>
    public void Warning(string message) => Write($"[yellow]warning[/]: {Markup.Escape(message)}");

    /// <summary>The end of a build.</summary>
    public void Success(string message)
    {
        if (!_quiet)
        {
            Write($"[green]{Markup.Escape(message)}[/]");
        }
    }

    /// <summary>The summary of <c>tazel test</c>, a table of the tests and how each ended.</summary>
    public void TestSummary(IReadOnlyList<TestOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Test").AddColumn("Result").AddColumn(new TableColumn("Time").RightAligned());
        foreach (var outcome in outcomes)
        {
            var status = outcome.Status switch
            {
                TestStatus.Passed => "[green]PASSED[/]",
                TestStatus.Flaky => "[yellow]FLAKY[/]",
                TestStatus.TimedOut => "[red]TIMEOUT[/]",
                TestStatus.NotBuilt => "[red]NOT BUILT[/]",
                _ => "[red]FAILED[/]",
            };
            table.AddRow(Markup.Escape(outcome.Label), status, $"{outcome.Duration.TotalSeconds:0.0} s");
        }

        lock (_lock)
        {
            Console.Write(table);
            var failed = outcomes.Count(o => o.Status is TestStatus.Failed or TestStatus.TimedOut or TestStatus.NotBuilt);
            Console.MarkupLine(failed == 0
                ? $"[green]{outcomes.Count} test{(outcomes.Count == 1 ? "" : "s")} passed[/]"
                : $"[red]{failed} of {outcomes.Count} test{(outcomes.Count == 1 ? "" : "s")} failed[/]");
        }
    }

    /// <summary>The output of a test that failed.</summary>
    public void TestLog(string label, string log)
    {
        lock (_lock)
        {
            Console.MarkupLine($"[red]--- {Markup.Escape(label)} ---[/]");
            Console.WriteLine(log.TrimEnd('\n'));
        }
    }

    /// <summary>Plain text, as help prints it.</summary>
    public void Plain(string text)
    {
        lock (_lock)
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>A command line as a shell would take it, for messages.</summary>
    public static string Quote(IEnumerable<string> command) =>
        string.Join(' ', command.Select(part =>
            part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,+@%".Contains(c, StringComparison.Ordinal))
                ? part
                : "'" + part.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));

    private void Write(string markup)
    {
        lock (_lock)
        {
            Console.MarkupLine(markup);
        }
    }
}
