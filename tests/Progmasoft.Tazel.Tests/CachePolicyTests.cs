// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class CachePolicyTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        CC.StaticLibrary("math", new() { Sources = ["math.c"], Headers = ["math.h"] });
        CC.Executable("app", new() { Sources = ["main.c"], Depends = ["//:math"] });
        CC.Test("math_test", new() { Sources = ["math_test.c"], Depends = ["//:math"] });
        """;

    private static TemporaryModule Sample() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile)
        .Write("BUILD.csx", Build)
        .Write("math.h", "int add(int a, int b);\n")
        .Write("math.c", "#include \"math.h\"\nint add(int a, int b) { return a + b; }\n")
        .Write("main.c", "#include \"math.h\"\nint main(void) { return add(1, 1) - 2; }\n")
        .Write("math_test.c", "#include \"math.h\"\nint main(void) { return add(2, 2) == 4 ? 0 : 1; }\n");

    private static async Task<string> Ok(TemporaryModule module, params string[] arguments)
    {
        var (status, output) = await module.TazelAsync(".", ["--color", "never", "--progress", "always", .. arguments]);
        Assert.True(status == 0, output);
        return output;
    }

    private static int Count(string output, string what) =>
        output.Split('\n').Count(line => line.Contains(what, StringComparison.Ordinal));

    [Fact]
    public void TheFlagsBelongToTheBuildingCommandsOnly()
    {
        foreach (var command in new[] { "build", "run", "test", "coverage" })
        {
            Assert.NotNull(CommandLine.Parse([command, "//...", "--recompile", "--no-cache"]));
        }

        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["query", "//...", "--recompile"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["clean", "--no-cache"]));
    }

    [Fact]
    public async Task RecompileCompilesAgainAndLinksOnlyWhatChanged()
    {
        Assert.SkipWhen(Host.Which("cc") is null && Host.Which("gcc") is null && Host.Which("clang") is null, "no C compiler");
        using var module = Sample();
        await Ok(module, "build", "//:app");
        Assert.Equal(0, Count(await Ok(module, "build", "//:app"), "Compiling"));
        var forced = await Ok(module, "build", "//:app", "--recompile");
        Assert.Equal(2, Count(forced, "Compiling"));
        // The objects came out the same, so the archive and the program are current.
        Assert.Equal(0, Count(forced, "Archiving"));
        Assert.Equal(0, Count(forced, "Linking"));
        Assert.Equal(0, Count(await Ok(module, "build", "//:app"), "Compiling"));
    }

    [Fact]
    public async Task NoCacheRunsEverythingAndLeavesTheRecordsForTheNextBuild()
    {
        Assert.SkipWhen(Host.Which("cc") is null && Host.Which("gcc") is null && Host.Which("clang") is null, "no C compiler");
        using var module = Sample();
        await Ok(module, "build", "//:app");
        var records = Directory.EnumerateFiles(Path.Combine(module.Root, ".tazel"), "*.action", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllText);
        var forced = await Ok(module, "build", "//:app", "--no-cache");
        Assert.Equal(2, Count(forced, "Compiling"));
        Assert.Equal(1, Count(forced, "Archiving"));
        Assert.Equal(1, Count(forced, "Linking"));
        // Nothing was written to the cache and nothing erased: the records are those of the first build.
        Assert.All(records, entry => Assert.Equal(entry.Value, File.ReadAllText(entry.Key)));
        var after = await Ok(module, "build", "//:app");
        Assert.Equal(0, Count(after, "Compiling") + Count(after, "Linking") + Count(after, "Archiving"));
    }

    [Fact]
    public async Task NoCacheRunsATestThatPassedAndKeepsItsPass()
    {
        Assert.SkipWhen(Host.Which("cc") is null && Host.Which("gcc") is null && Host.Which("clang") is null, "no C compiler");
        using var module = Sample();
        Assert.Contains("PASSED", await Ok(module, "test", "//:math_test"), StringComparison.Ordinal);
        Assert.Contains("PASSED (cached)", await Ok(module, "test", "//:math_test"), StringComparison.Ordinal);
        var forced = await Ok(module, "test", "//:math_test", "--no-cache");
        Assert.Contains("PASSED", forced, StringComparison.Ordinal);
        Assert.DoesNotContain("(cached)", forced, StringComparison.Ordinal);
        Assert.Contains("PASSED (cached)", await Ok(module, "test", "//:math_test"), StringComparison.Ordinal);
        // --recompile leaves the test result cache to its own rules: the same program is the same test.
        Assert.Contains("PASSED (cached)", await Ok(module, "test", "//:math_test", "--recompile"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCacheDominatesRecompile()
    {
        Assert.SkipWhen(Host.Which("cc") is null && Host.Which("gcc") is null && Host.Which("clang") is null, "no C compiler");
        using var module = Sample();
        await Ok(module, "build", "//:app");
        Assert.Equal(1, Count(await Ok(module, "build", "//:app", "--recompile", "--no-cache"), "Linking"));
    }

    [Theory]
    [InlineData("Normal", "Compile", false)]
    [InlineData("Recompile", "Compile", true)]
    [InlineData("Recompile", "CompileAndLink", true)]
    [InlineData("Recompile", "Link", false)]
    [InlineData("Recompile", "Archive", false)]
    [InlineData("Recompile", "Step", false)]
    [InlineData("NoCache", "Link", true)]
    [InlineData("NoCache", "Step", true)]
    public void ForcesWhatTheTableSays(string policy, string kind, bool forced)
    {
        using var reporter = new Reporter(MessageFormat.Jsonl, new StringWriter(), When.Never, true, When.Never);
        var executor = new Executor(1, false, new ActionCache(new Digests()), reporter, null, Enum.Parse<CachePolicy>(policy));
        var action = new BuildAction { Kind = Enum.Parse<ActionKind>(kind), Description = "x", Command = ["true"], Directory = ".", Inputs = [], Outputs = ["x"] };
        Assert.Equal(forced, executor.Forced(action));
    }

    [Fact]
    public async Task AForcedCompilationGetsAFreshCompilerCacheThatIsRemovedAfter()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the command is sh");
        using var module = new TemporaryModule();
        var scratch = Path.Combine(module.Root, "forced");
        string? seen = null;
        var action = new BuildAction
        {
            Kind = ActionKind.CompileAndLink,
            Description = "Compiling x",
            // Run as written, it fails: only the command with the fresh cache writes the output.
            Command = ["sh", "-c", "exit 3"],
            WithCompilerCache = fresh =>
            {
                seen = fresh;
                return ["sh", "-c", $"test -d '{fresh}' && test -z \"$(ls -A '{fresh}')\" && echo made > out.o"];
            },
            Directory = module.Root,
            Inputs = [],
            Outputs = ["out.o"],
        };
        using var reporter = new Reporter(MessageFormat.Jsonl, new StringWriter(), When.Never, true, When.Never);
        var executor = new Executor(1, false, new ActionCache(new Digests()), reporter, null, CachePolicy.Recompile, scratch);
        Assert.True(await executor.RunAsync([action], TestContext.Current.CancellationToken));
        Assert.NotNull(seen);
        Assert.StartsWith(scratch, seen, StringComparison.Ordinal);
        Assert.False(Directory.Exists(seen));
        Assert.Equal("made\n", File.ReadAllText(Path.Combine(module.Root, "out.o")));
    }

    [Fact]
    public async Task ZigCompilesAgainUnderRecompile()
    {
        Assert.SkipWhen(Host.Which("zig") is null, "no zig on PATH");
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nZig.Executable(\"app\", new() { RootSourceFile = \"main.zig\" });\n")
            .Write("main.zig", "pub fn main() void {}\n");
        await Ok(module, "build", "//:app");
        var zigCache = Path.Combine(module.Root, ".tazel", "zig-cache");
        var before = Directory.EnumerateFiles(zigCache, "*", SearchOption.AllDirectories).Count();
        var forced = await Ok(module, "build", "//:app", "--recompile");
        Assert.Equal(1, Count(forced, "Compiling //:app"));
        // The run used a cache of its own, which is gone, and the module's zig cache took nothing from it.
        Assert.Equal(before, Directory.EnumerateFiles(zigCache, "*", SearchOption.AllDirectories).Count());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(module.Root, ".tazel", "forced")));
    }
}
