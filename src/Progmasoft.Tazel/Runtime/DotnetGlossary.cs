// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;

namespace Progmasoft.Tazel.Runtime;

/// <summary>One term of <c>tazel dotnet what-is</c>: its name, what it stands for, and what it is.</summary>
/// <param name="Term">The term, as it is typed.</param>
/// <param name="Title">What it stands for, or null when it stands for nothing longer.</param>
/// <param name="Text">What it is.</param>
internal sealed record GlossaryEntry(string Term, string? Title, string Text);

/// <summary>
/// The terms of .NET that <c>tazel dotnet what-is</c> explains, offline: they are part of Tazel. Terms are matched as
/// written; an unknown one gets the nearest.
/// </summary>
internal static class DotnetGlossary
{
    /// <summary>Every term, in the order the help lists them.</summary>
    public static IReadOnlyList<GlossaryEntry> Entries { get; } =
    [
        new("GCHeapHardLimit", null,
            "A limit, in bytes, on what the garbage collector commits for the managed heap and its own bookkeeping. It is " +
            "not a limit on the memory of the whole process: native memory, thread stacks and the code of the runtime " +
            "are outside it. --dotnet-settings sets it for this Tazel process alone."),
        new("RyuJIT", null,
            "The just-in-time compiler of .NET. It turns CIL into the machine code of the processor as a method is first " +
            "called, and again with more optimization for methods called often (tiered compilation)."),
        new(".NET", null,
            "A platform for building and running programs: runtimes that execute CIL, the base class libraries, the " +
            "compilers of C#, F# and Visual Basic, and the tools of the dotnet command. Tazel is written for it and runs on it."),
        new("CLI", "Common Language Infrastructure",
            "The execution and type-system infrastructure specified by ECMA-335. It includes the Common Type System, the " +
            "Common Intermediate Language, metadata, and the Virtual Execution System. In tazel dotnet what-is, CLI is " +
            "always this infrastructure of ECMA-335."),
        new("CIL", "Common Intermediate Language",
            "The instruction language the CLI standardizes: compilers of C# and the other .NET languages emit it, and the " +
            "runtime compiles it to machine code. Also called IL or MSIL."),
        new("CLR", "Common Language Runtime",
            "Microsoft's implementation of the runtime of .NET: it loads assemblies, compiles CIL with RyuJIT, collects " +
            "garbage, and enforces the type system."),
        new("C++/CLI", null,
            "An extension of C++ that adds the managed types of .NET to it, so that C++ code and .NET code call each " +
            "other directly. It is built by the Microsoft C++ compiler, on Windows."),
        new("C#", null,
            "A general-purpose, statically typed programming language, the one most used on .NET. Tazel's project files, " +
            "MODULE.csx and BUILD.csx, are C# scripts."),
        new("WorkstationGC", null,
            "The configuration of the garbage collector meant for interactive programs: one heap, collections that pause " +
            "the program briefly, and usually background collection of the oldest generation alongside it."),
        new("BackgroundGC", null,
            "Garbage collection in which much of the work on generation 2, the oldest objects, runs on a thread of its " +
            "own while the program goes on, instead of stopping it for the whole collection."),
        new("daemon", null,
            "A long-running background process that serves other processes. This Tazel starts every command in a process " +
            "of its own and has no persistent daemon; tazel dotnet start-daemon says so."),
        new("CTS", "Common Type System",
            "The rules of the CLI for defining types and how they relate: classes, value types, interfaces, inheritance, " +
            "and how one language's types are seen by another."),
        new("VES", "Virtual Execution System",
            "The abstract machine the CLI specifies to run CIL: how it loads code, calls methods, handles exceptions and " +
            "manages memory. The CLR is an implementation of it."),
        new("ECMA-335", null,
            "The ECMA standard of the Common Language Infrastructure: the CTS, CIL, metadata and the VES. .NET follows it."),
    ];

    /// <summary>The entry of <paramref name="term"/>, as written.</summary>
    public static GlossaryEntry? Find(string term) => Entries.FirstOrDefault(entry => entry.Term == term);

    /// <summary>The term nearest to <paramref name="term"/>, for a suggestion.</summary>
    public static string? Nearest(string term) =>
        Entries.FirstOrDefault(entry => string.Equals(entry.Term, term, StringComparison.OrdinalIgnoreCase))?.Term ??
        Cli.Suggestions.Nearest(term, Entries.Select(entry => entry.Term));
}
