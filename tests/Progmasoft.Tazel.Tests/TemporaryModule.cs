// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Dsl.Build.Prelude;
using Progmasoft.Tazel.Dsl.Common;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Tests;

/// <summary>A directory that is removed after the test, with the files a test writes into it.</summary>
internal sealed class TemporaryModule : IDisposable
{
    public const string ModuleFile = """
        using Progmasoft.Tazel.Dsl.Module.Prelude;
        module = Module.Define(new() { Group = "com.example", Name = "sample", Version = "0.1.0" });
        """;

    public TemporaryModule()
    {
        Root = Path.Combine(Path.GetTempPath(), "tazel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>Writes <paramref name="text"/> to <paramref name="relative"/>, making its directories.</summary>
    public TemporaryModule Write(string relative, string text)
    {
        // A test writes inside its module only.
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{relative}' is outside the module", nameof(relative));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return this;
    }

    /// <summary>Loads the module as a debug build for this machine.</summary>
    public Task<Workspace> LoadAsync(string? directory = null) =>
        Workspace.LoadAsync(directory ?? Root, new BuildContext(BuildMode.Debug, Host.Platform), new Version(0, 1, 0));

    /// <summary>Runs tazel with <paramref name="arguments"/> in <paramref name="directory"/>, keeping what it prints.</summary>
    public async Task<(int Status, string Output)> TazelAsync(string directory, params string[] arguments)
    {
        using var output = new StringWriter();
        // Before the command: after tazel custom, every word belongs to the custom command.
        var status = await Program.RunAsync(["--color", "never", .. arguments], Path.Combine(Root, directory), output,
            Xunit.TestContext.Current.CancellationToken);
        return (status, output.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // On Windows a program a test started, or a plugin the test process loaded, may still hold a file; the
            // system's temporary directory keeps it.
        }
    }
}
