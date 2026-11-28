// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cache;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>What the remote cache takes and refuses, action by action, with no compiler.</summary>
public sealed class RemoteActionsTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static BuildAction Copy(TemporaryModule module, string input = "in.txt", string? depfile = null) => new()
    {
        Description = "Copying",
        Command = ["/bin/cp", input, "out.txt"],
        Directory = module.Root,
        Inputs = [input],
        Outputs = ["out.txt"],
        Depfile = depfile,
    };

    private static string Put(FakeRemoteCache server, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Digests.Sha256(bytes);
        server.Content[hash] = bytes;
        return hash;
    }

    [Fact]
    public async Task AnActionWithAMissingInputHasNoKeyAndIsNeitherFetchedNorStored()
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule();
        using var cache = new RemoteCache(new Uri(server.Address), token: null);
        var remote = new RemoteActions(cache, read: true, write: true, new Digests(), _ => { });
        var action = Copy(module, "missing.txt");
        Assert.Null(remote.KeyOf(action));
        Assert.False(await remote.FetchAsync(action, Cancel));
        await remote.StoreAsync(action, Cancel);
        Assert.Empty(server.Actions);
    }

    [Fact]
    public async Task ACacheThatCannotBeReachedWhileStoringIsSaidOnceAndNotTriedAgain()
    {
        using var module = new TemporaryModule().Write("in.txt", "in\n").Write("out.txt", "in\n");
        var warnings = new List<string>();
        // A port nothing listens on: the server is made and closed at once.
        string address;
        using (var gone = new FakeRemoteCache())
        {
            address = gone.Address;
        }

        using var cache = new RemoteCache(new Uri(address), token: null);
        var remote = new RemoteActions(cache, read: true, write: true, new Digests(), warnings.Add);
        await remote.StoreAsync(Copy(module), Cancel);
        await remote.StoreAsync(Copy(module), Cancel);
        Assert.False(remote.Writes);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task StoringStopsWhenAnOutputOrAFoundInputIsMissing()
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule().Write("in.txt", "in\n");
        using var cache = new RemoteCache(new Uri(server.Address), token: null);
        var remote = new RemoteActions(cache, read: false, write: true, new Digests(), _ => { });
        // No out.txt: nothing is stored.
        await remote.StoreAsync(Copy(module), Cancel);
        Assert.Empty(server.Actions);
        // The depfile names a header that is not there.
        module.Write("out.txt", "in\n").Write("out.d", "out.txt: in.txt gone.h\n");
        await remote.StoreAsync(Copy(module, depfile: "out.d"), Cancel);
        Assert.Empty(server.Actions);
        // With every file there, the result is stored with the list of found inputs.
        module.Write("gone.h", "\n");
        await remote.StoreAsync(Copy(module, depfile: "out.d"), Cancel);
        Assert.Single(server.Actions);
    }

    [Fact]
    public async Task AListOfFoundInputsThatIsMissingWrongOrDifferentHereRefusesTheResult()
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule().Write("in.txt", "in\n");
        using var cache = new RemoteCache(new Uri(server.Address), token: null);
        var digests = new Digests();
        var remote = new RemoteActions(cache, read: true, write: false, digests, _ => { });
        var action = Copy(module);
        var key = remote.KeyOf(action)!;
        var output = Put(server, "in\n");
        var inHere = digests.Of(Path.Combine(module.Root, "in.txt"))!;
        string[] lists =
        [
            "",                                             // not in the cache
            "short line\n",                                 // not a digest and a path
            new string('0', 64) + " in.txt\n",              // a digest the input does not have here
        ];
        foreach (var text in lists)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var hash = Digests.Sha256(bytes);
            if (text.Length > 0)
            {
                server.Content[hash] = bytes;
            }

            server.Actions[key] = ActionResultMessage.Encode([
                new OutputFile("out.txt", output, 3, false),
                new OutputFile(RemoteActions.DiscoveredInputs, text.Length > 0 ? hash : new string('9', 64), bytes.Length, false),
            ]);
            Assert.False(await remote.FetchAsync(action, Cancel), text);
        }

        // A list that holds, and content of another size than the result says: refused too.
        var good = Put(server, inHere + " in.txt\n");
        server.Actions[key] = ActionResultMessage.Encode([
            new OutputFile("out.txt", output, 99, false),
            new OutputFile(RemoteActions.DiscoveredInputs, good, (inHere + " in.txt\n").Length, false),
        ]);
        Assert.False(await remote.FetchAsync(action, Cancel));
        Assert.False(File.Exists(Path.Combine(module.Root, "out.txt")));
    }

    [Theory]
    [InlineData("tazel signature 1\nalgorithm ed25519\nsignature !!!not base64!!!\n")]
    [InlineData("tazel signature 1\nalgorithm unknown\nsignature AAAA\n")]
    [InlineData("tazel signature 2\nalgorithm ed25519\nsignature AAAA\n")]
    [InlineData("")]
    public async Task ASignatureRecordTazelCannotReadIsNotTrusted(string record)
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule().Write("in.txt", "in\n");
        using var cache = new RemoteCache(new Uri(server.Address), token: null);
        var digests = new Digests();
        var trusted = CacheKey.ParsePublic(CacheKey.Generate(SigningAlgorithm.Ed25519).PublicKey, "PublicKey");
        var warnings = new List<string>();
        var remote = new RemoteActions(cache, read: true, write: false, digests, warnings.Add, trusted);
        var action = Copy(module);
        var output = Put(server, "in\n");
        var inHere = digests.Of(Path.Combine(module.Root, "in.txt"))!;
        var list = Put(server, inHere + " in.txt\n");
        // An empty record is a record the cache does not have.
        var signature = record.Length > 0 ? Put(server, record) : new string('8', 64);
        server.Actions[remote.KeyOf(action)!] = ActionResultMessage.Encode([
            new OutputFile("out.txt", output, 3, false),
            new OutputFile(RemoteActions.DiscoveredInputs, list, (inHere + " in.txt\n").Length, false),
            new OutputFile(RemoteActions.Signature, signature, Encoding.UTF8.GetByteCount(record), false),
        ]);
        Assert.False(await remote.FetchAsync(action, Cancel));
        Assert.Single(warnings);
    }
}
