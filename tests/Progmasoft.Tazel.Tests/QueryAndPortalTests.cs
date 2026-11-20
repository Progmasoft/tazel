// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Cli;
using Progmasoft.Tazel.Driver;
using Progmasoft.Tazel.Label;
using Xunit;

namespace Progmasoft.Tazel.Tests;

public sealed class QueryTests
{
    private const string Build = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        var core = CC.StaticLibrary("core", new() { Sources = ["core.c"], Visibility = Visibility.Module });
        var util = CC.StaticLibrary("util", new() { Sources = ["util.c"], Depends = [core], Visibility = Visibility.Module });
        CC.Executable("app", new() { Sources = ["main.c"], Depends = [util] });
        CC.Test("util_test", new() { Sources = ["test.c"], Depends = [util] });
        ObjectiveC.StaticLibrary("mac", new());
        """;

    private static TemporaryModule Module() => new TemporaryModule()
        .Write("MODULE.csx", TemporaryModule.ModuleFile).Write("BUILD.csx", Build)
        .Write("core.c", "").Write("util.c", "").Write("main.c", "").Write("test.c", "");

    [Theory]
    [InlineData("deps(//:app)", "//:app //:core //:util")]
    [InlineData("deps(//:app, 1)", "//:app //:util")]
    [InlineData("rdeps(//..., //:core)", "//:app //:core //:util //:util_test")]
    [InlineData("rdeps(//..., //:core, 1)", "//:core //:util")]
    [InlineData("kind(Test, //...)", "//:util_test")]
    [InlineData("tests(//...)", "//:util_test")]
    [InlineData("//... - tests(//...) - //:mac", "//:app //:core //:util")]
    [InlineData("deps(//:app) ^ deps(//:util_test)", "//:core //:util")]
    [InlineData("(//:app union //:core) except //:app", "//:core")]
    [InlineData(":app", "//:app")]
    public async Task QueriesTheTargetGraph(string expression, string expected)
    {
        using var module = Module();
        var workspace = await module.LoadAsync();
        var found = new Query(workspace, PackageId.Root).Evaluate(expression);
        Assert.Equal(expected, string.Join(' ', found.Select(target => target.Label.ToString())));
    }

    [Theory]
    [InlineData("deps(//:app", "ends where it needs ')'")]
    [InlineData("//:nope", "//:nope is not a target")]
    [InlineData("//:app //:core", "has '//:core' where it should end")]
    [InlineData("deps(//:app, x)", "'x' is not a depth")]
    public async Task SaysWhatIsWrongWithAQuery(string expression, string message)
    {
        using var module = Module();
        var workspace = await module.LoadAsync();
        var error = Assert.Throws<Project.ProjectException>(() => new Query(workspace, PackageId.Root).Evaluate(expression));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CQueryLeavesOutWhatDoesNotBuildAndAQueryPrintsActions()
    {
        using var module = Module();
        var (status, output) = await module.TazelAsync(".", "cquery", "//...", "--platform", "linux-amd64", "--quiet");
        Assert.True(status == 0, output);
        Assert.Contains("//:app (linux-amd64-debug)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("//:mac", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "query", "//...", "--output", "label_kind", "--quiet");
        Assert.True(status == 0, output);
        Assert.Contains("ObjectiveC.StaticLibrary //:mac", output, StringComparison.Ordinal);

        Assert.SkipWhen(Host.Which("clang") is null && Host.Which("gcc") is null && Host.Which("zig") is null, "no C compiler");
        (status, output) = await module.TazelAsync(".", "aquery", "//:util", "--quiet");
        Assert.True(status == 0, output);
        Assert.Contains("action 'Compiling //:util util.c'", output, StringComparison.Ordinal);
        Assert.Contains("action 'Archiving //:util'", output, StringComparison.Ordinal);
        Assert.DoesNotContain("//:core core.c", output, StringComparison.Ordinal);
    }
}

public sealed class HelpTests
{
    [Fact]
    public void EveryCommandHasShortAndLongHelp()
    {
        foreach (var command in CommandLine.Commands)
        {
            var brief = Help.Text(command);
            var full = Help.Text(command, @long: true);
            Assert.Contains($"tazel help {command} --long", brief, StringComparison.Ordinal);
            Assert.True(full.Length > brief.Length, command);
        }

        Assert.Contains("Exit status", Help.Text(null, @long: true), StringComparison.Ordinal);
        Assert.DoesNotContain("Exit status", Help.Text(null), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "help" }, null, false)]
    [InlineData(new[] { "help", "--long" }, null, true)]
    [InlineData(new[] { "help", "query" }, "query", false)]
    [InlineData(new[] { "help", "query", "--long" }, "query", true)]
    public void ReadsHelpAndLong(string[] arguments, string? topic, bool @long)
    {
        var help = Assert.IsType<HelpCommand>(CommandLine.Parse(arguments));
        Assert.Equal(topic, help.Topic);
        Assert.Equal(@long, help.Long);
    }
}

public sealed class PortalCommandLineTests
{
    [Theory]
    [InlineData(new[] { "portal-modules", "search", "json" }, "search", "json", false)]
    [InlineData(new[] { "portal-modules", "search", "com.example:json", "-e" }, "search", "com.example:json", true)]
    [InlineData(new[] { "portal-modules", "search", "com.example:json", "--exact" }, "search", "com.example:json", true)]
    [InlineData(new[] { "portal-modules", "search", "--", "-json" }, "search", "-json", false)]
    [InlineData(new[] { "portal-modules", "search", "-e", "--", "com.example:json" }, "search", "com.example:json", true)]
    [InlineData(new[] { "portal-modules", "info", "json" }, "info", "json", false)]
    [InlineData(new[] { "portal-modules", "info", "--exact", "--", "com.example:json" }, "info", "com.example:json", true)]
    public void ReadsSearchAndInfo(string[] arguments, string action, string query, bool exact)
    {
        var command = Assert.IsType<PortalModulesCommand>(CommandLine.Parse(arguments));
        Assert.Equal(action == "search" ? PortalAction.Search : PortalAction.Info, command.Action);
        Assert.Equal(query, command.Query);
        Assert.Equal(exact, command.Exact);
    }

    [Theory]
    [InlineData(new[] { "portal-modules", "search", "json", "-e" }, "needs the group too: group:json")]
    [InlineData(new[] { "portal-modules", "info", "-e", "--", "json" }, "needs the group too")]
    [InlineData(new[] { "portal-modules", "serch", "json" }, "did you mean search?")]
    [InlineData(new[] { "portal-modules", "info", "a:b:c" }, "is not module or group:module")]
    [InlineData(new[] { "fetch", "x" }, "tazel fetch takes no 'x'")]
    [InlineData(new[] { "build", "--exact" }, "--exact is not an option of tazel build")]
    public void RefusesWhatIsWrong(string[] arguments, string message)
    {
        var error = Assert.Throws<CommandLineException>(() => CommandLine.Parse(arguments));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }
}

/// <summary>Tazel Portal Modules served from a directory by a server on this machine.</summary>
internal sealed class FakePortal : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<(string, string), List<Dictionary<string, object>>> _versions = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    public FakePortal(string directory)
    {
        Directory = directory;
        var port = FreePort();
        Address = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(Address + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Directory { get; }

    public string Address { get; }

    /// <summary>Puts a module with these versions and dependencies on the portal, its archive with its .sha256sums.</summary>
    public void Push(string group, string module, string version, string build, params (string Group, string Module, string Version)[] dependencies)
    {
        var source = Path.Combine(Directory, "sources", $"{module}-{version}");
        System.IO.Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "MODULE.csx"), $$"""
            using Progmasoft.Tazel.Dsl.Module.Prelude;
            module = Module.Define(new() { Group = "{{group}}", Name = "{{module}}", Version = "{{version}}" });
            """);
        File.WriteAllText(Path.Combine(source, "BUILD.csx"), build);
        var name = ModuleArchive.BaseName(group, module, version);
        var directory = Path.Combine(Directory, group, module, version);
        var archive = Path.Combine(directory, name + ModuleArchive.Extension);
        ModuleArchive.Write(source, name, archive, ["BUILD.csx", "MODULE.csx"]);
        File.WriteAllText(archive + ".sha256sums", Sha256Sums.SumsLine(archive));
        if (!_versions.TryGetValue((group, module), out var versions))
        {
            versions = [];
            _versions[(group, module)] = versions;
        }

        versions.Add(new Dictionary<string, object>
        {
            ["version"] = version,
            ["pushed"] = "2026-11-20T10:00:00Z",
            ["yanked"] = false,
            ["dependencies"] = dependencies.Select(d => new Dictionary<string, object>
            {
                ["group"] = d.Group, ["module"] = d.Module, ["version"] = d.Version, ["test"] = false,
            }).ToList(),
        });
        File.WriteAllText(Path.Combine(Directory, group, module, "index.json"), System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object> { ["group"] = group, ["module"] = module, ["versions"] = versions }));
    }

    public void Catalog(params (string Group, string Module, string Latest, string Description)[] modules) =>
        File.WriteAllText(Path.Combine(Directory, "index.json"),
            "{\"modules\":[" + string.Join(",", modules.Select(m =>
                $$"""{"group":"{{m.Group}}","module":"{{m.Module}}","latest":"{{m.Latest}}","description":"{{m.Description}}"}""")) + "]}");

    public void Dispose()
    {
        // A test stops the portal on purpose before its using does.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            // The portal serves its own directory and nothing above it.
            var root = Path.GetFullPath(Directory) + Path.DirectorySeparatorChar;
            var file = Path.GetFullPath(Path.Combine(root, context.Request.Url!.AbsolutePath.TrimStart('/')));
            if (file.StartsWith(root, StringComparison.Ordinal) && File.Exists(file))
            {
                var bytes = await File.ReadAllBytesAsync(file);
                context.Response.StatusCode = 200;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                context.Response.StatusCode = 404;
            }

            context.Response.Close();
        }
    }
}

[Collection("environment")]
public sealed class PortalTests
{
    private const string Library = """
        using Progmasoft.Tazel.Dsl.Build.Prelude;
        CC.StaticLibrary("lib", new() { Visibility = Visibility.Public });
        """;

    [Fact]
    public async Task ResolvesFetchesAndVendorsModulesOfThePortal()
    {
        using var served = new TemporaryModule();
        using var portal = new FakePortal(served.Root);
        portal.Push("com.example", "base", "1.0.0", Library);
        portal.Push("com.example", "base", "1.2.0", Library);
        portal.Push("com.example", "json", "2.0.0", Library, ("com.example", "base", "1.2.0"));
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + """

