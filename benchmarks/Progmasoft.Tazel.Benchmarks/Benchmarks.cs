// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using TazelCommandLine = Progmasoft.Tazel.Cli.CommandLine;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Benchmarks;

/// <summary>What a build does many times: read labels, match globs, read depfiles and command lines.</summary>
[MemoryDiagnoser]
public class Readers
{
    private const string Depfile = """
        .tazel/out/linux-amd64-debug/_objs/app/src/main.cpp.o: src/main.cpp \
          /usr/include/stdio.h /usr/include/bits/libc-header-start.h \
          /usr/include/features.h include/math/vec\ tor.hpp include/math/details.inl
        """;

    private const string Custom = """
        build-no-cache: ("tazel" 'build' `--no-cache`);
        fetch: ("cargo" "fetch") ("tazel" "fetch");
        build with: ("tazel" "build" <1> <2>);
        """;

    private static readonly string[] BuildLine =
        ["build", "//app/...", "--mode=release", "--compiler-options=-O3,-g", "--feature", "lto", "--color:never"];

    /// <summary>Reads one label.</summary>
    [Benchmark]
    public Selection Label() => Labels.Selection("@dep//app/cli:greeter-main", PackageId.Root);

    /// <summary>Matches a path against a pattern with <c>**</c>.</summary>
    [Benchmark]
    public bool Glob() => FileSets.Matches("src/**/test_*.cpp", "src/a/b/c/test_vector.cpp");

    /// <summary>Reads the inputs of a depfile.</summary>
    [Benchmark]
    public IReadOnlyList<string> DepfileInputs() => Depfiles.Inputs(Depfile);

    /// <summary>Reads a tazel_custom.txt.</summary>
    [Benchmark]
    public int CustomCommands() => CustomParser.Parse(Custom).Count;

    /// <summary>Reads a command line.</summary>
    [Benchmark]
    public object CommandLineOfABuild() => TazelCommandLine.Parse(BuildLine);
}

/// <summary>Runs the benchmarks: <c>dotnet run -c Release --project benchmarks/Progmasoft.Tazel.Benchmarks</c>.</summary>
public static class Program
{
    /// <summary>Runs what the arguments select.</summary>
    public static void Main(string[] arguments) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments);
}
