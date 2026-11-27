// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Builds for the BSDs from this machine, with the zig on PATH: what Tazel's BSD platforms are for.</summary>
public sealed class BsdCrossTests
{
    private const string ZigProgram = "const std = @import(\"std\");\npub fn main() void { std.debug.print(\"hi\\n\", .{}); }\n";

    // The note an ELF file of the system carries: name size 8, description size 4, type 1, then the name.
    private static bool IsFor(string file, string system)
    {
        var note = new byte[] { 8, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes(system + "\0")).ToArray();
        return File.ReadAllBytes(file).AsSpan().IndexOf(note) >= 0;
    }

    // A module whose zig, the one on PATH, is registered for the platform: Tazel takes this machine's tools for this
    // machine alone, and a toolchain for every other platform.
    private static TemporaryModule ZigModule(string member, string build)
    {
        var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile + "\ntoolchains = Toolchains.Define(\"//toolchains:zig\");")
            .Write("toolchains/BUILD.csx", $$"""
                using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
                using Progmasoft.Tazel.Dsl.Common;
                Zig.Toolchain("zig", new()
                {
                    ExecutionPlatform = Platform.{{Host.Platform}},
                    TargetPlatform = Platform.{{member}},
                    Tools = new() { Zig = "zig" },
                });
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\n" + build);
        return module;
    }

    private static string Built(TemporaryModule module, string name) =>
        Directory.EnumerateFiles(Path.Combine(module.Root, ".tazel", "out"), name, SearchOption.AllDirectories).Single();

    [Theory]
    [InlineData("FreeBSDAmd64", "freebsd-amd64", "FreeBSD")]
    [InlineData("FreeBSDAarch64", "freebsd-aarch64", "FreeBSD")]
    [InlineData("OpenBSDAmd64", "openbsd-amd64", "OpenBSD")]
    [InlineData("OpenBSDAarch64", "openbsd-aarch64", "OpenBSD")]
    public async Task BuildsAZigProgramForABsd(string member, string platform, string system)
    {
        Assert.SkipWhen(Host.Which("zig") is null, "no zig on PATH");
        using var module = ZigModule(member, "Zig.Executable(\"app\", new() { RootSourceFile = \"main.zig\" });\n")
            .Write("main.zig", ZigProgram);
        var (status, output) = await module.TazelAsync(".", "build", "//:app", "--platform", platform);
        Assert.True(status == 0, output);
        Assert.True(IsFor(Built(module, "app"), system), $"//:app is not a {system} program");
    }

    [Theory]
    [InlineData("FreeBSDAarch64", "freebsd-aarch64", "aarch64-freebsd", "FreeBSD")]
    [InlineData("OpenBSDAmd64", "openbsd-amd64", "x86_64-openbsd", "OpenBSD")]
    public async Task BuildsACppProgramForABsdWithARegisteredToolchain(string member, string platform, string triple, string system)
    {
        Assert.SkipWhen(Host.Which("zig") is null, "no zig on PATH");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the compilers of the toolchain are shell scripts");
        using var module = new TemporaryModule();
        // zig cc for the BSD, as a user would wrap a cross compiler: a toolchain names programs, not arguments.
        string Wrapper(string name, string command)
        {
            var path = Path.Combine(module.Root, "bin", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"#!/bin/sh\nexec zig {command} \"$@\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return path;
        }

        var cc = Wrapper("cc", $"cc -target {triple}");
        var cxx = Wrapper("c++", $"c++ -target {triple}");
        var ar = Wrapper("ar", "ar");
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + "\ntoolchains = Toolchains.Define(\"//toolchains:bsd\");")
            .Write("toolchains/BUILD.csx", $$"""
                using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
                using Progmasoft.Tazel.Dsl.Common;
                CC.Toolchain("bsd", new()
                {
                    ExecutionPlatform = Platform.{{Host.Platform}},
                    TargetPlatform = Platform.{{member}},
                    Tools = new() { CCompiler = "{{cc}}", CppCompiler = "{{cxx}}", Linker = "{{cxx}}", Archiver = "{{ar}}" },
                });
                """)
            .Write("BUILD.csx", """
                using Progmasoft.Tazel.Dsl.Build.Prelude;
                CC.StaticLibrary("greeting", new() { Sources = ["greeting.cpp"] });
                CC.Executable("app", new() { Sources = ["main.cpp"], Depends = [":greeting"] });
                """)
            .Write("greeting.cpp", "#include <string>\nstd::string greeting() { return \"hi\"; }\n")
            .Write("main.cpp", "#include <iostream>\n#include <string>\nstd::string greeting();\nint main() { std::cout << greeting() << '\\n'; }\n");
        var (status, output) = await module.TazelAsync(".", "build", "//:app", "--platform", platform);
        Assert.True(status == 0, output);
        Assert.True(IsFor(Built(module, "app"), system), $"//:app is not a {system} program");
    }

    [Fact]
    public async Task ZigLinksNothingForDragonFlyAndSaysWhy()
    {
        using var module = ZigModule("DragonFlyBSDAmd64", "Zig.Executable(\"app\", new() { RootSourceFile = \"main.zig\" });\n")
            .Write("main.zig", ZigProgram);
        var (status, output) = await module.TazelAsync(".", "build", "//:app", "--platform", "dragonfly-bsd-amd64");
        Assert.Equal(1, status);
        Assert.Contains("zig brings no libc for DragonFly BSD, so it cannot link //:app for dragonfly-bsd-amd64", output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZigBuildsAStaticLibraryForDragonFly()
    {
        Assert.SkipWhen(Host.Which("zig") is null, "no zig on PATH");
        using var module = ZigModule("DragonFlyBSDAmd64", "Zig.StaticLibrary(\"core\", new() { RootSourceFile = \"core.zig\" });\n")
            .Write("core.zig", "export fn add(a: i32, b: i32) i32 { return a + b; }\n");
        var (status, output) = await module.TazelAsync(".", "build", "//:core", "--platform", "dragonfly-bsd-amd64");
        Assert.True(status == 0, output);
    }
}
