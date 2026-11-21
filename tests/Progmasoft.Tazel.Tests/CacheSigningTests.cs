// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cache;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class CacheSigningTests
{
    private static void SkipWithoutCompiler()
    {
        Assert.SkipWhen(Host.Which("clang") is null && Host.Which("gcc") is null, "no C compiler on PATH");
        Assert.SkipWhen(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INCLUDE") is null,
            "no MSVC environment (INCLUDE) for the headers of clang on Windows");
    }

    /// <summary>A module that uses the cache at <paramref name="url"/>, with the keys given.</summary>
    private static TemporaryModule Module(string url, bool write, string? publicKey, string? signingKey)
    {
        static string Text(string? value) => value is null ? "null" : $"\"{value}\"";
        return new TemporaryModule()
            .Write("MODULE.csx", $$"""
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
                cache = Cache.Define(new() {
                    Remote = Remote.From("{{url}}", new() {
                        Read = true, Write = {{(write ? "true" : "false")}},
                        PublicKey = {{Text(publicKey)}}, SigningKey = {{Text(signingKey)}}
                    })
                });
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.Executable(\"app\", new() { Sources = [\"main.c\"] });\n")
            .Write("main.c", "int main(void) { return 0; }\n");
    }

    [Theory]
    [InlineData("ed25519")]
    [InlineData("rsa-4096")]
    [InlineData("ml-dsa-65")]
    public void EachAlgorithmSignsAndChecks(string name)
    {
        var algorithm = CacheKey.AlgorithmNamed(name)!.Value;
        var (publicText, signingText) = CacheKey.Generate(algorithm);
        Assert.StartsWith(name + ":", publicText, StringComparison.Ordinal);
        Assert.StartsWith(name + ":", signingText, StringComparison.Ordinal);

        var publicKey = CacheKey.ParsePublic(publicText, "the key");
        var signingKey = CacheKey.ParseSigning(signingText, "the key");
        var message = Encoding.UTF8.GetBytes("tazel signed result 1\nkey\n");
        var signature = signingKey.Sign(message);

        Assert.True(publicKey.Verify(message, signature));
        Assert.False(publicKey.Verify([.. message, 0x0A], signature));
        signature[^1] ^= 0x01;
        Assert.False(publicKey.Verify(message, signature));
        var other = CacheKey.ParsePublic(CacheKey.Generate(algorithm).PublicKey, "the other key");
        Assert.False(other.Verify(message, signingKey.Sign(message)));
    }

    [Theory]
    [InlineData("AAAA", "does not start with")]
    [InlineData("ed448:AAAA", "does not start with")]
    [InlineData("ed25519:not base64!", "is not base64")]
    [InlineData("ed25519:AAAA", "is not a public key of ed25519")]
    [InlineData("ml-dsa-65:AAAA", "is not a public key of ml-dsa-65")]
    [InlineData("rsa-4096:AAAA", "is not a public key of rsa-4096")]
    public void AKeyThatDoesNotSayItsAlgorithmOrHasTheWrongSizeIsRefused(string text, string said)
    {
        var error = Assert.Throws<FormatException>(() => CacheKey.ParsePublic(text, "the PublicKey"));
        Assert.Contains(said, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeygenTakesTheAlgorithmAndNothingElse()
    {
        Assert.Equal(SigningAlgorithm.Ed25519, Assert.IsType<CacheKeygenCommand>(CommandLine.Parse(["cache", "keygen"])).Algorithm);
        Assert.Equal(SigningAlgorithm.MlDsa65,
            Assert.IsType<CacheKeygenCommand>(CommandLine.Parse(["cache", "keygen", "--algorithm=ml-dsa"])).Algorithm);
        Assert.Equal(SigningAlgorithm.Rsa4096,
            Assert.IsType<CacheKeygenCommand>(CommandLine.Parse(["cache", "keygen", "--algorithm", "rsa-4096"])).Algorithm);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["cache"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["cache", "keygen", "--algorithm=dsa"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["build", "--algorithm=ed25519"]));
    }

    [Fact]
    public async Task AKeyTazelCannotReadStopsTheBuildWithItsCode()
    {
        using var module = Module("https://cache.example.com", write: false, publicKey: "AAAA", signingKey: null);
        var (status, output) = await module.TazelAsync(".", "build", "//:app");
        Assert.Equal(1, status);
        Assert.Contains("error[E0505]", output, StringComparison.Ordinal);
        Assert.Contains("does not start with ed25519:, rsa-4096: or ml-dsa-65:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyResultsSignedByTheTrustedKeyAreTaken()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        var (publicKey, signingKey) = CacheKey.Generate(SigningAlgorithm.Ed25519);
        using (var writer = Module(server.Address, write: true, publicKey, signingKey))
        {
            var (status, output) = await writer.TazelAsync(".", "build", "//:app");
            Assert.True(status == 0, output);
        }

        using (var reader = Module(server.Address, write: false, publicKey, signingKey: null))
        {
            var (status, output) = await reader.TazelAsync(".", "build", "//:app");
            Assert.True(status == 0, output);
            Assert.Contains("main.c (remote cache)", output, StringComparison.Ordinal);
        }

        // Another key of the same algorithm, and a key of another algorithm: nothing is taken, and that is said once.
        foreach (var algorithm in new[] { SigningAlgorithm.Ed25519, SigningAlgorithm.MlDsa65 })
        {
            using var stranger = Module(server.Address, write: false, CacheKey.Generate(algorithm).PublicKey, signingKey: null);
            var (status, output) = await stranger.TazelAsync(".", "build", "//:app");
            Assert.True(status == 0, output);
            Assert.DoesNotContain("(remote cache)", output, StringComparison.Ordinal);
            Assert.Single(output.Split('\n'), line => line.Contains("not signed with the PublicKey", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task AnUnsignedResultIsNotTakenByAReaderThatTrustsAKey()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        using (var writer = Module(server.Address, write: true, publicKey: null, signingKey: null))
        {
            Assert.Equal(0, (await writer.TazelAsync(".", "build", "//:app")).Status);
        }

        Assert.DoesNotContain(server.Actions.Values, bytes =>
            ActionResultMessage.Decode(bytes)!.Any(file => file.Path == RemoteActions.Signature));
        using var reader = Module(server.Address, write: false, CacheKey.Generate(SigningAlgorithm.Rsa4096).PublicKey, signingKey: null);
        var (status, output) = await reader.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("(remote cache)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritingWithAPublicKeyAndNoSigningKeyIsSaid()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        using var writer = Module(server.Address, write: true, CacheKey.Generate(SigningAlgorithm.Ed25519).PublicKey, signingKey: null);
        var (status, output) = await writer.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.Contains("without a SigningKey", output, StringComparison.Ordinal);
    }
}
