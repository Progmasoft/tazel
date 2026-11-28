// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>
/// The command lines for other platforms, planned with toolchains of files that are never run: macOS libraries and
/// Objective-C, Apple's libtool, fully static programs, Zig libraries with their headers, and each Zig target.
/// </summary>
public sealed class CrossPlannerTests
{
    private static string Toolchains(string root, Platform target, string archiver) => $$"""
        using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
        CC.Toolchain("cc", new()
        {
            ExecutionPlatform = Platform.{{Host.Platform}},
            TargetPlatform = Platform.{{target}},
            Tools = new()
            {
                CCompiler = @"{{Path.Combine(root, "tools", "clang")}}",
                CppCompiler = @"{{Path.Combine(root, "tools", "clang++")}}",
                Archiver = @"{{Path.Combine(root, "tools", archiver)}}",
            },
        });
        ObjectiveC.Toolchain("objc", new()
        {
            ExecutionPlatform = Platform.{{Host.Platform}},
            TargetPlatform = Platform.{{target}},
            Tools = new()
            {
                CCompiler = @"{{Path.Combine(root, "tools", "clang")}}",
                CppCompiler = @"{{Path.Combine(root, "tools", "clang++")}}",
                Archiver = @"{{Path.Combine(root, "tools", archiver)}}",
            },
        });
        Zig.Toolchain("zig", new()
        {
            ExecutionPlatform = Platform.{{Host.Platform}},
            TargetPlatform = Platform.{{target}},
            Tools = new() { Zig = @"{{Path.Combine(root, "tools", "zig")}}" },
        });
        """;

    private const string Build = """
        using System;
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        ObjectiveC.SharedLibrary("kit", new()
        {
            Sources = ["kit/a.m", "kit/b.mm", "kit/c.s", "kit/kit.h"],
            NonAutomaticReferenceCountingSources = ["kit/b.mm"],
            EnableClangModules = true,
            ModuleName = "Kit",
            ModuleMap = "kit/module.modulemap",
            LibraryVersion = new Version(2, 3, 4),
            SdkFrameworks = ["Foundation"],
            WeakSdkFrameworks = ["AppKit"],
            SdkDynamicLibraries = ["z"],
        });
        ObjectiveC.StaticLibrary("plain", new() { Sources = ["kit/p.m"] });
        CC.Executable("tool", new()
        {
            Sources = ["tool.c"],
            Depends = [":kit", ":plain"],
            LinkingMode = LinkingMode.FullyStatic,
            StampBuildInformation = true,
            AdditionalLinkerInputs = ["extra.o"],
        });
        Zig.StaticLibrary("zs", new() { RootSourceFile = "z/s.zig", EmitHeader = "zs.h" });
        Zig.SharedLibrary("zd", new() { RootSourceFile = "z/d.zig", EmitHeader = "zd.h", LibraryVersion = new Version(1, 0) });
        Zig.Executable("zapp", new() { RootSourceFile = "z/main.zig", Depends = [":zs", ":zd"] });
        """;

