// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Output;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Cli;

/// <summary>The remote part of <c>tazel cache validate --remote</c> and <c>tazel cache repair --remote</c>.</summary>
internal sealed partial class Commands
{
    private async Task<int> CacheCheckAsync(CacheCheckCommand command, CancellationToken cancel)
    {
        var broken = new CacheCommands(reporter, workingDirectory).RunLocal(command.Repair);
        var remoteFailed = command.Remote && !await RemoteCheckAsync(command.Repair, cancel).ConfigureAwait(false);
        if (broken == 0 && !remoteFailed)
        {
            reporter.Success(command.Repair ? "the caches hold nothing broken now" : "nothing in the caches is broken");
        }

        return broken > 0 || remoteFailed ? 1 : 0;
    }

    /// <summary>
    /// Whether the remote cache of MODULE.csx answers and its keys hold: a request for a key no action has, which a
    /// cache that is reached and lets us in answers with 404; and the SigningKey, when there is one with a PublicKey,
    /// signing what the PublicKey verifies. With <paramref name="repair"/>, results of this machine's valid records
    /// that the cache lacks, or holds without a signature the PublicKey takes, are stored again.
    /// </summary>
    private async Task<bool> RemoteCheckAsync(bool repair, CancellationToken cancel)
    {
        var main = await Workspace.LoadMainModuleAsync(workingDirectory, TazelVersion).ConfigureAwait(false);
        if (main.Globals.cache?.Settings.Remote is not { } remote)
        {
            reporter.Plain("remote cache: MODULE.csx sets none");
            return true;
        }

        using var cache = new Cache.RemoteCache(remote.Url, remote.Settings.Token);
        try
        {
            var probe = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            await cache.GetActionAsync(probe, cancel).ConfigureAwait(false);
            reporter.Plain($"remote cache: {remote.Url.Host} answers and lets this machine in");
        }
        catch (Exception error) when (error is Cache.RemoteCacheException or HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            reporter.Error(ErrorCodes.CacheBroken, $"the remote cache at {remote.Url.Host} cannot be used: {error.Message}",
                "check its address, and the Token of Remote.From in MODULE.csx");
            return false;
        }

        if (!KeysMatch(remote.Settings))
        {
            return false;
        }

        return !repair || await StoreMissingAsync(cache, remote.Settings, cancel).ConfigureAwait(false);
    }

    private bool KeysMatch(Dsl.Module.Prelude.RemoteSettings settings)
    {
        var digests = new Digests();
        using var unused = new Cache.RemoteCache(new Uri("http://127.0.0.1/"), null);
        // RemoteActionsOf reads both keys and says which one it cannot.
        RemoteActionsOf(unused, settings, digests);
        if (settings.PublicKey is not { Length: > 0 } publicText || settings.SigningKey is not { Length: > 0 } signingText)
        {
            reporter.Plain(settings.PublicKey is { Length: > 0 }
                ? "remote cache keys: a PublicKey; results are taken only with its signature"
                : "remote cache keys: none; results are taken unsigned");
            return true;
        }

        var message = RandomNumberGenerator.GetBytes(64);
        var publicKey = Cache.CacheKey.ParsePublic(publicText, "the PublicKey of the remote cache");
        var signingKey = Cache.CacheKey.ParseSigning(signingText, "the SigningKey of the remote cache");
        if (publicKey.Algorithm == signingKey.Algorithm && publicKey.Verify(message, signingKey.Sign(message)))
        {
            reporter.Plain($"remote cache keys: the SigningKey signs what the PublicKey verifies ({Cache.CacheKey.NameOf(publicKey.Algorithm)})");
            return true;
        }

        reporter.Error(ErrorCodes.CacheBroken, "the SigningKey of the remote cache does not sign what its PublicKey verifies; " +
            "what this machine writes is not taken by those who trust that key", "make a pair with tazel cache keygen and use both halves");
        return false;
    }

    // The valid local results of the module's targets for this machine, stored again where the cache lacks them.
    private async Task<bool> StoreMissingAsync(Cache.RemoteCache cache, Dsl.Module.Prelude.RemoteSettings settings, CancellationToken cancel)
    {
        if (!settings.Write)
        {
            reporter.Plain("remote cache: Remote.From does not write, so nothing is stored again");
            return true;
        }

        var (options, _) = Dsl.Options.TazelOptionsFile.Effective(workingDirectory);
        var platform = options.Platform ?? Host.Platform;
        var workspace = await Workspace.LoadAsync(workingDirectory, new BuildContext(BuildMode.Debug, platform), TazelVersion,
            reporter.Status, ["//..."]).ConfigureAwait(false);
        var buildOptions = new BuildOptions { Platform = platform, Mode = BuildMode.Debug };
        var planner = new Planner(workspace, buildOptions, new ToolchainChooser(workspace, platform));
        var selector = new Selector(workspace, platform);
        var actions = new HashSet<BuildAction>();
        foreach (var target in Select(selector, ["//..."], BuildKind.Build, platform))
        {
            Collect(planner.Plan(target).Main, actions);
        }

        var digests = new Digests();
        var local = new ActionCache(digests);
        var remote = RemoteActionsOf(cache, settings, digests);
        var stored = 0;
        foreach (var action in actions.Where(local.IsCurrent))
        {
            if (!await remote.HoldsTrustedAsync(action, cancel).ConfigureAwait(false))
            {
                await remote.StoreAsync(action, cancel).ConfigureAwait(false);
                stored++;
            }
        }

        reporter.Plain($"remote cache: stored {stored} result{(stored == 1 ? "" : "s")} of {platform.Name()}, debug, again");
        return true;
    }

    private static void Collect(BuildAction? action, HashSet<BuildAction> into)
    {
        if (action is null || !into.Add(action))
        {
            return;
        }

        foreach (var prerequisite in action.Prerequisites)
        {
            Collect(prerequisite, into);
        }
    }
}
