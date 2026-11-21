// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Cache;
using Progmasoft.Tazel.Driver;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class RemoteCacheTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        CC.Executable("app", new() { Sources = ["main.c"] });
        """;

    private static void SkipWithoutCompiler()
    {
        Assert.SkipWhen(Host.Which("clang") is null && Host.Which("gcc") is null, "no C compiler on PATH");
        Assert.SkipWhen(OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("INCLUDE") is null,
            "no MSVC environment (INCLUDE) for the headers of clang on Windows");
    }

    /// <summary>A module whose main.c includes value.h, which it does not declare, using the cache at <paramref name="url"/>.</summary>
    private static TemporaryModule Module(string url, bool write, string value = "42", string? token = "secret") =>
        new TemporaryModule()
            .Write("MODULE.csx", $$"""
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
                cache = Cache.Define(new() {
                    Remote = Remote.From("{{url}}", new() { Read = true, Write = {{(write ? "true" : "false")}}, Token = {{(token is null ? "null" : $"\"{token}\"")}} })
                });
                """)
            .Write("BUILD.csx", Build)
            .Write("value.h", $"#define VALUE {value}\n")
            .Write("main.c", "#include \"value.h\"\nint main(void) { return VALUE == 42 ? 0 : 1; }\n");

    [Fact]
    public async Task AProjectFileReadsItsTokenFromTheEnvironment()
    {
        // As the examples write it: Environment without System., which project files import.
        using var module = new TemporaryModule()
            .Write("MODULE.csx", """
                using Progmasoft.Tazel.Dsl.Module.Prelude;
                module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
                cache = Cache.Define(new() {
                    Remote = Remote.From("https://cache.example.com", new() { Token = Environment.GetEnvironmentVariable("TAZEL_TEST_NO_SUCH_TOKEN") })
                });
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\n");

        var workspace = await module.LoadAsync();

        Assert.Equal("cache.example.com", workspace.Main.Globals.cache!.Settings.Remote!.Url.Host);
        Assert.Null(workspace.Main.Globals.cache!.Settings.Remote!.Settings.Token);
    }

    [Fact]
    public void AResultIsTheProtocolBufferOfTheRemoteExecutionApi()
    {
        OutputFile[] files = [new("out/app", new string('a', 64), 1234, true), new("out/app.d", new string('b', 64), 7, false)];

        var bytes = ActionResultMessage.Encode(files);

        // Field 2, length-delimited: an OutputFile; inside, field 1 is its path.
        Assert.Equal(0x12, bytes[0]);
        Assert.Equal(files, ActionResultMessage.Decode(bytes));
        Assert.Null(ActionResultMessage.Decode(bytes.AsSpan(0, bytes.Length - 3)));
        // An exit code other than 0 (field 4) is not a result Tazel takes.
        Assert.Null(ActionResultMessage.Decode([.. bytes, 0x20, 0x01]));
    }

    [Fact]
    public async Task AnotherCheckoutTakesTheResultsAndRunsNoCompiler()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        using var first = Module(server.Address, write: true);
        var (status, output) = await first.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.NotEmpty(server.Actions);
        Assert.All(server.Authorizations, header => Assert.Equal("Bearer secret", header));

        using var second = Module(server.Address, write: false);
        (status, output) = await second.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.Contains("Compiling //:app main.c (remote cache)", output, StringComparison.Ordinal);
        Assert.Contains("(remote cache)", output.Split('\n').Single(line => line.Contains("Linking", StringComparison.Ordinal)),
            StringComparison.Ordinal);
        (status, output) = await second.TazelAsync(".", "run", "//:app");
        Assert.True(status == 0, output);
    }

    [Fact]
    public async Task AHeaderThatDiffersHereIsCompiledAgain()
    {
        SkipWithoutCompiler();
        using var server = new FakeRemoteCache();
        using var first = Module(server.Address, write: true);
        Assert.Equal(0, (await first.TazelAsync(".", "build", "//:app")).Status);

        // The same declared inputs, but value.h, which the compiler found, is not the same.
        using var second = Module(server.Address, write: false, value: "41");
        var (status, output) = await second.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("main.c (remote cache)", output, StringComparison.Ordinal);
        (status, _) = await second.TazelAsync(".", "run", "//:app");
        Assert.Equal(1, status);
    }

    [Fact]
    public async Task ACacheThatCannotBeReachedIsSaidOnceAndTheBuildGoesOn()
    {
        SkipWithoutCompiler();
        string address;
        using (var server = new FakeRemoteCache())
        {
            address = server.Address;
        }

        using var module = Module(address, write: true);
        var (status, output) = await module.TazelAsync(".", "build", "//:app");
        Assert.True(status == 0, output);
        Assert.Single(output.Split('\n'), line => line.Contains("cannot be used", StringComparison.Ordinal));
        Assert.Contains("building without it", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResultWhosePathsLeaveTheActionIsRefused()
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule().Write("in.txt", "in\n");
        var action = new BuildAction
        {
            Description = "Copying",
            Command = ["/bin/cp", "in.txt", "out.txt"],
            Directory = module.Root,
            Inputs = ["in.txt"],
            Outputs = ["out.txt"],
        };
        var digests = new Digests();
        using var cache = new RemoteCache(new Uri(server.Address), token: null);
        var remote = new RemoteActions(cache, read: true, write: false, digests, _ => { });
        var evil = Encoding.UTF8.GetBytes("owned\n");
        var hash = Digests.Sha256(evil);
        server.Content[hash] = evil;
        var empty = Digests.Sha256([]);
        server.Content[empty] = [];
        server.Actions[remote.KeyOf(action)!] = ActionResultMessage.Encode([
            new OutputFile("out.txt", hash, evil.Length, false),
            new OutputFile("../escaped.txt", hash, evil.Length, false),
            new OutputFile(RemoteActions.DiscoveredInputs, empty, 0, false),
        ]);

        Assert.False(await remote.FetchAsync(action, Xunit.TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(module.Root, "out.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(module.Root)!, "escaped.txt")));
    }

    [Fact]
    public async Task ContentThatIsNotWhatItsHashSaysIsRefused()
    {
        using var server = new FakeRemoteCache();
        using var module = new TemporaryModule().Write("in.txt", "in\n");
        var action = new BuildAction
        {
            Description = "Copying",
            Command = ["/bin/cp", "in.txt", "out.txt"],
            Directory = module.Root,
            Inputs = ["in.txt"],
            Outputs = ["out.txt"],
        };
        using var cache = new RemoteCache(new Uri(server.Address), token: "Basic dXNlcjpwYXNz");
        var remote = new RemoteActions(cache, read: true, write: false, new Digests(), _ => { });
        var claimed = Digests.Sha256(Encoding.UTF8.GetBytes("right\n"));
        server.Content[claimed] = Encoding.UTF8.GetBytes("wrong\n");
        var empty = Digests.Sha256([]);
        server.Content[empty] = [];
        server.Actions[remote.KeyOf(action)!] = ActionResultMessage.Encode([
            new OutputFile("out.txt", claimed, 6, false),
            new OutputFile(RemoteActions.DiscoveredInputs, empty, 0, false),
        ]);

        Assert.False(await remote.FetchAsync(action, Xunit.TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(module.Root, "out.txt")));
        Assert.Contains("Basic dXNlcjpwYXNz", server.Authorizations);
    }
}

/// <summary>
/// A remote cache on this machine, as bazel-remote serves one over HTTP: <c>/ac/</c> takes only messages that decode
/// as an ActionResult, as bazel-remote checks them, and <c>/cas/</c> only content whose SHA-256 is its name.
/// </summary>
internal sealed class FakeRemoteCache : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeRemoteCache()
    {
        using (var socket = new TcpListener(IPAddress.Loopback, 0))
        {
            socket.Start();
            Address = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}";
        }

        _listener.Prefixes.Add(Address + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Address { get; }

    public ConcurrentDictionary<string, byte[]> Actions { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, byte[]> Content { get; } = new(StringComparer.Ordinal);

    public ConcurrentBag<string> Authorizations { get; } = [];

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await AnswerAsync(context);
            }
            catch (HttpListenerException)
            {
                // The client went away.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        Authorizations.Add(context.Request.Headers["Authorization"] ?? "");
        var parts = context.Request.Url!.AbsolutePath.Trim('/').Split('/');
        var store = parts is ["ac", _] ? Actions : parts is ["cas", _] ? Content : null;
        if (store is null || parts[1].Length != 64)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var key = parts[1];
        switch (context.Request.HttpMethod)
        {
            case "GET" or "HEAD" when store.TryGetValue(key, out var bytes):
                context.Response.StatusCode = 200;
                context.Response.ContentLength64 = bytes.Length;
                if (context.Request.HttpMethod == "GET")
                {
                    await context.Response.OutputStream.WriteAsync(bytes);
                }

                break;
            case "PUT":
                using (var body = new MemoryStream())
                {
                    await context.Request.InputStream.CopyToAsync(body);
                    var bytes = body.ToArray();
                    var valid = store == Actions ? ActionResultMessage.Decode(bytes) is not null : Digests.Sha256(bytes) == key;
                    context.Response.StatusCode = valid ? 200 : 400;
                    if (valid)
                    {
                        store[key] = bytes;
                    }
                }

                break;
            default:
                context.Response.StatusCode = 404;
                break;
        }
    }
}
