// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Progmasoft.Tazel.Dsl.Custom;

/// <summary>What is wrong with a <c>tazel_custom.txt</c> or <c>tazel_options.txt</c>, with where it is.</summary>
public sealed class CustomCommandsException : Exception
{
    /// <summary>A problem of the file.</summary>
    public CustomCommandsException(string message)
        : base(message)
    {
    }

    /// <summary>A problem of the file, with all it takes to show it well.</summary>
    public CustomCommandsException(Diagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Plain)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>The place, the line, the help and the joke, when the problem has a place.</summary>
    public Diagnostic? Diagnostic { get; }

    /// <summary>Nothing to say.</summary>
    public CustomCommandsException()
    {
    }

    /// <summary>A problem and its cause.</summary>
    public CustomCommandsException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The constants of <c>tazel custom</c>.</summary>
public static class CustomCommandFiles
{
    /// <summary>The file that holds the commands of <c>tazel custom</c>, in the directory they run in.</summary>
    public static string FileName { get; } = "tazel_custom.txt";

    /// <summary>The most words <c>tazel custom</c> takes, those of the name of the command included.</summary>
    public static int MaxArguments { get; } = 191;

    /// <summary>Ends the name of a custom command on the command line; what follows it is arguments.</summary>
    public static string NameEnd { get; } = "--";
}

/// <summary>One argument of a program in a custom command.</summary>
public abstract record CustomArgument
{
    /// <summary>A string of the file.</summary>
    /// <param name="Value">The string.</param>
    public sealed record Text(string Value) : CustomArgument;

    /// <summary><c>&lt;N&gt;</c>: the Nth argument of the command line, from 1.</summary>
    /// <param name="Number">N.</param>
    public sealed record Position(int Number) : CustomArgument;

    /// <summary><c>&lt;*&gt;</c>: all the arguments of the command line.</summary>
    public sealed record All : CustomArgument;
}

/// <summary>One step of a custom command.</summary>
public abstract record CustomStep
{
    /// <summary>What to start: the program and its arguments, given those of the command line and the host.</summary>
    public abstract IReadOnlyList<string> ToStart(IReadOnlyList<string> given, bool windows);

    /// <summary>
    /// A program started directly, with these arguments and no shell between; the first is the program, a string.
    /// </summary>
    /// <param name="Arguments">The arguments.</param>
    public sealed record Program(IReadOnlyList<CustomArgument> Arguments) : CustomStep
    {
        /// <summary>What to start, given the arguments of the command line.</summary>
        public IReadOnlyList<string> Expanded(IReadOnlyList<string> given)
        {
            ArgumentNullException.ThrowIfNull(given);
            return Arguments.SelectMany(argument => argument switch
            {
                CustomArgument.Text text => [text.Value],
                CustomArgument.Position position => [given[position.Number - 1]],
                _ => given,
            }).ToList();
        }

        /// <inheritdoc/>
        public override IReadOnlyList<string> ToStart(IReadOnlyList<string> given, bool windows) => Expanded(given);
    }

    /// <summary>A whole command line for the shell of the host: CMD on Windows, <c>sh</c> elsewhere.</summary>
    /// <param name="Line">The line.</param>
    public sealed record Shell(string Line) : CustomStep
    {
        /// <summary>What to start on this machine; the line reaches the shell unchanged.</summary>
        public IReadOnlyList<string> Started(bool windows) =>
            windows ? ["cmd.exe", "/d", "/s", "/c", $"\"{Line}\""] : ["sh", "-c", Line];

        /// <inheritdoc/>
        public override IReadOnlyList<string> ToStart(IReadOnlyList<string> given, bool windows) => Started(windows);
    }
}

/// <summary>A named command of <c>tazel_custom.txt</c>: steps that run in order until one fails.</summary>
/// <param name="Name">The words of its name, joined by one space.</param>
/// <param name="Steps">Its steps.</param>
public sealed record CustomCommand(string Name, IReadOnlyList<CustomStep> Steps)
{
    /// <summary>The words of <see cref="Name"/>.</summary>
    public IReadOnlyList<string> Words => Name.Split(' ');

    /// <summary>The highest <c>&lt;N&gt;</c>: the arguments the command cannot do without.</summary>
    public int Required => Placeholders.OfType<CustomArgument.Position>().Select(p => p.Number).DefaultIfEmpty(0).Max();

    /// <summary>Whether <c>&lt;*&gt;</c> takes any number of arguments.</summary>
    public bool TakesAll => Placeholders.OfType<CustomArgument.All>().Any();

    private IEnumerable<CustomArgument> Placeholders =>
        Steps.OfType<CustomStep.Program>().SelectMany(program => program.Arguments);
}

/// <summary>Finds the command a command line names.</summary>
public static class CustomCommandMatching
{
    /// <summary>
    /// The command that <paramref name="words"/> name, with the arguments after its name. Names may start with other
    /// names, so the longest one that fits wins. The first <c>--</c> stops the name; everything after it is an argument.
    /// </summary>
    public static (CustomCommand Command, IReadOnlyList<string> Arguments)? Matching(
        this IReadOnlyList<CustomCommand> commands, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(words);
        var end = words.ToList().IndexOf(CustomCommandFiles.NameEnd);
        var named = end < 0 ? words.ToList() : words.Take(end).ToList();
        var after = end < 0 ? [] : words.Skip(end + 1).ToList();
        var best = commands
            .Where(command => command.Words.Count <= named.Count && named.Take(command.Words.Count).SequenceEqual(command.Words))
            .MaxBy(command => command.Words.Count);
        return best is null ? null : (best, named.Skip(best.Words.Count).Concat(after).ToList());
    }
}
