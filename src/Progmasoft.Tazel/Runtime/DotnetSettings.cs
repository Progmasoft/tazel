// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Threading;

namespace Progmasoft.Tazel.Runtime;

/// <summary>A .NET setting Tazel cannot take, or one the runtime refused.</summary>
public sealed class DotnetSettingsException : Exception
{
    /// <summary>What is wrong.</summary>
    public DotnetSettingsException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public DotnetSettingsException()
    {
    }

    /// <summary>What is wrong, and why.</summary>
    public DotnetSettingsException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>What a setting takes.</summary>
internal enum SettingKind
{
    /// <summary>A size in bytes, with an optional B, K, M, G or T.</summary>
    Bytes,

    /// <summary>A whole percentage.</summary>
    Percent,

    /// <summary>A <see cref="GCLatencyMode"/>: Batch, Interactive, LowLatency or SustainedLowLatency.</summary>
    LatencyMode,

    /// <summary>A number of threads.</summary>
    Threads,
}

/// <summary>One setting: its name, its value, and where it was written.</summary>
/// <param name="Name">One of the thirteen names.</param>
/// <param name="Value">A ulong for sizes and percentages, an int for threads, a <see cref="GCLatencyMode"/>.</param>
/// <param name="Source">The command line or the file, for messages.</param>
public sealed record DotnetSetting(string Name, object Value, string Source);

/// <summary>
/// <c>--dotnet-settings</c> and <c>dotnet-settings</c> of <c>tazel_options.txt</c>: thirteen settings of the .NET runtime
/// of this Tazel process alone, never of what it builds or starts. A setting not given is left to .NET.
/// </summary>
internal static class DotnetSettings
{
    private const string LatencyName = "GCSettings.LatencyMode";

    /// <summary>The settings, and what each takes, in the order <c>tazel help dotnet</c> lists them.</summary>
    public static IReadOnlyList<(string Name, SettingKind Kind)> All { get; } =
    [
        ("GCHeapHardLimit", SettingKind.Bytes),
        ("GCHeapHardLimitPercent", SettingKind.Percent),
        ("GCHeapHardLimitSOH", SettingKind.Bytes),
        ("GCHeapHardLimitLOH", SettingKind.Bytes),
        ("GCHeapHardLimitPOH", SettingKind.Bytes),
        ("GCHeapHardLimitSOHPercent", SettingKind.Percent),
        ("GCHeapHardLimitLOHPercent", SettingKind.Percent),
        ("GCHeapHardLimitPOHPercent", SettingKind.Percent),
        (LatencyName, SettingKind.LatencyMode),
        ("ThreadPool.MinWorkerThreads", SettingKind.Threads),
        ("ThreadPool.MaxWorkerThreads", SettingKind.Threads),
        ("ThreadPool.MinCompletionPortThreads", SettingKind.Threads),
        ("ThreadPool.MaxCompletionPortThreads", SettingKind.Threads),
    ];

    private static readonly string[] LatencyModes = ["Batch", "Interactive", "LowLatency", "SustainedLowLatency"];

    /// <summary>Whether <paramref name="name"/> is one of the thirteen.</summary>
    public static bool IsName(string name) => All.Any(setting => setting.Name == name);

    /// <summary><c>Name=Value</c>, split at its first <c>=</c>, checked against what the name takes.</summary>
    public static DotnetSetting Parse(string entry, string source)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var equals = entry.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0)
        {
            throw new DotnetSettingsException($"{source}: '{entry}' is not Name=Value");
        }

        var (name, value) = (entry[..equals], entry[(equals + 1)..]);
        var kind = All.FirstOrDefault(setting => setting.Name == name).Kind;
        if (!IsName(name))
        {
            var near = Cli.Suggestions.Nearest(name, All.Select(setting => setting.Name));
            throw new DotnetSettingsException(
                $"{source}: '{name}' is not a .NET setting Tazel takes{(near is null ? "; tazel help dotnet --long lists the thirteen" : $"; did you mean {near}?")}");
        }

