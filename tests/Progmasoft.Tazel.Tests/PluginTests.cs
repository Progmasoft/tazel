// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class StepTests
{
    private const string Build = """
        using System.IO;
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        using Progmasoft.Tazel.PluginApi;
        using Progmasoft.Tazel.PluginApi.Steps;
        Step.Declare("copy", new()
        {
            Inputs = ["in.txt"],
            Outputs = ["out.txt"],
            Key = "1",
            Run = async (step, cancel) =>
            {
                step.Log("copying");
                File.WriteAllText(Path.Combine(step.OutputDirectory, "out.txt"), File.ReadAllText(step.Inputs[0]).ToUpperInvariant());
                await System.Threading.Tasks.Task.CompletedTask;
            },
        });
        Step.Declare("lies", new() { Outputs = ["never.txt"], Tags = ["manual"], Run = (step, cancel) => System.Threading.Tasks.Task.CompletedTask });
        Step.Declare("fails", new() { Tags = ["manual"], Run = (step, cancel) => throw new PluginException("the step cannot go on") });
        """;

    [Fact]
    public async Task AStepRunsOnceUntilItsInputChanges()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Build).Write("in.txt", "hello");
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.Contains("Running //:copy\ncopying", output, StringComparison.Ordinal);
        var made = Directory.GetFiles(Path.Combine(module.Root, ".tazel"), "out.txt", SearchOption.AllDirectories).Single();
        Assert.Equal("HELLO", await File.ReadAllTextAsync(made, TestContext.Current.CancellationToken));

        (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("Running", output, StringComparison.Ordinal);

        module.Write("in.txt", "again");
        (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.Contains("Running //:copy", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//:lies", "the step said it writes")]
    [InlineData("//:fails", "the step cannot go on")]
    public async Task AFailedStepSaysWhy(string label, string message)
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Build).Write("in.txt", "hello");
        var (status, output) = await module.TazelAsync(".", "build", label);
        Assert.Equal(1, status);
        Assert.Contains($"FAILED Running {label}", output, StringComparison.Ordinal);
        Assert.Contains(message, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepNeedsItsWork()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", """
            using Progmasoft.Tazel.PluginApi.Steps;
            Step.Declare("idle", new());
            """);
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("BUILD.csx:2: the step 'idle' needs its Run", error.Message, StringComparison.Ordinal);
    }
}

public sealed class PluginLoadingTests
{
    [Fact]
    public async Task APluginOfTazelPortalIsRefused()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile + "\nplugins = Plugins.Define(Plugin.From(\"com.example\", \"x\", \"1.0.0\"));");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("this Tazel fetches no plugins; use LocalPlugin.From", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"group":"com.example","name":"x","version":"1.0.0","pluginApi":"9.0.0","entryAssembly":"X.dll"}""", "was built against Progmasoft.Tazel.PluginApi 9.0.0")]
    [InlineData("""{"group":"com.example","name":"x","version":"1.0","pluginApi":"0.1.0","entryAssembly":"X.dll"}""", "'1.0' is not a SemVer version")]
    [InlineData("""{"group":"com..example","name":"x","version":"1.0.0","pluginApi":"0.1.0","entryAssembly":"X.dll"}""", "is not names joined by dots")]
    [InlineData("""{"group":"com.example","name":"x","version":"1.0.0","pluginApi":"0.1.0","entryAssembly":"X.dll"}""", "names the entry assembly X.dll, which it does not hold")]
    [InlineData("""not json""", "cannot be read")]
    public async Task APluginArchiveIsChecked(string manifest, string message)
    {
        using var module = new TemporaryModule();
        module.Write("plugin/tazel-plugin.json", manifest);
        ModuleArchive.Write(Path.Combine(module.Root, "plugin"), "com.example.x-1.0.0",
            Path.Combine(module.Root, "plugins", "x.tazel.tar.xz"), ["tazel-plugin.json"]);
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + "\nplugins = Plugins.Define(LocalPlugin.From(\"plugins/x.tazel.tar.xz\"));");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchiveWithoutAManifestIsNotAPlugin()
    {
        using var module = new TemporaryModule();
        module.Write("plugin/readme.txt", "x");
        ModuleArchive.Write(Path.Combine(module.Root, "plugin"), "x", Path.Combine(module.Root, "x.tazel.tar.xz"), ["readme.txt"]);
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + "\nplugins = Plugins.Define(LocalPlugin.From(\"x.tazel.tar.xz\"));");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("has no x/tazel-plugin.json", error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The packages of the plugin API, the SDK and the template, packed and used as a plugin author uses them: dotnet new
/// tazel-plugin, dotnet pack, and a module that loads the archive.
/// </summary>
public sealed class PluginSdkTests
{
    [Fact]
    public async Task APluginFromTheTemplateIsPackedAndLoaded()
    {
        var repository = Repository();
        Assert.SkipWhen(repository is null || Environment.GetEnvironmentVariable("TAZEL_SKIP_SDK_TESTS") == "1",
            "the sources of the packages are not beside the tests");
        using var work = new TemporaryModule();
        var feed = Path.Combine(work.Root, "feed");
        string[] isolated = [$"NUGET_PACKAGES={Path.Combine(work.Root, "packages")}"];
        foreach (var project in new[] { "Dsl", "PluginApi", "PluginSdk", "PluginTemplates" })
        {
            await Run(work.Root, isolated, "pack", Path.Combine(repository!, "src", $"Progmasoft.Tazel.{project}"),
                "-c", "Release", "-o", feed);
        }

        work.Write("nuget.config", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        var hive = Path.Combine(work.Root, "hive");
        await Run(work.Root, isolated, "new", "install", Path.Combine(feed, "Progmasoft.Tazel.PluginTemplates.0.1.0.nupkg"),
            "--debug:custom-hive", hive);
        await Run(work.Root, isolated, "new", "tazel-plugin", "-n", "Greeter", "--group", "com.example.tools",
            "--pluginName", "greeter", "--debug:custom-hive", hive);
        await Run(Path.Combine(work.Root, "Greeter"), isolated, "pack", "-c", "Release", "-o", "out");

        var archive = Path.Combine(work.Root, "Greeter", "out", "com.example.tools.greeter-0.1.0.tazel.tar.xz");
        Assert.Equal(ReproducibleArchive.SumsLine(archive),
            await File.ReadAllTextAsync(archive + ".sha256sums", TestContext.Current.CancellationToken));
        Directory.CreateDirectory(Path.Combine(work.Root, "module", "plugins"));
        File.Copy(archive, Path.Combine(work.Root, "module", "plugins", "greeter.tazel.tar.xz"));
        work.Write("module/MODULE.csx", TemporaryModule.ModuleFile + "\nplugins = Plugins.Define(LocalPlugin.From(\"plugins/greeter.tazel.tar.xz\"));")
            .Write("module/BUILD.csx", "Greetings.File(\"hello\", \"Hello from a plugin\");");
        var (status, output) = await work.TazelAsync("module", "build", "//...");
        Assert.True(status == 0, output);
        var greeting = Directory.GetFiles(Path.Combine(work.Root, "module", ".tazel"), "greeting.txt", SearchOption.AllDirectories)
            .Single();
        Assert.Equal("Hello from a plugin\n", await File.ReadAllTextAsync(greeting, TestContext.Current.CancellationToken));
    }

    private static string? Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tazel.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    private static async Task Run(string directory, string[] environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var entry in environment)
        {
            var equals = entry.IndexOf('=', StringComparison.Ordinal);
            start.Environment[entry[..equals]] = entry[(equals + 1)..];
        }

        // The SDK of the test run would otherwise pass its own build to the dotnet it starts.
        start.Environment.Remove("MSBuildExtensionsPath");
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment.Remove("MSBUILD_EXE_PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"dotnet {string.Join(' ', arguments)} failed:\n{await output}{await errors}");
    }
}
