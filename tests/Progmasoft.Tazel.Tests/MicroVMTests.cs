// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

extern alias guest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Agent = guest::Progmasoft.Tazel.Guest.Agent;
using Share = guest::Progmasoft.Tazel.Guest.Share;
using Progmasoft.Tazel.MicroVM;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class MicroVMProtocolTests
{
    [Fact]
    public async Task ARunRequestCrossesTheConnectionUnchanged()
    {
        var request = new RunRequest(["sh", "-c", "echo ü \"x\""],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin", ["EMPTY"] = "" }, "/work/my module");
        using var stream = new MemoryStream();
        await MicroVMProtocol.WriteAsync(stream, MessageKind.Run, MicroVMProtocol.Encode(request), TestContext.Current.CancellationToken);
        stream.Position = 0;
        var message = await MicroVMProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);
        Assert.Equal(MessageKind.Run, message!.Value.Kind);
        var read = MicroVMProtocol.DecodeRun(message.Value.Payload);
        Assert.Equal(request.Arguments, read.Arguments);
        Assert.Equal(request.Environment.OrderBy(e => e.Key), read.Environment.OrderBy(e => e.Key));
        Assert.Equal(request.Directory, read.Directory);
        Assert.Null(await MicroVMProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AStatusIsFourBytes()
    {
        Assert.Equal(-1, MicroVMProtocol.DecodeStatus(MicroVMProtocol.EncodeStatus(-1)));
        Assert.Throws<InvalidDataException>(() => MicroVMProtocol.DecodeStatus([1, 2]));
    }

    [Theory]
    [InlineData(new byte[] { 1, 255, 255, 255, 127 })]
    [InlineData(new byte[] { 1, 10, 0, 0, 0, 1, 2 })]
    public async Task ABrokenStreamIsRefused(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => MicroVMProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
        Assert.True(error is InvalidDataException or EndOfStreamException, error.GetType().Name);
    }

    [Fact]
    public void ARunMessageWithBytesLeftOverIsRefused() =>
        Assert.Throws<InvalidDataException>(() => MicroVMProtocol.DecodeRun(
            [.. MicroVMProtocol.Encode(new RunRequest(["x"], new Dictionary<string, string>(), "/")), 0]));

    [Theory]
    [InlineData("/home/someone/my module")]
    [InlineData("/ü/ö")]
    [InlineData("/")]
    public void SharesNameTheirPathOnTheKernelCommandLine(string path)
    {
        var shares = Share.Parse($"console=hvc0 tazel.share=s0,rw,{MicroVMProtocol.EncodePath(path)} quiet tazel.share=s1,ro,{MicroVMProtocol.EncodePath("/usr")}");
        Assert.Equal([new Share("s0", path, false), new Share("s1", "/usr", true)], shares);
    }

    [Theory]
    [InlineData("tazel.share=s0,rx,Lw")]
    [InlineData("tazel.share=s0,rw")]
    [InlineData("tazel.share=s0,rw,cmVsYXRpdmU")]
    [InlineData("tazel.share=s0,rw,Ly4uL2V0Yw")]
    public void AShareThatIsNotRightStopsTheAgent(string word) =>
        Assert.Throws<FormatException>(() => Share.Parse(word));

    [Fact]
    public void TheAgentRunsOnlyInsideWhatIsSharedForWriting()
    {
        var agent = new Agent([new Share("s0", "/work/module", false), new Share("s1", "/usr", true)]);
        Assert.True(agent.Inside("/work/module"));
        Assert.True(agent.Inside("/work/module/sub"));
        Assert.False(agent.Inside("/work/module-other"));
        Assert.False(agent.Inside("/work/module/../other"));
        Assert.False(agent.Inside("/usr/bin"));
        Assert.False(agent.Inside("relative/path"));
        Assert.False(agent.Inside("/../work/module"));
        Assert.True(agent.Inside("/work/./module/x/.."));
    }
}

public sealed class MicroVMFilesTests
{
    [Fact]
    public void AnImageThatDoesNotMatchItsSumsIsRefused()
    {
        using var image = new TemporaryModule().Write("vmlinux", "kernel").Write("initramfs.cpio.gz", "initramfs");
        Assert.Contains("has no SHA256SUMS", Assert.Throws<MicroVMException>(() => MicroVMFiles.CheckImage(image.Root)).Message,
            StringComparison.Ordinal);
        image.Write("SHA256SUMS",
            $"{Archive.Sha256Sums.Sha256(Path.Combine(image.Root, "vmlinux"))}  vmlinux\n{new string('0', 64)}  initramfs.cpio.gz\n");
        Assert.Contains("initramfs.cpio.gz does not match", Assert.Throws<MicroVMException>(() => MicroVMFiles.CheckImage(image.Root)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANetworkIsRefusedBeforeAnythingStarts()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("tazel_options.txt", "sandboxing-with-microvm: true;\nmicrovm-network: true;\n")
            .Write("tazel_custom.txt", "hello: (\"echo\" \"hi\");\n");
        var (status, output) = await module.TazelAsync(".", "custom", "hello");
        Assert.Equal(1, status);
        Assert.Contains("error[E0504]", output, StringComparison.Ordinal);
        Assert.Contains("has no network, so nothing ran", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildsAreStillStoppedByASandbox()
    {
        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("tazel_options.txt", "sandboxing-with-microvm: true;\n");
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.Equal(1, status);
        Assert.Contains("runs tazel custom inside its MicroVM, and not builds, tests or plugins", output, StringComparison.Ordinal);
    }
}

/// <summary>
/// The MicroVM itself, booted: on x86_64 Linux with KVM and an image named by TAZEL_MICROVM_IMAGE, as CI has them.
/// </summary>
public sealed class MicroVMBootTests
{
    private static string? Unavailable()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/dev/kvm"))
        {
            return "no KVM here";
        }

        return Environment.GetEnvironmentVariable("TAZEL_MICROVM_IMAGE") is { Length: > 0 } ? null : "TAZEL_MICROVM_IMAGE is not set";
    }

    [Fact]
    public async Task ACustomCommandRunsInsideAndSeesOnlyTheModule()
    {
        // CI sets TAZEL_REQUIRE_MICROVM, so a MicroVM that cannot start there fails instead of being skipped.
        if (Unavailable() is { } why)
        {
            Assert.False(Environment.GetEnvironmentVariable("TAZEL_REQUIRE_MICROVM") == "1", why);
            Assert.Skip(why);
        }

        using var module = new TemporaryModule().Write("MODULE.csx", TemporaryModule.ModuleFile)
            .Write("tazel_options.txt", "sandboxing-with-microvm: true;\nmicrovm-memory: 768;\nmicrovm-cpus: 1;\n")
            .Write("tazel_custom.txt", """
                inside: s("echo kernel $(uname -r); echo home=$HOME; test -d /home && ls /home | wc -l || echo no-home") ("touch" "made-inside");
                fail: s("exit 7") ("touch" "never");
                """);
        var (status, output) = await module.TazelAsync(".", "custom", "inside");
        Assert.True(status == 0, output);
        Assert.True(File.Exists(Path.Combine(module.Root, "made-inside")), output);

        (status, output) = await module.TazelAsync(".", "custom", "fail");
        Assert.Equal(7, status);
        Assert.False(File.Exists(Path.Combine(module.Root, "never")));
    }
}
