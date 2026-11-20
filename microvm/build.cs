// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

// Builds the guest image of Tazel's MicroVM for x86_64: the Linux kernel of kernel.org with kernel.config on top of
// tinyconfig, and an initramfs that holds the agent, published self-contained for linux-musl, and the four libraries
// of Alpine it runs on. Every download is checked against the SHA-256 written here; the same inputs give the same
// bytes.
//
//   dotnet run microvm/build.cs -- <work directory> <output directory>
//
// The output directory gets vmlinux, initramfs.cpio.gz, kernel.config (the whole configuration) and SHA256SUMS.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;

const string KernelVersion = "6.18.64";
const string KernelSha256 = "020d45510c539972e6f14c5c71566878d3b7ef56fb082b3e09134f2ebaade562";
const string Alpine = "https://dl-cdn.alpinelinux.org/alpine/v3.24/main/x86_64";
(string File, string Sha256)[] packages =
[
    ("musl-1.2.6-r3.apk", "e142255d0fa397f14318996bc10378093dcc069dc3dd1b6818c8f28a789c21da"),
    ("libgcc-15.2.0-r5.apk", "393dcd32629f06d7d85409c272d142d0c082772d10b87ef55ee82f47de3be637"),
    ("libstdc++-15.2.0-r5.apk", "14c987b556f5385a5db18376e788c75f37d85321b8dc1920d926ea7daac1d6f6"),
];

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("the image of the MicroVM is built on Linux");
    return 2;
}

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run microvm/build.cs -- <work directory> <output directory>");
    return 2;
}

var repository = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? "microvm", ".."));
var work = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(work);
Directory.CreateDirectory(output);
using var http = new HttpClient();

// The kernel.
var tarball = await Fetch($"https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-{KernelVersion}.tar.xz", KernelSha256,
    Path.Combine(work, $"linux-{KernelVersion}.tar.xz"));
var source = Path.Combine(work, $"linux-{KernelVersion}");
if (!File.Exists(Path.Combine(source, "Makefile")))
{
    Run(work, "tar", "-xf", tarball);
}

string[] reproducible = ["KBUILD_BUILD_TIMESTAMP=2026-01-01", "KBUILD_BUILD_USER=tazel", "KBUILD_BUILD_HOST=tazel"];
Run(source, "make", "tinyconfig");
Run(source, Path.Combine(source, "scripts", "kconfig", "merge_config.sh"), "-m", ".config", Path.Combine(repository, "microvm", "kernel.config"));
Run(source, "make", "olddefconfig");
RunWith(source, reproducible, ["make", $"-j{Environment.ProcessorCount}", "vmlinux"]);
File.Copy(Path.Combine(source, "vmlinux"), Path.Combine(output, "vmlinux"), overwrite: true);
File.Copy(Path.Combine(source, ".config"), Path.Combine(output, "kernel.config"), overwrite: true);

// The agent and the libraries it runs on.
var root = Path.Combine(work, "initramfs");
if (Directory.Exists(root))
{
    Directory.Delete(root, recursive: true);
}

var agent = Path.Combine(root, "tazel", "agent");
var libraries = Path.Combine(root, "tazel", "lib");
Directory.CreateDirectory(libraries);
Run(repository, "dotnet", "publish", "src/Progmasoft.Tazel.Guest", "-c", "Release", "-r", "linux-musl-x64", "--self-contained",
    "-p:PublishTrimmed=true", "-p:DebugType=none", "-o", agent);
var unpacked = Path.Combine(work, "alpine");
foreach (var (file, sha) in packages)
{
    var apk = await Fetch($"{Alpine}/{Uri.EscapeDataString(file)}", sha, Path.Combine(work, file));
    Directory.CreateDirectory(unpacked);
    // An apk is gzip members one after another; tar reads through them.
    Run(unpacked, "tar", "-xzf", apk, "--warning=no-unknown-keyword");
}

foreach (var (from, to) in new[]
{
    ("lib/libc.musl-x86_64.so.1", "libc.musl-x86_64.so.1"),
    ("usr/lib/libgcc_s.so.1", "libgcc_s.so.1"),
    ("usr/lib/libstdc++.so.6.0.34", "libstdc++.so.6"),
})
{
    File.Copy(Path.Combine(unpacked, from), Path.Combine(libraries, to), overwrite: true);
}

