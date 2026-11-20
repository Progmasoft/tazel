// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Driver;

namespace Progmasoft.Tazel.MicroVM;

/// <summary>What a MicroVM is started from: the guest kernel and initramfs, the monitor, and virtiofsd.</summary>
/// <param name="Kernel">The guest kernel, an ELF vmlinux that boots through PVH.</param>
/// <param name="Initramfs">The initramfs with the agent.</param>
/// <param name="Monitor">Cloud Hypervisor.</param>
/// <param name="Virtiofsd">The virtio-fs daemon of the host.</param>
internal sealed record MicroVMFiles(string Kernel, string Initramfs, string Monitor, string Virtiofsd)
{
    /// <summary>The Cloud Hypervisor release Tazel runs, and the SHA-256 of its static x86_64 program.</summary>
    public const string MonitorVersion = "v55.0";

    private const string MonitorSha256 = "ce7133bf535ed5a4e2a63ea1f26025445a5b1241f73ddfb81ff2fbcb2e803a59";

    /// <summary>
    /// Finds every file, fetching what is missing into the cache of the user: the image from
    /// <c>TAZEL_MICROVM_IMAGE</c> (a directory with <c>SHA256SUMS</c>, as <c>microvm/build.cs</c> writes it) or the
    /// release of this Tazel, the monitor from <c>TAZEL_CLOUD_HYPERVISOR</c> or its release, and virtiofsd from the
    /// host. Each file is checked against its SHA-256 before it is used.
    /// </summary>
    public static async Task<MicroVMFiles> FindAsync(Version tazel, Action<string> status, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(tazel);
        ArgumentNullException.ThrowIfNull(status);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new MicroVMException(
                $"this Tazel starts its MicroVM on x86_64 Linux with KVM, and this machine is {RuntimeInformation.RuntimeIdentifier}");
        }

        if (!File.Exists("/dev/kvm"))
        {
            throw new MicroVMException("/dev/kvm is not there; the MicroVM needs KVM, so turn on virtualization in the firmware");
        }

        var cache = Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "tazel");
        var image = Environment.GetEnvironmentVariable("TAZEL_MICROVM_IMAGE") is { Length: > 0 } given
            ? given
            : await FetchImageAsync(tazel, Path.Combine(cache, "microvm", tazel.ToString(3)), status, cancel).ConfigureAwait(false);
        CheckImage(image);
        var monitor = Environment.GetEnvironmentVariable("TAZEL_CLOUD_HYPERVISOR") is { Length: > 0 } chosen
            ? chosen
            : await FetchMonitorAsync(Path.Combine(cache, "cloud-hypervisor", MonitorVersion), status, cancel).ConfigureAwait(false);
        var virtiofsd = Environment.GetEnvironmentVariable("TAZEL_VIRTIOFSD") is { Length: > 0 } daemon
            ? daemon
            : new[] { "/usr/libexec/virtiofsd", "/usr/lib/virtiofsd", "/usr/lib/qemu/virtiofsd" }.FirstOrDefault(File.Exists)
              ?? Host.Which("virtiofsd")
              ?? throw new MicroVMException("virtiofsd is not installed; the MicroVM shares the module through it (the package is virtiofsd)");
        return new MicroVMFiles(Path.Combine(image, "vmlinux"), Path.Combine(image, "initramfs.cpio.gz"), monitor, virtiofsd);
    }

    /// <summary>Checks that every file of an image matches its <c>SHA256SUMS</c>.</summary>
    public static void CheckImage(string directory)
    {
        var sums = Path.Combine(directory, "SHA256SUMS");
        if (!File.Exists(sums))
        {
            throw new MicroVMException($"{directory} has no SHA256SUMS, so its image cannot be checked");
        }

        var text = File.ReadAllText(sums);
        foreach (var name in new[] { "vmlinux", "initramfs.cpio.gz" })
        {
            var expected = Sha256Sums.SumFor(text, name, sums)
                ?? throw new MicroVMException($"{sums} does not name {name}");
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || !string.Equals(Sha256Sums.Sha256(path), expected, StringComparison.Ordinal))
            {
                throw new MicroVMException($"{path} does not match its SHA256SUMS; build or fetch the image again");
            }
        }
    }

    private static async Task<string> FetchImageAsync(Version tazel, string directory, Action<string> status, CancellationToken cancel)
    {
        if (File.Exists(Path.Combine(directory, "SHA256SUMS")))
        {
            return directory;
        }

        var version = tazel.ToString(3);
        var name = $"tazel-microvm-{version}-x86_64.tazel.tar.xz";
        var url = $"https://github.com/Progmasoft/tazel/releases/download/tazel-{version}/{name}";
        status($"fetching the MicroVM image {name}");
        var downloads = directory + ".download";
        Directory.CreateDirectory(downloads);
        var archive = Path.Combine(downloads, name);
        var sums = await DownloadAsync(url + ".sha256sums", archive + ".sha256sums", cancel).ConfigureAwait(false);
        await DownloadAsync(url, archive, cancel).ConfigureAwait(false);
        var expected = Sha256Sums.SumFor(await File.ReadAllTextAsync(sums, cancel).ConfigureAwait(false), name, url + ".sha256sums");
        if (expected is null || !string.Equals(Sha256Sums.Sha256(archive), expected, StringComparison.Ordinal))
        {
            throw new MicroVMException($"{url} does not match its .sha256sums");
        }

        var root = ModuleArchive.Unpack(archive, directory + ".partial", "vmlinux");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.Move(root, directory);
        Directory.Delete(downloads, recursive: true);
        return directory;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<string> FetchMonitorAsync(string directory, Action<string> status, CancellationToken cancel)
    {
        var path = Path.Combine(directory, "cloud-hypervisor");
        if (File.Exists(path) && Sha256Sums.Sha256(path) == MonitorSha256)
        {
            return path;
        }

        status($"fetching Cloud Hypervisor {MonitorVersion}");
        Directory.CreateDirectory(directory);
        var url = $"https://github.com/cloud-hypervisor/cloud-hypervisor/releases/download/{MonitorVersion}/cloud-hypervisor-static";
        await DownloadAsync(url, path + ".partial", cancel).ConfigureAwait(false);
        var actual = Sha256Sums.Sha256(path + ".partial");
        if (actual != MonitorSha256)
        {
            File.Delete(path + ".partial");
            throw new MicroVMException($"{url} has the SHA-256 {actual}, not that of Cloud Hypervisor {MonitorVersion}");
        }

        File.SetUnixFileMode(path + ".partial", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(path + ".partial", path, overwrite: true);
        return path;
    }

    private static async Task<string> DownloadAsync(string url, string path, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicroVMException(
                $"{url} answered {(int)response.StatusCode}; build the image with dotnet run microvm/build.cs and set TAZEL_MICROVM_IMAGE");
        }

        var file = File.Create(path);
        await using (file.ConfigureAwait(false))
        {
            await response.Content.CopyToAsync(file, cancel).ConfigureAwait(false);
        }

        return path;
    }
}

/// <summary>The MicroVM could not be started or stopped answering.</summary>
public sealed class MicroVMException : Exception
{
    /// <summary>What went wrong.</summary>
    public MicroVMException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public MicroVMException()
    {
    }

    /// <summary>What went wrong, and why.</summary>
    public MicroVMException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
