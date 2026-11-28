// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>The commands that print, remove or refuse, through tazel's own entry, as a user types them.</summary>
public sealed class CommandTests
{
    private static TemporaryModule Empty() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile)
        .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\n");

    [Fact]
    public async Task VersionNamesTazelDotNetAndThePlatform()
    {
        using var module = Empty();
        var (status, output) = await module.TazelAsync(".", "version");
        Assert.Equal(0, status);
        Assert.Matches(@"^tazel \d+\.\d+\.\d+ \(\.NET \d+\.\d+\.\d+, [a-z0-9_-]+\)", output);
    }

    [Fact]
    public async Task HelpListsTheCommandsAndHelpOfOneSaysWhatItTakes()
    {
        using var module = Empty();
        var (status, output) = await module.TazelAsync(".", "help");
        Assert.Equal(0, status);
        Assert.Contains("cache", output, StringComparison.Ordinal);
        Assert.Contains("a key pair", (await module.TazelAsync(".", "help", "cache")).Output, StringComparison.Ordinal);
        (status, output) = await module.TazelAsync(".", "help", "cache", "--long");
        Assert.Equal(0, status);
        Assert.Contains("--algorithm ed25519|rsa-4096|ml-dsa", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeygenPrintsAPairOfTheAlgorithmAsked()
    {
        using var module = Empty();
        var (status, output) = await module.TazelAsync(".", "cache", "keygen", "--algorithm=rsa-4096");
        Assert.Equal(0, status);
        Assert.Contains("PublicKey:  rsa-4096:", output, StringComparison.Ordinal);
        Assert.Contains("SigningKey: rsa-4096:", output, StringComparison.Ordinal);
        Assert.Contains("keep the SigningKey secret", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDaemonCommandsSayThereIsNoDaemon()
    {
        using var module = Empty();
        var (status, output) = await module.TazelAsync(".", "dotnet", "start-daemon");
        Assert.Equal(1, status);
        Assert.Contains("error[E0502]", output, StringComparison.Ordinal);
        Assert.Contains("tazel dotnet start-daemon", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanRemovesTheOutputsAndExpungeAllOfTheState()
    {
        using var module = Empty();
        var state = Path.Combine(module.Root, ".tazel");
        module.Write(".tazel/out/x.o", "o").Write(".tazel/testlogs/t.log", "log").Write(".tazel/external/kept.txt", "kept");

        var (status, output) = await module.TazelAsync(".", "clean");
        Assert.Equal(0, status);
        Assert.Contains("removed the outputs", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(state, "out")));
        Assert.False(Directory.Exists(Path.Combine(state, "testlogs")));
        Assert.True(File.Exists(Path.Combine(state, "external", "kept.txt")));

        (status, output) = await module.TazelAsync(".", "clean", "--expunge");
        Assert.Equal(0, status);
        Assert.Contains("removed", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public async Task SettingsThisTazelDoesNotActOnAreSaid()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", """
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                using Progmasoft.Tazel.Dsl.Common;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
                execution = Execution.Define(new() {
                    Memory = 2L.Gibibytes(),
                    Remote = Remote.From("https://build.example.com", new())
                });
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\n");

        var (status, output) = await module.TazelAsync(".", "build");
        Assert.True(status == 0, output);
        Assert.Contains("names the remote execution https://build.example.com/", output, StringComparison.Ordinal);
        Assert.Contains("sets Execution.Memory", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomCommandRunsItsStepsUntilOneFails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the steps are written for sh");
        using var module = Empty().Write("tazel_custom.txt", """
            make-file: ("sh" "-c" "echo made > made.txt") s("echo more >> made.txt");
            fail-early: s("exit 3") ("sh" "-c" "echo never > never.txt");
            missing: ("no-such-program-of-tazel");
            """);

        var (status, output) = await module.TazelAsync(".", "custom", "make-file");
        Assert.True(status == 0, output);
        Assert.Equal("made\nmore\n", File.ReadAllText(Path.Combine(module.Root, "made.txt")));

        (status, _) = await module.TazelAsync(".", "custom", "fail-early");
        Assert.Equal(3, status);
        Assert.False(File.Exists(Path.Combine(module.Root, "never.txt")));

        (status, output) = await module.TazelAsync(".", "custom", "missing");
        Assert.Equal(1, status);
        Assert.Contains("the program 'no-such-program-of-tazel' is not found", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomCommandNeedsItsFileAndAtMostTheWordsItTakes()
    {
        using var module = Empty();
        var (status, output) = await module.TazelAsync(".", "custom", "anything");
        Assert.Equal(1, status);
        Assert.Contains("there is no tazel_custom.txt", output, StringComparison.Ordinal);

        module.Write("tazel_custom.txt", "hello: (\"sh\" \"-c\" \"true\");\n");
        var words = Enumerable.Range(0, Dsl.Custom.CustomCommandFiles.MaxArguments + 1).Select(i => $"w{i}").ToArray();
        (status, output) = await module.TazelAsync(".", ["custom", .. words]);
        Assert.NotEqual(0, status);
        Assert.Contains($"tazel custom takes at most {Dsl.Custom.CustomCommandFiles.MaxArguments} words", output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWantsExactlyOneProgram()
    {
        using var module = Empty().Write("BUILD.csx", """
            using Progmasoft.Tazel.Dsl.Build.Prelude;
            CC.StaticLibrary("lib", new() { Sources = ["lib.c"] });
            CC.Executable("one", new() { Sources = ["main.c"] });
            CC.Executable("two", new() { Sources = ["main.c"] });
            """).Write("lib.c", "int f(void) { return 0; }\n").Write("main.c", "int main(void) { return 0; }\n");

        var (status, output) = await module.TazelAsync(".", "run", "//:lib");
        Assert.Equal(1, status);
        Assert.True(output.Contains("an executable or a test", StringComparison.Ordinal) ||
            output.Contains("found no executable", StringComparison.Ordinal), output);

        (status, output) = await module.TazelAsync(".", "run", "//...");
        Assert.Equal(1, status);
        Assert.Contains("tazel run runs one program, and these are 2", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetsThatDoNotBuildForThePlatformAreLeftOutAndCounted()
    {
        var other = Host.Platform == Dsl.Common.Platform.LinuxAmd64 ? "MacOSArm64" : "LinuxAmd64";
        using var module = Empty().Write("BUILD.csx", $$"""
            using Progmasoft.Tazel.Dsl.Build.Prelude;
            using Progmasoft.Tazel.Dsl.Common;
            CC.StaticLibrary("elsewhere", new() { Sources = ["lib.c"], TargetConstraints = [Platform.{{other}}] });
            """).Write("lib.c", "int f(void) { return 0; }\n");

        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.Contains("left out 1 target that does not build for", output.Replace("do not", "does not", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }
}
