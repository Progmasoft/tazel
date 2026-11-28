// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.MicroVM;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Fetching the image and the monitor, from a server on this machine in place of the releases.</summary>
[SupportedOSPlatform("linux")]
public sealed class MicroVMDownloadTests
{
    private static readonly Version Tazel = new(0, 1, 0);
    private const string ImageName = "tazel-microvm-0.1.0-x86_64.tazel.tar.xz";

    private static MicroVMSources SourcesOf(FakePortal server, string monitorSha256) =>
        new($"{server.Address}/releases", $"{server.Address}/cloud-hypervisor-static", monitorSha256, "/dev/kvm");

    /// <summary>An image archive with its .sha256sums on the server, as a release has it.</summary>
    private static void PublishImage(TemporaryModule module, string served, bool goodSums = true)
    {
        module.Write("image/vmlinux", "kernel").Write("image/initramfs.cpio.gz", "initramfs").Write("image/SHA256SUMS", "sums\n");
        var directory = Path.Combine(served, "releases", "tazel-0.1.0");
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, ImageName);
        ModuleArchive.Write(Path.Combine(module.Root, "image"), "tazel-microvm-0.1.0-x86_64", archive,
            ["SHA256SUMS", "initramfs.cpio.gz", "vmlinux"]);
        File.WriteAllText(archive + ".sha256sums", goodSums ? Sha256Sums.SumsLine(archive) : $"{new string('0', 64)}  {ImageName}\n");
    }

    [Fact]
    public async Task TheImageOfTheReleaseIsFetchedCheckedAndKept()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the MicroVM runs on Linux");
        using var module = new TemporaryModule();
        var served = Path.Combine(module.Root, "served");
        PublishImage(module, served);
        var cache = Path.Combine(module.Root, "cache", "image");
        var said = new List<string>();
        using (var server = new FakePortal(served))
        {
            var image = await MicroVMFiles.FetchImageAsync(Tazel, cache, SourcesOf(server, ""), said.Add, TestContext.Current.CancellationToken);
            Assert.Equal(cache, image);
            Assert.Equal("kernel", File.ReadAllText(Path.Combine(image, "vmlinux")));
            Assert.Contains($"fetching the MicroVM image {ImageName}", said);
            Assert.False(Directory.Exists(cache + ".download"));
        }

        // The server is gone: the image kept is used.
        var nowhere = new MicroVMSources("http://127.0.0.1:9/releases", "http://127.0.0.1:9/monitor", "", "/dev/kvm");
        var again = await MicroVMFiles.FetchImageAsync(Tazel, cache, nowhere, said.Add, TestContext.Current.CancellationToken);
        Assert.Equal(cache, again);
    }

    [Fact]
    public async Task AnImageThatDoesNotMatchItsSumsOrIsNotThereIsRefused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the MicroVM runs on Linux");
        using var module = new TemporaryModule();
        var served = Path.Combine(module.Root, "served");
        PublishImage(module, served, goodSums: false);
        using var server = new FakePortal(served);
        var error = await Assert.ThrowsAsync<MicroVMException>(() => MicroVMFiles.FetchImageAsync(
            Tazel, Path.Combine(module.Root, "cache", "a"), SourcesOf(server, ""), _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("does not match its .sha256sums", error.Message, StringComparison.Ordinal);

        error = await Assert.ThrowsAsync<MicroVMException>(() => MicroVMFiles.FetchImageAsync(
            new Version(9, 9, 9), Path.Combine(module.Root, "cache", "b"), SourcesOf(server, ""), _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("answered 404", error.Message, StringComparison.Ordinal);
        Assert.Contains("TAZEL_MICROVM_IMAGE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMonitorIsFetchedOnceAndOnlyWithItsSha256()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the MicroVM runs on Linux");
        using var module = new TemporaryModule().Write("served/cloud-hypervisor-static", "#!/bin/sh\necho monitor\n");
        var served = Path.Combine(module.Root, "served");
        var sha = Sha256Sums.Sha256(Path.Combine(served, "cloud-hypervisor-static"));
        var directory = Path.Combine(module.Root, "cache", "monitor");
        using var server = new FakePortal(served);

        var error = await Assert.ThrowsAsync<MicroVMException>(() => MicroVMFiles.FetchMonitorAsync(
            directory, SourcesOf(server, new string('0', 64)), _ => { }, TestContext.Current.CancellationToken));
        Assert.Contains("has the SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "cloud-hypervisor.partial")));

        var said = new List<string>();
        var monitor = await MicroVMFiles.FetchMonitorAsync(directory, SourcesOf(server, sha), said.Add, TestContext.Current.CancellationToken);
        Assert.True((File.GetUnixFileMode(monitor) & UnixFileMode.UserExecute) != 0);
        Assert.Single(said);

        said.Clear();
        Assert.Equal(monitor, await MicroVMFiles.FetchMonitorAsync(directory, SourcesOf(server, sha), said.Add, TestContext.Current.CancellationToken));
        Assert.Empty(said);
    }

    [Fact]
    public async Task NoKvmIsSaid()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
            System.Runtime.InteropServices.Architecture.X64, "the MicroVM runs on x86_64 Linux");
        var sources = MicroVMSources.Published with { Kvm = "/no/such/kvm" };
        var error = await Assert.ThrowsAsync<MicroVMException>(() =>
            MicroVMFiles.FindAsync(Tazel, _ => { }, sources, TestContext.Current.CancellationToken));
        Assert.Contains("/no/such/kvm is not there", error.Message, StringComparison.Ordinal);
    }
}
