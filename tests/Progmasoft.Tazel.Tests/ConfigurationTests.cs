// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Parser;
using Progmasoft.Tazel.Dsl.Envs;
using Progmasoft.Tazel.Dsl.Options;
using Progmasoft.Tazel.Output;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class ConfigurationTests
{
    private static TazelOptions Options(TemporaryModule module, string text, Platform? requested = null)
    {
        var file = Path.Combine(module.Root, "tazel_options.txt");
        File.WriteAllText(file, text);
        return TazelOptionsFile.Parse(text, "tazel_options.txt", file, requested);
    }

    private static Diagnostic Problem(Action read)
    {
        var error = Assert.ThrowsAny<Exception>(read);
        return error switch
        {
            TazelOptionsException { Diagnostic: { } d } => d,
            CustomCommandsException { Diagnostic: { } d } => d,
            _ => throw new Xunit.Sdk.XunitException($"no diagnostic: {error}"),
        };
    }

    private static ConfigurationContext Context(Platform host, Platform? target, Dictionary<string, string>? environment = null) =>
        new(SystemFacts.Of(host), target is { } t ? SystemFacts.Of(t) : null, environment ?? []);

    [Fact]
    public void IncludesFilesBesideEachFileThatNamesThem()
    {
        using var module = new TemporaryModule()
            .Write("config/cpus.options.txt", ".include \"deeper/memory.options.txt\"\nmicrovm-cpus: 3")
            .Write("config/deeper/memory.options.txt", "microvm-memory: 2048");
        var options = Options(module, ".include \"config/cpus.options.txt\"\nmicrovm-network: true;");
        Assert.Equal(3, options.MicroVMCpus.Amount);
        Assert.Equal(2048, options.MicroVMMemory.Amount);
        Assert.True(options.MicroVMNetwork);
    }

    [Fact]
    public void ImportAndPragmaOnceReadAFileOnce()
    {
        using var module = new TemporaryModule()
            .Write("a.options.txt", "microvm-cpus: 2")
            .Write("b.options.txt", ".pragma once\nmicrovm-memory: 512");
        // Read twice, either would set an option twice.
        var options = Options(module, ".import \"a.options.txt\"\n.import \"a.options.txt\"\n.include \"a.options.txt\"\n" +
            ".include \"b.options.txt\"\n.include \"b.options.txt\"\n");
        Assert.Equal(2, options.MicroVMCpus.Amount);
        Assert.Equal(512, options.MicroVMMemory.Amount);
        using var twice = new TemporaryModule().Write("a.options.txt", "microvm-cpus: 2");
        Assert.Equal(ErrorCodes.OptionsTwice, Problem(() => Options(twice, ".include \"a.options.txt\"\n.include \"a.options.txt\"\n")).Code);
    }

    [Fact]
    public void AFileThatIncludesItselfIsAnErrorUnlessItIsOnce()
    {
        using var module = new TemporaryModule()
            .Write("a.options.txt", ".include \"b.options.txt\"\nmicrovm-cpus: 2")
            .Write("b.options.txt", ".include \"a.options.txt\"\n")
            .Write("c.options.txt", ".pragma once\n.include \"c.options.txt\"\nmicrovm-cpus: 4");
        var cycle = Problem(() => Options(module, ".include \"a.options.txt\"\n"));
        Assert.Equal(ErrorCodes.IncludeTooDeep, cycle.Code);
        Assert.Contains("a.options.txt includes itself", cycle.Message, StringComparison.Ordinal);
        Assert.Contains("tazel_options.txt -> ", cycle.Message, StringComparison.Ordinal);
        Assert.Equal(4, Options(module, ".include \"c.options.txt\"\n").MicroVMCpus.Amount);
    }

    [Fact]
    public void IncludesNestAtMost64Deep()
    {
        using var module = new TemporaryModule();
        for (var i = 0; i < 70; i++)
        {
            module.Write($"n{i}.options.txt", $".include \"n{i + 1}.options.txt\"\n");
        }

        module.Write("n70.options.txt", "microvm-cpus: 1");
        var deep = Problem(() => Options(module, ".include \"n0.options.txt\"\n"));
        Assert.Equal(ErrorCodes.IncludeTooDeep, deep.Code);
        Assert.Contains("nest more than 64 deep", deep.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".include \"*.options.txt\"", "has a wildcard")]
    [InlineData(".include \"https://example.com/x.options.txt\"", "is a URL")]
    [InlineData(".include \"/etc/x.options.txt\"", "is absolute")]
    [InlineData(".include \"missing.options.txt\"", "is not a file beside")]
    [InlineData(".include missing.options.txt", "needs one path, in quotes or in angle brackets")]
    [InlineData(".include <../x.options.txt>", "has . or .. parts")]
    public void RefusesPathsItDoesNotTake(string directive, string message)
    {
        using var module = new TemporaryModule();
        var problem = Problem(() => Options(module, directive + "\n"));
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".pragma once;", "takes no ';'")]
    [InlineData("microvm-cpus: 1;\n.pragma once", "must come first")]
    [InlineData(".pragma twice", "is not a pragma")]
    [InlineData(".inclde \"x\"", ".inclde is not a directive")]
    [InlineData(".elif true", ".elif without an .if")]
    [InlineData(".end", ".end without an .if")]
    [InlineData(".if true\n.else\n.else\n.end", ".else after .else")]
    [InlineData(".if true\n.else\n.elif false\n.end", ".elif after .else")]
    [InlineData(".if true\n.else false\n.end", ".else takes no condition")]
    [InlineData(".if true\nmicrovm-cpus: 1;", ".if without its .end")]
    [InlineData(".if host_system.os = \"linux\"\n.end", "'=' is not an operator of conditions; compare with ==")]
    [InlineData(".if host_system.kernel == \"x\"\n.end", "is not a field")]
    [InlineData(".if host_system.os\n.end", "is a string, not true or false")]
    [InlineData(".if host_system.os == true\n.end", "compares a string with a boolean")]
    [InlineData(".if hots_system.os == \"x\"\n.end", "did you mean host_system?")]
    [InlineData(".if !host_system.os\n.end", "! takes true or false")]
    public void SaysWhatIsWrongWithADirective(string text, string message)
    {
        using var module = new TemporaryModule();
        var problem = Problem(() => Options(module, text));
        Assert.Contains(message, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisspeltDirectiveGetsTheNearest()
    {
        using var module = new TemporaryModule();
        Assert.Equal("did you mean .include?", Problem(() => Options(module, ".inclde \"x\"\n")).Help);
    }

    [Fact]
    public void ConditionsNest64DeepAtMost()
    {
        using var module = new TemporaryModule();
        var text = string.Concat(Enumerable.Repeat(".if true\n", 65)) + string.Concat(Enumerable.Repeat(".end\n", 65));
        Assert.Contains("nest more than 64 deep", Problem(() => Options(module, text)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true || false && false", true)]
    [InlineData("(true || false) && false", false)]
    [InlineData("!false && true", true)]
    [InlineData("host_system.os == \"linux\" && host_system.arch == \"amd64\"", true)]
    [InlineData("host_system.abi == \"gnu\" && host_system.platform == \"linux-amd64\"", true)]
    [InlineData("target_system.os == \"windows\" && target_system.abi == \"msvc\"", true)]
    [InlineData("defined(\"SET\") && env(\"SET\") == \"yes\" && !defined(\"UNSET\") && env(\"UNSET\") == \"\"", true)]
    [InlineData("'single' == `back`", false)]
    [InlineData("\"a\" != 'a'", false)]
    public void EvaluatesConditions(string text, bool expected)
    {
        var condition = new ConditionReader(text).Read();
        var context = Context(Platform.LinuxAmd64, Platform.WindowsX64, new Dictionary<string, string> { ["SET"] = "yes" });
        Assert.Equal(expected, ConditionEvaluator.Evaluate(condition, context.Host, context.Target, context.Environment));
    }

    [Fact]
    public void ABranchNotTakenOpensNoFileButIsRead()
    {
        using var module = new TemporaryModule();
        var options = Options(module, ".if false\n.include \"missing.options.txt\"\n.elif true\nmicrovm-cpus: 2;\n.else\nmicrovm-cpus: 3;\n.end\n");
        Assert.Equal(2, options.MicroVMCpus.Amount);
        // Its syntax is checked all the same.
        Assert.Equal(ErrorCodes.ConditionInvalid, Problem(() => Options(module, ".if false\n.elif nonsense\n.end\n")).Code);
    }

    [Fact]
    public void TheTargetIsChosenFirstThenTheRestIsReadForIt()
    {
        using var module = new TemporaryModule();
        const string Text = "platform: windows-x64;\n.if target_system.os == \"windows\"\n    microvm-memory: 4096;\n.else\n" +
            "    microvm-memory: 2048;\n.end\n.if host_system.os == \"linux\" || host_system.os != \"linux\"\n    microvm-cpus: 2;\n.end\n";
        var options = Options(module, Text);
        Assert.Equal(Platform.WindowsX64, options.Platform);
        Assert.Equal(4096, options.MicroVMMemory.Amount);
        Assert.Equal(2, options.MicroVMCpus.Amount);
        // The command line's platform is what target_system is.
        Assert.Equal(2048, Options(module, Text, Platform.LinuxAarch64).MicroVMMemory.Amount);
    }

    [Fact]
    public void APlatformChosenOnTheTargetIsAnError()
    {
        using var module = new TemporaryModule().Write("p.options.txt", "platform: linux-amd64;");
        var problem = Problem(() => Options(module, ".if target_system.os == \"linux\"\n    platform: windows-x64;\n.else\n    platform: linux-amd64;\n.end\n"));
        Assert.Equal(ErrorCodes.PlatformDependsOnTarget, problem.Code);
        Assert.Equal(2, problem.Line);
        // Through an include of such a branch too.
        Assert.Equal(ErrorCodes.PlatformDependsOnTarget,
            Problem(() => Options(module, ".if target_system.os == \"linux\" || true\n.include \"p.options.txt\"\n.end\n")).Code);
    }

    [Fact]
    public void AProblemInAnIncludedFileIsShownInThatFile()
    {
        using var module = new TemporaryModule().Write("bad.options.txt", "\nmicrovm-cpus: lots;");
        var problem = Problem(() => Options(module, "microvm-network: true;\n.include \"bad.options.txt\"\n"));
        Assert.EndsWith("bad.options.txt", problem.File, StringComparison.Ordinal);
        Assert.Equal(2, problem.Line);
        Assert.Equal("microvm-cpus: lots;", problem.SourceLine);
    }

    [Fact]
    public void CustomCommandsAndEnvsIncludeAndChooseToo()
    {
        using var module = new TemporaryModule()
            .Write("more.custom.txt", "hello: (\"echo\" \"hi\")")
            .Write("more.envs.txt", "name: 'demo'");
        var custom = CustomParser.Parse(".include \"more.custom.txt\"\n.if host_system.os == \"nowhere\"\nnever: (\"false\");\n.end\nbye: (\"echo\" \"bye\");",
            "tazel_custom.txt", Path.Combine(module.Root, "tazel_custom.txt"));
        Assert.Equal(["hello", "bye"], custom.Select(command => command.Name));
        var envs = TazelEnvsFile.Parse(".include \"more.envs.txt\"\nversion: '1.0';", "tazel_envs.txt", Path.Combine(module.Root, "tazel_envs.txt"));
        Assert.Equal(["name", "version"], envs.Select(entry => entry.Name));
    }

    [Fact]
    public void ADirectiveStartsALineOnly()
    {
        // In a string, or after other text, a dot is no directive.
        using var module = new TemporaryModule();
        var custom = CustomParser.Parse("say: (\"echo\" \".include x\");", "tazel_custom.txt", Path.Combine(module.Root, "tazel_custom.txt"));
        Assert.Single(custom);
        Assert.Equal(ErrorCodes.DirectiveUnknown,
            Problem(() => CustomParser.Parse(".hidden: (\"x\");", "tazel_custom.txt", Path.Combine(module.Root, "tazel_custom.txt"))).Code);
    }
}

/// <summary>The tests that set TAZEL_HOME, which every test of the process would see: one at a time.</summary>
[CollectionDefinition(nameof(TazelHomeCollection), DisableParallelization = true)]
public sealed class TazelHomeCollection
{
}

[Collection(nameof(TazelHomeCollection))]
public sealed class TazelHomeTests
{
    private static TazelOptions Options(TemporaryModule module, string text)
    {
        var file = Path.Combine(module.Root, "tazel_options.txt");
        File.WriteAllText(file, text);
        return TazelOptionsFile.Parse(text, "tazel_options.txt", file);
    }

    private sealed class Home : IDisposable
    {
        private readonly string? _was = Environment.GetEnvironmentVariable(TazelHome.Variable);

        public Home(string? value) => Environment.SetEnvironmentVariable(TazelHome.Variable, value);

        public void Dispose() => Environment.SetEnvironmentVariable(TazelHome.Variable, _was);
    }

    [Fact]
    public void IncludesFromTheIncludeDirectoryOfTazelHome()
    {
        using var module = new TemporaryModule().Write("home/Include/Options/security.options.txt", "sandboxing-with-microvm: true");
        using var home = new Home(Path.Combine(module.Root, "home"));
        Assert.True(Options(module, ".import <security.options.txt>\n").SandboxingWithMicroVM);
        var missing = Assert.Throws<TazelOptionsException>(() => Options(module, ".include <nope.options.txt>\n"));
        Assert.Contains("is not a file of", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkOutOfTheIncludeDirectoryIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need a privilege on Windows");
        using var module = new TemporaryModule().Write("outside.options.txt", "microvm-cpus: 9").Write("home/Include/Options/keep", "");
        File.CreateSymbolicLink(Path.Combine(module.Root, "home", "Include", "Options", "link.options.txt"),
            Path.Combine(module.Root, "outside.options.txt"));
        using var home = new Home(Path.Combine(module.Root, "home"));
        var error = Assert.Throws<TazelOptionsException>(() => Options(module, ".include <link.options.txt>\n"));
        Assert.Contains("through a link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATazelHomeThatIsNotAbsoluteIsAnError()
    {
        using var home = new Home("relative/home");
        Assert.Throws<ConfigurationHomeException>(() => TazelHome.Directory());
        using var module = new TemporaryModule();
        var error = Assert.Throws<TazelOptionsException>(() => Options(module, ".include <x.options.txt>\n"));
        Assert.Contains("not an absolute path", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultHomeIsDotTazelOrAppDataTazel()
    {
        using var home = new Home(null);
        var directory = TazelHome.Directory();
        Assert.Equal(OperatingSystem.IsWindows() ? "Tazel" : ".Tazel", Path.GetFileName(directory));
    }
}
