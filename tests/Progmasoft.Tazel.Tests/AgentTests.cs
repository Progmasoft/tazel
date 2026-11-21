// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

extern alias guest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.MicroVM;
using Xunit;
using Agent = guest::Progmasoft.Tazel.Guest.Agent;
using Libc = guest::Progmasoft.Tazel.Guest.Libc;
using Share = guest::Progmasoft.Tazel.Guest.Share;
using VsockStream = guest::Progmasoft.Tazel.Guest.VsockStream;

namespace Progmasoft.Tazel.Tests;

/// <summary>
/// The agent's serving of one connection, on this machine: the agent at one end of a pair of sockets, the test at the
/// other, speaking the protocol Tazel speaks to the guest.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AgentTests
{
    private static readonly Lazy<bool> Resolved = new(() =>
    {
        Libc.Resolve();
        return true;
    });

    private static void SkipOffLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "the agent runs on Linux");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>Two connected streams: the agent's end and the test's.</summary>
    private static async Task<(NetworkStream Agent, NetworkStream Host, IDisposable Sockets)> PairAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, Cancel);
        var server = await listener.AcceptTcpClientAsync(Cancel);
        return (server.GetStream(), client.GetStream(), new Both(server, client));
    }

    private sealed class Both(TcpClient one, TcpClient two) : IDisposable
    {
        public void Dispose()
        {
            one.Dispose();
            two.Dispose();
        }
    }

    /// <summary>Runs <paramref name="arguments"/> through the agent in <paramref name="directory"/> and reads every answer.</summary>
    private static async Task<(int? Status, string Output, string Errors, string? Refused)> RunAsync(
        Agent agent, string directory, params string[] arguments)
    {
        var (agentEnd, host, sockets) = await PairAsync();
        using (sockets)
        {
            var serving = agent.ServeAsync(agentEnd, () => { });
            var request = new RunRequest(arguments,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin:/bin" }, directory);
            await MicroVMProtocol.WriteAsync(host, MessageKind.Run, MicroVMProtocol.Encode(request), Cancel);
            var output = new StringBuilder();
            var errors = new StringBuilder();
            while (await MicroVMProtocol.ReadAsync(host, Cancel) is { } message)
            {
                switch (message.Kind)
                {
                    case MessageKind.Output:
                        (message.Payload[0] == 1 ? output : errors).Append(Encoding.UTF8.GetString(message.Payload, 1, message.Payload.Length - 1));
                        break;
                    case MessageKind.Exit:
                        host.Close();
                        await serving;
                        return (MicroVMProtocol.DecodeStatus(message.Payload), output.ToString(), errors.ToString(), null);
                    case MessageKind.Error:
                        await serving;
                        return (null, output.ToString(), errors.ToString(), Encoding.UTF8.GetString(message.Payload));
                    default:
                        throw new InvalidDataException($"the agent answered {message.Kind}");
                }
            }

            throw new EndOfStreamException("the agent closed the connection without an answer");
        }
    }

    [Fact]
    public async Task HelloIsAnsweredWithTheVersionAndPowerOffIsPassedOn()
    {
        SkipOffLinux();
        var agent = new Agent([]);
        var (agentEnd, host, sockets) = await PairAsync();
        using (sockets)
        {
            var serving = agent.ServeAsync(agentEnd, () => { });
            await MicroVMProtocol.WriteAsync(host, MessageKind.Hello, ReadOnlyMemory<byte>.Empty, Cancel);
            var answer = await MicroVMProtocol.ReadAsync(host, Cancel);
            Assert.Equal(MessageKind.Ready, answer!.Value.Kind);
            Assert.Equal(Agent.Version, Encoding.UTF8.GetString(answer.Value.Payload));
            await serving;
        }

        (agentEnd, host, sockets) = await PairAsync();
        using (sockets)
        {
            var off = false;
            var serving = agent.ServeAsync(agentEnd, () => off = true);
            await MicroVMProtocol.WriteAsync(host, MessageKind.PowerOff, ReadOnlyMemory<byte>.Empty, Cancel);
            await serving;
            Assert.True(off);
        }
    }

    [Theory]
    [InlineData((byte)MessageKind.Stop)]
    [InlineData((byte)MessageKind.Ready)]
    public async Task AConnectionThatOpensWithAnythingElseIsClosedUnanswered(byte kind)
    {
        SkipOffLinux();
        var (agentEnd, host, sockets) = await PairAsync();
        using (sockets)
        {
            var serving = new Agent([]).ServeAsync(agentEnd, () => { });
            await MicroVMProtocol.WriteAsync(host, (MessageKind)kind, ReadOnlyMemory<byte>.Empty, Cancel);
            await serving;
            Assert.Null(await MicroVMProtocol.ReadAsync(host, Cancel));
        }
    }

    [Fact]
    public async Task ABrokenMessageEndsTheConnectionAndNotTheAgent()
    {
        SkipOffLinux();
        var (agentEnd, host, sockets) = await PairAsync();
        using (sockets)
        {
            var serving = new Agent([]).ServeAsync(agentEnd, () => { });
            // A Run frame that says 100 bytes follow, and then the host goes away.
            await host.WriteAsync(new byte[] { (byte)MessageKind.Run, 100, 0, 0, 0, 1, 2 }, Cancel);
            host.Close();
            await serving;
        }
    }

    [Fact]
    public async Task ACommandRunsInASharedDirectoryWithItsOutputErrorsAndStatus()
    {
        SkipOffLinux();
        using var module = new TemporaryModule();
        var agent = new Agent([new Share("work", module.Root, ReadOnly: false)]);

        var (status, output, errors, refused) = await RunAsync(agent, module.Root, "sh", "-c", "echo out; echo err >&2; exit 4");

        Assert.Null(refused);
        Assert.Equal(4, status);
        Assert.Equal("out\n", output);
        Assert.Equal("err\n", errors);
    }

    [Fact]
    public async Task OutputAndErrorsWrittenTogetherArriveAsWholeFrames()
    {
        SkipOffLinux();
        using var module = new TemporaryModule();
        var agent = new Agent([new Share("work", module.Root, ReadOnly: false)]);

        var (status, output, errors, _) = await RunAsync(agent, module.Root, "sh", "-c",
            "i=0; while [ $i -lt 3000 ]; do echo o$i; echo e$i >&2; i=$((i+1)); done");

        Assert.Equal(0, status);
        Assert.Equal(Enumerable.Range(0, 3000).Select(i => $"o{i}\n"), output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l + "\n"));
        Assert.Equal(3000, errors.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task WhatCannotRunIsRefusedWithItsReason()
    {
        SkipOffLinux();
        using var module = new TemporaryModule().Write("plain.txt", "not a program\n");
        var outside = Path.GetTempPath();
        var agent = new Agent([new Share("work", module.Root, ReadOnly: false), new Share("tools", "/usr", ReadOnly: true)]);

        Assert.Equal("the command is empty", (await RunAsync(agent, module.Root)).Refused);
        Assert.Contains("is not inside a directory Tazel shared", (await RunAsync(agent, outside, "true")).Refused, StringComparison.Ordinal);
        Assert.Contains("is not inside a directory Tazel shared", (await RunAsync(agent, "/usr", "true")).Refused, StringComparison.Ordinal);
        Assert.Contains("is not inside a directory Tazel shared",
            (await RunAsync(agent, Path.Combine(module.Root, "missing"), "true")).Refused, StringComparison.Ordinal);
        Assert.Contains("no-such-program is not found in the MicroVM",
            (await RunAsync(agent, module.Root, "no-such-program")).Refused, StringComparison.Ordinal);
        Assert.Contains("could not be started",
            (await RunAsync(agent, module.Root, Path.Combine(module.Root, "plain.txt"))).Refused, StringComparison.Ordinal);
        Assert.Contains("is not found in the MicroVM",
            (await RunAsync(agent, module.Root, Path.Combine(module.Root, "missing.sh"))).Refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOrAHostThatLeavesEndsTheCommand()
    {
        SkipOffLinux();
        using var module = new TemporaryModule();
        var agent = new Agent([new Share("work", module.Root, ReadOnly: false)]);
        foreach (var leave in new[] { false, true })
        {
            var (agentEnd, host, sockets) = await PairAsync();
            using (sockets)
            {
                var serving = agent.ServeAsync(agentEnd, () => { });
                var request = new RunRequest(["sleep", "60"], new Dictionary<string, string>(StringComparer.Ordinal), module.Root);
                await MicroVMProtocol.WriteAsync(host, MessageKind.Run, MicroVMProtocol.Encode(request), Cancel);
                await Task.Delay(300, Cancel);
                var started = DateTime.UtcNow;
                if (leave)
                {
                    host.Socket.Shutdown(SocketShutdown.Send);
                }
                else
                {
                    await MicroVMProtocol.WriteAsync(host, MessageKind.Stop, ReadOnlyMemory<byte>.Empty, Cancel);
                }

                var answer = await MicroVMProtocol.ReadAsync(host, Cancel);
                Assert.Equal(MessageKind.Exit, answer!.Value.Kind);
                Assert.NotEqual(0, MicroVMProtocol.DecodeStatus(answer.Value.Payload));
                Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
                host.Close();
                await serving;
            }
        }
    }

    [Fact]
    public void FindTakesAPathWithASlashAsItIsAndSearchesThePathForAName()
    {
        SkipOffLinux();
        Assert.Equal("/bin/sh", Agent.Find("/bin/sh", ""));
        Assert.Null(Agent.Find("/no/such/program", ""));
        Assert.EndsWith("/sh", Agent.Find("sh", "/nowhere:/usr/bin:/bin"), StringComparison.Ordinal);
        Assert.Null(Agent.Find("sh", "/nowhere"));
        Assert.NotNull(Agent.Find("sh", null!));
    }

    [Fact]
    public async Task AVsockStreamReadsAndWritesItsDescriptorAndRefusesWhatItCannotDo()
    {
        SkipOffLinux();
        _ = Resolved.Value;
        var (one, two) = SocketPair();
        using var left = new VsockStream(one);
        using var right = new VsockStream(two);
        Assert.True(left.CanRead && left.CanWrite && !left.CanSeek);
        Assert.Throws<NotSupportedException>(() => left.Length);
        Assert.Throws<NotSupportedException>(() => left.Position);
        Assert.Throws<NotSupportedException>(() => left.Position = 0);
        Assert.Throws<NotSupportedException>(() => left.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => left.SetLength(0));

        left.Write("hello"u8.ToArray(), 0, 5);
        left.Flush();
        var buffer = new byte[16];
        Assert.Equal(5, right.Read(buffer, 0, buffer.Length));
        Assert.Equal("hello", Encoding.ASCII.GetString(buffer, 0, 5));

        await left.WriteAsync("again"u8.ToArray(), 0, 5, Cancel);
        await left.WriteAsync("!"u8.ToArray().AsMemory(), Cancel);
        var memory = new byte[16];
        var read = 0;
        while (read < 6)
        {
            read += await right.ReadAsync(memory.AsMemory(read), Cancel);
        }

        Assert.Equal("again!", Encoding.ASCII.GetString(memory, 0, 6));
        Assert.Equal(0, await right.ReadAsync(buffer, 0, 0, Cancel));

        right.Dispose();
        Assert.Throws<IOException>(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                left.Write(new byte[65536], 0, 65536);
            }
        });
        Assert.Throws<IOException>(() => right.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void AVsockAddressIsEqualByEachOfItsFields()
    {
        var address = new Libc.SockaddrVM { Family = Libc.AfVsock, Port = MicroVMProtocol.Port, Cid = Libc.VmaddrCidAny };
        var same = address;
        var other = address with { Port = 1025 };

        Assert.True(address == same);
        Assert.False(address != same);
        Assert.True(address != other);
        Assert.True(address.Equals((object)same));
        Assert.False(address.Equals((object)"not an address"));
        Assert.Equal(address.GetHashCode(), same.GetHashCode());
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SocketPairFunction(int domain, int type, int protocol, [Out] int[] descriptors);

    /// <summary>A pair of connected Unix sockets, from the C library the test runs with (AF_UNIX, SOCK_STREAM).</summary>
    private static (int, int) SocketPair()
    {
        var socketpair = Marshal.GetDelegateForFunctionPointer<SocketPairFunction>(
            NativeLibrary.GetExport(NativeLibrary.GetMainProgramHandle(), "socketpair"));
        var descriptors = new int[2];
        Assert.Equal(0, socketpair(1, 1, 0, descriptors));
        return (descriptors[0], descriptors[1]);
    }
}
