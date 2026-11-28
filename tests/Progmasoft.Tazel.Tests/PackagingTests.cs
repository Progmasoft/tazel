// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using CsCheck;
using Progmasoft.Tazel.Packaging;
using Progmasoft.Tazel.Packaging.Spdx;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Progmasoft.Tazel.Packaging: identities, archives, sums, the plugin manifest and SPDX expressions.</summary>
public sealed class PackagingTests
{
    [Fact]
    public void AnIdentityNamesItsArchive()
    {
        var identity = new TazelPackageIdentity("com.example", "greeter", "1.2.0-rc.1");
        Assert.Equal("com.example.greeter-1.2.0-rc.1", identity.BaseName);
        Assert.Equal("com.example.greeter-1.2.0-rc.1.tazel.tar.xz", identity.FileName);
    }

    [Theory]
    [InlineData("com..example", "greeter", "1.0.0", "group")]
    [InlineData("com.example", "greet er", "1.0.0", "name")]
    [InlineData("com.example", "greeter", "1.0", "SemVer")]
    [InlineData("com.example", "greeter", "01.0.0", "SemVer")]
    public void AnIdentityRefusesWhatItCannotBe(string group, string name, string version, string part)
    {
        var error = Assert.Throws<TazelPackageException>(() => new TazelPackageIdentity(group, name, version));
        Assert.Contains(part, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnArchiveIsTheSameBytesAndUnpacksToItsFiles()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", "m").Write("src/a.c", "int a;");
        var files = new[] { "MODULE.csx", "src/a.c" };
        var first = Path.Combine(module.Root, "out", "first" + TazelPackageArchive.Extension);
        var second = Path.Combine(module.Root, "out", "second" + TazelPackageArchive.Extension);
        TazelPackageArchive.Write(module.Root, "com.example.m-1.0.0", first, files);
        TazelPackageArchive.Write(module.Root, "com.example.m-1.0.0", second, files);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        var top = TazelPackageArchive.Unpack(first, Path.Combine(module.Root, "unpacked"), "MODULE.csx");
        Assert.Equal("com.example.m-1.0.0", Path.GetFileName(top));
        Assert.Equal("int a;", File.ReadAllText(Path.Combine(top, "src", "a.c")));

        var missing = Assert.Throws<TazelPackageException>(() =>
            TazelPackageArchive.Unpack(first, Path.Combine(module.Root, "again"), "tazel-plugin.json"));
        Assert.Contains("has no com.example.m-1.0.0/tazel-plugin.json", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SumsAreReadAsSha256sumWritesThem()
    {
        using var module = new TemporaryModule().Write("x.bin", "x");
        var file = Path.Combine(module.Root, "x.bin");
        var line = Sha256Sums.Line(file);
        Assert.Equal(Sha256Sums.Sha256(file), Sha256Sums.SumFor(line, "x.bin", "x.bin.sha256sums"));
        Assert.Null(Sha256Sums.SumFor(line, "y.bin", "x.bin.sha256sums"));
        Assert.Throws<TazelPackageException>(() => Sha256Sums.SumFor("not a sum\n", "x.bin", "x.bin.sha256sums"));
        Assert.Throws<TazelPackageException>(() => Sha256Sums.SumFor(line + line, "x.bin", "x.bin.sha256sums"));
    }

    [Fact]
    public void AManifestIsWrittenAndReadBack()
    {
        var manifest = new TazelPluginManifest("com.example", "greeter", "0.1.0", "0.2.0", "Greeter.dll");
        var json = manifest.ToJson();
        Assert.StartsWith("{\n  \"entryAssembly\": \"Greeter.dll\",", json.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(manifest, TazelPluginManifest.Read(json, "tazel-plugin.json"));
        Assert.Equal("com.example.greeter-0.1.0", manifest.Identity.BaseName);
    }

    [Theory]
    [InlineData("""{"group":"com.example","name":"g","version":"0.1.0","pluginApi":"0.2.0","entryAssembly":"lib/G.dll"}""", "not a .dll at the top")]
    [InlineData("""{"group":"com.example","name":"g","version":"x","pluginApi":"0.2.0","entryAssembly":"G.dll"}""", "not a SemVer version")]
    [InlineData("""{"group":"","name":"g","version":"0.1.0","pluginApi":"0.2.0","entryAssembly":"G.dll"}""", "not names joined by dots")]
    [InlineData("""{"group":"com.example",""", "cannot be read")]
    public void AManifestThatCannotStandIsRefused(string json, string why)
    {
        var error = Assert.Throws<TazelPackageException>(() => TazelPluginManifest.Read(json, "p.tazel.tar.xz"));
        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MIT OR Apache-2.0 AND BSD-2-Clause", "MIT OR Apache-2.0 AND BSD-2-Clause")]
    [InlineData("(MIT OR Apache-2.0) AND BSD-2-Clause", "(MIT OR Apache-2.0) AND BSD-2-Clause")]
    [InlineData("((MIT))", "MIT")]
    [InlineData("mit or apache-2.0", "mit OR apache-2.0")]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0 OR MIT", "GPL-2.0-only WITH Classpath-exception-2.0 OR MIT")]
    [InlineData("MIT AND (Apache-2.0 AND BSD-3-Clause)", "MIT AND (Apache-2.0 AND BSD-3-Clause)")]
    [InlineData("DocumentRef-spdx-tool-1.2:LicenseRef-MIT-Style-2", "DocumentRef-spdx-tool-1.2:LicenseRef-MIT-Style-2")]
    public void ExpressionsAreReadWithTheirPrecedence(string text, string written) =>
        Assert.Equal(written, SpdxExpressionParser.Parse(text).ToString());

    [Fact]
    public void AndBindsTighterThanOrAndWithTighterThanBoth()
    {
        var parsed = SpdxExpressionParser.Parse("MIT OR GPL-2.0-only WITH Classpath-exception-2.0 AND Apache-2.0+");
        Assert.Equal(
            new SpdxOr(new SpdxLicense("MIT", false),
                new SpdxAnd(new SpdxWith(new SpdxLicense("GPL-2.0-only", false), "Classpath-exception-2.0"),
                    new SpdxLicense("Apache-2.0", true))),
            parsed);
        Assert.Equal(["MIT", "GPL-2.0-only", "Apache-2.0+"], parsed.Leaves().Select(leaf => leaf.ToString()));
    }

    [Theory]
    [InlineData("MIT,Apache-2.0", "','")]
    [InlineData("MIT Apache-2.0", "'Apache-2.0' cannot follow")]
    [InlineData("MIT+Foo", "'+' only at the end")]
    [InlineData("LicenseRef-", "no license reference")]
    [InlineData("MIT)", "a ')' closes no '('")]
    [InlineData("", "the expression is empty")]
    public void MalformedExpressionsSayWhy(string text, string why)
    {
        var error = Assert.Throws<SpdxExpressionException>(() => SpdxExpressionParser.Parse(text));
        Assert.Contains(why, error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NoInputCanExhaustTheStack()
    {
        var nested = new string('(', SpdxExpressionParser.MaxDepth) + "MIT" + new string(')', SpdxExpressionParser.MaxDepth);
        Assert.Equal("MIT", SpdxExpressionParser.Parse(nested).ToString());
        Assert.Contains("deeper", Assert.Throws<SpdxExpressionException>(() =>
            SpdxExpressionParser.Parse(new string('(', 1_000_000) + "MIT")).Reason, StringComparison.Ordinal);
        var chain = string.Join(" AND ", Enumerable.Repeat("MIT", SpdxExpressionParser.MaxLicenses));
        Assert.Equal(chain, SpdxExpressionParser.Parse(chain).ToString());
        Assert.Contains("more than", Assert.Throws<SpdxExpressionException>(() =>
            SpdxExpressionParser.Parse(chain + " OR MIT")).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WrittenExpressionsReadBackToTheSameTree()
    {
        var ids = Gen.OneOf("MIT", "Apache-2.0", "BSD-3-Clause", "GPL-2.0-only", "LicenseRef-Own");
        var leaf = Gen.Select(ids, Gen.Bool, (id, orLater) =>
            (SpdxExpression)(id.StartsWith("LicenseRef-", StringComparison.Ordinal)
                ? new SpdxLicenseReference(null, id)
                : new SpdxLicense(id, orLater)));
        var tree = Gen.Recursive<SpdxExpression>((depth, inner) => depth > 4
            ? leaf
            : Gen.Frequency(
                (3, leaf),
                (1, Gen.Select(inner, inner, (left, right) => (SpdxExpression)new SpdxAnd(left, right))),
                (1, Gen.Select(inner, inner, (left, right) => (SpdxExpression)new SpdxOr(left, right))),
                (1, Gen.Select(leaf, Gen.OneOf("LLVM-exception", "Classpath-exception-2.0"),
                    (license, exception) => (SpdxExpression)new SpdxWith(license, exception)))));
        tree.Sample(expression => Assert.Equal(expression, SpdxExpressionParser.Parse(expression.ToString())));
    }

    [Fact]
    public void TheListFindsUnknownAndDeprecatedIdentifiers()
    {
        var list = SpdxLicenseList.Current;
        Assert.Equal("3.29.0", list.Version);
        Assert.Equal("Apache-2.0", list.License("apache-2.0")?.Id);
        var problems = list.Check(SpdxExpressionParser.Parse("MTI OR GPL-2.0+ OR Apache-2.0 WITH Nope-exception OR LicenseRef-x"));
        Assert.Equal([SpdxProblemKind.UnknownLicense, SpdxProblemKind.DeprecatedLicense, SpdxProblemKind.UnknownException],
            problems.Select(problem => problem.Kind));
        Assert.Contains("MIT", problems[0].Suggestions);
        Assert.Equal(["GPL-2.0-or-later"], problems[1].Suggestions);
        Assert.Empty(list.Check(SpdxExpressionParser.Parse("MIT OR Apache-2.0 WITH LLVM-exception")));
    }
}