                dependencies = Dependencies.Define(
                    Module.From(new() { Group = "com.example", Name = "json", Version = "2.0.0" }),
                    Module.From(new() { Group = "com.example", Name = "base", Version = "1.0.0", Alias = "base" }));
                """)
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\nCC.StaticLibrary(\"app\", new() { Depends = [\"@json//:lib\", \"@base//:lib\"] });");
        using var address = new EnvironmentVariable("TAZEL_PORTAL_MODULES_URL", portal.Address);

        var workspace = await module.LoadAsync();
        // The highest version anything asks for: json asks for base 1.2.0.
        Assert.Equal(["com.example:base 1.2.0", "com.example:json 2.0.0"],
            workspace.PortalModules.Select(m => $"{m.Key} {m.Version}"));

        var (status, output) = await module.TazelAsync(".", "vendor");
        Assert.True(status == 0, output);
        Assert.True(File.Exists(Path.Combine(module.Root, "vendor", "com.example.json-2.0.0.tazel.tar.xz.sha256sums")));

        // With vendor/ and no state, nothing is downloaded: the portal may be gone.
        System.IO.Directory.Delete(Path.Combine(module.Root, ".tazel"), recursive: true);
        portal.Dispose();
        (status, output) = await module.TazelAsync(".", "fetch");
        Assert.True(status == 0, output);
        Assert.Contains("fetched 2 modules (2 of Tazel Portal Modules)", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("com.example", "..", "is not the name of a module")]
    [InlineData("../etc", "json", "is not a group")]
    [InlineData("Com.Example", "json", "is not a group")]
    public async Task ANameThatIsNotOneIsRefusedBeforeItBecomesAPath(string group, string name, string message)
    {
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + $$"""

