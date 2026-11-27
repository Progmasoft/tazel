// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class SpdxLicensesTests
{
    [Fact]
    public void TheListIsTheOneInTheRepository() => Assert.Equal("3.29.0", SpdxLicenses.Version);

    [Theory]
    [InlineData("MIT")]
    [InlineData("mit")]
    [InlineData("MIT OR Apache-2.0")]
    [InlineData("(MIT AND BSD-2-Clause) OR Apache-2.0")]
    [InlineData("Apache-2.0 WITH LLVM-exception")]
    [InlineData("GPL-3.0-or-later WITH GCC-exception-3.1")]
    [InlineData("MPL-1.1+")]
    [InlineData("LicenseRef-Proprietary")]
    [InlineData("MIT OR LicenseRef-Commercial")]
    public void ValidExpressionsPass(string expression) => SpdxLicenses.Check(expression, "MODULE.csx");

    [Theory]
    [InlineData("MTI", "'MTI' is no license", "MIT")]
    [InlineData("Apache2.0", "'Apache2.0' is no license", "Apache-2.0")]
    [InlineData("GPL-2.0", "deprecated", "GPL-2.0-only or GPL-2.0-or-later")]
    [InlineData("LGPL-2.1+", "deprecated", "write LGPL-2.1-or-later")]
    [InlineData("Apache-2.0 WITH LLVM-exeption", "'LLVM-exeption' is no exception", "LLVM-exception")]
    [InlineData("MIT AND", "not an SPDX expression", "AND and OR")]
    [InlineData("(MIT OR Apache-2.0", "not an SPDX expression", "parentheses")]
    [InlineData("   ", "License is empty", "leave it out")]
    // These made Tethys.SPDX 2.2.0 recurse until the stack overflowed.
    [InlineData("MIT OR", "ends with OR", "AND and OR")]
    [InlineData("AND MIT", "stands where a license should", "AND and OR")]
    [InlineData("MIT WITH", "an exception must follow WITH", "WITH")]
    [InlineData("(", "a license is missing", "parentheses")]
    public void InvalidExpressionsSayWhatToWrite(string expression, string problem, string fix)
    {
        var error = Assert.Throws<LicenseException>(() => SpdxLicenses.Check(expression, "MODULE.csx"));
        Assert.StartsWith("MODULE.csx: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(problem, error.Message, StringComparison.Ordinal);
        Assert.Contains(fix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownIdentifierFarFromAnyNamesLicenseRef()
    {
        var error = Assert.Throws<LicenseException>(() => SpdxLicenses.Check("OurOwnTerms", "MODULE.csx"));
        Assert.Contains("LicenseRef-<name>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleWithAnInvalidLicenseDoesNotLoad()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", """
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0", License = "GPL-2.0" });
                """)
            .Write("BUILD.csx", "");
        var error = await Assert.ThrowsAsync<LicenseException>(() =>
            Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Debug, Platform.LinuxAmd64), new Version(0, 1, 0)));
        Assert.Contains("GPL-2.0-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleWithAValidLicenseLoads()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", """
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0", License = "MIT OR Apache-2.0" });
                """)
            .Write("BUILD.csx", "");
        var workspace = await Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Debug, Platform.LinuxAmd64),
            new Version(0, 1, 0));
        Assert.Equal("MIT OR Apache-2.0", workspace.Main.Info.Definition.License);
    }
}
