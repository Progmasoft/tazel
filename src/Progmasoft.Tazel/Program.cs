// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel;

/// <summary>The tazel command.</summary>
internal static class Program
{
    /// <summary>Exit status of a command line Tazel cannot take.</summary>
    public const int UsageError = 2;

    private static async Task<int> Main(string[] arguments)
    {
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            cancel.Cancel();
        };
        return await RunAsync(arguments, Directory.GetCurrentDirectory(), null, cancel.Token).ConfigureAwait(false);
    }

    /// <summary>Runs one command line in <paramref name="directory"/>; tests give <paramref name="output"/>.</summary>
    public static async Task<int> RunAsync(string[] arguments, string directory, TextWriter? output, CancellationToken cancel)
    {
        Command command;
        try
        {
            command = CommandLine.Parse(arguments);
        }
        catch (CommandLineException error)
        {
            new Reporter(Reporter.CreateConsole(When.Auto, output), quiet: false, When.Never).Error(error.Message);
            return UsageError;
        }

        var reporter = new Reporter(Reporter.CreateConsole(command.Output.Color, output), command.Output.Quiet, command.Output.Progress);
        try
        {
            return await new Commands(reporter, directory).RunAsync(command, cancel).ConfigureAwait(false);
        }
        catch (CommandLineException error)
        {
            reporter.Error(error.Message);
            return UsageError;
        }
        catch (Exception error) when (error is ProjectException or LabelException or TazelOptionsException or
            CustomCommandsException)
        {
            reporter.Error(error.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Error("stopped");
            return 130;
        }
    }
}