        return new DotnetSetting(name, kind switch
        {
            SettingKind.Bytes => Bytes(value) ?? throw Bad(source, name, value, "a size in bytes, with B, K, M, G or T after it if you like"),
            SettingKind.Percent => Whole(value) is { } percent and >= 1 and <= 100 ? percent
                : throw Bad(source, name, value, "a whole percentage from 1 to 100"),
            SettingKind.Threads => Whole(value) is { } threads and >= 1 and <= int.MaxValue ? (int)threads
                : throw Bad(source, name, value, "a whole number of threads, 1 or more"),
            _ => LatencyModes.Contains(value, StringComparer.Ordinal) ? Enum.Parse<GCLatencyMode>(value)
                : throw Bad(source, name, value, $"one of {string.Join(", ", LatencyModes)}"),
        }, source);
    }

    private static DotnetSettingsException Bad(string source, string name, string value, string what) =>
        new($"{source}: {name} takes {what}, not '{value}'");

    // A size: digits, then nothing, B, K, M, G or T; KiB and up, in checked arithmetic.
    internal static ulong? Bytes(string text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var (digits, multiplier) = text[^1] switch
        {
            'B' => (text[..^1], 1UL),
            'K' => (text[..^1], 1UL << 10),
            'M' => (text[..^1], 1UL << 20),
            'G' => (text[..^1], 1UL << 30),
            'T' => (text[..^1], 1UL << 40),
            _ => (text, 1UL),
        };
        if (Whole(digits) is not { } number)
        {
            return null;
        }

        try
        {
            return checked(number * multiplier);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static ulong? Whole(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit) &&
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// The settings that hold: those of the file, then those of the command line, the last of a name winning; the
    /// whole set is checked before anything is applied.
    /// </summary>
    public static IReadOnlyDictionary<string, DotnetSetting> Effective(IEnumerable<DotnetSetting> file, IEnumerable<DotnetSetting> commandLine)
    {
        var settings = new Dictionary<string, DotnetSetting>(StringComparer.Ordinal);
        foreach (var setting in file.Concat(commandLine))
        {
            settings[setting.Name] = setting;
        }

        Check(settings);
        return settings;
    }

    private static readonly string[] PerHeap = ["GCHeapHardLimitSOH", "GCHeapHardLimitLOH", "GCHeapHardLimitPOH"];

    private static readonly string[] PerHeapPercent = ["GCHeapHardLimitSOHPercent", "GCHeapHardLimitLOHPercent", "GCHeapHardLimitPOHPercent"];

    private static void Check(Dictionary<string, DotnetSetting> settings)
    {
        CheckHeapKinds(settings);
        CheckPerHeap(settings, PerHeap, "in bytes");
        CheckPerHeap(settings, PerHeapPercent, "as percentages");
        CheckThreads(Bounds(settings), settings);
    }

    // The settings of names that are given, with where each was written, for messages.
    private static string Where(Dictionary<string, DotnetSetting> settings, params string[] names) =>
        string.Join(", ", names.Where(settings.ContainsKey).Select(name => $"{name} ({settings[name].Source})"));

    // Two ways of saying one limit: .NET would take one and leave the other unsaid; Tazel takes neither and says so.
    private static void CheckHeapKinds(Dictionary<string, DotnetSetting> settings)
    {
        var (bytes, percent) = (settings.ContainsKey("GCHeapHardLimit"), settings.ContainsKey("GCHeapHardLimitPercent"));
        var heaps = PerHeap.Any(settings.ContainsKey);
        var heapPercents = PerHeapPercent.Any(settings.ContainsKey);
        string? problem = (bytes, percent, heaps, heapPercents) switch
        {
            (true, true, _, _) => "give the limit in bytes or as a percentage, not both",
            (true, _, true, _) or (true, _, _, true) or (_, true, true, _) or (_, true, _, true) =>
                "a limit for the whole heap and limits per object heap are two ways to say one thing; give one",
            (_, _, true, true) => "give the object heaps' limits in bytes or as percentages, not both",
            _ => null,
        };
        if (problem is not null)
        {
            throw new DotnetSettingsException(
                $"{Where(settings, ["GCHeapHardLimit", "GCHeapHardLimitPercent", .. PerHeap, .. PerHeapPercent])}: {problem}");
        }
    }

    // Limits per object heap need SOH and LOH; their percentages are each below 100, and so is their sum.
    private static void CheckPerHeap(Dictionary<string, DotnetSetting> settings, string[] names, string kind)
    {
        if (!names.Any(settings.ContainsKey))
        {
            return;
        }

        if (!settings.ContainsKey(names[0]) || !settings.ContainsKey(names[1]))
        {
            throw new DotnetSettingsException(
                $"{Where(settings, names)}: limits per object heap {kind} need both {names[0]} and {names[1]}; {names[2]} may be left out");
        }

        var percents = names == PerHeapPercent ? names.Where(settings.ContainsKey).Select(name => (ulong)settings[name].Value).ToList() : [];
        if (percents.Exists(value => value >= 100) || percents.Aggregate(0UL, (sum, value) => sum + value) >= 100)
        {
            throw new DotnetSettingsException($"{Where(settings, names)}: each percentage is below 100, and so is their sum");
        }
    }

    // The thread pool's bounds once the settings hold: each one given, else what the pool has now.
    private sealed record ThreadBounds(int Min, int MinPort, int Max, int MaxPort);

    private static ThreadBounds Bounds(IReadOnlyDictionary<string, DotnetSetting> settings)
    {
        int? Value(string name) => settings.TryGetValue(name, out var setting) ? (int)setting.Value : null;
        ThreadPool.GetMinThreads(out var min, out var minPort);
        ThreadPool.GetMaxThreads(out var max, out var maxPort);
        return new ThreadBounds(
            Value("ThreadPool.MinWorkerThreads") ?? min, Value("ThreadPool.MinCompletionPortThreads") ?? minPort,
            Value("ThreadPool.MaxWorkerThreads") ?? max, Value("ThreadPool.MaxCompletionPortThreads") ?? maxPort);
    }

    private static void CheckThreads(ThreadBounds bounds, Dictionary<string, DotnetSetting> settings)
    {
        if (bounds.Min > bounds.Max)
        {
            throw new DotnetSettingsException(
                $"the worker threads' minimum {bounds.Min} is above their maximum {bounds.Max} ({Where(settings, "ThreadPool.MinWorkerThreads", "ThreadPool.MaxWorkerThreads")})");
        }

        if (bounds.MinPort > bounds.MaxPort)
        {
            throw new DotnetSettingsException(
                $"the completion port threads' minimum {bounds.MinPort} is above their maximum {bounds.MaxPort} ({Where(settings, "ThreadPool.MinCompletionPortThreads", "ThreadPool.MaxCompletionPortThreads")})");
        }

        if (Math.Min(bounds.Max, bounds.MaxPort) < Environment.ProcessorCount)
        {
            throw new DotnetSettingsException(
                $"a maximum of threads below the {Environment.ProcessorCount} processors of this machine is refused by .NET ({Where(settings, "ThreadPool.MaxWorkerThreads", "ThreadPool.MaxCompletionPortThreads")})");
        }
    }

    /// <summary>Applies <paramref name="settings"/> to this process: the heap limits, the latency mode, the thread pool.</summary>
    public static void Apply(IReadOnlyDictionary<string, DotnetSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var limits = settings.Values.Where(setting => setting.Name.StartsWith("GCHeapHardLimit", StringComparison.Ordinal)).ToList();
        if (limits.Count > 0)
        {
            foreach (var limit in limits)
            {
                AppContext.SetData(limit.Name, (ulong)limit.Value);
            }

            try
            {
                GC.RefreshMemoryLimit();
            }
            catch (InvalidOperationException error)
            {
                throw new DotnetSettingsException($"the GC took no new heap limit ({string.Join(", ", limits.Select(l => l.Name))}): {error.Message}", error);
            }
        }

        if (settings.TryGetValue(LatencyName, out var latency))
        {
            var mode = (GCLatencyMode)latency.Value;
            GCSettings.LatencyMode = mode;
            if (GCSettings.LatencyMode != mode)
            {
                throw new DotnetSettingsException(
                    $"{LatencyName}={mode} ({latency.Source}): the GC of this process stays in {GCSettings.LatencyMode}; " +
                    "LowLatency needs the workstation GC");
            }
        }

        ApplyThreads(settings);
    }

    private static void ApplyThreads(IReadOnlyDictionary<string, DotnetSetting> settings)
    {
        if (!settings.Keys.Any(name => name.StartsWith("ThreadPool.", StringComparison.Ordinal)))
        {
            return;
        }

        var bounds = Bounds(settings);
        ThreadPool.GetMinThreads(out var nowMin, out var nowMinPort);

        // A maximum may not fall below the minimum of the moment, nor a minimum rise above the maximum: whichever
        // order keeps both true at every step.
        if (bounds.Max >= nowMin && bounds.MaxPort >= nowMinPort)
        {
            Took(ThreadPool.SetMaxThreads(bounds.Max, bounds.MaxPort), "maximum", bounds);
            Took(ThreadPool.SetMinThreads(bounds.Min, bounds.MinPort), "minimum", bounds);
        }
        else
        {
            Took(ThreadPool.SetMinThreads(bounds.Min, bounds.MinPort), "minimum", bounds);
            Took(ThreadPool.SetMaxThreads(bounds.Max, bounds.MaxPort), "maximum", bounds);
        }
    }

    private static void Took(bool taken, string what, ThreadBounds bounds)
    {
        if (!taken)
        {
            throw new DotnetSettingsException(
                $"the thread pool of this process took no new {what} (workers {bounds.Min} to {bounds.Max}, completion ports {bounds.MinPort} to {bounds.MaxPort})");
        }
    }

    /// <summary>What this process has for <paramref name="name"/>: its value, or <c>default</c> where .NET says none.</summary>
    public static IReadOnlyList<string> Get(string name)
    {
        if (!IsName(name))
        {
            var near = Cli.Suggestions.Nearest(name, All.Select(setting => setting.Name));
            throw new DotnetSettingsException($"'{name}' is not a .NET setting Tazel takes{(near is null ? "" : $"; did you mean {near}?")}");
        }

        switch (name)
        {
            case LatencyName:
                return [$"{name}: {GCSettings.LatencyMode}"];
            case "ThreadPool.MinWorkerThreads" or "ThreadPool.MinCompletionPortThreads":
                ThreadPool.GetMinThreads(out var minWorker, out var minPort);
                return [$"{name}: {(name.Contains("Worker", StringComparison.Ordinal) ? minWorker : minPort)}"];
            case "ThreadPool.MaxWorkerThreads" or "ThreadPool.MaxCompletionPortThreads":
                ThreadPool.GetMaxThreads(out var maxWorker, out var maxPort);
                return [$"{name}: {(name.Contains("Worker", StringComparison.Ordinal) ? maxWorker : maxPort)}"];
            default:
                // The GC's own report of its configuration; a name it does not report, or reports as 0, was not set.
                var configured = GC.GetConfigurationVariables().TryGetValue(name[2..], out var value) ||
                    GC.GetConfigurationVariables().TryGetValue(name, out value) ? value : null;
                var shown = configured is null || Convert.ToUInt64(configured, CultureInfo.InvariantCulture) == 0
                    ? "default"
                    : Convert.ToString(configured, CultureInfo.InvariantCulture)!;
                return [$"{name}: {shown}", $"available to the GC: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes} bytes"];
        }
    }
}
