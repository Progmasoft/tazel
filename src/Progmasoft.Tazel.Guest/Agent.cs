// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.MicroVM;

namespace Progmasoft.Tazel.Guest;

/// <summary>A directory of the host the guest sees, from <c>tazel.share=tag,ro|rw,path</c> on the kernel command line.</summary>
/// <param name="Tag">The virtio-fs tag.</param>
/// <param name="Path">Where it is mounted: the same path as on the host.</param>
/// <param name="ReadOnly">Whether the guest may only read it.</param>
internal sealed record Share(string Tag, string Path, bool ReadOnly)
{
    /// <summary>The shares of a kernel command line; the path is base64url, as a path may hold spaces.</summary>
    public static IReadOnlyList<Share> Parse(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var shares = new List<Share>();
        foreach (var word in commandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!word.StartsWith("tazel.share=", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = word["tazel.share=".Length..].Split(',');
            if (parts.Length != 3 || parts[1] is not ("ro" or "rw"))
            {
                throw new FormatException($"'{word}' is not tazel.share=tag,ro|rw,path");
            }

            var path = MicroVMProtocol.DecodePath(parts[2]);
            if (!path.StartsWith('/') || path.Split('/').Contains(".."))
            {
                throw new FormatException($"the share {parts[0]} has the path '{path}'");
            }

            shares.Add(new Share(parts[0], path, parts[1] == "ro"));
        }

        return shares;
    }

}

/// <summary>
/// The first process of the guest: it mounts what the guest needs, listens on vsock, runs the commands Tazel sends,
/// and powers the machine off when told to or when it cannot go on.
/// </summary>
internal sealed class Agent(IReadOnlyList<Share> shares)
{
    /// <summary>The PATH of the agent and the one it searches when a command brings none.</summary>
    public const string DefaultPath = MicroVMProtocol.DefaultPath;

    public static string Version => typeof(Agent).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Mounts the kernel's file systems and the shares, and points the root's directories into /usr.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Boot()
    {
        MountOrFail("sysfs", "/sys", "sysfs", Libc.MsNosuid | Libc.MsNodev | Libc.MsNoexec, null);
        MountOrFail("devtmpfs", "/dev", "devtmpfs", Libc.MsNosuid, "mode=0755");
        MountOrFail("devpts", "/dev/pts", "devpts", Libc.MsNosuid | Libc.MsNoexec, "gid=5,mode=0620,ptmxmode=0666");
        MountOrFail("tmpfs", "/tmp", "tmpfs", Libc.MsNosuid | Libc.MsNodev, "mode=1777");
        MountOrFail("tmpfs", "/run", "tmpfs", Libc.MsNosuid | Libc.MsNodev, "mode=0755");
        foreach (var share in shares)
        {
            MountOrFail(share.Tag, share.Path, "virtiofs", share.ReadOnly ? Libc.MsRdonly : 0, null);
        }

        // Merged /usr: the host's tools find their loader and libraries where they were built to look.
        if (Directory.Exists("/usr/bin"))
        {
            Link("/bin", "usr/bin");
            Link("/sbin", Directory.Exists("/usr/sbin") ? "usr/sbin" : "usr/bin");
            Link("/lib", "usr/lib");
            Link("/lib64", Directory.Exists("/usr/lib64") ? "usr/lib64" : "usr/lib");
        }

        Directory.CreateDirectory("/root");
        var name = "tazel"u8;
        _ = Libc.SetHostName(name, (nuint)name.Length);
        Environment.SetEnvironmentVariable("PATH", DefaultPath);
    }

    /// <summary>Serves connections until one asks the machine to power off.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Serve()
    {
        var listener = Libc.Socket(Libc.AfVsock, Libc.SockStream, 0);
        if (listener < 0)
        {
            throw new IOException($"vsock: {Libc.LastError()}");
        }

        var address = new Libc.SockaddrVM { Family = Libc.AfVsock, Port = MicroVMProtocol.Port, Cid = Libc.VmaddrCidAny };
        if (Libc.Bind(listener, address, 16) != 0 || Libc.Listen(listener, 64) != 0)
        {
            throw new IOException($"vsock port {MicroVMProtocol.Port}: {Libc.LastError()}");
        }

        using var off = new ManualResetEventSlim();
        var accepting = new Thread(() =>
        {
            while (true)
            {
                var connection = Libc.Accept(listener, IntPtr.Zero, IntPtr.Zero);
                if (connection >= 0)
                {
                    _ = Task.Run(() => ServeAsync(new VsockStream(connection), off.Set));
                }
            }
        }) { IsBackground = true, Name = "vsock" };
        accepting.Start();
        off.Wait();
    }

