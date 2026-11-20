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

    /// <summary>It passed before with the same program, data, arguments and environment, so it did not run again.</summary>
    Cached,
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

    /// <summary>What went wrong, ending the command, with how to fix it and a light line when there are ones.</summary>
    public void Error(string code, string message, string? help = null, string? joke = null)
    {
        lock (_lock)
        {
            var fancy = Console.Profile.Capabilities.Unicode;
            Console.MarkupLine($"[red bold]{(fancy ? "✖ " : "")}{Heading(code)}[/] [bold]{Markup.Escape(message)}[/]");
            if (help is not null)
            {
                Console.MarkupLine($"  [blue]{(fancy ? "├─" : "|-")}[/] [green]{(fancy ? "💡 " : "")}help:[/] {Markup.Escape(help)}");
            }

            Console.MarkupLine($"  [blue]{(fancy ? "├─" : "|-")}[/] [cyan]{(fancy ? "📖 " : "")}more:[/] tazel explain {code}");

            if (joke is not null)
            {
                Console.MarkupLine($"  [blue]{(fancy ? "╰─" : "`-")}[/] [magenta italic]{(fancy ? "🎭 " : "")}{Markup.Escape(joke)}[/]");
            }
        }
    }

    /// <summary>
    /// A problem in a file the user wrote, drawn as a little picture: the line, a mark under the problem with what it
    /// says, how to fix it, and a light line.
    /// </summary>
    public void Diagnostic(Dsl.Custom.Diagnostic problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        lock (_lock)
        {
            var fancy = Console.Profile.Capabilities.Unicode;
            var number = problem.Line.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pad = new string(' ', number.Length);
            var bar = fancy ? "│" : "|";
            var before = new string(' ', Math.Max(0, problem.Column - 1));
            var width = Math.Max(1, problem.Width);
            string Gutter(string left) => $"[blue]{left} {bar}[/]";

            Console.MarkupLine($"[red bold]{(fancy ? "✖ " : "")}{Heading(problem.Code)}[/] [bold]{Markup.Escape(problem.Message)}[/]");
            Console.MarkupLine($"[blue]{pad} {(fancy ? "╭─[[" : ",-[[")}[/] [cyan]{Markup.Escape($"{Shown(problem.File)}:{problem.Line}:{problem.Column}")}[/] [blue]]][/]");
            Console.MarkupLine(Gutter(pad));
            if (problem.SourceLine.Length > 0 || problem.Column == 1)
            {
                Console.MarkupLine($"{Gutter(number)} {Markup.Escape(problem.SourceLine)}");
                var mark = fancy
                    ? (width == 1 ? "┬" : new string('─', width / 2) + "┬" + new string('─', width - (width / 2) - 1))
                    : new string('^', width);
                var tee = fancy ? before + new string(' ', width / 2) : before;
                Console.MarkupLine($"{Gutter(pad)} [red bold]{Markup.Escape(before + mark)}[/]");
                Console.MarkupLine($"{Gutter(pad)} [red bold]{Markup.Escape(tee + (fancy ? "╰── " : "`-- "))}{Markup.Escape(problem.Label)}[/]");
                Console.MarkupLine(Gutter(pad));
            }

            if (problem.Help is not null)
            {
                Console.MarkupLine($"[blue]{pad} {(fancy ? "├─" : "|-")}[/] [green]{(fancy ? "💡 " : "")}help:[/] {Markup.Escape(problem.Help)}");
            }

            Console.MarkupLine($"[blue]{pad} {(fancy ? "├─" : "|-")}[/] [cyan]{(fancy ? "📖 " : "")}more:[/] tazel explain {problem.Code}");
            if (problem.Joke is not null)
            {
                Console.MarkupLine($"[blue]{pad} {(fancy ? "╰─" : "`-")}[/] [magenta italic]{(fancy ? "🎭 " : "")}{Markup.Escape(problem.Joke)}[/]");
            }
        }
    }

    // error[E0406]: the code in brackets, as rustc writes it; markup needs its brackets doubled.
    private static string Heading(string code) => $"error[[{Markup.Escape(code)}]]:";

    // A file below the current directory is shown from there, which is shorter and what the user typed.
    private static string Shown(string file)
    {
        if (!System.IO.Path.IsPathRooted(file))
        {
            return file;
        }

        var relative = System.IO.Path.GetRelativePath(Environment.CurrentDirectory, file);
        return relative.StartsWith("..", StringComparison.Ordinal) ? file : relative;
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
                TestStatus.Cached => "[green]PASSED[/] [grey](cached)[/]",
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
