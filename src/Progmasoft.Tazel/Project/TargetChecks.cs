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
        var place = target.Place;
        switch (target.Settings)
        {
            case ZigModuleSettings zig:
                if (zig.RootSourceFile.Length == 0)
                {
                    throw new ProjectException($"{place}: {target.Label} needs its RootSourceFile");
                }

                if (!zig.RootSourceFile.EndsWith(".zig", StringComparison.Ordinal))
                {
                    throw new ProjectException($"{place}: the RootSourceFile '{zig.RootSourceFile}' is not a .zig file");
                }

                foreach (var name in zig.ModuleImports.Keys)
                {
                    if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    {
                        throw new ProjectException($"{place}: '{name}' cannot be the name of an @import");
                    }
                }

                break;
            case CCSettings cc:
                foreach (var option in cc.PreprocessorDefinitions.Concat(cc.LocalPreprocessorDefinitions))
                {
                    if (option.Length == 0 || option.StartsWith('-'))
                    {
                        throw new ProjectException($"{place}: '{option}' is not a macro; write NAME or NAME=value");
                    }
                }

                break;
        }

        if (target.Settings is ITestSettings test)
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

        if (target.Family == Family.Zig && target.Settings is not ZigModuleSettings)
        {
            throw new ProjectException($"{place}: {target.Label} has settings of another family");
        }
    }
}
