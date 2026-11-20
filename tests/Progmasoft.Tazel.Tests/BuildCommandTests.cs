// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Whole commands on a real compiler; skipped where none is on PATH.</summary>
public sealed class BuildCommandTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        var math = CC.StaticLibrary("math", new()
        {
            Sources = ["math.cpp"],
            Headers = ["math.hpp"],
            Visibility = Visibility.Module,
        });
        CC.Executable("app", new()
        {
            Sources = ["main.cpp", "half.c"],
            Depends = [math],
            CCompilerOptions = ["-DONLY_C"],
            CppCompilerOptions = ["-DONLY_CXX"],
            RuntimeData = ["data.txt"],
        });
        CC.Test("math_test", new() { Sources = ["math_test.cpp"], Depends = [math], ShardCount = 2 });
        CC.Test("failing", new() { Sources = ["failing.cpp"], Tags = ["manual"] });
        """;

    private static TemporaryModule Sample() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile)
        .Write("BUILD.csx", Build)
        .Write("math.hpp", "int add(int a, int b);\n")
        .Write("math.cpp", "#include \"math.hpp\"\nint add(int a, int b) { return a + b; }\n")
        .Write("half.c", "#ifdef ONLY_CXX\n#error C++ options reached C\n#endif\nint half(int x) { return x / 2; }\n")
        .Write("main.cpp", """
            #include <cstdio>
            #include "math.hpp"
            #ifdef ONLY_C
            #error C options reached C++
            #endif
            extern "C" int half(int);
            int main() {
              std::FILE* f = std::fopen("data.txt", "r");
              return f != nullptr && add(half(40), 1) == 21 ? 0 : 1;
            }
            """)
        .Write("data.txt", "data\n")
        .Write("math_test.cpp", "#include \"math.hpp\"\nint main() { return add(2, 2) == 4 ? 0 : 1; }\n")
        .Write("failing.cpp", "int main() { return 3; }\n");

    /// <summary>Skips a test that needs a C++ compiler with its headers.</summary>
    private static void SkipWithoutCompiler()
    {
        Assert.SkipWhen(Host.Which("clang++") is null && Host.Which("g++") is null, "no C++ compiler on PATH");
        // clang on Windows takes the C and C++ headers of MSVC from the environment that vcvars sets.
        Assert.SkipWhen(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INCLUDE") is null,
            "no MSVC environment (INCLUDE) for the headers of clang on Windows");
    }

    [Fact]
    public async Task BuildsRunsAndTestsAModule()
    {
        SkipWithoutCompiler();
        using var module = Sample();
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.Contains("Compiling //:app half.c", output, StringComparison.Ordinal);

        // Nothing changed, so nothing runs again.
        (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("Compiling", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "run", "//:app");
        Assert.True(status == 0, output);

        (status, output) = await module.TazelAsync(".", "test", "//...");
        Assert.True(status == 0, output);
        Assert.Contains("//:math_test", output, StringComparison.Ordinal);
        Assert.DoesNotContain("//:failing", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(module.Root, ".tazel", "testlogs", "math_test", "test.log")));

        (status, output) = await module.TazelAsync(".", "test", "//:failing");
        Assert.Equal(1, status);
        Assert.Contains("FAILED", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompilerErrorStopsTheBuildWithItsCommand()
    {
        SkipWithoutCompiler();
        using var module = Sample();
        module.Write("math.cpp", "this is not C++\n");
        var (status, output) = await module.TazelAsync(".", "build", "//:app");
        Assert.Equal(1, status);
        Assert.True(output.Contains("FAILED Compiling //:math math.cpp", StringComparison.Ordinal), output);
        Assert.True(output.Contains("-c math.cpp", StringComparison.Ordinal), output);
    }

    [Fact]
    public async Task ASandboxAskedForStopsTheBuild()
    {
        using var module = Sample();
        module.Write("tazel_options.txt", "sandboxing-with-microvm: true;");
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.Equal(1, status);
        Assert.Contains("and not builds, tests or plugins, so nothing ran", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildsAZigProgramThatImportsAModule()
    {
        Assert.SkipWhen(Host.Which("zig") is null, "no zig on PATH");
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", """
                using System.Collections.Generic;
                using Progmasoft.Tazel.Dsl.Build.Prelude;
                Zig.Module("core", new() { RootSourceFile = "core.zig" });
                Zig.Executable("app", new()
                {
                    RootSourceFile = "main.zig",
                    ModuleImports = new Dictionary<string, string> { ["core"] = ":core" },
                });
                Zig.Test("core_test", new() { RootSourceFile = "core.zig" });
                """)
            .Write("core.zig", "const std = @import(\"std\");\npub fn add(a: i32, b: i32) i32 { return a + b; }\ntest \"add\" { try std.testing.expect(add(1, 2) == 3); }\n")
            .Write("main.zig", "const core = @import(\"core\");\npub fn main() !void { if (core.add(1, 2) != 3) return error.Wrong; }\n");
        var (status, output) = await module.TazelAsync(".", "test", "//...");
        Assert.True(status == 0, output);
        (status, output) = await module.TazelAsync(".", "run", "//:app");
        Assert.True(status == 0, output);
    }

    [Fact]
    public async Task PackagesTheModuleReproducibly()
    {
        using var module = Sample();
        var (status, output) = await module.TazelAsync(".", "package");
        Assert.True(status == 0, output);
        var archive = Path.Combine(module.Root, ".tazel", "package", "com.example.sample-0.1.0.tazel.tar.xz");
        var first = Archive.Sha256Sums.Sha256(archive);
        Assert.Equal($"{first}  com.example.sample-0.1.0.tazel.tar.xz\n", await File.ReadAllTextAsync(archive + ".sha256sums", TestContext.Current.CancellationToken));
        (status, _) = await module.TazelAsync(".", "package");
        Assert.Equal(0, status);
        Assert.Equal(first, Archive.Sha256Sums.Sha256(archive));
    }

    [Fact]
    public async Task ADependencyComesFromItsArchive()
    {
        using var library = new TemporaryModule()
            .Write("MODULE.csx", """
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "lib", Version = "1.0.0" });
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"lib\", new() { Visibility = Visibility.Public });\nCC.StaticLibrary(\"hidden\", new() { Visibility = Visibility.Module });");
        var (status, output) = await library.TazelAsync(".", "package");
        Assert.True(status == 0, output);
        var archive = Path.Combine(library.Root, ".tazel", "package", "com.example.lib-1.0.0.tazel.tar.xz");
        var sha = Archive.Sha256Sums.Sha256(archive);

        using var app = new TemporaryModule();
        Directory.CreateDirectory(Path.Combine(app.Root, "deps"));
        File.Copy(archive, Path.Combine(app.Root, "deps", "lib.tazel.tar.xz"));
        app.Write("MODULE.csx", TemporaryModule.ModuleFile + $$"""

                dependencies = Dependencies.Define(LocalModule.From(new() { Name = "lib", Include = "deps/lib.tazel.tar.xz", Sha256 = "{{sha}}" }));
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"app\", new() { Depends = [\"@lib//:lib\"] });");
        var workspace = await app.LoadAsync();
        Assert.Contains(workspace.Targets.Keys, label => label.ToString() == "@lib//:lib");

        app.Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"app\", new() { Depends = [\"@lib//:hidden\"] });");
        var error = await Assert.ThrowsAsync<Project.ProjectException>(() => app.LoadAsync());
        Assert.Contains("@lib//:hidden is module", error.Message, StringComparison.Ordinal);

        app.Write("MODULE.csx", TemporaryModule.ModuleFile + """

                dependencies = Dependencies.Define(LocalModule.From(new() { Name = "lib", Include = "deps/lib.tazel.tar.xz", Sha256 = "0000000000000000000000000000000000000000000000000000000000000000" }));
                """);
        error = await Assert.ThrowsAsync<Project.ProjectException>(() => app.LoadAsync());
        Assert.Contains("has the SHA-256", error.Message, StringComparison.Ordinal);
    }
}
