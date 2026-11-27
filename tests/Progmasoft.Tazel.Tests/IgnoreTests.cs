// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Dsl.Configuration;
using Progmasoft.Tazel.Project;
using Progmasoft.Tazel.Project.Ignore;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class GitignoreTests
{
    private static bool? Matches(string rule, string path, bool directory = false)
    {
        var parsed = Gitignore.Parse(rule);
        if (parsed is null)
        {
            return null;
        }

        var (pattern, _, directoryOnly) = parsed.Value;
        return (!directoryOnly || directory) && pattern.IsMatch(path);
    }

    [Theory]
    [InlineData("*.log", "a.log", true)]
    [InlineData("*.log", "deep/down/a.log", true)]
    [InlineData("*.log", "a.log.txt", false)]
    [InlineData("/a.log", "a.log", true)]
    [InlineData("/a.log", "sub/a.log", false)]
    [InlineData("doc/*.txt", "doc/a.txt", true)]
    [InlineData("doc/*.txt", "doc/x/a.txt", false)]
    [InlineData("doc/*.txt", "x/doc/a.txt", false)]
    [InlineData("**/foo", "foo", true)]
    [InlineData("**/foo", "a/b/foo", true)]
    [InlineData("**/foo/bar", "a/foo/bar", true)]
    [InlineData("abc/**", "abc/x", true)]
    [InlineData("abc/**", "abc/x/y", true)]
    [InlineData("abc/**", "abc", false)]
    [InlineData("a/**/b", "a/b", true)]
    [InlineData("a/**/b", "a/x/y/b", true)]
    [InlineData("a/**/b", "a/xb", false)]
    [InlineData("?.c", "a.c", true)]
    [InlineData("?.c", "ab.c", false)]
    [InlineData("[ab].c", "b.c", true)]
    [InlineData("[ab].c", "c.c", false)]
    [InlineData("[a-c].c", "b.c", true)]
    [InlineData("[!a].c", "a.c", false)]
    [InlineData("[!a].c", "b.c", true)]
    [InlineData("[^a].c", "b.c", true)]
    [InlineData("a*b", "a/b", false)]
    [InlineData("\\#name", "#name", true)]
    [InlineData("\\!name", "!name", true)]
    [InlineData("a\\*", "a*", true)]
    [InlineData("a\\*", "ab", false)]
    [InlineData("name   ", "name", true)]
    [InlineData("name\\ ", "name ", true)]
    [InlineData("[ab", "[ab", true)]
    [InlineData("a.b", "aXb", false)]
    public void MatchesAsGitDoes(string rule, string path, bool expected) => Assert.Equal(expected, Matches(rule, path));

    [Fact]
    public void ATrailingSlashMeansDirectoriesOnly()
    {
        Assert.Equal(true, Matches("build/", "build", directory: true));
        Assert.Equal(false, Matches("build/", "build", directory: false));
        Assert.Equal(true, Matches("build/", "x/build", directory: true));
    }

    [Fact]
    public void NegationAndEmptyRules()
    {
        Assert.True(Gitignore.Parse("!keep.log")!.Value.Negated);
        Assert.Null(Gitignore.Parse("   "));
        Assert.Null(Gitignore.Parse("!"));
    }
}

[Collection(nameof(TazelHomeCollection))]
public sealed class IgnoreTests
{
    private sealed class Home : IDisposable
    {
        private readonly string? _was = Environment.GetEnvironmentVariable(TazelHome.Variable);

        public Home(string value) => Environment.SetEnvironmentVariable(TazelHome.Variable, value);

        public void Dispose() => Environment.SetEnvironmentVariable(TazelHome.Variable, _was);
    }

    private static IgnoreException Problem(Action read) => Assert.Throws<IgnoreException>(read);

    [Fact]
    public void TheLastMatchingRuleDecidesAndNestedFilesComeAfter()
    {
        using var module = new TemporaryModule()
            .Write(".tazelignore", "*.log\n!keep.log\nsub/drop.txt\n")
            .Write("sub/.tazelignore", "!*.log\n*.tmp\n");
        var rules = IgnoreRules.For(module.Root);
        Assert.True(rules.Ignored("a.log", false));
        Assert.False(rules.Ignored("keep.log", false));
        Assert.False(rules.Ignored("sub/a.log", false));
        Assert.True(rules.Ignored("sub/x.tmp", false));
        Assert.False(rules.Ignored("x.tmp", false));
        Assert.True(rules.Ignored("sub/drop.txt", false));
    }

    [Fact]
    public void NothingInsideAnIgnoredDirectoryComesBack()
    {
        using var module = new TemporaryModule().Write(".tazelignore", "gen/\n!gen/keep.c\n");
        var rules = IgnoreRules.For(module.Root);
        Assert.True(rules.Ignored("gen", true));
        Assert.True(rules.Ignored("gen/keep.c", false));
        Assert.True(rules.Ignored("gen/deep/x.c", false));
        Assert.False(rules.Ignored("gen", false));
    }

