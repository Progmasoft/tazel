// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
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
            using var early = new Reporter(CommandLine.FormatOf(arguments), output, When.Auto, quiet: false, When.Never);
            early.Error(ErrorCodes.CommandLine, error.Message, HelpFor(arguments), Jokes.ForCommandLine(error.Message));
            return UsageError;
        }

        using var reporter = new Reporter(command.Output.Format, output, command.Output.Color, command.Output.Quiet,
            command.Output.Progress);
        try
        {
            return await new Commands(reporter, directory).RunAsync(command, cancel).ConfigureAwait(false);
        }
        catch (CommandLineException error)
        {
            reporter.Error(ErrorCodes.CommandLine, error.Message, HelpFor(arguments), Jokes.ForCommandLine(error.Message));
            return UsageError;
        }
        catch (Exception error) when (Placed(error) is { } diagnostic)
        {
            reporter.Diagnostic(diagnostic);
            return 1;
        }
        catch (Cache.CacheKeyException error)
        {
            reporter.Error(ErrorCodes.CacheKey, error.Message, "make a key pair with tazel cache keygen",
                "A key with no name on it opens nothing, least of all a cache.");
            return 1;
        }
        catch (MicroVM.MicroVMException error)
        {
            reporter.Error(ErrorCodes.MicroVM, error.Message, null,
                "The sandbox stayed shut, and so did the door to your machine.");
            return 1;
        }
        catch (Exception error) when (error is ProjectException or LabelException or TazelOptionsException or
            CustomCommandsException)
        {
            var code = error switch
            {
                LabelException => ErrorCodes.Label,
                TazelOptionsException => ErrorCodes.OptionsUnreadable,
                _ => ErrorCodes.Project,
            };
            reporter.Error(code, error.Message, null, Jokes.ForProject(error.Message));
            return 1;
        }
        catch (OperationCanceledException)
        {
            reporter.Error(ErrorCodes.Stopped, "stopped", null, "Tazel put its tools down mid-sentence.");
            return 130;
        }
    }

    /// <summary>The diagnostic of a problem with a place in a file, found through the causes too.</summary>
    private static Diagnostic? Placed(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is CustomCommandsException { Diagnostic: { } custom })
            {
                return custom;
            }

            if (error is TazelOptionsException { Diagnostic: { } options })
            {
                return options;
            }
        }

        return null;
    }

    /// <summary>Where to read about the command that was typed.</summary>
    private static string HelpFor(string[] arguments)
    {
        var command = arguments.FirstOrDefault(argument => CommandLine.Commands.Contains(argument, StringComparer.Ordinal));
        return command is null ? "tazel help lists the commands" : $"tazel help {command} --long says what {command} takes";
    }
}