    private static TemporaryModule Module(Platform target, string archiver)
    {
        var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + "\ntoolchains = Toolchains.Define(\"//toolchains:cc\", \"//toolchains:objc\", \"//toolchains:zig\");\n")
            .Write("toolchains/BUILD.csx", Toolchains(module.Root, target, archiver))
            .Write("BUILD.csx", Build)
            .Write("kit/a.m", "").Write("kit/b.mm", "").Write("kit/c.s", "").Write("kit/kit.h", "").Write("kit/p.m", "")
            .Write("kit/module.modulemap", "").Write("tool.c", "").Write("extra.o", "")
            .Write("z/s.zig", "").Write("z/d.zig", "").Write("z/main.zig", "")
            .Write("tools/clang", "").Write("tools/clang++", "").Write("tools/" + archiver, "").Write("tools/zig", "");
        return module;
    }

    private static async Task<List<BuildAction>> PlanAsync(TemporaryModule module, Platform target, params string[] names)
    {
        var workspace = await Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Release, target), new Version(0, 1, 0));
        var options = new BuildOptions { Platform = target, FeaturesOff = [] };
        var planner = new Planner(workspace, options, new ToolchainChooser(workspace, target));
        var actions = new List<BuildAction>();
        foreach (var name in names)
        {
            Collect(planner.Plan(workspace.Targets[new TargetLabel(PackageId.Root, name)]).Main!, actions);
        }

        return actions;
    }

    private static void Collect(BuildAction action, List<BuildAction> into)
    {
        if (into.Contains(action))
        {
            return;
        }

        foreach (var prerequisite in action.Prerequisites)
        {
            Collect(prerequisite, into);
        }

        into.Add(action);
    }

    private static string Line(IEnumerable<BuildAction> actions, string description) =>
        string.Join(' ', (actions.SingleOrDefault(action => action.Description.StartsWith(description, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"no action '{description}' among: {string.Join(" | ", actions.Select(a => a.Description))}"))
            .Command.Skip(1));

    [Fact]
    public async Task MacOSLibrariesObjectiveCAndAppleLibtool()
    {
        using var module = Module(Platform.MacOSArm64, "libtool");
        var actions = await PlanAsync(module, Platform.MacOSArm64, "tool");

        Assert.Contains("-fobjc-arc", Line(actions, "Compiling //:kit kit/a.m"), StringComparison.Ordinal);
        var nonArc = Line(actions, "Compiling //:kit kit/b.mm");
        Assert.Contains("-fno-objc-arc", nonArc, StringComparison.Ordinal);
        Assert.Contains("-fmodules", nonArc, StringComparison.Ordinal);
        Assert.Contains("-fmodule-name=Kit", nonArc, StringComparison.Ordinal);
        Assert.Contains("-fmodule-map-file=kit/module.modulemap", nonArc, StringComparison.Ordinal);
        Assert.DoesNotContain(actions, action => action.Description.Contains("kit.h", StringComparison.Ordinal));
        Assert.Contains(actions, action => action.Description.StartsWith("Compiling //:kit kit/c.s", StringComparison.Ordinal));
        Assert.DoesNotContain("-fmodules", Line(actions, "Compiling //:plain kit/p.m"), StringComparison.Ordinal);

        var dylib = Line(actions, "Linking //:kit");
        Assert.Contains("-dynamiclib -install_name @rpath/libkit.2.dylib", dylib, StringComparison.Ordinal);
        Assert.Contains("-current_version 2.3.4 -compatibility_version 2.0.0", dylib, StringComparison.Ordinal);
        Assert.Contains("-framework Foundation", dylib, StringComparison.Ordinal);
        Assert.Contains("-weak_framework AppKit", dylib, StringComparison.Ordinal);
        Assert.Contains("-lz", dylib, StringComparison.Ordinal);
        Assert.Contains("-static -o", Line(actions, "Archiving //:plain"), StringComparison.Ordinal);

        var tool = Line(actions, "Compiling //:tool tool.c");
        Assert.Contains("TAZEL_BUILD_LABEL=\"//:tool\"", tool, StringComparison.Ordinal);
        Assert.Contains("TAZEL_BUILD_VERSION=\"0.1.0\"", tool, StringComparison.Ordinal);
        var link = Line(actions, "Linking //:tool");
        Assert.Contains("-static", link, StringComparison.Ordinal);
        // As in Bazel: a file the linker options may name, an input of the link and not an argument.
        Assert.Contains("extra.o", actions.Single(action => action.Description == "Linking //:tool").Inputs);
        Assert.DoesNotContain("extra.o", link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZigLibrariesWithTheirHeaders()
    {
        using var module = Module(Platform.MacOSArm64, "libtool");
        var actions = await PlanAsync(module, Platform.MacOSArm64, "zapp");

        var archive = Line(actions, "Compiling //:zs");
        Assert.Contains("build-lib", archive, StringComparison.Ordinal);
        Assert.Contains("-femit-h=", archive, StringComparison.Ordinal);
        var shared = Line(actions, "Compiling //:zd");
        Assert.Contains("-dynamic", shared, StringComparison.Ordinal);
        Assert.Contains("zd.h", shared, StringComparison.Ordinal);
        Assert.Contains("-fsoname=libzd.1.dylib", shared, StringComparison.Ordinal);
        Assert.Contains("@loader_path/", Line(actions, "Compiling //:zapp"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALinuxSharedZigLibraryHasItsSoname()
    {
        using var module = Module(Platform.LinuxAarch64, "llvm-ar");
        var actions = await PlanAsync(module, Platform.LinuxAarch64, "zapp");
        Assert.Contains("-fsoname=libzd.so.1", Line(actions, "Compiling //:zd"), StringComparison.Ordinal);
        Assert.Contains("-target aarch64-linux", Line(actions, "Compiling //:zapp"), StringComparison.Ordinal);
        Assert.Contains("$ORIGIN/", Line(actions, "Compiling //:zapp"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WindowsX64", "x86_64-windows")]
    [InlineData("WindowsArm64", "aarch64-windows")]
    [InlineData("MacOSX86_64", "x86_64-macos")]
    [InlineData("FreeBSDAmd64", "x86_64-freebsd")]
    [InlineData("FreeBSDAarch64", "aarch64-freebsd")]
    [InlineData("OpenBSDAmd64", "x86_64-openbsd")]
    [InlineData("OpenBSDAarch64", "aarch64-openbsd")]
    [InlineData("DragonFlyBSDAmd64", "x86_64-dragonfly")]
    public async Task EachPlatformIsItsZigTarget(string platform, string triple)
    {
        var target = Enum.Parse<Platform>(platform);
        using var module = Module(target, "llvm-ar");
        var actions = await PlanAsync(module, target, "zs");
        Assert.Contains($"-target {triple}", Line(actions, "Compiling //:zs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectiveCInACTargetAndAFileOfNoLanguageAreRefused()
    {
        using var module = Module(Platform.LinuxAmd64, "llvm-ar");
        module.Write("BUILD.csx", """
            using Progmasoft.Tazel.Dsl.Build.Prelude;
            CC.StaticLibrary("objc", new() { Sources = ["kit/a.m"] });
            CC.StaticLibrary("text", new() { Sources = ["notes.txt"] });
            """).Write("notes.txt", "");

        var error = await Assert.ThrowsAsync<ProjectException>(() => PlanAsync(module, Platform.LinuxAmd64, "objc"));
        Assert.Contains("is Objective-C; declare //:objc with ObjectiveC", error.Message, StringComparison.Ordinal);
        error = await Assert.ThrowsAsync<ProjectException>(() => PlanAsync(module, Platform.LinuxAmd64, "text"));
        Assert.Contains("its extension is not C, C++, Objective-C or assembly", error.Message, StringComparison.Ordinal);
    }
}
