// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Driver;

/// <summary>
/// The report of <c>tazel coverage</c>: the profiles the tests wrote, merged by llvm-profdata, read by llvm-cov into an
/// LCOV file and a summary. Both tools are taken beside the clang of the build, else from PATH.
/// </summary>
internal static class CoverageReport
{
    public static async Task<bool> WriteAsync(
        Planner planner, ToolchainChooser chooser,
        IReadOnlyList<(ProjectTarget Target, TargetProducts Products, bool Built)> tests, Reporter reporter,
        CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(chooser);
        ArgumentNullException.ThrowIfNull(tests);
        var layout = planner.Layout;
        var programs = tests.Where(test => test.Built && test.Target.Family != Family.Zig && test.Products.Executable is not null)
            .Select(test => layout.Full(test.Products.Executable!)).ToList();
        foreach (var zig in tests.Where(test => test.Target.Family == Family.Zig))
        {
            reporter.Status($"{zig.Target.Label}: Zig tests are run, and LLVM coverage covers C-family code only");
        }

        var profiles = Directory.Exists(layout.Full(layout.TestLogs))
            ? Directory.EnumerateFiles(layout.Full(layout.TestLogs), "coverage-*.profraw", SearchOption.AllDirectories).Order().ToList()
            : [];
        if (programs.Count == 0 || profiles.Count == 0)
        {
            reporter.Warning("no test wrote a coverage profile, so there is no report");
            return programs.Count == 0;
        }

        var clangDirectory = chooser.UsedCC.Select(toolchain => Path.GetDirectoryName(toolchain.C.Program)).FirstOrDefault();
        var profdata = Tool("llvm-profdata", clangDirectory);
        var cov = Tool("llvm-cov", clangDirectory);
        var directory = layout.Full(layout.CoverageRoot);
        Directory.CreateDirectory(directory);
        var merged = Path.Combine(directory, "coverage.profdata");
        var environment = Processes.ForActions();
        var merge = await Processes.RunAsync([profdata, "merge", "-sparse", "-o", merged, .. profiles], layout.MainRoot,
            environment, null, cancel).ConfigureAwait(false);
        if (merge.ExitCode != 0)
        {
            reporter.ActionFailed("Merging the coverage profiles", [profdata, "merge"], merge.Output);
            return false;
        }

        var objects = programs.Skip(1).SelectMany(program => new[] { "-object", program }).ToList();
        var export = await Processes.RunAsync(
            [cov, "export", "-format=lcov", $"-instr-profile={merged}", programs[0], .. objects], layout.MainRoot,
            environment, null, cancel).ConfigureAwait(false);
        if (export.ExitCode != 0)
        {
            reporter.ActionFailed("Writing the coverage report", [cov, "export"], export.Output);
            return false;
        }

        var lcov = Path.Combine(directory, "coverage.lcov");
        await File.WriteAllTextAsync(lcov, export.Output, cancel).ConfigureAwait(false);
        var summary = await Processes.RunAsync(
            [cov, "report", $"-instr-profile={merged}", programs[0], .. objects], layout.MainRoot, environment, null, cancel)
            .ConfigureAwait(false);
        if (summary.ExitCode == 0)
        {
            reporter.Plain(summary.Output.TrimEnd('\n'));
        }

        reporter.Success($"wrote {FileSets.ToRoot(lcov, layout.MainRoot)}");
        return true;
    }

    private static string Tool(string name, string? beside)
    {
        if (beside is not null)
        {
            foreach (var candidate in new[] { name, name + ".exe" })
            {
                var path = Path.Combine(beside, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return Host.Which(name) ?? throw new ProjectException(
            $"tazel coverage needs {name} beside clang or on PATH; it comes with LLVM");
    }
}
