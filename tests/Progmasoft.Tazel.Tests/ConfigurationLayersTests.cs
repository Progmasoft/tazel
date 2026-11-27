// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Dsl.Options;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class ProjectKeyTests
{
    // Made with Agenceland's projectKey (extensions/agenceland/src/core/paths.ts), so both give the same key.
    [Theory]
    [InlineData(false, "/home/ada/Projects/example", "-home-ada-Projects--example-4438d2120e07e07e")]
    [InlineData(false, "/example", "-example-99d0533064c83d04")]
    [InlineData(true, "C:\\Users\\Ada\\Projects\\example", "C--Users-Ada-Projects-example-f02e6912f5bd8b46")]
    [InlineData(false, "/a/b-c/d", "-a-b-c--d-f454c3d357078a1d")]
    [InlineData(false, "/a-b/c/d", "-a-b-c--d-80f3776f287feca0")]
    [InlineData(false, "/home/ada/Projekte/Über", "-home-ada-Projekte---ber-277c6621570ad431")]
    [InlineData(false, "/home/ada/😀/x", "-home-ada-----x-a14b49261c6fbba2")]
    public void IsTheKeyOfAgenceland(bool windows, string path, string key) => Assert.Equal(key, ProjectKey.Of(path, windows));

    [Fact]
    public void ALongPathKeepsTheEndOfItsReadablePart()
    {
        var deep = $"/home/ada/{string.Join('/', Enumerable.Range(0, 40).Select(i => $"directory{i}"))}/example";
        var key = ProjectKey.Of(deep, windows: false);
        Assert.Equal("directory24-directory25-directory26-directory27-directory28-directory29-directory30-directory31-directory32-" +
            "directory33-directory34-directory35-directory36-directory37-directory38-directory39--example-c7091a666fbcdce7", key);
        Assert.Equal(ProjectKey.MaxReadableLength + 17, key.Length);
    }
}

[Collection(nameof(TazelHomeCollection))]
public sealed class ConfigurationLayersTests
{
    private sealed class Home : IDisposable
    {
        private readonly string? _was = Environment.GetEnvironmentVariable(TazelHome.Variable);

        public Home(string value) => Environment.SetEnvironmentVariable(TazelHome.Variable, value);

        public void Dispose() => Environment.SetEnvironmentVariable(TazelHome.Variable, _was);
    }

    // A home, a directory above a module, the module, and a directory in it to run in.
    private static (TemporaryModule Tree, Home Home, string Here) Tree()
    {
        var tree = new TemporaryModule()
            .Write("outer/mod/MODULE.csx", TemporaryModule.ModuleFile)
            .Write("outer/mod/sub/keep", "")
            .Write("home/User/keep", "");
        return (tree, new Home(Path.Combine(tree.Root, "home")), Path.Combine(tree.Root, "outer", "mod", "sub"));
    }

    private static string ProjectFile(string here, string name) => ConfigurationLayers.FileOf(LayerKind.Project, name, here, create: true);

