// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class MigrationTests
{
    private const string Prelude = "using Progmasoft.Tazel.Dsl.Build.Prelude;\n";

    private static TemporaryModule Module() => new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile);

    private static string Read(TemporaryModule module, string path) =>
        File.ReadAllText(Path.Combine(module.Root, path)).ReplaceLineEndings("\n");

    [Fact]
    public void WritesTheExampleOfTheSpecification()
    {
        using var module = Module().Write("BUILD.tzl", Prelude + """
            var mathematics = CC.StaticLibrary("math", new()
            {
                Sources = ["math.cpp"]
            });
            CC.Executable("app", new()
            {
                Sources = ["main.cpp"],
                Depends = [mathematics]
            });
            """);
        var result = CsxMigration.Run(module.Root);
        Assert.Empty(result.Problems);
        Assert.Equal(["BUILD.tzl -> BUILD.csx"], result.Renamed);
        Assert.False(File.Exists(Path.Combine(module.Root, "BUILD.tzl")));
        Assert.Equal(Prelude + """
            CC.StaticLibrary("math", new()
            {
                Sources = ["math.cpp"]
            });
            CC.Executable("app", new()
            {
                Sources = ["main.cpp"],
                Depends = ["//:math"]
            });
            """, Read(module, "BUILD.csx"));
    }

    [Theory]
    [InlineData("BUILD.tzl")]
    [InlineData("BUILD.tazel")]
    [InlineData("BUILD")]
    [InlineData("Tazel.build")]
    public void RenamesEachEarlierName(string name)
    {
        using var module = Module().Write($"lib/{name}", Prelude + "CC.StaticLibrary(\"core\", new());\n");
        var result = CsxMigration.Run(module.Root);
        Assert.Equal([$"lib/{name} -> BUILD.csx"], result.Renamed);
        Assert.Equal(Prelude + "CC.StaticLibrary(\"core\", new());\n", Read(module, "lib/BUILD.csx"));
    }

    [Fact]
    public void KeepsAVariableThatIsUsedElsewhereAndLabelsFromThePackage()
    {
        using var module = Module().Write("lib/BUILD.csx", Prelude + """
            var core = CC.StaticLibrary("core", new() { Visibility = Visibility.Public });
            var util = CC.StaticLibrary("util", new() { Depends = [core, "//:math"] });
            CC.Test("util_test", new() { Depends = [util, core] });
            System.Console.WriteLine(core.Label);
            """);
        var result = CsxMigration.Run(module.Root);
        Assert.Equal(["lib/BUILD.csx"], result.Rewritten);
        Assert.Equal(Prelude + """
            var core = CC.StaticLibrary("core", new() { Visibility = Visibility.Public });
            CC.StaticLibrary("util", new() { Depends = ["//lib:core", "//:math"] });
            CC.Test("util_test", new() { Depends = ["//lib:util", "//lib:core"] });
            System.Console.WriteLine(core.Label);
            """, Read(module, "lib/BUILD.csx"));
    }

    [Fact]
    public void ARunAfterTheFirstChangesNothing()
    {
        using var module = Module().Write("BUILD", Prelude + "var a = CC.StaticLibrary(\"a\", new());\nCC.Executable(\"b\", new() { Depends = [a] });\n");
        Assert.Single(CsxMigration.Run(module.Root).Renamed);
        var once = Read(module, "BUILD.csx");
        var again = CsxMigration.Run(module.Root);
        Assert.Empty(again.Renamed);
        Assert.Empty(again.Rewritten);
        Assert.Empty(again.Problems);
        Assert.Equal(once, Read(module, "BUILD.csx"));
    }

    [Fact]
    public void ATargetNamedOtherwiseThanByItsVariableChangesNoFile()
    {
        using var module = Module()
            .Write("lib/BUILD.tzl", Prelude + "CC.StaticLibrary(\"x\", new());\n")
            .Write("BUILD.csx", Prelude + """
                Target Pick() => CC.StaticLibrary("a", new());
                CC.Executable("b", new() { Depends = [Pick()] });
                """);
        var result = CsxMigration.Run(module.Root);
        Assert.Contains(result.Problems, problem => problem.Contains("BUILD.csx:3: Depends names 'Pick()'", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(module.Root, "lib", "BUILD.tzl")));
        Assert.False(File.Exists(Path.Combine(module.Root, "lib", "BUILD.csx")));
    }

    [Fact]
    public void TwoProjectFilesInOneDirectoryChangeNoFile()
    {
        using var module = Module()
            .Write("a/BUILD", Prelude + "CC.StaticLibrary(\"x\", new());\n")
            .Write("a/BUILD.tzl", Prelude + "CC.StaticLibrary(\"y\", new());\n")
            .Write("b/BUILD.tazel", Prelude + "CC.StaticLibrary(\"z\", new());\n")
            .Write("b/BUILD.csx", Prelude + "CC.StaticLibrary(\"w\", new());\n");
        var result = CsxMigration.Run(module.Root);
        Assert.Contains("a: BUILD.tzl and BUILD are both here; keep one, and run this again", result.Problems);
        Assert.Contains("b: BUILD.tazel and BUILD.csx are both here; keep one, and run this again", result.Problems);
        Assert.True(File.Exists(Path.Combine(module.Root, "a", "BUILD")));
        Assert.Equal(Prelude + "CC.StaticLibrary(\"w\", new());\n", Read(module, "b/BUILD.csx"));
    }

    [Fact]
    public void AfterAnInterruptedRunTheEarlierFileGoes()
    {
        using var module = Module()
            .Write("BUILD.tzl", Prelude + "CC.StaticLibrary(\"a\", new());\n")
            .Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"a\", new());\n");
        var result = CsxMigration.Run(module.Root);
        Assert.Empty(result.Problems);
        Assert.False(File.Exists(Path.Combine(module.Root, "BUILD.tzl")));
    }

    [Fact]
    public void LabelsANestedModuleFromItsOwnRootAndSkipsHiddenDirectories()
    {
        using var module = Module()
            .Write("dep/MODULE.csx", TemporaryModule.ModuleFile)
            .Write("dep/lib/BUILD.tzl", Prelude + "var a = CC.StaticLibrary(\"a\", new());\nCC.Executable(\"b\", new() { Depends = [a] });\n")
            .Write(".tazel/external/x/BUILD", "not C# at all");
        var result = CsxMigration.Run(module.Root);
        Assert.Equal(["dep/lib/BUILD.tzl -> BUILD.csx"], result.Renamed);
        Assert.Contains("Depends = [\"//lib:a\"]", Read(module, "dep/lib/BUILD.csx"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(module.Root, ".tazel", "external", "x", "BUILD")));
    }

    [Fact]
    public async Task ABuildRefusesAnEarlierNameAndSaysHowToMove()
    {
        using var module = Module().Write("lib/BUILD.tzl", Prelude + "CC.StaticLibrary(\"a\", new());\n");
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.Equal(1, status);
        Assert.Contains("a project file is named BUILD.csx and nothing else", output, StringComparison.Ordinal);
        Assert.Contains("tazel limited-time C#", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATargetVariableInDependsSaysToWriteItsLabel()
    {
        using var module = Module().Write("BUILD.csx", Prelude + "var a = CC.StaticLibrary(\"a\", new());\nCC.Executable(\"b\", new() { Depends = [a] });\n");
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.Equal(1, status);
        Assert.Contains("Depends takes the labels of targets, such as \"//:math\", not their variables", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCommandMigratesAndThenFindsNothing()
    {
        using var module = Module().Write("BUILD", Prelude + "var a = CC.StaticLibrary(\"a\", new());\nCC.Executable(\"b\", new() { Depends = [a] });\n");
        var (status, output) = await module.TazelAsync(".", "limited-time", "C#");
        Assert.True(status == 0, output);
        Assert.Contains("renamed BUILD -> BUILD.csx", output, StringComparison.Ordinal);
        (status, output) = await module.TazelAsync(".", "limited-time", "C#");
        Assert.Equal(0, status);
        Assert.Contains("nothing to change", output, StringComparison.Ordinal);
        // What it wrote loads: //:b depends on //:a by its label.
        (status, output) = await module.TazelAsync(".", "query", "deps(//:b)");
        Assert.True(status == 0, output);
        Assert.Contains("//:a", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c#")]
    [InlineData("CSharp")]
    public void TheWordIsCSharpAsWritten(string word)
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["limited-time", word]));
        Assert.Contains("tazel limited-time C#", error.Message, StringComparison.Ordinal);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["limited-time"]));
        Assert.IsType<MigrateCommand>(CommandLine.Parse(["limited-time", "C#"]));
    }

    [Fact]
    public void TheHelpSaysItGoesIn100() =>
        Assert.Contains("removed in 1.0.0", Help.Text("limited-time", false), StringComparison.Ordinal);
}
