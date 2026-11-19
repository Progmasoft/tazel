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
        var path = Path.Combine(Root, relative);
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
        var status = await Program.RunAsync([.. arguments, "--color", "never"], Path.Combine(Root, directory), output,
            Xunit.TestContext.Current.CancellationToken);
        return (status, output.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A program a test started may still hold a file on Windows; the system's temporary directory keeps it.
        }
    }
}
