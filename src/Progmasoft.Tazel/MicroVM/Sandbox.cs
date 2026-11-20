// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.MicroVM;

/// <summary>A directory of the host the MicroVM sees at the same path.</summary>
/// <param name="Path">The directory.</param>
/// <param name="ReadOnly">Whether the guest may only read it.</param>
internal sealed record SharedDirectory(string Path, bool ReadOnly);

/// <summary>How big a MicroVM is.</summary>
/// <param name="MemoryMiB">Its memory.</param>
/// <param name="Processors">Its processors.</param>
internal sealed record MicroVMSize(int MemoryMiB, int Processors);

/// <summary>
/// One MicroVM: Cloud Hypervisor and a virtiofsd per shared directory, each a process of its own, so the death of any
/// of them ends the commands inside with an error and never Tazel. Commands reach the agent over vsock: Tazel connects
/// to the Unix socket of the monitor and asks for the agent's port.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class Sandbox : IAsyncDisposable
{
    /// <summary>The environment every command inside gets, and nothing of the host's.</summary>
    public static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PATH"] = MicroVMProtocol.DefaultPath,
        ["HOME"] = "/root",
        ["TMPDIR"] = "/tmp",
        ["LANG"] = "C.UTF-8",
    };

    private readonly string _runtime;
    private readonly List<Process> _daemons = [];
    private readonly Process _monitor;
    private readonly string _vsock;
    private readonly string _console;

    private Sandbox(string runtime, Process monitor, string vsock, string console)
    {
        _runtime = runtime;
        _monitor = monitor;
        _vsock = vsock;
        _console = console;
    }

    /// <summary>Starts a MicroVM that sees <paramref name="shares"/> and waits until its agent answers.</summary>
    public static async Task<Sandbox> StartAsync(
        MicroVMFiles files, IReadOnlyList<SharedDirectory> shares, MicroVMSize size, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(size);
        var runtime = Directory.CreateTempSubdirectory("tazel-microvm-").FullName;
        File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var daemons = new List<Process>();
        try
        {
            // The guest's /usr is the host's, read-only, and its /etc a few files of Tazel's.
            var all = new List<SharedDirectory>(shares) { new("/usr", ReadOnly: true), new(WriteEtc(runtime), ReadOnly: true) };
            var command = new StringBuilder("console=hvc0 quiet panic=0 rdinit=/tazel/agent/ld-musl-x86_64.so.1");
            var arguments = new List<string>();
            for (var i = 0; i < all.Count; i++)
            {
                var tag = $"s{i}";
                var socket = Path.Combine(runtime, $"{tag}.sock");
                daemons.Add(StartVirtiofsd(files.Virtiofsd, all[i], socket));
                arguments.AddRange(["--fs", $"tag={tag},socket={socket},num_queues=1,queue_size=512"]);
                var guestPath = i == all.Count - 1 ? "/etc" : all[i].Path;
                command.Append(CultureInfo.InvariantCulture,
                    $" tazel.share={tag},{(all[i].ReadOnly ? "ro" : "rw")},{MicroVMProtocol.EncodePath(guestPath)}");
            }

            command.Append(" -- --library-path /tazel/lib /tazel/agent/tazel-guest");
            foreach (var share in all.Select((share, i) => (share, i)))
            {
                await WaitForAsync(Path.Combine(runtime, $"s{share.i}.sock"), daemons[share.i], "virtiofsd", cancel).ConfigureAwait(false);
            }

            var vsock = Path.Combine(runtime, "vsock.sock");
            var console = Path.Combine(runtime, "console.log");
            var start = new ProcessStartInfo(files.Monitor)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[]
            {
                "--kernel", files.Kernel, "--initramfs", files.Initramfs, "--cmdline", command.ToString(),
                "--cpus", $"boot={size.Processors}", "--memory", $"size={size.MemoryMiB}M,shared=on",
                "--vsock", $"cid=3,socket={vsock}", "--console", $"file={console}", "--serial", "off",
            }.Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            var monitor = Process.Start(start) ?? throw new MicroVMException($"{files.Monitor} could not be started");
            monitor.StandardInput.Close();
            var sandbox = new Sandbox(runtime, monitor, vsock, console);
            sandbox._daemons.AddRange(daemons);
            await sandbox.WaitUntilReadyAsync(cancel).ConfigureAwait(false);
            return sandbox;
        }
        catch
        {
            foreach (var daemon in daemons)
            {
                Kill(daemon);
            }

            Directory.Delete(runtime, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="arguments"/> in <paramref name="directory"/> inside, with <see cref="Environment"/>; what it
    /// prints goes to <paramref name="output"/> and <paramref name="errors"/>. Returns its exit status.
    /// </summary>
    public async Task<int> RunAsync(
        IReadOnlyList<string> arguments, string directory, Stream output, Stream errors, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(errors);
        using var connection = await ConnectAsync(cancel).ConfigureAwait(false);
        var stream = new NetworkStream(connection, ownsSocket: false);
        await using (stream.ConfigureAwait(false))
        {
            await MicroVMProtocol.WriteAsync(stream, MessageKind.Run,
                MicroVMProtocol.Encode(new RunRequest(arguments, Environment, directory)), cancel).ConfigureAwait(false);
            using var stop = cancel.Register(() => connection.Shutdown(SocketShutdown.Both));
            while (true)
            {
                var message = await MicroVMProtocol.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
                switch (message?.Kind)
                {
                    case MessageKind.Output:
                        var target = message.Value.Payload[0] == 2 ? errors : output;
                        await target.WriteAsync(message.Value.Payload.AsMemory(1), CancellationToken.None).ConfigureAwait(false);
                        await target.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        break;
                    case MessageKind.Exit:
                        return MicroVMProtocol.DecodeStatus(message.Value.Payload);
                    case MessageKind.Error:
                        throw new MicroVMException(Encoding.UTF8.GetString(message.Value.Payload));
                    case null:
                        cancel.ThrowIfCancellationRequested();
                        throw new MicroVMException($"the MicroVM stopped answering while {arguments[0]} ran{ConsoleTail()}");
                    default:
                        throw new MicroVMException($"the agent sent a message of the kind {message.Value.Kind}");
                }
            }
        }
    }

    /// <summary>Asks the guest to power off, then stops whatever is left.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_monitor.HasExited)
            {
                using var connection = await ConnectAsync(CancellationToken.None, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                var stream = new NetworkStream(connection, ownsSocket: false);
                await using (stream.ConfigureAwait(false))
                {
                    await MicroVMProtocol.WriteAsync(stream, MessageKind.PowerOff, ReadOnlyMemory<byte>.Empty, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _monitor.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is MicroVMException or IOException or SocketException or OperationCanceledException)
        {
            // It does not answer; it is stopped below.
        }
        finally
        {
            Kill(_monitor);
            foreach (var daemon in _daemons)
            {
                Kill(daemon);
            }

            _monitor.Dispose();
            Directory.Delete(_runtime, recursive: true);
        }
    }

    private static string WriteEtc(string runtime)
    {
        var etc = Path.Combine(runtime, "etc");
        Directory.CreateDirectory(etc);
        File.WriteAllText(Path.Combine(etc, "passwd"), "root:x:0:0:root:/root:/bin/sh\nnobody:x:65534:65534:nobody:/:/bin/false\n");
        File.WriteAllText(Path.Combine(etc, "group"), "root:x:0:\ntty:x:5:\nnobody:x:65534:\n");
        File.WriteAllText(Path.Combine(etc, "hosts"), "127.0.0.1 localhost tazel\n::1 localhost tazel\n");
        File.WriteAllText(Path.Combine(etc, "hostname"), "tazel\n");
        // The host's tools look for their libraries through the cache of the host's loader.
        foreach (var name in new[] { "ld.so.cache", "ld.so.conf" })
        {
            if (File.Exists(Path.Combine("/etc", name)))
            {
                File.Copy(Path.Combine("/etc", name), Path.Combine(etc, name));
            }
        }

        return etc;
    }

    private static Process StartVirtiofsd(string program, SharedDirectory share, string socket)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { $"--socket-path={socket}", $"--shared-dir={share.Path}", "--sandbox=namespace", "--cache=auto" }
                     .Concat(share.ReadOnly ? ["--readonly"] : Array.Empty<string>()))
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new MicroVMException($"{program} could not be started");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task WaitForAsync(string socket, Process daemon, string what, CancellationToken cancel)
    {
        for (var i = 0; i < 100 && !File.Exists(socket); i++)
        {
            if (daemon.HasExited)
            {
                throw new MicroVMException($"{what} ended with {daemon.ExitCode} before it was ready");
            }

            await Task.Delay(50, cancel).ConfigureAwait(false);
        }

        if (!File.Exists(socket))
        {
            throw new MicroVMException($"{what} did not open {socket} within five seconds");
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_monitor.HasExited)
            {
                var errors = await _monitor.StandardError.ReadToEndAsync(cancel).ConfigureAwait(false);
                throw new MicroVMException(
                    $"Cloud Hypervisor ended with {_monitor.ExitCode} while the MicroVM started: {errors.Trim()}{ConsoleTail()}");
            }

            try
            {
                using var connection = await ConnectAsync(cancel, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                var stream = new NetworkStream(connection, ownsSocket: false);
                await using (stream.ConfigureAwait(false))
                {
                    await MicroVMProtocol.WriteAsync(stream, MessageKind.Hello, ReadOnlyMemory<byte>.Empty, cancel).ConfigureAwait(false);
                    if (await MicroVMProtocol.ReadAsync(stream, cancel).ConfigureAwait(false) is { Kind: MessageKind.Ready })
                    {
                        return;
                    }
                }
            }
            catch (Exception error) when (error is MicroVMException or IOException or SocketException)
            {
                // The agent is not listening yet.
            }

            await Task.Delay(100, cancel).ConfigureAwait(false);
        }

        throw new MicroVMException($"the agent of the MicroVM did not answer within a minute{ConsoleTail()}");
    }

    /// <summary>A connection to the agent: Cloud Hypervisor's hybrid vsock takes "CONNECT port" and answers "OK".</summary>
    private async Task<Socket> ConnectAsync(CancellationToken cancel, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_vsock), limit.Token).ConfigureAwait(false);
            await socket.SendAsync(Encoding.ASCII.GetBytes($"CONNECT {MicroVMProtocol.Port}\n"), limit.Token).ConfigureAwait(false);
            var line = new List<byte>();
            var one = new byte[1];
            while (line.Count < 64)
            {
                if (await socket.ReceiveAsync(one, limit.Token).ConfigureAwait(false) == 0)
                {
                    throw new MicroVMException("the monitor closed the vsock connection");
                }

                if (one[0] == '\n')
                {
                    break;
                }

                line.Add(one[0]);
            }

            return Encoding.ASCII.GetString(line.ToArray()).StartsWith("OK ", StringComparison.Ordinal)
                ? socket
                : throw new MicroVMException("the agent is not listening");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private string ConsoleTail()
    {
        try
        {
            var lines = File.Exists(_console) ? File.ReadAllLines(_console) : [];
            return lines.Length == 0 ? "" : "; the console of the MicroVM ends with:\n  " + string.Join("\n  ", lines.TakeLast(8));
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // It ended by itself.
        }
    }
}