            dependencies = Dependencies.Define(Module.From(new() { Group = "{{group}}", Name = "{{name}}", Version = "1.0.0" }));
            """);
        using var address = new EnvironmentVariable("TAZEL_PORTAL_MODULES_URL", "http://127.0.0.1:9");
        var error = await Assert.ThrowsAsync<Project.ProjectException>(() => module.LoadAsync());
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnIndexThatNamesAPathIsRefused()
    {
        using var served = new TemporaryModule();
        using var portal = new FakePortal(served.Root);
        portal.Push("com.example", "json", "1.0.0", Library, ("com.example", "..", "1.0.0"));
        using var module = new TemporaryModule();
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + """

            dependencies = Dependencies.Define(Module.From(new() { Group = "com.example", Name = "json", Version = "1.0.0" }));
            """);
        using var address = new EnvironmentVariable("TAZEL_PORTAL_MODULES_URL", portal.Address);
        var error = await Assert.ThrowsAsync<Project.ProjectException>(() => module.LoadAsync());
        Assert.Contains("the index of com.example:json: '..' is not the name of a module", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchesAndShowsModules()
    {
        using var served = new TemporaryModule();
        using var portal = new FakePortal(served.Root);
        portal.Push("com.example", "json", "1.0.0", Library);
        portal.Push("com.example", "json", "1.1.0", Library);
        portal.Push("org.other", "json", "0.1.0", Library);
        portal.Catalog(("com.example", "json", "1.1.0", "JSON for C"), ("org.other", "json", "0.1.0", "Another"),
            ("com.example", "yaml", "0.2.0", "YAML"));
        using var module = new TemporaryModule();
        using var address = new EnvironmentVariable("TAZEL_PORTAL_MODULES_URL", portal.Address);

        var (status, output) = await module.TazelAsync(".", "portal-modules", "search", "json");
        Assert.True(status == 0, output);
        Assert.Contains("com.example:json", output, StringComparison.Ordinal);
        Assert.Contains("org.other:json", output, StringComparison.Ordinal);
        Assert.DoesNotContain("yaml", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "portal-modules", "search", "com.example:json", "-e");
        Assert.True(status == 0, output);
        Assert.DoesNotContain("org.other", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "portal-modules", "info", "json");
        Assert.Equal(2, status);
        Assert.Contains("json is a module of com.example, org.other; write the group", output, StringComparison.Ordinal);

        (status, output) = await module.TazelAsync(".", "portal-modules", "info", "com.example:json", "--exact");
        Assert.True(status == 0, output);
        Assert.Contains("latest 1.1.0", output, StringComparison.Ordinal);
        Assert.Contains("1.0.0", output, StringComparison.Ordinal);
    }
}

/// <summary>Sets an environment variable for one test and puts it back.</summary>
internal sealed class EnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _old;

    public EnvironmentVariable(string name, string value)
    {
        _name = name;
        _old = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);
}

[CollectionDefinition("environment", DisableParallelization = true)]
public sealed class EnvironmentCollection;

public sealed class CoverageTests
{
    [Fact]
    public async Task WritesTheCoverageOfTheTests()
    {
        Assert.SkipWhen(Host.Which("clang") is null || Host.Which("llvm-profdata") is null || Host.Which("llvm-cov") is null,
            "clang, llvm-profdata and llvm-cov are not all on PATH");
        using var module = new TemporaryModule()
            .Write("MODULE.csx", TemporaryModule.ModuleFile + """

