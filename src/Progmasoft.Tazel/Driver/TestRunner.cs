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
internal sealed class TestRunner(OutputLayout layout, int jobs, Reporter reporter)
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

    private async Task<TestOutcome> RunOneAsync(ProjectTarget target, TargetProducts products, CancellationToken cancel)
    {
        var settings = (ITestSettings)target.Settings;
        var runfiles = Runfiles.Prepare(target, products, layout);
        var logDirectory = layout.Full($"{layout.TestLogs}/{Sanitize(target.Label.ToString())}");
        if (Directory.Exists(logDirectory))
        {
            Directory.Delete(logDirectory, recursive: true);
        }

        Directory.CreateDirectory(logDirectory);
        var timeout = settings.Timeout ?? TimeoutOf(settings.TestSize);
        var attempts = settings.Flaky ? 3 : 1;
        var watch = Stopwatch.StartNew();
        var log = new StringBuilder();
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var failed = false;
            var timedOut = false;
            for (var shard = 0; shard < settings.ShardCount; shard++)
            {
                var environment = Environment(target, settings, logDirectory, shard, runfiles);
                Runfiles.AddLibraryPath(environment, products, layout);
                var command = new List<string> { layout.Full(products.Executable!) };
                command.AddRange(settings.Arguments);
                var result = await Processes.RunAsync(command, runfiles, environment, timeout, cancel).ConfigureAwait(false);
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

            await File.WriteAllTextAsync(Path.Combine(logDirectory, "test.log"), log.ToString(), cancel).ConfigureAwait(false);
            if (!failed)
            {
                var status = attempt == 1 ? TestStatus.Passed : TestStatus.Flaky;
                return new TestOutcome(target.Label.ToString(), status, watch.Elapsed, log.ToString());
            }

            if (attempt == attempts)
            {
                return new TestOutcome(target.Label.ToString(), timedOut ? TestStatus.TimedOut : TestStatus.Failed,
                    watch.Elapsed, log.ToString());
            }

            log.Append(CultureInfo.InvariantCulture, $"--- attempt {attempt} failed; the test is flaky, so it runs again ---\n");
        }

        throw new InvalidOperationException("a test ended without an outcome");
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
        Directory.CreateDirectory(temporary);
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