    [Fact]
    public void EachOptionComesFromTheHighestLayerThatSetsIt()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_options.txt", "microvm-cpus: 1; microvm-memory: 300; microvm-network: true; platform: windows-x64;")
                .Write("outer/tazel_options.txt", "microvm-cpus: 2; microvm-memory: 400;")
                .Write("outer/mod/tazel_options.txt", "microvm-cpus: 3;");
            File.WriteAllText(ProjectFile(here, "tazel_options.txt"), "microvm-cpus: 4; microvm-network: false;");
            File.WriteAllText(Path.Combine(here, "tazel_options.txt"), "microvm-cpus: 5;");
            var layered = TazelOptionsFile.Layered(here);
            Assert.Equal(5, layered.Options.MicroVMCpus.Amount);
            Assert.Equal(400, layered.Options.MicroVMMemory.Amount);
            // An explicit false is a value: the project's beats the user's true.
            Assert.False(layered.Options.MicroVMNetwork);
            Assert.Equal(LayerKind.Project, layered.Origins["microvm-network"].Kind);
            Assert.Equal(Platform.WindowsX64, layered.Options.Platform);
            Assert.Equal([LayerKind.User, LayerKind.Parent, LayerKind.Parent, LayerKind.Project, LayerKind.Local], layered.Layers.Select(l => l.Kind));
            // The command line is above every layer.
            Assert.Equal(Platform.LinuxAarch64, TazelOptionsFile.Parse("", "x", Path.Combine(here, "x"), Platform.LinuxAarch64).Platform ?? Platform.LinuxAarch64);
        }
    }

    [Fact]
    public void TheProjectOutranksTheModuleRootWhenItIsAbove()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("outer/mod/tazel_options.txt", "microvm-cpus: 3;");
            File.WriteAllText(ProjectFile(here, "tazel_options.txt"), "microvm-cpus: 4;");
            Assert.Equal(4, TazelOptionsFile.Effective(here).Options.MicroVMCpus.Amount);
            // Run in the module root, its own file is the highest layer.
            Assert.Equal(3, TazelOptionsFile.Effective(Path.Combine(tree.Root, "outer", "mod")).Options.MicroVMCpus.Amount);
        }
    }

    [Fact]
    public void ASandboxAskedForInAnyLayerStays()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_options.txt", "sandboxing-with-microvm: true;");
            File.WriteAllText(Path.Combine(here, "tazel_options.txt"), "sandboxing-with-microvm: false;");
            var (options, file) = TazelOptionsFile.Effective(here);
            Assert.True(options.SandboxingWithMicroVM);
            Assert.EndsWith(Path.Combine("User", "tazel_options.txt"), file, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OutsideAModuleThereIsNoProjectLayer()
    {
        using var tree = new TemporaryModule().Write("plain/keep", "").Write("home/User/keep", "");
        using var home = new Home(Path.Combine(tree.Root, "home"));
        Assert.DoesNotContain(ConfigurationLayers.For("tazel_options.txt", Path.Combine(tree.Root, "plain")), layer => layer.Kind == LayerKind.Project);
        Assert.Throws<ConfigurationHomeException>(() => ConfigurationLayers.FileOf(LayerKind.Project, "x", Path.Combine(tree.Root, "plain")));
    }

    [Fact]
    public void AProjectDirectoryOfAnotherRootIsRefused()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            var directory = Path.GetDirectoryName(ProjectFile(here, "x"))!;
            File.WriteAllText(Path.Combine(directory, ProjectKey.IdentityFile), "/somewhere/else\n");
            var error = Assert.Throws<ConfigurationHomeException>(() => ConfigurationLayers.For("tazel_options.txt", here));
            Assert.Contains("belongs to /somewhere/else", error.Message, StringComparison.Ordinal);
            File.Delete(Path.Combine(directory, ProjectKey.IdentityFile));
            Assert.Contains("has no identity.txt", Assert.Throws<ConfigurationHomeException>(() => ConfigurationLayers.For("tazel_options.txt", here)).Message,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryDirectoryOfAModuleSharesItsProject()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            Assert.Equal(ProjectFile(here, "x"), ProjectFile(Path.Combine(tree.Root, "outer", "mod"), "x"));
        }
    }

    [Fact]
    public void ADotnetSettingIsMergedNameByName()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_options.txt", "dotnet-settings: [\"ThreadPool.MinWorkerThreads=4\" \"GCSettings.LatencyMode=Batch\"];");
            File.WriteAllText(Path.Combine(here, "tazel_options.txt"), "dotnet-settings: [\"ThreadPool.MinWorkerThreads=6\"];");
            var effective = Runtime.DotnetSettings.Effective(TazelOptionsFile.Effective(here).Options.DotnetSettings, []);
            Assert.Equal(6, effective["ThreadPool.MinWorkerThreads"].Value);
            Assert.Equal(System.Runtime.GCLatencyMode.Batch, effective["GCSettings.LatencyMode"].Value);
        }
    }

    [Fact]
    public async Task CustomCommandsAreMergedNameByName()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            // The commands write a file: their own output goes to the terminal, not to the test.
            tree.Write("home/User/tazel_custom.txt", "hello: (\"sh\" \"-c\" \"echo user > said\");\nonly-user: (\"sh\" \"-c\" \"echo only > said\");")
                .Write("outer/mod/sub/tazel_custom.txt", "hello: (\"sh\" \"-c\" \"echo here > said\");");
            Assert.SkipWhen(OperatingSystem.IsWindows(), "the commands use sh");
            var said = Path.Combine(here, "said");
            var (status, output) = await tree.TazelAsync("outer/mod/sub", "custom", "hello");
            Assert.True(status == 0, output);
            Assert.Equal("here\n", File.ReadAllText(said));
            (status, output) = await tree.TazelAsync("outer/mod/sub", "custom", "only-user");
            Assert.True(status == 0, output);
            Assert.Equal("only\n", File.ReadAllText(said));
        }
    }

    [Fact]
    public void TheMainModuleTakesTheLayersOfEnvsAndADependencyItsOwnAlone()
    {
        var (tree, home, _) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_envs.txt", "who: 'user'; color: 'blue';")
                .Write("outer/mod/tazel_envs.txt", "who: 'module';");
            var root = Path.Combine(tree.Root, "outer", "mod");
            var main = ModuleEnvironment.Load(root, "tazel_envs.txt", null, inherit: true)!;
            Assert.Equal("module", main.Get("who"));
            Assert.Equal("blue", main.Get("color"));
            var dependency = ModuleEnvironment.Load(root, "@dep//tazel_envs.txt", null, inherit: false)!;
            Assert.Null(dependency.Get("color"));
        }
    }

    [Fact]
    public async Task GetSaysWhereAValueCameFrom()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_options.txt", "microvm-cpus: 3;");
            var (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "get", "microvm-cpus");
            Assert.Equal(0, status);
            Assert.Contains("microvm-cpus: 3 (", output, StringComparison.Ordinal);
            Assert.Contains("the user's", output, StringComparison.Ordinal);
            (_, output) = await tree.TazelAsync("outer/mod/sub", "options", "get", "microvm-memory");
            Assert.Contains("microvm-memory: auto (default)", output, StringComparison.Ordinal);
            (_, output) = await tree.TazelAsync("outer/mod/sub", "options", "get", "microvm-cpus", "--local");
            Assert.Contains("not set", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "what-is", "microvm-cpu");
            Assert.Equal(2, status);
            Assert.Contains("did you mean microvm-cpus?", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpdateEditsOneFileAndKeepsTheRest()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            var file = Path.Combine(here, "tazel_options.txt");
            File.WriteAllText(file, "# kept\nmicrovm-cpus: 2;   # also kept\nmicrovm-network: true");
            var (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "8");
            Assert.True(status == 0, output);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "platform", "linux-aarch64");
            Assert.True(status == 0, output);
            Assert.Equal("# kept\nmicrovm-cpus: 8;   # also kept\nmicrovm-network: true;\nplatform: linux-aarch64;\n", File.ReadAllText(file));
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "lots");
            Assert.Equal(1, status);
            Assert.Contains("microvm-cpus: 8;", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpdateNeedsCreateForANewFileAndRefusesWhatItCannotEditSafely()
    {
        var (tree, home, here) = Tree();
        using (tree)
        using (home)
        {
            var (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "8", "--user");
            Assert.Equal(2, status);
            Assert.Contains("add --create", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "8", "--project", "--create");
            Assert.True(status == 0, output);
            Assert.Equal("microvm-cpus: 8;\n", File.ReadAllText(ProjectFile(here, "tazel_options.txt")));

            tree.Write("outer/mod/sub/inc.options.txt", "microvm-memory: 512");
            File.WriteAllText(Path.Combine(here, "tazel_options.txt"), ".include \"inc.options.txt\"\n.if true\nmicrovm-cpus: 2;\n.end\n");
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "4");
            Assert.Contains("inside .if", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-memory", "1024");
            Assert.Contains("in a file that tazel_options.txt includes", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "update", "microvm-cpus", "4", "--local");
            Assert.Contains("--local is for reading", output, StringComparison.Ordinal);
            Assert.Equal(2, status);
        }
    }

    [Fact]
    public async Task ValidateChecksEveryLayerOrOne()
    {
        var (tree, home, _) = Tree();
        using (tree)
        using (home)
        {
            tree.Write("home/User/tazel_options.txt", "microvm-cpus: 3;").Write("outer/tazel_envs.txt", "x: 'y';")
                .Write("outer/mod/sub/tazel_custom.txt", "hi: (\"echo\");");
            var (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "validate");
            Assert.True(status == 0, output);
            Assert.Contains("(the user's)", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "validate", "--local");
            Assert.Contains("no tazel_options.txt holds here in that layer", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "envs", "validate");
            Assert.True(status == 0, output);
            Assert.Contains("(a directory above)", output, StringComparison.Ordinal);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "custom-cmds", "validate", "--local");
            Assert.True(status == 0, output);
            Assert.Contains("valid: tazel_custom.txt", output, StringComparison.Ordinal);
            tree.Write("outer/mod/sub/tazel_custom.txt", "hi (\"echo\");");
            (status, _) = await tree.TazelAsync("outer/mod/sub", "custom-cmds", "validate");
            Assert.Equal(1, status);
            (status, output) = await tree.TazelAsync("outer/mod/sub", "options", "validate", "--user", "--local");
            Assert.Equal(2, status);
            Assert.Contains("name two layers", output, StringComparison.Ordinal);
        }
    }
}
