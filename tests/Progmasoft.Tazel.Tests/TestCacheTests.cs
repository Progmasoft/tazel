// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class TestCacheTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        CC.Test("reads_test", new() { Sources = ["reads.c"], RuntimeData = ["data.txt"], Arguments = ["data.txt"] });
        CC.Test("always_test", new() { Sources = ["reads.c"], RuntimeData = ["data.txt"], Arguments = ["data.txt"], Tags = ["no-cache"] });
        """;

    private const string Reads = """
        #include <stdio.h>
        int main(int argc, char **argv) { FILE *f = fopen(argv[1], "r"); if (!f) return 1; puts("read it"); fclose(f); return 0; }
        """;

    [Fact]
    public async Task APassedTestRunsAgainOnlyWhenWhatItRunsWithChanges()
    {
        Assert.SkipWhen(Host.Which("clang") is null && Host.Which("gcc") is null && Host.Which("zig") is null, "no C compiler");
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Build)
            .Write("reads.c", Reads).Write("data.txt", "one");
        var (status, output) = await module.TazelAsync(".", "test", "//...");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("(cached)", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "test", "//...");
        Assert.True(status == 0, output);
        Assert.Matches(@"//:reads_test\s*│?\s*PASSED \(cached\)", output);
        Assert.DoesNotMatch(@"//:always_test\s*│?\s*PASSED \(cached\)", output);

        module.Write("data.txt", "two");
        (status, output) = await module.TazelAsync(".", "test", "//:reads_test");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("(cached)", output, StringComparison.Ordinal);

        module.Write("BUILD.csx", Build.Replace("Arguments = [\"data.txt\"] });\nCC.Test(\"always", "Arguments = [\"data.txt\", \"x\"] });\nCC.Test(\"always", StringComparison.Ordinal));
        (status, output) = await module.TazelAsync(".", "test", "//:reads_test");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("(cached)", output, StringComparison.Ordinal);
    }
}
