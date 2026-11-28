// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Output;

namespace Progmasoft.Tazel.Driver;

/// <summary>
/// Runs the actions of a build, at most <c>jobs</c> at a time, each once all it reads has been made. After a failure no
/// action starts unless <c>keepGoing</c> asks for it; those running are let finish, so no compiler is left writing
/// behind the back of the command.
/// </summary>
internal sealed class Executor(
    int jobs, bool keepGoing, ActionCache cache, Reporter reporter, Cache.RemoteActions? remote = null,
    CachePolicy policy = CachePolicy.Normal, string? scratch = null)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<BuildAction, bool> _made = new();
    private int _done;
    private int _failed;

    /// <summary>Whether <paramref name="action"/> was made, now or by an earlier build.</summary>
    public bool Succeeded(BuildAction action) => _made.ContainsKey(action);

    /// <summary>How many actions failed.</summary>
    public int Failed => _failed;

    /// <summary>
    /// Whether <paramref name="action"/> failed: its program said so, or an output it should have written is not there.
    /// A failure is reported, and stops the build unless it keeps going.
    /// </summary>
    private async Task<bool> FailedAsync(BuildAction action, ProcessResult result, CancellationTokenSource stop)
    {
        var missing = action.Outputs.FirstOrDefault(output => !File.Exists(action.Full(output)));
        if (result.ExitCode == 0 && missing is null)
        {
            return false;
        }

        var hint = missing is not null && action.MissingOutputHints.TryGetValue(missing, out var said) ? $"; {said}" : "";
        var output = result.ExitCode == 0 ? result.Output + $"{missing} was not written{hint}\n" : result.Output;
        Interlocked.Increment(ref _failed);
        reporter.ActionFailed(action.Description, action.Command, output);
        if (!keepGoing)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Runs <paramref name="goals"/> and what they need; true when all of them were made.</summary>
    public async Task<bool> RunAsync(IReadOnlyCollection<BuildAction> goals, CancellationToken cancel)
    {
        var all = Closure(goals);
        var total = all.Count;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        using var slots = new SemaphoreSlim(Math.Max(1, jobs));
        var tasks = new Dictionary<BuildAction, Lazy<Task<bool>>>();
        foreach (var action in all)
        {
            tasks[action] = new Lazy<Task<bool>>(() => RunOneAsync(action), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        var results = await Task.WhenAll(goals.Select(goal => tasks[goal].Value)).ConfigureAwait(false);
        return results.All(result => result);

        async Task<bool> RunOneAsync(BuildAction action)
        {
            var ready = await Task.WhenAll(action.Prerequisites.Select(prerequisite => tasks[prerequisite].Value))
                .ConfigureAwait(false);
            if (!ready.All(result => result) || stop.IsCancellationRequested)
            {
                return false;
            }

            await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                return !stop.IsCancellationRequested && await MakeAsync(action, total, stop).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="action"/>: it is current here, or the remote cache has its outputs, or it runs, and then the
    /// caches record it.
    /// </summary>
    private async Task<bool> MakeAsync(BuildAction action, int total, CancellationTokenSource stop)
    {
        var forced = Forced(action);
        if (!forced && cache.IsCurrent(action))
        {
            action.After?.Invoke();
            Interlocked.Increment(ref _done);
            _made[action] = true;
            return true;
        }

        foreach (var output in action.Outputs)
        {
            var full = action.Full(output);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.Delete(full);
        }

        // --no-cache writes no record, and so erases none: a later build checks the old one against the files.
        var writes = policy != CachePolicy.NoCache;
        if (writes)
        {
            cache.Forget(action);
        }
        else
        {
            cache.ForgetOutputs(action);
        }

        if (!forced && remote is not null && await remote.FetchAsync(action, stop.Token).ConfigureAwait(false))
        {
            Made(action, total, $"{action.Description} (remote cache)", "", record: true);
            return true;
        }

        var result = await RunAsync(action, forced, stop.Token).ConfigureAwait(false);
        // Stopped because another action failed: that one is reported, this one did not fail.
        if (result.Canceled || await FailedAsync(action, result, stop).ConfigureAwait(false))
        {
            return false;
        }

        if (writes)
        {
            cache.Record(action);
        }

        if (writes && remote is { Writes: true })
        {
            await remote.StoreAsync(action, stop.Token).ConfigureAwait(false);
        }

        Made(action, total, action.Description, result.Output, record: false);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="action"/> runs whatever the caches say: every action with <c>--no-cache</c>, the
    /// compilations with <c>--recompile</c>. A link or an archive under <c>--recompile</c> runs only when the objects it
    /// reads came out different.
    /// </summary>
    internal bool Forced(BuildAction action) => policy switch
    {
        CachePolicy.NoCache => true,
        CachePolicy.Recompile => action.Kind is ActionKind.Compile or ActionKind.CompileAndLink,
        _ => false,
    };

    // The action's work or command; a forced compilation with a cache of the compiler's own gets a fresh one, which
    // is removed after.
    private async Task<ProcessResult> RunAsync(BuildAction action, bool forced, CancellationToken cancel)
    {
        if (action.Work is { } work)
        {
            return await work(cancel).ConfigureAwait(false);
        }

        if (!forced || action.WithCompilerCache is not { } withCache || scratch is null)
        {
            return await Processes.RunAsync(action.Command, action.Directory, action.Environment, null, cancel).ConfigureAwait(false);
        }

        var fresh = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh);
        try
        {
            return await Processes.RunAsync(withCache(fresh), action.Directory, action.Environment, null, cancel).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(fresh, recursive: true);
        }
    }

    /// <summary>Marks <paramref name="action"/> made and says so; with <paramref name="record"/>, the local cache records it.</summary>
    private void Made(BuildAction action, int total, string description, string output, bool record)
    {
        if (record)
        {
            cache.Record(action);
        }

        action.After?.Invoke();
        _made[action] = true;
        reporter.ActionDone(Interlocked.Increment(ref _done), total, description, output);
    }

    private static List<BuildAction> Closure(IEnumerable<BuildAction> goals)
    {
        var seen = new HashSet<BuildAction>();
        var order = new List<BuildAction>();
        var stack = new Stack<BuildAction>(goals);
        while (stack.Count > 0)
        {
            var action = stack.Pop();
            if (seen.Add(action))
            {
                order.Add(action);
                foreach (var prerequisite in action.Prerequisites)
                {
                    stack.Push(prerequisite);
                }
            }
        }

        return order;
    }
}