    /// <summary>
    /// Runs the process-exit handlers, writes everything out and powers off; the monitor ends with the machine. As the
    /// first process the agent never exits, so the handlers a normal exit runs (among them those of Coverlet, which
    /// write the lines the agent ran) run here.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static void PowerOff()
    {
        RunExitHandlers();
        Libc.Sync();
        _ = Libc.Reboot(Libc.RebootPowerOff);
        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>Raises <see cref="AppDomain.ProcessExit"/> as the runtime does when a process exits.</summary>
    [System.Diagnostics.CodeAnalysis.DynamicDependency("OnProcessExit", typeof(AppContext))]
    internal static void RunExitHandlers()
    {
        var onProcessExit = typeof(AppContext).GetMethod("OnProcessExit",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        try
        {
            onProcessExit?.Invoke(null, null);
        }
        catch (System.Reflection.TargetInvocationException error)
        {
            // A handler that fails does not keep the machine on.
            Console.Error.WriteLine($"tazel-guest: an exit handler failed: {error.InnerException?.Message}");
        }
    }

    /// <summary>The program <paramref name="name"/> names: itself when it has a slash, else the first on <paramref name="path"/>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public static string? Find(string name, string path)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return File.Exists(name) ? name : null;
        }

        return (path ?? DefaultPath).Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(candidate => File.Exists(candidate) &&
                (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);
    }

    /// <summary>Whether <paramref name="directory"/> is inside a share the guest may write.</summary>
    public bool Inside(string directory)
    {
        var full = Normalize(directory);
        return full is not null && shares.Any(share => !share.ReadOnly &&
            (full == share.Path || full.StartsWith(share.Path.TrimEnd('/') + "/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// An absolute guest path with its "." and ".." taken out, by the rules of the guest whatever runs this code; null
    /// for a path that is not absolute or climbs above the root.
    /// </summary>
    public static string? Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.StartsWith('/'))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        return "/" + string.Join('/', parts);
    }

    /// <summary>Serves one connection: Hello, a command, or the order to power off, which calls <paramref name="off"/>.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    internal async Task ServeAsync(Stream stream, Action off)
    {
        using (stream)
        {
            try
            {
                var message = await MicroVMProtocol.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
                switch (message?.Kind)
                {
                    case MessageKind.Hello:
                        await MicroVMProtocol.WriteAsync(stream, MessageKind.Ready, Encoding.UTF8.GetBytes(Version), CancellationToken.None)
                            .ConfigureAwait(false);
                        break;
                    case MessageKind.Run:
                        await RunAsync(stream, MicroVMProtocol.DecodeRun(message.Value.Payload)).ConfigureAwait(false);
                        break;
                    case MessageKind.PowerOff:
                        off();
                        break;
                    default:
                        // A connection opens with Hello, Run or PowerOff; anything else is closed unanswered.
                        break;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or EndOfStreamException)
            {
                // The host went away or sent something broken; the connection ends, the agent goes on.
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task RunAsync(Stream stream, RunRequest request)
    {
        if (request.Arguments.Count == 0)
        {
            await Refuse(stream, "the command is empty").ConfigureAwait(false);
            return;
        }

        if (!Inside(request.Directory) || !Directory.Exists(request.Directory))
        {
            await Refuse(stream, $"{request.Directory} is not inside a directory Tazel shared").ConfigureAwait(false);
            return;
        }

        var program = Find(request.Arguments[0], request.Environment.GetValueOrDefault("PATH", DefaultPath));
        if (program is null)
        {
            await Refuse(stream, $"{request.Arguments[0]} is not found in the MicroVM").ConfigureAwait(false);
            return;
        }

        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = request.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in request.Arguments.Skip(1))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        foreach (var (name, value) in request.Environment)
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            await Refuse(stream, $"{program} could not be started: {error.Message}").ConfigureAwait(false);
            return;
        }

        process.StandardInput.Close();
        var stop = Task.Run(async () =>
        {
            // The host sends Stop, or goes away, while the command runs.
            try
            {
                await MicroVMProtocol.ReadAsync(stream, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or EndOfStreamException)
            {
                // The host went away: that is a stop too.
                Console.Error.WriteLine($"tazel-guest: the host left while {program} ran: {error.Message}");
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        });
        // Output and errors are written from two tasks; the frames go out one at a time.
        using var writing = new SemaphoreSlim(1, 1);
        var output = Pump(process.StandardOutput.BaseStream, 1, stream, writing);
        var errors = Pump(process.StandardError.BaseStream, 2, stream, writing);
        await Task.WhenAll(output, errors).ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await MicroVMProtocol.WriteAsync(stream, MessageKind.Exit, MicroVMProtocol.EncodeStatus(process.ExitCode), CancellationToken.None)
            .ConfigureAwait(false);
        _ = stop;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task Pump(Stream from, byte which, Stream to, SemaphoreSlim writing)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var read = await from.ReadAsync(buffer.AsMemory(1, buffer.Length - 1)).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            buffer[0] = which;
            await writing.WaitAsync().ConfigureAwait(false);
            try
            {
                await MicroVMProtocol.WriteAsync(to, MessageKind.Output, buffer.AsMemory(0, read + 1), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static Task Refuse(Stream stream, string why) =>
        MicroVMProtocol.WriteAsync(stream, MessageKind.Error, Encoding.UTF8.GetBytes(why), CancellationToken.None);

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void MountOrFail(string source, string target, string type, ulong flags, string? data)
    {
        Directory.CreateDirectory(target);
        if (Libc.Mount(source, target, type, flags, data) != 0)
        {
            throw new IOException($"mounting {type} {source} on {target}: {Libc.LastError()}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void Link(string path, string target)
    {
        if (!Path.Exists(path))
        {
            File.CreateSymbolicLink(path, target);
        }
    }
}
