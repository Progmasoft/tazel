// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class WorkspaceTests
{
    private const string Prelude = "using Progmasoft.Tazel.Dsl.Build.Prelude;\n";

    [Fact]
    public async Task LoadsTheTargetsOfEveryPackage()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", Prelude + """
                var core = CC.StaticLibrary("core", new() { Sources = ["core.c"], Visibility = Visibility.Module });
                CC.Executable("app", new() { Sources = ["main.c"], Depends = [core, "//lib:util"] });
                """)
            .Write("core.c", "").Write("main.c", "")
            .Write("lib/BUILD.csx", Prelude + """
                CC.StaticLibrary("util", new() { Sources = new Glob("*.c"), Visibility = Visibility.Public });
                """)
            .Write("lib/u.c", "");
        var workspace = await module.LoadAsync();
        var app = workspace.Targets[new TargetLabel(PackageId.Root, "app")];
        Assert.Equal(TargetKind.Executable, app.Kind);
        Assert.Equal(["//:core", "//lib:util"], app.Dependencies.Select(d => d.Label.ToString()));
        Assert.Equal("BUILD.csx:3", app.Place);
        Assert.Equal(["//", "//lib"], workspace.Packages.Select(p => p.ToString()).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("CC.Executable(\"a\", new() { Depends = [\":nope\"] });", "depends on //:nope, which is not declared")]
    [InlineData("CC.Executable(\"a\", new() { Depends = [\":b\"] }); CC.Executable(\"b\", new());", "only libraries are linked")]
    [InlineData("CC.StaticLibrary(\"a\", new()); CC.StaticLibrary(\"a\", new());", "'a' is declared twice")]
    [InlineData("CC.StaticLibrary(\"a\", new() { Depends = [\":b\"] }); CC.StaticLibrary(\"b\", new() { Depends = [\":a\"] });", "depend on each other")]
    [InlineData("CC.StaticLibrary(\"bad name\", new());", "must be letters")]
    [InlineData("CC.StaticLibrary(\"a\", new() { Sources = [\"missing.c\"] });", "")]
    [InlineData("CC.StaticLibrary(\"a\", new() { Nope = 1 });", "BUILD.csx:2:")]
    [InlineData("throw new System.InvalidOperationException(\"no\");", "InvalidOperationException: no")]
    public async Task ReportsWhatIsWrongWithItsPlace(string body, string message)
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + body);
        if (message.Length == 0)
        {
            // A missing source is found when the target is planned, not when the file runs.
            var workspace = await module.LoadAsync();
            Assert.Single(workspace.Targets);
            return;
        }

        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivateTargetIsSeenOnlyInItsFile()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("BUILD.csx", Prelude + "CC.Executable(\"app\", new() { Depends = [\"//lib:hidden\"] });")
            .Write("lib/BUILD.csx", Prelude + "CC.StaticLibrary(\"hidden\", new());");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("//lib:hidden is private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleFileMustDefineAValidModule()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", """
            using Progmasoft.Tazel.Dsl.Module.Prelude;
            module = Module.Define(new() { Group = "com.example", Name = "x", Version = "1.2" });
            """);
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("'1.2' is not a SemVer version", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleSaysWhichTazelItBuildsWith()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", """
            using Progmasoft.Tazel.Dsl.Module.Prelude;
            module = Module.Define(new() { Group = "com.example", Name = "x", Version = "1.0.0",
                Compatibility = TazelVersion.AtLeast(9, 0) });
            """);
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("builds with Tazel 9.0.0 or later; this is Tazel 0.1.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectiveCTargetsBuildForMacOSUnlessTheySayOtherwise()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + """
            ObjectiveC.StaticLibrary("mac", new());
            ObjectiveC.StaticLibrary("gnustep", new() { TargetConstraints = [Platform.LinuxAmd64] });
            """);
        var workspace = await module.LoadAsync();
        var mac = workspace.Targets[new TargetLabel(PackageId.Root, "mac")];
        var gnustep = workspace.Targets[new TargetLabel(PackageId.Root, "gnustep")];
        Assert.True(mac.BuildsFor(Dsl.Common.Platform.MacOSArm64));
        Assert.False(mac.BuildsFor(Dsl.Common.Platform.LinuxAmd64));
        Assert.True(gnustep.BuildsFor(Dsl.Common.Platform.LinuxAmd64));
    }

    [Fact]
    public async Task ARegisteredToolchainMustBeDeclared()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + "\ntoolchains = Toolchains.Define(\"//toolchains:clang\");")
            .Write("toolchains/BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;\n");
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains("no toolchain //toolchains:clang is declared", error.Message, StringComparison.Ordinal);
    }

    private static string Module(string group = "com.example", string name = "sample", string extra = "") => $$"""
        using Progmasoft.Tazel.Dsl.Module.Prelude;
        module = Module.Define(new() { Group = "{{group}}", Name = "{{name}}", Version = "0.1.0" });
        {{extra}}
        """;

    [Theory]
    [InlineData("com.example", "bad name", "", "the module name 'bad name' must be letters")]
    [InlineData("com..example", "sample", "", "the group 'com..example' must be names joined by dots")]
    [InlineData("", "sample", "", "the group '' must be names joined by dots")]
    [InlineData("com.example", "sample", "dependencies = Dependencies.Define(LocalModule.From(new() { Name = \"no way\", Include = \"x.tazel.tar.xz\", Sha256 = \"00\" }));", "the local module name 'no way' must be letters")]
    [InlineData("com.example", "sample", "toolchains = Toolchains.Define(\"//:a:b\");", "MODULE.csx: ")]
    public async Task AModuleFileWithAWrongNameIsRefused(string group, string name, string extra, string message)
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", Module(group, name, extra)).Write("BUILD.csx", Prelude);
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CC.Executable(\"a\", new() { Depends = [\":t\"] }); CC.Test(\"t\", new());", "//:t is a test; only libraries are linked")]
    [InlineData("Zig.Module(\"m\", new() { RootSourceFile = \"m.zig\" }); Zig.Executable(\"a\", new() { RootSourceFile = \"a.zig\", Depends = [\":m\"] });", "//:m is a Zig module; name it in ModuleImports")]
    [InlineData("CC.StaticLibrary(\"l\", new()); Zig.Executable(\"a\", new() { RootSourceFile = \"a.zig\", ModuleImports = new System.Collections.Generic.Dictionary<string, string> { [\"l\"] = \":l\" } });", "ModuleImports[\"l\"] names //:l, which is not a Zig module")]
    [InlineData("CC.Executable(\"a\", new() { Depends = [\"//:x:y\"] });", "BUILD.csx:2: ")]
    public async Task ADependencyOfTheWrongKindIsRefused(string body, string message)
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + body);
        var error = await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATargetIsWrittenAsItsLabel()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Prelude + "CC.StaticLibrary(\"core\", new());");
        var workspace = await module.LoadAsync();
        Assert.Equal("//:core", workspace.Targets.Values.Single().ToString());
    }
}
