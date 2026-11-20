// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;

namespace Progmasoft.Tazel.Project;

/// <summary>Checks the settings of one target, apart from what it depends on.</summary>
internal static class TargetChecks
{
    public static void Check(ProjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var place = target.Place;
        if (target.Settings is ZigModuleSettings zig)
        {
            CheckZig(target, zig, place);
        }
        else if (target.Settings is CCSettings cc)
        {
            CheckMacros(cc, place);
        }

        if (target.Settings is ITestSettings test)
        {
            CheckTest(test, place);
        }

        if (target.Family == Family.Plugin)
        {
            CheckStepOutputs((PluginApi.Steps.StepSettings)target.Settings, place);
        }

        if (target.Family == Family.Zig && target.Settings is not ZigModuleSettings)
        {
            throw new ProjectException($"{place}: {target.Label} has settings of another family");
        }
    }

    private static void CheckZig(ProjectTarget target, ZigModuleSettings zig, string place)
    {
        if (zig.RootSourceFile.Length == 0)
        {
            throw new ProjectException($"{place}: {target.Label} needs its RootSourceFile");
        }

        if (!zig.RootSourceFile.EndsWith(".zig", StringComparison.Ordinal))
        {
            throw new ProjectException($"{place}: the RootSourceFile '{zig.RootSourceFile}' is not a .zig file");
        }

        var bad = zig.ModuleImports.Keys.FirstOrDefault(name => name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        if (bad is not null)
        {
            throw new ProjectException($"{place}: '{bad}' cannot be the name of an @import");
        }
    }

    private static void CheckMacros(CCSettings cc, string place)
    {
        var bad = cc.PreprocessorDefinitions.Concat(cc.LocalPreprocessorDefinitions)
            .FirstOrDefault(option => option.Length == 0 || option.StartsWith('-'));
        if (bad is not null)
        {
            throw new ProjectException($"{place}: '{bad}' is not a macro; write NAME or NAME=value");
        }
    }

    private static void CheckTest(ITestSettings test, string place)
    {
        if (test.ShardCount < 1)
        {
            throw new ProjectException($"{place}: ShardCount is at least 1");
        }

        if (test.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ProjectException($"{place}: Timeout must be longer than nothing");
        }
    }

    private static void CheckStepOutputs(PluginApi.Steps.StepSettings step, string place)
    {
        var bad = step.Outputs.FirstOrDefault(output =>
            output.Length == 0 || System.IO.Path.IsPathRooted(output) || output.Contains('\\', StringComparison.Ordinal) ||
            output.Split('/').Any(part => part is "" or "." or ".."));
        if (bad is not null)
        {
            throw new ProjectException($"{place}: the output '{bad}' must be a path below the step's output directory");
        }
    }
}
