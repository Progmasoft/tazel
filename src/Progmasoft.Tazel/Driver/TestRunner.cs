// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>The files a program reads, in the directory it runs in, as Bazel's runfiles.</summary>
internal static class Runfiles
{
    /// <summary>
    /// Makes the runfiles directory of <paramref name="target"/> beside its program, with each file of RuntimeData at its
    /// path in the module, and returns it.
    /// </summary>
    public static string Prepare(ProjectTarget target, TargetProducts products, OutputLayout layout)
    {
        var directory = layout.Full(products.Executable! + ".runfiles");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        var data = ((IRunSettings)target.Settings).RuntimeData;
        foreach (var file in FileSets.Resolve(data, target.PackageDirectory, target.Module.Root, target.Place))
        {
            var destination = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(target.Module.Root, file), destination);
        }

        return directory;
    }

    /// <summary>The environment variable that finds shared libraries on Windows, with the directories prepended.</summary>
    public static void AddLibraryPath(Dictionary<string, string> environment, TargetProducts products, OutputLayout layout)
    {
        if (!OperatingSystem.IsWindows() || products.SharedLibraryDirectories.Count == 0)
        {
            return;
        }

        var directories = products.SharedLibraryDirectories.Select(layout.Full);
        environment.TryGetValue("PATH", out var path);
        environment["PATH"] = string.Join(Path.PathSeparator, directories.Append(path ?? ""));
    }
}

/// <summary>Runs tests: each with a small environment, its own temporary directory, a timeout, and retries when flaky.</summary>
internal sealed class TestRunner(OutputLayout layout, int jobs, Reporter reporter, bool coverage = false)
{
    /// <summary>The timeout of a size, as in Bazel.</summary>
    public static TimeSpan TimeoutOf(TestSize size) => size switch
    {
        TestSize.Small => TimeSpan.FromMinutes(1),
        TestSize.Medium => TimeSpan.FromMinutes(5),
        TestSize.Large => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromMinutes(60),
    };

