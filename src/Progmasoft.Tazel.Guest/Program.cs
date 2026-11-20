// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;

namespace Progmasoft.Tazel.Guest;

/// <summary>The entry of the agent; as the first process of the guest, it never returns.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class Program
{
    public static int Main()
    {
        Libc.Resolve();
        try
        {
            // The shares are on the kernel command line, so /proc comes first.
            Directory.CreateDirectory("/proc");
            if (Libc.Mount("proc", "/proc", "proc", Libc.MsNosuid | Libc.MsNodev | Libc.MsNoexec, null) != 0)
            {
                throw new IOException($"mounting /proc: {Libc.LastError()}");
            }

            var shares = Share.Parse(File.ReadAllText("/proc/cmdline"));
            var agent = new Agent(shares);
            agent.Boot();
            Console.Error.WriteLine($"tazel-guest {Agent.Version}: ready on vsock port {MicroVM.MicroVMProtocol.Port}");
            agent.Serve();
        }
        catch (Exception error) when (error is IOException or FormatException or UnauthorizedAccessException)
        {
            // The console is the monitor's log; Tazel shows its end when the machine does not answer.
            Console.Error.WriteLine($"tazel-guest: {error.Message}");
        }

        Agent.PowerOff();
        return 0;
    }
}
