// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using CsCheck;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class GlobTests
{
    [Theory]
    [InlineData("src/*.cpp", "src/a.cpp", true)]
    [InlineData("src/*.cpp", "src/x/a.cpp", false)]
    [InlineData("src/**/*.cpp", "src/a.cpp", true)]
    [InlineData("src/**/*.cpp", "src/x/y/a.cpp", true)]
    [InlineData("**", "a/b/c", true)]
    [InlineData("a?.c", "ab.c", true)]
    [InlineData("a?.c", "a.c", false)]
    [InlineData("include/**", "include", true)]
    public void MatchesAsBazelGlobsDo(string pattern, string path, bool matches) =>
        Assert.Equal(matches, FileSets.Matches(pattern, path));

    [Fact]
    public void APathWithoutWildcardsMatchesOnlyItself() =>
        Gen.String[Gen.Char['a', 'z'], 1, 8].Array[1, 4].Sample(parts =>
        {
            var path = string.Join('/', parts);
            Assert.True(FileSets.Matches(path, path));
            Assert.True(FileSets.Matches("**", path));
            Assert.False(FileSets.Matches(path + "x", path));
        });

    [Fact]
    public void AGlobLeavesOutSubpackagesHiddenFilesAndWhatItExcludes()
    {
        using var module = new TemporaryModule();
        module.Write("src/a.c", "").Write("src/b.c", "").Write("src/.hidden.c", "").Write("src/sub/c.c", "")
            .Write("src/sub2/BUILD.csx", "").Write("src/sub2/d.c", "");
        var files = FileSets.Resolve(new Glob("src/**/*.c", ["src/b.c"]), module.Root, module.Root, "BUILD.csx:1");
        Assert.Equal(["src/a.c", "src/sub/c.c"], files);
    }

    [Theory]
    [InlineData("../x.c")]
    [InlineData("/etc/passwd")]
    [InlineData("a//b.c")]
    public void APathMustStayBelowItsProjectFile(string path)
    {
        using var module = new TemporaryModule();
        Assert.Throws<ProjectException>(() =>
            FileSets.Resolve([path], module.Root, module.Root, "BUILD.csx:1"));
    }
}

public sealed class DepfileTests
{
    [Fact]
    public void ReadsTheInputsOfADepfile()
    {
        var inputs = Depfiles.Inputs("out/a.o: src/a.c \\\n include/a\\ b.h \\\r\n x$$y.h\n");
        Assert.Equal(["src/a.c", "include/a b.h", "x$y.h"], inputs);
    }

    [Fact]
    public void ADepfileWithoutAColonHasNoInputs() => Assert.Empty(Depfiles.Inputs("nothing"));
}

public sealed class ArchiveTests
{
    [Fact]
    public void AnArchiveIsTheSameWhenWrittenTwice()
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", "").Write("src/a.c", "int a;")
            .Write(".git/HEAD", "x").Write("docs/x.md", "x");
        var files = ModuleArchive.Files(module.Root, relative => relative.StartsWith("docs/", StringComparison.Ordinal));
        Assert.Equal(["BUILD.csx", "MODULE.csx", "src/a.c"], files);
        var first = Path.Combine(module.Root, "out", "one" + ModuleArchive.Extension);
        var second = Path.Combine(module.Root, "out", "two" + ModuleArchive.Extension);
        ModuleArchive.Write(module.Root, "com.example.sample-0.1.0", first, files);
        File.SetLastWriteTimeUtc(Path.Combine(module.Root, "src/a.c"), DateTime.UtcNow.AddDays(-3));
        ModuleArchive.Write(module.Root, "com.example.sample-0.1.0", second, files);
        Assert.Equal(Sha256Sums.Sha256(first), Sha256Sums.Sha256(second));

        var unpacked = ModuleArchive.Unpack(first, Path.Combine(module.Root, "unpacked"));
        Assert.Equal("int a;", File.ReadAllText(Path.Combine(unpacked, "src/a.c")));
        Assert.Equal("com.example.sample-0.1.0", Path.GetFileName(unpacked));
    }

    [Fact]
    public void ReadsTheLinesOfSha256Sums()
    {
        var sum = new string('a', 64);
        Assert.Equal(sum, Sha256Sums.SumFor($"{sum}  x.tazel.tar.xz\n\n{new string('b', 64)} *y\n", "x.tazel.tar.xz", "s"));
        Assert.Null(Sha256Sums.SumFor($"{sum}  other\n", "x", "s"));
        Assert.Throws<ProjectException>(() => Sha256Sums.SumFor("not a line\n", "x", "s"));
        Assert.Throws<ProjectException>(() => Sha256Sums.SumFor($"{sum}  x\n{sum}  x\n", "x", "s"));
    }
}

public sealed class ActionCacheTests
{
    [Fact]
    public void AnActionIsCurrentWhileItsFilesKeepTheirContent()
    {
        using var module = new TemporaryModule();
        module.Write("in.txt", "one").Write("out.txt", "made");
        var action = new BuildAction
        {
            Description = "copy",
            Command = ["cp", "in.txt", "out.txt"],
            Directory = module.Root,
            Inputs = ["in.txt"],
            Outputs = ["out.txt"],
        };
        var cache = new ActionCache(new Digests());
        Assert.False(cache.IsCurrent(action));
        cache.Record(action);
        Assert.True(cache.IsCurrent(action));

        // Touching a file changes nothing; changing what it holds does.
        File.SetLastWriteTimeUtc(Path.Combine(module.Root, "in.txt"), DateTime.UtcNow.AddMinutes(-5));
        Assert.True(new ActionCache(new Digests()).IsCurrent(action));
        module.Write("in.txt", "two");
        Assert.False(new ActionCache(new Digests()).IsCurrent(action));

        var other = new BuildAction
        {
            Description = "copy",
            Command = ["cp", "-p", "in.txt", "out.txt"],
            Directory = module.Root,
            Inputs = ["in.txt"],
            Outputs = ["out.txt"],
        };
        cache.Record(action);
        Assert.False(cache.IsCurrent(other));
        Assert.NotEqual(action.Key, other.Key);
        Assert.Equal(action.Key, new BuildAction
        {
            Description = "another description",
            Command = ["cp", "in.txt", "out.txt"],
            Directory = module.Root,
            Inputs = [],
            Outputs = ["x"],
        }.Key);
    }
}