// The loader beside the agent: run as the program, it makes the agent's directory the one .NET looks in.
File.Copy(Path.Combine(unpacked, "lib", "ld-musl-x86_64.so.1"), Path.Combine(agent, "ld-musl-x86_64.so.1"), overwrite: true);
foreach (var directory in new[] { "dev", "proc", "sys", "tmp", "run", "usr", "etc", "root" })
{
    Directory.CreateDirectory(Path.Combine(root, directory));
}

var initramfs = Path.Combine(output, "initramfs.cpio.gz");
using (var file = File.Create(initramfs))
using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
{
    Cpio.Write(gzip, root);
}

var sums = new StringBuilder();
foreach (var name in new[] { "initramfs.cpio.gz", "kernel.config", "vmlinux" })
{
    sums.Append(CultureInvariant($"{Sha256(Path.Combine(output, name))}  {name}\n"));
}

await File.WriteAllTextAsync(Path.Combine(output, "SHA256SUMS"), sums.ToString());
Console.WriteLine($"wrote {output}");
Console.Write(sums);
return 0;

async Task<string> Fetch(string url, string sha, string path)
{
    if (!File.Exists(path) || Sha256(path) != sha)
    {
        Console.WriteLine($"fetching {url}");
        await using var download = await http.GetStreamAsync(url);
        await using var file = File.Create(path + ".partial");
        await download.CopyToAsync(file);
        file.Close();
        File.Move(path + ".partial", path, overwrite: true);
    }

    var actual = Sha256(path);
    return actual == sha ? path : throw new InvalidDataException($"{url} has the SHA-256 {actual}, not {sha}");
}

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static void Run(string directory, params string[] command) => RunWith(directory, [], command);

static void RunWith(string directory, string[] environment, string[] command)
{
    var start = new ProcessStartInfo(command[0]) { WorkingDirectory = directory, UseShellExecute = false };
    foreach (var argument in command.Skip(1))
    {
        start.ArgumentList.Add(argument);
    }

    foreach (var entry in environment)
    {
        var equals = entry.IndexOf('=');
        start.Environment[entry[..equals]] = entry[(equals + 1)..];
    }

    using var process = Process.Start(start)!;
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{string.Join(' ', command)} failed with {process.ExitCode}");
    }
}

static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);

/// <summary>
/// The "newc" cpio format the kernel unpacks: files sorted by path, owned by root, dated at the epoch, with
/// /dev/console as a device, so the agent has a console before it mounts /dev.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static class Cpio
{
    public static void Write(Stream stream, string root)
    {
        var inode = 1;
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var name in entries)
        {
            var path = Path.Combine(root, name);
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target)
            {
                Entry(stream, inode++, name, 0xA000 | 0x1FF, Encoding.UTF8.GetBytes(target), 0, 0);
            }
            else if (Directory.Exists(path))
            {
                Entry(stream, inode++, name, 0x4000 | 0x1ED, [], 0, 0);
                if (name == "dev")
                {
                    Entry(stream, inode++, "dev/console", 0x2000 | 0x180, [], 5, 1);
                }
            }
            else
            {
                var executable = (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
                Entry(stream, inode++, name, 0x8000 | (executable ? 0x1ED : 0x1A4), File.ReadAllBytes(path), 0, 0);
            }
        }

        Entry(stream, 0, "TRAILER!!!", 0, [], 0, 0);
    }

    private static void Entry(Stream stream, int inode, string name, int mode, byte[] data, int major, int minor)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var header = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"070701{inode:X8}{mode:X8}{0:X8}{0:X8}{1:X8}{0:X8}{data.Length:X8}{0:X8}{0:X8}{major:X8}{minor:X8}{nameBytes.Length:X8}{0:X8}");
        stream.Write(Encoding.ASCII.GetBytes(header));
        stream.Write(nameBytes);
        Pad(stream, 110 + nameBytes.Length);
        stream.Write(data);
        Pad(stream, data.Length);
    }

    private static void Pad(Stream stream, int length)
    {
        var padding = (4 - (length % 4)) % 4;
        stream.Write(new byte[padding]);
    }
}
