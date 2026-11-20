// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.Driver;

/// <summary>What a process did: its exit status, and its output and errors together, in the order they came.</summary>
/// <param name="ExitCode">The exit status; -1 when it could not start or ran out of time.</param>
/// <param name="Output">What it printed.</param>
/// <param name="TimedOut">Whether it was stopped because it ran out of time.</param>
/// <param name="Canceled">Whether it was stopped because the command stopped, such as after another action failed.</param>
internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut = false, bool Canceled = false);

/// <summary>Starts programs directly, with an argument list and no shell between.</summary>
internal static class Processes
{
    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="directory"/> with exactly <paramref name="environment"/>, and
    /// keeps what it prints. It is stopped, with what it started, after <paramref name="timeout"/>.
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        IReadOnlyList<string> command,
        string directory,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan? timeout,
        CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (var i = 1; i < command.Count; i++)
        {
            start.ArgumentList.Add(command[i]);
        }

        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, line) => Append(output, line.Data);
        process.ErrorDataReceived += (_, line) => Append(output, line.Data);
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            return new ProcessResult(-1, $"{command[0]} could not be started: {error.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout is { } span)
        {
            limit.CancelAfter(span);
        }

        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            var canceled = cancel.IsCancellationRequested;
            return new ProcessResult(-1, Text(output), TimedOut: !canceled, Canceled: canceled);
        }

        // WaitForExitAsync returns once the output has been read to its end as well.
        return new ProcessResult(process.ExitCode, Text(output));
    }

    /// <summary>Runs <paramref name="command"/> and keeps what it prints apart from its errors.</summary>
    public static async Task<(int ExitCode, string Output, string Errors)> CaptureAsync(
        IReadOnlyList<string> command, string directory, IReadOnlyDictionary<string, string> environment, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        var start = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < command.Count; i++)
        {
            start.ArgumentList.Add(command[i]);
        }

        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception error)
        {
            return (-1, "", $"{command[0]} could not be started: {error.Message}");
        }

        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancel);
        var errors = process.StandardError.ReadToEndAsync(cancel);
        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
    }

    /// <summary>Runs <paramref name="command"/> with the terminal of Tazel, as <c>tazel run</c> does.</summary>
    public static int RunAttached(IReadOnlyList<string> command, string directory, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(environment);
        var start = new ProcessStartInfo(command[0]) { WorkingDirectory = directory, UseShellExecute = false };
        for (var i = 1; i < command.Count; i++)
        {
            start.ArgumentList.Add(command[i]);
        }

        start.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{command[0]} did not start");
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Tazel's own environment, for what runs as the user would run it.</summary>
    public static Dictionary<string, string> Inherited()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// The small environment the actions of a build run with: the programs on PATH find their own files, and nothing
    /// else of the user's environment changes what they make.
    /// </summary>
    public static Dictionary<string, string> ForActions()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };
        foreach (var name in new[] { "PATH", "TMPDIR", "TMP", "TEMP", "SYSTEMROOT", "SystemRoot", "WINDIR", "HOME",
                     "DEVELOPER_DIR", "SDKROOT", "INCLUDE", "LIB", "LIBPATH" })
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            environment["ZERO_AR_DATE"] = "1";
        }

        return environment;
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.Append(line).Append('\n');
        }
    }

    private static string Text(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}
