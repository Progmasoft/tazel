// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class ScriptCacheTests
{
    private const string Prelude = "using Progmasoft.Tazel.Dsl.Build.Prelude;\n";

    private static string[] Kept(TemporaryModule module)
    {
        var directory = Path.Combine(module.Root, ".tazel", ScriptCache.Directory);
        return Directory.Exists(directory) ? Directory.GetFiles(directory).Order(StringComparer.Ordinal).ToArray() : [];
    }

    [Fact]
    public async Task KeepsEveryCompiledFileAndUsesItAgain()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"a\", new());")
            .Write("lib/BUILD.csx", Prelude + "CC.StaticLibrary(\"b\", new());");
        await module.LoadAsync();
        var kept = Kept(module);
        Assert.Equal(3, kept.Length);
        Assert.All(kept, file => Assert.EndsWith(".dll", file, StringComparison.Ordinal));
        var written = kept.Select(File.GetLastWriteTimeUtc).ToArray();

        var workspace = await module.LoadAsync();
        Assert.Equal(kept, Kept(module));
        Assert.Equal(written, kept.Select(File.GetLastWriteTimeUtc));
        Assert.Equal(["//:a", "//lib:b"],
            workspace.Targets.Keys.Select(label => label.ToString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnEditedFileIsCompiledAgainUnderAnotherName()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"a\", new());");
        await module.LoadAsync();
        module.Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"b\", new());");
        var workspace = await module.LoadAsync();
        Assert.Equal(3, Kept(module).Length);
        Assert.Equal(new TargetLabel(PackageId.Root, "b"), Assert.Single(workspace.Targets.Keys));
    }

    [Fact]
    public async Task ADamagedFileIsCompiledAgainAndReplaced()
    {
        using var module = new TemporaryModule();
        // A new environment each time: in it nothing is loaded yet, as in a new process.
        var first = new Counter();
        await ScriptHost.RunAsync("Count += 1;", "BUILD.csx", first, module.Root, ScriptEnvironment.With([], []));
        var kept = Assert.Single(Kept(module));
        File.WriteAllText(kept, "not an assembly");

        var second = new Counter();
        await ScriptHost.RunAsync("Count += 1;", "BUILD.csx", second, module.Root, ScriptEnvironment.With([], []));
        Assert.Equal(1, second.Count);
        Assert.NotEqual("not an assembly", File.ReadAllText(Assert.Single(Kept(module))));

        var third = new Counter();
        await ScriptHost.RunAsync("Count += 1;", "BUILD.csx", third, module.Root, ScriptEnvironment.With([], []));
        Assert.Equal(1, third.Count);
    }

    [Fact]
    public async Task AFileOfUsingsAloneRunsAndIsKept()
    {
        using var module = new TemporaryModule();
        var globals = new Counter();
        await ScriptHost.RunAsync("using System;", "BUILD.csx", globals, module.Root, ScriptEnvironment.With([], []));
        await ScriptHost.RunAsync("using System;", "BUILD.csx", globals, module.Root, ScriptEnvironment.With([], []));
        Assert.Equal(0, globals.Count);
        Assert.Empty(File.ReadAllBytes(Assert.Single(Kept(module))));
    }

    [Fact]
    public async Task WithoutARootNothingIsKept()
    {
        var globals = new Counter();
        await ScriptHost.RunAsync("Count += 2;", "BUILD.csx", globals, null, ScriptEnvironment.With([], []));
        Assert.Equal(2, globals.Count);
    }

    [Fact]
    public async Task AnExceptionNamesItsLineWhenTheFileWasKept()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", Prelude + "\nthrow new System.InvalidOperationException(\"no\");");
        for (var run = 0; run < 2; run++)
        {
            var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
            Assert.Equal("BUILD.csx: 3: InvalidOperationException: no", error.Message);
        }
    }

    [Fact]
    public async Task CleanRemovesTheCompiledFiles()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"a\", new());");
        await module.LoadAsync();
        Assert.NotEmpty(Kept(module));
        var (status, output) = await module.TazelAsync("", "clean");
        Assert.True(status == 0, output);
        Assert.Empty(Kept(module));
    }

    [Fact]
    public void TheNameDependsOnTheTextThePathAndTheGlobals()
    {
        var environment = ScriptEnvironment.Default;
        var key = ScriptCache.Key("x", "BUILD.csx", typeof(object), environment);
        Assert.Equal(64, key.Length);
        Assert.Equal(key, ScriptCache.Key("x", "BUILD.csx", typeof(object), environment));
        Assert.NotEqual(key, ScriptCache.Key("y", "BUILD.csx", typeof(object), environment));
        Assert.NotEqual(key, ScriptCache.Key("x", "lib/BUILD.csx", typeof(object), environment));
        Assert.NotEqual(key, ScriptCache.Key("x", "BUILD.csx", typeof(string), environment));
        // Framing: moving text from one part to the next is another name.
        Assert.NotEqual(ScriptCache.Key("ab", "c", typeof(object), environment), ScriptCache.Key("a", "bc", typeof(object), environment));
        Assert.NotEqual(key, ScriptCache.Key("x", "BUILD.csx", typeof(object),
            ScriptEnvironment.With([typeof(ScriptCacheTests).Assembly], [])));
        Assert.NotEqual(key, ScriptCache.Key("x", "BUILD.csx", typeof(object), ScriptEnvironment.With([], ["System.Text"])));
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsNotKept()
    {
        using var module = new TemporaryModule();
        var blocked = Path.Combine(module.Root, "file");
        File.WriteAllText(blocked, "");
        var key = new string('a', 64);
        ScriptCache.TryWrite(Path.Combine(blocked, "scripts"), key, [1, 2, 3]);
        Assert.Null(ScriptCache.TryRead(Path.Combine(blocked, "scripts"), key));
        Assert.Null(ScriptCache.TryRead(module.Root, new string('b', 64)));
    }

    [Theory]
    [InlineData("k")]
    [InlineData("../../../../etc/passwd")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void OnlyAKeyNamesAFile(string key)
    {
        using var module = new TemporaryModule();
        Assert.Throws<ArgumentException>(() => ScriptCache.IsKept(module.Root, key));
        Assert.Throws<ArgumentException>(() => ScriptCache.TryWrite(module.Root, key, [1]));
    }

    /// <summary>The globals of the scripts above.</summary>
    public sealed class Counter
    {
        public int Count { get; set; }
    }
}
