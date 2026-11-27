// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Runtime;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class DotnetSettingsTests
{
    private static DotnetSetting Setting(string entry) => DotnetSettings.Parse(entry, "test");

    // Another tazel process: settings applied there do not change the GC or the thread pool of the tests.
    private static async Task<(int Status, string Output)> TazelProcess(string directory, params string[] arguments)
    {
        var tazel = Path.Combine(AppContext.BaseDirectory, "tazel.dll");
        var start = new ProcessStartInfo("dotnet", ["exec", tazel, "--color", "never", .. arguments])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        output += await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, output);
    }

    [Theory]
    [InlineData("536870912")]
    [InlineData("536870912B")]
    [InlineData("524288K")]
    [InlineData("512M")]
    public void SizesScaleBySuffix(string size) =>
        Assert.Equal(536870912UL, Setting($"GCHeapHardLimit={size}").Value);

    [Fact]
    public void TheLargestSuffixIsATebibyte() => Assert.Equal(2UL << 40, Setting("GCHeapHardLimitSOH=2T").Value);

    [Theory]
    [InlineData("GCHeapHardLimit=99999999999T", "a size in bytes")]
    [InlineData("GCHeapHardLimit=-1", "a size in bytes")]
    [InlineData("GCHeapHardLimit=1.5M", "a size in bytes")]
    [InlineData("GCHeapHardLimit=512m", "a size in bytes")]
    [InlineData("GCHeapHardLimit=", "a size in bytes")]
    [InlineData("GCHeapHardLimitPercent=0", "a whole percentage from 1 to 100")]
    [InlineData("GCHeapHardLimitPercent=101", "a whole percentage from 1 to 100")]
    [InlineData("GCHeapHardLimitPercent=10K", "a whole percentage from 1 to 100")]
    [InlineData("ThreadPool.MinWorkerThreads=0", "a whole number of threads")]
    [InlineData("GCSettings.LatencyMode=NoGCRegion", "one of Batch, Interactive, LowLatency, SustainedLowLatency")]
    [InlineData("GCSettings.LatencyMode=interactive", "one of Batch")]
    [InlineData("gcServer=true", "is not a .NET setting Tazel takes")]
    [InlineData("GCHeapHardLimt=1", "did you mean GCHeapHardLimit?")]
    [InlineData("GCHeapHardLimit", "is not Name=Value")]
    public void RefusesWhatItDoesNotTake(string entry, string message)
    {
        var error = Assert.Throws<DotnetSettingsException>(() => Setting(entry));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitsAtTheFirstEqualsSign()
    {
        var error = Assert.Throws<DotnetSettingsException>(() => Setting("GCSettings.LatencyMode=Batch=1"));
        Assert.Contains("not 'Batch=1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLastOfANameWinsAcrossTheFileAndTheCommandLine()
    {
        var effective = DotnetSettings.Effective(
            [Setting("GCSettings.LatencyMode=Batch"), Setting("ThreadPool.MinWorkerThreads=4")],
            [Setting("GCSettings.LatencyMode=Interactive"), Setting("GCSettings.LatencyMode=SustainedLowLatency")]);
        Assert.Equal(GCLatencyMode.SustainedLowLatency, effective["GCSettings.LatencyMode"].Value);
        Assert.Equal(4, effective["ThreadPool.MinWorkerThreads"].Value);
    }

    [Theory]
    [InlineData("GCHeapHardLimit=1G GCHeapHardLimitPercent=20", "in bytes or as a percentage, not both")]
    [InlineData("GCHeapHardLimit=1G GCHeapHardLimitSOH=1G GCHeapHardLimitLOH=1G", "a limit for the whole heap and limits per object heap")]
    [InlineData("GCHeapHardLimitSOH=1G GCHeapHardLimitLOHPercent=5 GCHeapHardLimitSOHPercent=5", "in bytes or as percentages, not both")]
    [InlineData("GCHeapHardLimitSOH=1G", "need both GCHeapHardLimitSOH and GCHeapHardLimitLOH")]
    [InlineData("GCHeapHardLimitPOH=1G GCHeapHardLimitLOH=1G", "need both GCHeapHardLimitSOH and GCHeapHardLimitLOH")]
    [InlineData("GCHeapHardLimitSOHPercent=50 GCHeapHardLimitLOHPercent=40 GCHeapHardLimitPOHPercent=10", "and so is their sum")]
    [InlineData("GCHeapHardLimitSOHPercent=100 GCHeapHardLimitLOHPercent=1", "each percentage is below 100")]
    [InlineData("ThreadPool.MinWorkerThreads=50 ThreadPool.MaxWorkerThreads=40", "minimum 50 is above their maximum 40")]
    [InlineData("ThreadPool.MaxCompletionPortThreads=0", "a whole number of threads")]
    public void RefusesSettingsThatCannotHoldTogether(string entries, string message)
    {
        var error = Assert.Throws<DotnetSettingsException>(() =>
            DotnetSettings.Effective(entries.Split(' ').Select(Setting).ToList(), []));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TakesLimitsPerObjectHeapWithoutThePinnedOne()
    {
        var effective = DotnetSettings.Effective([Setting("GCHeapHardLimitSOH=1G"), Setting("GCHeapHardLimitLOH=256M")], []);
        Assert.Equal(2, effective.Count);
    }

    [Theory]
    [InlineData("--dotnet-settings=GCHeapHardLimit=512M")]
    [InlineData("--dotnet-settings:GCHeapHardLimit=512M")]
    [InlineData("--dotnet-settings|GCHeapHardLimit=512M")]
    public void TheCommandLineTakesEachSeparator(string option)
    {
        var command = CommandLine.Parse(["build", "//...", .. option.Split('|')]);
        Assert.Equal(536870912UL, Assert.Single(command.DotnetSettings).Value);
    }

    [Fact]
    public void TheCommandLineTakesListsRepeatedAndMixed()
    {
        var command = CommandLine.Parse(["build", "//...", "--dotnet-settings", "GCHeapHardLimit=512M,ThreadPool.MinWorkerThreads=8",
            "--dotnet-settings:GCSettings.LatencyMode=Interactive", "--dotnet-settings=GCHeapHardLimit=1G"]);
        Assert.Equal(["GCHeapHardLimit", "ThreadPool.MinWorkerThreads", "GCSettings.LatencyMode", "GCHeapHardLimit"],
            command.DotnetSettings.Select(setting => setting.Name));
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(["version", "--dotnet-settings=GCSettings.LatencyMode=Batch\\,Interactive"]));
        Assert.Contains("not 'Batch,Interactive'", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not a .NET setting", Assert.Throws<CommandLineException>(() =>
            CommandLine.Parse(["version", "--dotnet-settings", "Nope=1"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOptionsFileTakesAListInAnyQuotes()
    {
        var options = TazelOptionsFile.Parse("dotnet-settings: [\n    \"GCHeapHardLimit=512M\"\n    'GCSettings.LatencyMode=Interactive'\n    `ThreadPool.MinWorkerThreads=8`\n]",
            "tazel_options.txt", Path.Combine(Path.GetTempPath(), "tazel_options.txt"));
        Assert.Equal(["GCHeapHardLimit", "GCSettings.LatencyMode", "ThreadPool.MinWorkerThreads"], options.DotnetSettings.Select(s => s.Name));
        Assert.Empty(TazelOptionsFile.Parse("platform: linux-amd64;", "tazel_options.txt", Path.Combine(Path.GetTempPath(), "tazel_options.txt")).DotnetSettings);
    }

    [Theory]
    [InlineData("dotnet-settings: GCHeapHardLimit;", "'[' to open the list")]
    [InlineData("dotnet-settings: [\"GCHeapHardLimit=512M\", \"x\"];", "',' has no meaning here")]
    [InlineData("dotnet-settings: [\"GCHeapHardLimit=512M\"", "ends with ]")]
    [InlineData("dotnet-settings: [\"GCHeapHardLimit=lots\"];", "takes a size in bytes")]
    [InlineData("dotnet-settings: [\"x\"];\ndotnet-settings: [];", "is not Name=Value")]
    public void TheOptionsFileSaysWhatIsWrongWithTheList(string text, string message)
    {
        var error = Assert.Throws<TazelOptionsException>(() =>
            TazelOptionsFile.Parse(text, "tazel_options.txt", Path.Combine(Path.GetTempPath(), "tazel_options.txt")));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetReadsThisProcessAndASettingDoesNotOutliveItsCommand()
    {
        using var module = new TemporaryModule();
        var (status, output) = await TazelProcess(module.Root, "dotnet", "get", "GCHeapHardLimit", "--dotnet-settings", "GCHeapHardLimit=512M");
        Assert.True(status == 0, output);
        Assert.Contains("GCHeapHardLimit: 536870912", output, StringComparison.Ordinal);
        (status, output) = await TazelProcess(module.Root, "dotnet", "get", "GCHeapHardLimit");
        Assert.Equal(0, status);
        Assert.Contains("GCHeapHardLimit: default", output, StringComparison.Ordinal);
        Assert.Contains("available to the GC:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFileComesFirstAndTheCommandLineAfter()
    {
        using var module = new TemporaryModule().Write("tazel_options.txt",
            "dotnet-settings: [\"ThreadPool.MinWorkerThreads=6\" \"GCSettings.LatencyMode=Batch\"];");
        var (_, output) = await TazelProcess(module.Root, "dotnet", "get", "ThreadPool.MinWorkerThreads", "--dotnet-settings", "ThreadPool.MinWorkerThreads=7");
        Assert.Contains("ThreadPool.MinWorkerThreads: 7", output, StringComparison.Ordinal);
        (_, output) = await TazelProcess(module.Root, "dotnet", "get", "GCSettings.LatencyMode");
        Assert.Contains("GCSettings.LatencyMode: Batch", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeapLimitTheGCRefusesIsAnError()
    {
        using var module = new TemporaryModule();
        var (status, output) = await TazelProcess(module.Root, "dotnet", "get", "GCHeapHardLimit", "--dotnet-settings", "GCHeapHardLimit=1K");
        Assert.Equal(1, status);
        Assert.Contains("error[E0506]: the GC took no new heap limit", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GCHeapHardLimit")]
    [InlineData("GCHeapHardLimitPOHPercent")]
    [InlineData("GCSettings.LatencyMode")]
    [InlineData("ThreadPool.MinWorkerThreads")]
    [InlineData("ThreadPool.MaxCompletionPortThreads")]
    public void GetKnowsEveryName(string name) =>
        Assert.StartsWith($"{name}: ", DotnetSettings.Get(name)[0], StringComparison.Ordinal);

    [Fact]
    public void ThereAreThirteenSettings() => Assert.Equal(13, DotnetSettings.All.Count);

    [Fact]
    public void TheGlossaryHasItsFourteenTermsOffline()
    {
        string[] terms = ["GCHeapHardLimit", "RyuJIT", ".NET", "CLI", "CIL", "CLR", "C++/CLI", "C#", "WorkstationGC", "BackgroundGC",
            "daemon", "CTS", "VES", "ECMA-335"];
        Assert.Equal(terms, DotnetGlossary.Entries.Select(entry => entry.Term));
        Assert.All(DotnetGlossary.Entries, entry => Assert.EndsWith(".", entry.Text, StringComparison.Ordinal));
        var cli = DotnetGlossary.Find("CLI")!;
        Assert.Equal("Common Language Infrastructure", cli.Title);
        Assert.DoesNotContain("Command-Line Interface", cli.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no persistent daemon", DotnetGlossary.Find("daemon")!.Text, StringComparison.Ordinal);
        Assert.Equal("CLI", DotnetGlossary.Nearest("cli"));
        Assert.Equal("RyuJIT", DotnetGlossary.Nearest("RyuJT"));
    }

    [Fact]
    public async Task WhatIsAndGetGoThroughTheCommandLine()
    {
        using var module = new TemporaryModule();
        var (status, output) = await module.TazelAsync(".", "dotnet", "what-is", "C++/CLI");
        Assert.Equal(0, status);
        Assert.Contains("A C++ language extension", output.Replace("An extension of C++", "A C++ language extension", StringComparison.Ordinal), StringComparison.Ordinal);
        (status, output) = await module.TazelAsync(".", "dotnet", "what-is", "Cli");
        Assert.Equal(2, status);
        Assert.Contains("did you mean CLI?", output, StringComparison.Ordinal);
        Assert.IsType<DotnetGetCommand>(CommandLine.Parse(["dotnet", "get", "GCHeapHardLimit"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["dotnet", "get"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["dotnet", "what-is", "a", "b"]));
        Assert.Contains("is not a .NET setting", Assert.Throws<DotnetSettingsException>(() => DotnetSettings.Get("gcServer")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelpOfDotnetListsTheSettingsAndTheCommands()
    {
        var help = Help.Text("dotnet", true);
        Assert.Contains("tazel dotnet get <setting>", help, StringComparison.Ordinal);
        Assert.Contains("tazel dotnet what-is <term>", help, StringComparison.Ordinal);
        Assert.Contains("--dotnet-settings", help, StringComparison.Ordinal);
        Assert.Contains("This Tazel has no", help, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.DotnetSettingRefused, ErrorCodes.Find(ErrorCodes.DotnetSettingRefused)!.Code);
    }
}
