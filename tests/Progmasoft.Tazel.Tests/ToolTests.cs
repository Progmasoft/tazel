// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class ToolTests
{
    // Another tazel process with its own PATH and TAZEL_TOOL_EXECUTABLE.
    private static async Task<(int Status, string Output)> Tazel(string directory, IDictionary<string, string?> environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet", ["exec", Path.Combine(AppContext.BaseDirectory, "tazel.dll"), "--color", "never", .. arguments])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Remove(Tools.ExecutableVariable);
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        output += await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output);
    }

    // A tool that prints each argument in brackets and leaves with status 7.
    private static string Tool(string directory, string name, bool executable = true)
    {
        var file = Path.Combine(directory, name);
        File.WriteAllText(file, "#!/bin/sh\nfor a in \"$@\"; do printf '[%s]' \"$a\"; done; echo; exit 7\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, executable ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return file;
    }

    private static string WithPath(params string[] directories) =>
        string.Join(Path.PathSeparator, [.. directories, .. (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)]);

    [Theory]
    [InlineData("fmt")]
    [InlineData("clang-format")]
    [InlineData("a.b_c-9")]
    public void TakesPlainNames(string name) => Assert.True(Tools.IsName(name));

    [Theory]
    [InlineData("")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a..b")]
    [InlineData("-x")]
    [InlineData(".x")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a$b")]
    [InlineData("a&b")]
    public void RefusesNamesThatAreNotPlain(string name) => Assert.False(Tools.IsName(name));

    [Theory]
    [InlineData("plain", true)]
    [InlineData("with space & (parentheses) | ^ < > !", true)]
    [InlineData("", true)]
    [InlineData("a\"b", false)]
    [InlineData("100%", false)]
    [InlineData("a\nb", false)]
    public void KnowsWhatABatchFileCanBeGiven(string argument, bool safe) => Assert.Equal(safe, Tools.IsBatchSafe(argument));

    [Fact]
    public void ANativeToolGetsItsArgumentsAsWritten() =>
        Assert.Equal(["/bin/tool", "", "--", "--help", "a b", "%x%"], Tools.Command("/bin/tool", ["", "--", "--help", "a b", "%x%"]));

    [Fact]
    public void TheCommandLineGivesEverythingAfterTheNameToTheTool()
    {
        var command = Assert.IsType<ToolLaunchCommand>(CommandLine.Parse(["tool", "launch", "fmt", "--check", "--", "-h", "--color", ""]));
        Assert.Equal("fmt", command.Name);
        Assert.Equal(["--check", "--", "-h", "--color", ""], command.Arguments);
        Assert.Equal("out", Assert.IsType<ToolWrapperCommand>(CommandLine.Parse(["tool", "create-wrapper", "fmt", "-o", "out"])).OutputDirectory);
        Assert.Equal("out", Assert.IsType<ToolWrapperCommand>(CommandLine.Parse(["tool", "create-wrapper", "fmt", "--output-dir=out"])).OutputDirectory);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tool", "launch"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tool", "create-wrapper", "a", "b"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["tool", "lunch", "x"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["build", "-o", "x"]));
    }

    [Fact]
    public async Task LaunchFindsTheToolOnPathAndPassesItsStatusOn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tools are shell scripts");
        using var module = new TemporaryModule().Write("bin/keep", "").Write("work/keep", "");
        Tool(Path.Combine(module.Root, "bin"), "tazel-show");
        var (status, output) = await Tazel(Path.Combine(module.Root, "work"),
            new Dictionary<string, string?> { ["PATH"] = WithPath(Path.Combine(module.Root, "bin")) },
            "tool", "launch", "show", "", "--", "--version", "a b");
        Assert.Equal(7, status);
        Assert.Contains("[][--][--version][a b]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSearchesNeitherTheWorkingDirectoryNorDotNorEmptyEntries()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tools are shell scripts");
        using var module = new TemporaryModule().Write("work/keep", "");
        var work = Path.Combine(module.Root, "work");
        Tool(work, "tazel-here");
        var path = string.Join(Path.PathSeparator, ".", "", work, "work", "/usr/bin", "/bin");
        var (status, output) = await Tazel(work, new Dictionary<string, string?> { ["PATH"] = path }, "tool", "launch", "here");
        Assert.Equal(1, status);
        Assert.Contains("error[E0507]: no tazel-here on PATH", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchSkipsAFileThisUserMayNotRun()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");
        using var module = new TemporaryModule().Write("a/keep", "").Write("b/keep", "");
        Tool(Path.Combine(module.Root, "a"), "tazel-two", executable: false);
        Tool(Path.Combine(module.Root, "b"), "tazel-two");
        var (status, output) = await Tazel(module.Root,
            new Dictionary<string, string?> { ["PATH"] = WithPath(Path.Combine(module.Root, "a"), Path.Combine(module.Root, "b")) },
            "tool", "launch", "two", "x");
        Assert.Equal(7, status);
        Assert.Contains("[x]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExecutableVariableWinsAndHasNoFallback()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tools are shell scripts");
        using var module = new TemporaryModule().Write("bin/keep", "");
        Tool(Path.Combine(module.Root, "bin"), "tazel-x");
        Tool(module.Root, "mine");
        Tool(module.Root, "locked", executable: false);
        var path = WithPath(Path.Combine(module.Root, "bin"));
        var (status, output) = await Tazel(module.Root, new Dictionary<string, string?> { ["PATH"] = path, [Tools.ExecutableVariable] = "./mine" },
            "tool", "launch", "x", "chosen");
        Assert.Equal(7, status);
        Assert.Contains("[chosen]", output, StringComparison.Ordinal);
        foreach (var (value, message) in new[] { ("", "is set but empty"), ("./nowhere", "is not a file"), ("./locked", "is not a program this user may run"), ("bin", "is not a file") })
        {
            (status, output) = await Tazel(module.Root, new Dictionary<string, string?> { ["PATH"] = path, [Tools.ExecutableVariable] = value },
                "tool", "launch", "x");
            Assert.Equal(1, status);
            Assert.Contains(message, output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task LaunchRefusesToRunWhenASandboxIsAskedFor()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the tools are shell scripts");
        using var module = new TemporaryModule().Write("tazel_options.txt", "sandboxing-with-microvm: true;");
        Tool(module.Root, "mine");
        var (status, output) = await Tazel(module.Root, new Dictionary<string, string?> { [Tools.ExecutableVariable] = "./mine" },
            "tool", "launch", "x");
        Assert.Equal(1, status);
        Assert.Contains("builds, tests, plugins or tools, so nothing ran", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateWrapperWritesARunnableWrapperOnceAndNeverOverwrites()
    {
        using var module = new TemporaryModule();
        var file = Tools.CreateWrapper("fmt", module.Root, windows: false);
        Assert.Equal(Path.Combine(module.Root, "tazel-fmt"), file);
        Assert.Equal("#!/bin/sh\nexec fmt \"$@\"\n", File.ReadAllText(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(Tools.IsRunnable(file));
        }

        Assert.Contains("already there", Assert.Throws<ToolException>(() => Tools.CreateWrapper("fmt", module.Root, windows: false)).Message,
            StringComparison.Ordinal);
        Assert.Equal("#!/bin/sh\nexec fmt \"$@\"\n", File.ReadAllText(file));
        var batch = Tools.CreateWrapper("fmt", module.Root, windows: true);
        Assert.Equal("@echo off\r\nfmt %*\r\nexit /b %errorlevel%\r\n", File.ReadAllText(batch));
        Assert.Throws<ToolException>(() => Tools.CreateWrapper("fmt", Path.Combine(module.Root, "missing"), windows: false));
        Assert.Throws<ToolException>(() => Tools.CreateWrapper("../fmt", module.Root, windows: false));
    }

    [Fact]
    public async Task AWrapperIsFoundAndRunsItsProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix wrapper");
        using var module = new TemporaryModule().Write("bin/keep", "").Write("work/keep", "");
        var bin = Path.Combine(module.Root, "bin");
        Tool(bin, "show");
        var (status, output) = await Tazel(Path.Combine(module.Root, "work"), new Dictionary<string, string?>(), "tool", "create-wrapper", "show", "-o", bin);
        Assert.True(status == 0, output);
        (status, output) = await Tazel(Path.Combine(module.Root, "work"), new Dictionary<string, string?> { ["PATH"] = WithPath(bin) },
            "tool", "launch", "show", "x y", "");
        Assert.Equal(7, status);
        Assert.Contains("[x y][]", output, StringComparison.Ordinal);
    }
}
