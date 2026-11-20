// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Text;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;
using SharpFuzz;

namespace Progmasoft.Tazel.Fuzz;

/// <summary>
/// The readers of what a user writes, under libFuzzer: <c>Progmasoft.Tazel.Fuzz &lt;target&gt;</c> with the libFuzzer
/// options after it, from libfuzzer-dotnet. Each target may refuse its input with the exception of its kind, and must
/// do nothing else: no other exception, no hang, no crash.
/// </summary>
public static class Program
{
    /// <summary>The fuzz targets by name.</summary>
    public static readonly string[] Targets = ["custom", "options", "envs", "label", "depfile", "command-line"];

    /// <summary>Runs one target on the inputs libFuzzer gives.</summary>
    public static void Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Action<string> target = (arguments.Length > 0 ? arguments[0] : "") switch
        {
            "custom" => text => Refused<CustomCommandsException>(() => CustomParser.Parse(text)),
            "options" => text => Refused<TazelOptionsException>(() => TazelOptionsFile.Parse(text)),
            "envs" => text => Refused<CustomCommandsException>(() => Dsl.Envs.TazelEnvsFile.Parse(text)),
            "label" => text => Refused<LabelException>(() => Labels.Selection(text, PackageId.Root)),
            "depfile" => text => Depfiles.Inputs(text),
            "command-line" => text => Refused<CommandLineException>(() => CommandLine.Parse(text.Split('\n'))),
            _ => throw new ArgumentException($"the targets are {string.Join(", ", Targets)}"),
        };
        Fuzzer.LibFuzzer.Run(span =>
        {
            string text;
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
            }
            catch (DecoderFallbackException)
            {
                return;
            }

            target(text);
        });
    }

    private static void Refused<TException>(Action read)
        where TException : Exception
    {
        try
        {
            read();
        }
        catch (TException)
        {
            // A refusal with a message is what a reader does with what it cannot take.
        }
    }
}