                toolchains = Toolchains.Define("//toolchains:clang");
                """)
            .Write("toolchains/BUILD.csx", $$"""
                using Progmasoft.Tazel.Dsl.Build.Toolchain.Prelude;
                CC.Toolchain("clang", new()
                {
                    ExecutionPlatform = Platform.{{Host.Platform}},
                    TargetPlatform = Platform.{{Host.Platform}},
                    Tools = new() { CCompiler = "clang", CppCompiler = "clang++", Archiver = "{{(Host.Which("llvm-ar") is null ? "ar" : "llvm-ar")}}" },
                });
                """)
            .Write("BUILD.csx", """
                using Progmasoft.Tazel.Dsl.Build.Prelude;
                CC.Test("half_test", new() { Sources = ["half.c"] });
                """)
            .Write("half.c", "int half(int x) { if (x < 0) { return -1; } return x / 2; }\nint main(void) { return half(4) == 2 ? 0 : 1; }\n");
        var (status, output) = await module.TazelAsync(".", "coverage", "//...");
        Assert.True(status == 0, output);
        var lcov = await File.ReadAllTextAsync(Path.Combine(module.Root, ".tazel", "coverage", "coverage.lcov"),
            TestContext.Current.CancellationToken);
        Assert.Contains("SF:", lcov, StringComparison.Ordinal);
        Assert.Contains("half.c", lcov, StringComparison.Ordinal);
        Assert.True(System.IO.Directory.Exists(System.IO.Directory.GetDirectories(Path.Combine(module.Root, ".tazel", "out"))
            .Single(d => d.EndsWith("-coverage", StringComparison.Ordinal))));
    }
}