    [Fact]
    public void RegexRulesMatchThePathFromTheirDirectory()
    {
        using var module = new TemporaryModule()
            .Write(".tazelignore", "regex:^src/.*\\.generated\\.cpp$\n!regex:^src/required\\.generated\\.cpp$\n")
            .Write("lib/.tazelignore", "regex:^x\\.c$\n");
        var rules = IgnoreRules.For(module.Root);
        Assert.True(rules.Ignored("src/a.generated.cpp", false));
        Assert.True(rules.Ignored("src/deep/a.generated.cpp", false));
        Assert.False(rules.Ignored("src/required.generated.cpp", false));
        Assert.True(rules.Ignored("lib/x.c", false));
        Assert.False(rules.Ignored("x.c", false));
    }

    [Fact]
    public void ARegexThatRunsTooLongIsAnError()
    {
        using var module = new TemporaryModule().Write(".tazelignore", "regex:^(a+)+$\n");
        var error = Problem(() => IgnoreRules.For(module.Root).Ignored(new string('a', 40) + "!", false));
        Assert.Contains("took more than 1 s", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("regex:([a-z", "is not a regular expression")]
    [InlineData("pragma:twice", "is not a pragma")]
    [InlineData("*.log\npragma:once", "must come first")]
    [InlineData("include:nothing", "needs one path")]
    [InlineData("include:\"*.txt\"", "has a wildcard")]
    [InlineData("include:\"missing.txt\"", "is not a file beside this one")]
    [InlineData("import:</etc/x>", "is absolute")]
    public void SaysWhatIsWrongWithALine(string text, string message)
    {
        using var module = new TemporaryModule().Write(".tazelignore", text);
        var error = Problem(() => IgnoreFile.Read(Path.Combine(module.Root, ".tazelignore"), text));
        Assert.Contains(message, error.Diagnostic!.Message, StringComparison.Ordinal);
        Assert.Equal(Output.ErrorCodes.IgnoreInvalid, error.Diagnostic.Code);
    }

    [Fact]
    public void IncludesImportsAndOnce()
    {
        using var module = new TemporaryModule()
            .Write("config/a.ignore.txt", "include:\"b.ignore.txt\"\n*.a\n")
            .Write("config/b.ignore.txt", "pragma:once\n*.b\n")
            .Write("config/loop.ignore.txt", "include:\"loop.ignore.txt\"\n");
        var root = Path.Combine(module.Root, ".tazelignore");
        var rules = IgnoreFile.Read(root, "include:\"config/a.ignore.txt\"\nimport:\"config/a.ignore.txt\"\ninclude:\"config/b.ignore.txt\"\n*.c\n");
        Assert.Equal(["*.b", "*.a", "*.c"], rules.Select(rule => rule.Text));
        var cycle = Problem(() => IgnoreFile.Read(root, "include:\"config/loop.ignore.txt\"\n"));
        Assert.Contains("includes itself", cycle.Diagnostic!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalIncludesComeFromTazelHome()
    {
        using var module = new TemporaryModule().Write("home/Include/Ignore/common.ignore.txt", "*.o\n");
        using var home = new Home(Path.Combine(module.Root, "home"));
        var rules = IgnoreFile.Read(Path.Combine(module.Root, ".tazelignore"), "import:<common.ignore.txt>\n");
        Assert.Equal("*.o", Assert.Single(rules).Text);
    }

    [Fact]
    public void ABackslashMakesADirectiveWordAName()
    {
        using var module = new TemporaryModule().Write(".tazelignore", "\\regex:name\n\\pragma:x\n!\\regex:keep\n");
        var rules = IgnoreRules.For(module.Root);
        Assert.True(rules.Ignored("regex:name", false));
        Assert.True(rules.Ignored("pragma:x", false));
        Assert.True(IgnoreFile.NeedsEscape("regex:x"));
        Assert.True(IgnoreFile.NeedsEscape("!regex:x"));
        Assert.False(IgnoreFile.NeedsEscape("!import:x"));
    }

    private static TemporaryModule Module() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile)
        .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.Executable(\"app\", new() { Sources = new Glob(\"**/*.c\"), Depends = [\"//skipped:lib\"] });\n")
        .Write("main.c", "int f(void);\nint main(void) { return f(); }\n")
        .Write("gen/bad.c", "#error ignored\n")
        .Write("skipped/BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"lib\", new() { Sources = [\"f.c\"], Visibility = Visibility.Public });\n")
        .Write("skipped/f.c", "int f(void) { return 0; }\n")
        .Write("other/BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"o\", new() { Sources = [\"o.c\"] });\n")
        .Write("other/o.c", "int o(void) { return 0; }\n")
        .Write(".tazelignore", "gen/\nskipped/\nother/\n");

    [Fact]
    public void DiscoveryAndGlobsLeaveIgnoredPathsOut()
    {
        using var module = Module();
        Assert.Equal([""], ModuleFiles.Packages(module.Root).Select(package => package.Path));
        var files = FileSets.Resolve(new Glob("**/*.c"), module.Root, module.Root, "BUILD.csx:2");
        Assert.Equal(["main.c"], files);
        // An explicit path is not ignored.
        Assert.Equal(["gen/bad.c"], FileSets.Resolve(["gen/bad.c"], module.Root, module.Root, "BUILD.csx:2"));
    }

    [Fact]
    public async Task ALabelNamesAnIgnoredPackageAndPatternsDoNot()
    {
        Assert.SkipWhen(Driver.Host.Which("cc") is null && Driver.Host.Which("gcc") is null && Driver.Host.Which("clang") is null, "no C compiler");
        using var module = Module();
        var (status, output) = await module.TazelAsync(".", "--color", "never", "build", "//:app");
        Assert.True(status == 0, output);
        (status, output) = await module.TazelAsync(".", "--color", "never", "query", "//...");
        Assert.Equal(0, status);
        Assert.Contains("//:app", output, StringComparison.Ordinal);
        Assert.DoesNotContain("//skipped:lib", output, StringComparison.Ordinal);
        Assert.DoesNotContain("//other:o", output, StringComparison.Ordinal);
        (status, output) = await module.TazelAsync(".", "--color", "never", "build", "//other:o");
        Assert.True(status == 0, output);
    }

    [Fact]
    public void ModuleArchivesLeaveIgnoredFilesOut()
    {
        using var module = Module();
        var files = ModuleArchive.Files(module.Root, _ => false);
        Assert.Contains("main.c", files);
        Assert.DoesNotContain(files, file => file.StartsWith("gen/", StringComparison.Ordinal) || file.StartsWith("skipped/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateFromEscapesDirectiveWordsAndNeverOverwrites()
    {
        using var module = new TemporaryModule().Write("home/keep", "").Write(".gitignore", "# kept\nregex:literal\n!regex:too\n*.log\n");
        using var home = new Home(Path.Combine(module.Root, "home"));
        var (status, output) = await module.TazelAsync(".", "--color", "never", "ignore", "create-from", ".gitignore");
        Assert.True(status == 0, output);
        Assert.Equal("# kept\n\\regex:literal\n!\\regex:too\n*.log\n", File.ReadAllText(Path.Combine(module.Root, ".tazelignore")));
        Assert.Equal("# kept\nregex:literal\n!regex:too\n*.log\n", File.ReadAllText(Path.Combine(module.Root, ".gitignore")));
        (status, output) = await module.TazelAsync(".", "--color", "never", "ignore", "create-from", ".gitignore");
        Assert.Equal(1, status);
        Assert.Contains("already here", output, StringComparison.Ordinal);
        (status, _) = await module.TazelAsync(".", "--color", "never", "ignore", "create-from", "missing");
        Assert.Equal(1, status);
    }

    [Fact]
    public async Task AddAndDeleteEditOnlyThisDirectorysFile()
    {
        using var module = new TemporaryModule().Write("home/keep", "").Write(".tazelignore", "# comment\n*.log\n\n*.o\n*.o\n");
        using var home = new Home(Path.Combine(module.Root, "home"));
        var file = Path.Combine(module.Root, ".tazelignore");
        Assert.Equal(0, (await module.TazelAsync(".", "ignore", "add", "regex:^x$")).Status);
        Assert.Contains("already", (await module.TazelAsync(".", "--color", "never", "ignore", "add", "*.log")).Output, StringComparison.Ordinal);
        Assert.Equal(1, (await module.TazelAsync(".", "ignore", "add", "regex:([")).Status);
        Assert.Equal(1, (await module.TazelAsync(".", "ignore", "add", "include:\"x\"")).Status);
        Assert.Equal(0, (await module.TazelAsync(".", "ignore", "del", "*.o")).Status);
        Assert.Equal("# comment\n*.log\n\nregex:^x$\n", File.ReadAllText(file));
        Assert.Equal(1, (await module.TazelAsync(".", "ignore", "delete", "*.o")).Status);
        Assert.Equal(0, (await module.TazelAsync(".", "ignore", "delete", "regex:^x$")).Status);
        Assert.Equal("# comment\n*.log\n\n", File.ReadAllText(file));
        File.Delete(file);
        Assert.Equal(1, (await module.TazelAsync(".", "ignore", "add", "*.x")).Status);
    }

    [Fact]
    public async Task ValidateReadsTheFilesOfIgnoredDirectoriesToo()
    {
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write(".tazelignore", "hidden/\n")
            .Write("hidden/.tazelignore", "regex:([\n");
        var (status, output) = await module.TazelAsync(".", "--color", "never", "ignore", "validate");
        Assert.Equal(1, status);
        Assert.Contains("is not a regular expression", output, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(module.Root, "hidden", ".tazelignore"), "*.x\n");
        (status, output) = await module.TazelAsync(".", "--color", "never", "ignore", "validate");
        Assert.True(status == 0, output);
        Assert.Contains("valid: hidden/.tazelignore (1 rule)", output, StringComparison.Ordinal);
    }
}
