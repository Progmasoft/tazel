// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cache;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class CacheCheckTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        CC.Executable("app", new() { Sources = ["main.c", "two.c"] });
        CC.Test("t", new() { Sources = ["t.c"] });
        """;

    private static TemporaryModule Sample(string module = TemporaryModule.ModuleFile) => new TemporaryModule()
        .Write("MODULE.csx", module)
        .Write("BUILD.csx", Build)
        .Write("main.c", "int two(void);\nint main(void) { return two() - 2; }\n")
        .Write("two.c", "int two(void) { return 2; }\n")
        .Write("t.c", "int main(void) { return 0; }\n");

    private static void SkipWithoutCompiler() =>
        Assert.SkipWhen(Host.Which("cc") is null && Host.Which("gcc") is null && Host.Which("clang") is null, "no C compiler");

    private static async Task<(int Status, string Output)> Tazel(TemporaryModule module, params string[] arguments) =>
        await module.TazelAsync(".", ["--color", "never", .. arguments]);

    private static async Task Built(TemporaryModule module)
    {
        Assert.Equal(0, (await Tazel(module, "build", "//:app")).Status);
        Assert.Equal(0, (await Tazel(module, "test", "//:t")).Status);
    }

    private static string Object(TemporaryModule module, string name) =>
        Directory.EnumerateFiles(Path.Combine(module.Root, ".tazel"), name, SearchOption.AllDirectories).Single();

    [Fact]
    public void TheCommandLineTakesValidateAndRepairWithRemote()
    {
        Assert.Equal(new CacheCheckCommand(new OutputOptions(Output.When.Auto, Output.When.Auto, false, Output.MessageFormat.RichStdio), false, true),
            CommandLine.Parse(["cache", "validate", "--remote"]) with { DotnetSettings = [] });
        Assert.True(Assert.IsType<CacheCheckCommand>(CommandLine.Parse(["cache", "repair"])).Repair);
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["cache", "keygen", "--remote"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["cache", "validate", "--algorithm", "ed25519"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["build", "--remote"]));
        Assert.Throws<CommandLineException>(() => CommandLine.Parse(["cache", "check"]));
    }

    [Fact]
    public async Task AFreshBuildHasNothingBrokenAndAChangedSourceOnlyMakesRecordsStale()
    {
        SkipWithoutCompiler();
        using var module = Sample();
        await Built(module);
        var (status, output) = await Tazel(module, "cache", "validate");
        Assert.True(status == 0, output);
        Assert.Contains("0 stale, 0 broken", output, StringComparison.Ordinal);
        Assert.Contains("test results: 1 valid", output, StringComparison.Ordinal);
        Assert.Contains("nothing in the caches is broken", output, StringComparison.Ordinal);
        module.Write("main.c", "int two(void);\nint main(void) { return two() - 2 + 0; }\n");
        (status, output) = await Tazel(module, "cache", "validate");
        Assert.True(status == 0, output);
        // Only the compilation of main.c is stale: the link reads the object, which has not been made again.
        Assert.Contains("1 stale, 0 broken", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutputChangedBehindTazelIsBrokenAndRepairLeavesItToTheNextBuild()
    {
        SkipWithoutCompiler();
        using var module = Sample();
        await Built(module);
        var two = Object(module, "two.c.o");
        File.AppendAllText(two, "junk");
        module.Write("main.c", "int two(void);\nint main(void) { return two() - 2 + 0; }\n");
        var (status, output) = await Tazel(module, "cache", "validate");
        Assert.Equal(1, status);
        Assert.Contains("error[E0508]", output, StringComparison.Ordinal);
        Assert.Contains("two.c.o changed while its inputs are as recorded", output, StringComparison.Ordinal);
        Assert.True(File.Exists(two + ".action"));
        var stale = Object(module, "main.c.o.action");
        var staleText = File.ReadAllText(stale);
        (status, output) = await Tazel(module, "cache", "repair");
        Assert.True(status == 0, output);
        Assert.False(File.Exists(two + ".action"));
        Assert.Equal(staleText, File.ReadAllText(stale));
        Assert.Equal(0, (await Tazel(module, "cache", "validate")).Status);
        (_, output) = await Tazel(module, "build", "//:app");
        Assert.Contains("Compiling //:app two.c", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsEveryKindOfEntry()
    {
        using var module = Sample();
        var state = Path.Combine(module.Root, ".tazel");
        var digest = new string('a', 64);
        module.Write(".tazel/out/x/good.o", "object")
            .Write(".tazel/out/x/good.o.action", $"key {digest}\nfile {Digests.Sha256("object"u8.ToArray())} {Path.Combine(state, "out", "x", "good.o")}\n")
            .Write(".tazel/out/x/bad.o.action", "not a record\n")
            .Write(".tazel/out/x/lost.o.action", $"key {digest}\nfile {digest} {Path.Combine(state, "out", "x", "lost.o")}\n")
            .Write(".tazel/out/x/other.o.action", $"key {digest}\nfile {digest} {Path.Combine(state, "out", "x", "elsewhere.o")}\n")
            .Write(".tazel/out/x/half.o.1234.partial", "")
            .Write(".tazel/testlogs/.passed/_kept", digest)
            .Write(".tazel/testlogs/kept/test.log", "ok")
            .Write(".tazel/testlogs/.passed/_alone", digest)
            .Write(".tazel/testlogs/.passed/pkg_t", "short")
            .Write($".tazel/scripts/{digest}.dll", "")
            .Write($".tazel/scripts/{new string('b', 64)}.dll", "not an assembly")
            .Write(".tazel/scripts/name.dll", "");
        File.Copy(typeof(CacheChecks).Assembly.Location, Path.Combine(state, "scripts", new string('c', 64) + ".dll"));
        var checks = new CacheChecks(module.Root);
        var actions = checks.Actions();
        Assert.Equal(1, actions.Valid);
        Assert.Equal(["bad.o.action", "lost.o.action", "other.o.action"], actions.Broken.Select(entry => Path.GetFileName(entry.File)));
        Assert.Contains("does not start with the key", actions.Broken[0].Reason, StringComparison.Ordinal);
        Assert.Contains("is gone while its inputs are as recorded", actions.Broken[1].Reason, StringComparison.Ordinal);
        Assert.Contains("does not name the output", actions.Broken[2].Reason, StringComparison.Ordinal);
        Assert.Single(actions.Leftovers);
        var tests = checks.Tests();
        Assert.Equal(1, tests.Valid);
        Assert.Equal(["_alone", "pkg_t"], tests.Broken.Select(entry => Path.GetFileName(entry.File)).Order(StringComparer.Ordinal));
        var scripts = checks.Scripts();
        Assert.Equal(2, scripts.Valid);
        Assert.Equal(2, scripts.Broken.Count);
    }

    [Fact]
    public async Task RemoteSaysWhenMODULEcsxSetsNone()
    {
        using var module = Sample();
        var (status, output) = await Tazel(module, "cache", "validate", "--remote");
        Assert.True(status == 0, output);
        Assert.Contains("remote cache: MODULE.csx sets none", output, StringComparison.Ordinal);
    }

    private static string WithRemote(string url, bool write, string? publicKey = null, string? signingKey = null) => $$"""
        using Progmasoft.Tazel.Dsl.Module.Prelude;
        module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
        cache = Cache.Define(new() {
            Remote = Remote.From("{{url}}", new() { Read = true, Write = {{(write ? "true" : "false")}}, Token = "secret",
                PublicKey = {{(publicKey is null ? "null" : $"\"{publicKey}\"")}}, SigningKey = {{(signingKey is null ? "null" : $"\"{signingKey}\"")}} })
        });
        """;

    [Fact]
    public async Task ARemoteThatCannotBeReachedIsAnError()
    {
        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            port = ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        using var module = Sample(WithRemote($"http://127.0.0.1:{port}", write: false));
        var (status, output) = await Tazel(module, "cache", "validate", "--remote");
        Assert.Equal(1, status);
        Assert.Contains("cannot be used", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKeysMustBeAPair()
    {
        using var server = new FakeRemoteCache();
        var (publicKey, signingKey) = CacheKey.Generate(SigningAlgorithm.Ed25519);
        var (otherPublic, _) = CacheKey.Generate(SigningAlgorithm.Ed25519);
        using (var module = Sample(WithRemote(server.Address, write: true, publicKey, signingKey)))
        {
            var (status, output) = await Tazel(module, "cache", "validate", "--remote");
            Assert.True(status == 0, output);
            Assert.Contains("answers and lets this machine in", output, StringComparison.Ordinal);
            Assert.Contains("the SigningKey signs what the PublicKey verifies (ed25519)", output, StringComparison.Ordinal);
            Assert.Contains("Bearer secret", server.Authorizations);
        }

        using (var module = Sample(WithRemote(server.Address, write: true, otherPublic, signingKey)))
        {
            var (status, output) = await Tazel(module, "cache", "validate", "--remote");
            Assert.Equal(1, status);
            Assert.Contains("does not sign what its PublicKey verifies", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RepairRemoteStoresWhatTheCacheLacks()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        using var module = Sample(WithRemote(server.Address, write: true));
        Assert.Equal(0, (await Tazel(module, "build", "//:app")).Status);
        var stored = server.Actions.Count;
        Assert.True(stored > 0);
        server.Actions.Clear();
        var (status, output) = await Tazel(module, "cache", "repair", "--remote");
        Assert.True(status == 0, output);
        Assert.Contains($"stored {stored} results", output, StringComparison.Ordinal);
        Assert.Equal(stored, server.Actions.Count);
        (_, output) = await Tazel(module, "cache", "repair", "--remote");
        Assert.Contains("stored 0 results", output, StringComparison.Ordinal);
    }
}
