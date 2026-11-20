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

/// <summary>The command lines a build makes, for a toolchain of files that are never run, so they are the same everywhere.</summary>
public sealed class PlannerTests
{
    private static string ModuleWithToolchain() => TemporaryModule.ModuleFile + """

        toolchains = Toolchains.Define("//toolchains:fake", "//toolchains:zig");
        """;

    private static string Toolchain(string root) => $$"""
        using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
        var warnings = CC.Feature("warnings", new()
        {
            Enabled = true,
            FlagSets = [ new() { Actions = [CCAction.CCompile, CCAction.CppCompile], Flags = ["-Wall"] } ]
        });
        var lto = CC.Feature("lto", new()
        {
            FlagSets = [ new() { Actions = [CCAction.CppCompile, CCAction.CppLinkSharedLibrary], Flags = ["-flto"] } ]
        });
        CC.Toolchain("fake", new()
        {
            ExecutionPlatform = Platform.{{Host.Platform}},
            TargetPlatform = Platform.LinuxAmd64,
            Tools = new()
            {
                CCompiler = @"{{Path.Combine(root, "tools", "cc")}}",
                CppCompiler = @"{{Path.Combine(root, "tools", "c++")}}",
                Archiver = @"{{Path.Combine(root, "tools", "llvm-ar")}}",
            },
            Features = [warnings, lto],
        });
        Zig.Toolchain("zig", new()
        {
            ExecutionPlatform = Platform.{{Host.Platform}},
            TargetPlatform = Platform.LinuxAmd64,
            Tools = new() { Zig = @"{{Path.Combine(root, "tools", "zig")}}" },
        });
        """;

    private const string Build = """
        using System;
        using System.Collections.Generic;
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        var math = CC.StaticLibrary("math", new()
        {
            Sources = new Glob("src/*.cpp"),
            Headers = ["include/math.hpp"],
            IncludeDirectories = ["include"],
            CppCompilerOptions = ["-std=c++23"],
            PreprocessorDefinitions = ["MATH_API=1"],
            LocalPreprocessorDefinitions = ["MATH_BUILD=1"],
            LinkerOptions = ["-lm"],
            AlwaysLink = true,
        });
        CC.SharedLibrary("shared", new()
        {
            Sources = ["src/shared.c"],
            Depends = [math],
            CCompilerOptions = ["-std=c17"],
            LibraryVersion = new Version(1, 2, 0),
            Features = [CC.Enable("lto"), CC.Disable("warnings")],
        });
        CC.Executable("app", new()
        {
            Sources = ["src/main.cc"],
            Depends = [":shared", math],
            LinkingMode = LinkingMode.PreferStatic,
        });
        Zig.Module("core", new() { RootSourceFile = "zig/core.zig" });
        Zig.Executable("zapp", new()
        {
            RootSourceFile = "zig/main.zig",
            ModuleImports = new Dictionary<string, string> { ["core"] = ":core" },
            Depends = [math],
            CSources = ["zig/shim.c"],
            CCompilerOptions = ["-std=c17"],
            LinkCStandardLibrary = true,
        });
        """;

    [Fact]
    public async Task TheCommandsOfABuild()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", ModuleWithToolchain())
            .Write("toolchains/BUILD.csx", Toolchain(module.Root))
            .Write("BUILD.csx", Build)
            .Write("src/a.cpp", "").Write("src/b.cpp", "").Write("src/shared.c", "").Write("src/main.cc", "")
            .Write("include/math.hpp", "").Write("zig/core.zig", "").Write("zig/main.zig", "").Write("zig/shim.c", "")
            .Write("tools/cc", "").Write("tools/c++", "").Write("tools/llvm-ar", "").Write("tools/zig", "");
        var workspace = await Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Debug, Platform.LinuxAmd64),
            new Version(0, 1, 0));
        var options = new BuildOptions
        {
            Platform = Platform.LinuxAmd64,
            FeaturesOff = [],
            CompilerOptions = ["-DFROM_COMMAND_LINE"],
            LinkerOptions = ["-Wl,--as-needed"],
        };
        var planner = new Planner(workspace, options, new ToolchainChooser(workspace, Platform.LinuxAmd64));
        var actions = new List<BuildAction>();
        foreach (var name in new[] { "app", "zapp" })
        {
            Collect(planner.Plan(workspace.Targets[new TargetLabel(PackageId.Root, name)]).Main!, actions);
        }

        // The program is left out: it is a path of this machine. Each argument is one line of the snapshot.
        string Shown(string text) => text.Replace(module.Root, "{root}", StringComparison.Ordinal);
        Snapshots.Match(actions.Select(action => new
        {
            action.Description,
            Arguments = action.Command.Skip(1).Select(Shown).ToList(),
            Inputs = action.Inputs.Select(Shown).ToList(),
            Outputs = action.Outputs.Select(Shown).ToList(),
        }).ToList());
    }

    [Fact]
    public async Task AFeatureATargetNamesMustBeInItsToolchain()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", ModuleWithToolchain())
            .Write("toolchains/BUILD.csx", Toolchain(module.Root))
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"x\", new() { Features = [CC.Enable(\"asan\")] });")
            .Write("tools/cc", "").Write("tools/c++", "").Write("tools/llvm-ar", "").Write("tools/zig", "");
        var workspace = await Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Debug, Platform.LinuxAmd64),
            new Version(0, 1, 0));
        var planner = new Planner(workspace, new BuildOptions { Platform = Platform.LinuxAmd64 },
            new ToolchainChooser(workspace, Platform.LinuxAmd64));
        var error = Assert.Throws<ProjectException>(() => planner.Plan(workspace.Targets.Values.Single()));
        Assert.Contains("has no feature 'asan'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherPlatformNeedsARegisteredToolchain()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"x\", new());");
        var other = Host.Platform == Platform.WindowsArm64 ? Platform.LinuxAmd64 : Platform.WindowsArm64;
        var workspace = await Workspace.LoadAsync(module.Root, new BuildContext(BuildMode.Debug, other), new Version(0, 1, 0));
        var planner = new Planner(workspace, new BuildOptions { Platform = other }, new ToolchainChooser(workspace, other));
        var error = Assert.Throws<ProjectException>(() => planner.Plan(workspace.Targets.Values.Single()));
        Assert.Contains($"builds for {other.Name()}", error.Message, StringComparison.Ordinal);
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
}