    /// <summary>Runs each test that was built and reports every one.</summary>
    public async Task<IReadOnlyList<TestOutcome>> RunAsync(
        IReadOnlyList<(ProjectTarget Target, TargetProducts Products, bool Built)> tests, CancellationToken cancel)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, jobs));
        var outcomes = await Task.WhenAll(tests.Select(async test =>
        {
            if (!test.Built)
            {
                return new TestOutcome(test.Target.Label.ToString(), TestStatus.NotBuilt, TimeSpan.Zero, "");
            }

            var exclusive = test.Target.Settings.Tags.Contains("exclusive", StringComparer.Ordinal);
            var held = exclusive ? Math.Max(1, jobs) : 1;
            for (var i = 0; i < held; i++)
            {
                await slots.WaitAsync(cancel).ConfigureAwait(false);
            }

            try
            {
                return await RunOneAsync(test.Target, test.Products, cancel).ConfigureAwait(false);
            }
            finally
            {
                slots.Release(held);
            }
        })).ConfigureAwait(false);
        foreach (var outcome in outcomes.Where(o => o.Status is TestStatus.Failed or TestStatus.TimedOut))
        {
            reporter.TestLog(outcome.Label, outcome.Log);
        }

        return outcomes.OrderBy(o => o.Label, StringComparer.Ordinal).ToList();
    }

    /// <summary>Where a test's runs go: its runfiles, its log directory, and the file that remembers its pass.</summary>
    private sealed record TestRun(
        ProjectTarget Target, ITestSettings Settings, TargetProducts Products, string Runfiles, string Logs, string Passed, string? Key)
    {
        public string Label => Target.Label.ToString();

        public string LogFile => Path.Combine(Logs, "test.log");
    }

    private async Task<TestOutcome> RunOneAsync(ProjectTarget target, TargetProducts products, CancellationToken cancel)
    {
        var settings = (ITestSettings)target.Settings;
        var runfiles = Runfiles.Prepare(target, products, layout);
        var name = Sanitize(target.Label.ToString());
        // Coverage needs the run itself, and a test tagged no-cache runs every time.
        var key = coverage || target.Settings.Tags.Contains("no-cache", StringComparer.Ordinal)
            ? null
            : CacheKey(target, settings, products, runfiles);
        var run = new TestRun(target, settings, products, runfiles, layout.Full($"{layout.TestLogs}/{name}"),
            Path.Combine(layout.Full(layout.TestLogs), ".passed", name.Replace('/', '_')), key);
        if (await CachedAsync(run, cancel).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        if (File.Exists(run.Passed))
        {
            File.Delete(run.Passed);
        }

        if (Directory.Exists(run.Logs))
        {
            Directory.Delete(run.Logs, recursive: true);
        }

        Directory.CreateDirectory(run.Logs);
        var attempts = settings.Flaky ? 3 : 1;
        var watch = Stopwatch.StartNew();
        var log = new StringBuilder();
        var timedOut = false;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var (failed, outOfTime) = await AttemptAsync(run, log, cancel).ConfigureAwait(false);
            timedOut = outOfTime;
            await File.WriteAllTextAsync(run.LogFile, log.ToString(), cancel).ConfigureAwait(false);
            if (!failed)
            {
                return await PassedAsync(run, attempt == 1 ? TestStatus.Passed : TestStatus.Flaky, watch.Elapsed, log, cancel)
                    .ConfigureAwait(false);
            }

            if (attempt < attempts)
            {
                log.Append(CultureInfo.InvariantCulture, $"--- attempt {attempt} failed; the test is flaky, so it runs again ---\n");
            }
        }

        return new TestOutcome(run.Label, timedOut ? TestStatus.TimedOut : TestStatus.Failed, watch.Elapsed, log.ToString());
    }

    /// <summary>The outcome of a test that passed with the same key, or null when it has to run.</summary>
    private static async Task<TestOutcome?> CachedAsync(TestRun run, CancellationToken cancel)
    {
        if (run.Key is null || !File.Exists(run.Passed) || !File.Exists(run.LogFile) ||
            await File.ReadAllTextAsync(run.Passed, cancel).ConfigureAwait(false) != run.Key)
        {
            return null;
        }

        var previous = await File.ReadAllTextAsync(run.LogFile, cancel).ConfigureAwait(false);
        return new TestOutcome(run.Label, TestStatus.Cached, TimeSpan.Zero, previous);
    }

    /// <summary>A test that passed; a first-time pass is remembered, a flaky one is not, so the next run shows it.</summary>
    private static async Task<TestOutcome> PassedAsync(
        TestRun run, TestStatus status, TimeSpan elapsed, StringBuilder log, CancellationToken cancel)
    {
        if (run.Key is not null && status == TestStatus.Passed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(run.Passed)!);
            await File.WriteAllTextAsync(run.Passed, run.Key, cancel).ConfigureAwait(false);
        }

        return new TestOutcome(run.Label, status, elapsed, log.ToString());
    }

    /// <summary>One attempt: every shard of the test, each with its own environment.</summary>
    private async Task<(bool Failed, bool TimedOut)> AttemptAsync(TestRun run, StringBuilder log, CancellationToken cancel)
    {
        var settings = run.Settings;
        var timeout = settings.Timeout ?? TimeoutOf(settings.TestSize);
        var failed = false;
        var timedOut = false;
        for (var shard = 0; shard < settings.ShardCount; shard++)
        {
            var environment = Environment(run.Target, settings, run.Logs, shard, run.Runfiles);
            if (coverage)
            {
                // Each process of the test writes its own profile; tazel coverage merges them.
                environment["LLVM_PROFILE_FILE"] = Path.Combine(run.Logs, $"coverage-{shard}-%p.profraw");
            }

            Runfiles.AddLibraryPath(environment, run.Products, layout);
            List<string> command = [layout.Full(run.Products.Executable!), .. settings.Arguments];
            var result = await Processes.RunAsync(command, run.Runfiles, environment, timeout, cancel).ConfigureAwait(false);
            if (settings.ShardCount > 1)
            {
                log.Append(CultureInfo.InvariantCulture, $"--- shard {shard + 1} of {settings.ShardCount} ---\n");
            }

            log.Append(result.Output);
            if (result.TimedOut)
            {
                log.Append(CultureInfo.InvariantCulture, $"the test ran out of its {timeout.TotalSeconds:0} s\n");
                timedOut = true;
            }

            failed |= result.ExitCode != 0;
        }

        return (failed, timedOut);
    }

    /// <summary>
    /// What a run of the test depends on: its program and the shared libraries beside it by content, its data, its
    /// arguments, its environment less the parts that change every run, its shards, timeout and retries, and where its
    /// outputs are, which names the platform and mode.
    /// </summary>
    private string CacheKey(ProjectTarget target, ITestSettings settings, TargetProducts products, string runfiles)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"out {layout.OutRoot}\nprogram {Archive.Sha256Sums.Sha256(layout.Full(products.Executable!))}\n");
        foreach (var directory in products.SharedLibraryDirectories.Select(layout.Full).Where(Directory.Exists).Order(StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                text.Append(CultureInfo.InvariantCulture, $"library {Path.GetFileName(file)} {Archive.Sha256Sums.Sha256(file)}\n");
            }
        }

        foreach (var file in Directory.EnumerateFiles(runfiles, "*", SearchOption.AllDirectories)
                     .Select(file => Path.GetRelativePath(runfiles, file).Replace('\\', '/')).Order(StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"data {file} {Archive.Sha256Sums.Sha256(Path.Combine(runfiles, file))}\n");
        }

        foreach (var argument in settings.Arguments)
        {
            text.Append(CultureInfo.InvariantCulture, $"argument {argument.Length}:{argument}\n");
        }

        var environment = Environment(target, settings, "<log>", 0, "<runfiles>");
        foreach (var (name, value) in environment.Where(e => e.Key is not ("TEST_TMPDIR" or "TMPDIR" or "HOME"))
                     .OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture, $"env {name}={value.Length}:{value}\n");
        }

        text.Append(CultureInfo.InvariantCulture,
            $"shards {settings.ShardCount}\ntimeout {(settings.Timeout ?? TimeoutOf(settings.TestSize)).Ticks}\nflaky {settings.Flaky}\n");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static Dictionary<string, string> Environment(
        ProjectTarget target, ITestSettings settings, string logDirectory, int shard, string runfiles)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in settings.InheritedEnvironmentVariables.Concat(["PATH", "SYSTEMROOT", "SystemRoot"]))
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        var temporary = Path.Combine(logDirectory, $"tmp{shard}");
        if (logDirectory != "<log>")
        {
            Directory.CreateDirectory(temporary);
        }

        environment["TEST_TMPDIR"] = temporary;
        environment["TMPDIR"] = temporary;
        environment["HOME"] = temporary;
        environment["TEST_SRCDIR"] = runfiles;
        environment["TEST_TARGET"] = target.Label.ToString();
        if (settings.ShardCount > 1)
        {
            environment["TEST_TOTAL_SHARDS"] = settings.ShardCount.ToString(CultureInfo.InvariantCulture);
            environment["TEST_SHARD_INDEX"] = shard.ToString(CultureInfo.InvariantCulture);
        }

        foreach (var (name, value) in settings.EnvironmentVariables)
        {
            environment[name] = value;
        }

        return environment;
    }

    private static string Sanitize(string label) =>
        label.TrimStart('/', '@').Replace(':', '/').Replace("//", "/", StringComparison.Ordinal);
}
