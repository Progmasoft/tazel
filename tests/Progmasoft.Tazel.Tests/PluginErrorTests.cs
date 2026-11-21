// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Progmasoft.Tazel.Archive;
using Progmasoft.Tazel.Project;
using Xunit;

namespace Progmasoft.Tazel.Tests;

/// <summary>Plugins that are wrong in each way the loader checks, built here as small assemblies.</summary>
public sealed class PluginErrorTests
{
    private const string Manifest = """{"group":"com.example","name":"x","version":"1.0.0","pluginApi":"0.1.0","entryAssembly":"X.dll"}""";

    private const string Works = """
        using Progmasoft.Tazel.PluginApi;
        public sealed class Plugin : ITazelPlugin
        {
            public void Apply(IPluginContext context)
            {
                context.Import("System.Text");
                context.Status($"{context.Group}.{context.Name} {context.Version} for {context.Platform} {context.Mode} in {System.IO.Path.GetFileName(context.ModuleRoot)}");
            }
        }
        """;

    /// <summary>An assembly of <paramref name="source"/>, compiled against the plugin API.</summary>
    private static byte[] Compile(string name, string source, params MetadataReference[] more)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(file => file.Length > 0)
            .Select(file => (MetadataReference)MetadataReference.CreateFromFile(file))
            .Concat([MetadataReference.CreateFromFile(typeof(PluginApi.ITazelPlugin).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Dsl.Common.Platform).Assembly.Location)])
            .Concat(more);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return stream.ToArray();
    }

    /// <summary>A module whose plugin archive holds <paramref name="manifest"/> and <paramref name="files"/>.</summary>
    private static TemporaryModule ModuleWith(string manifest, IReadOnlyDictionary<string, byte[]> files, string plugins = "LocalPlugin.From(\"plugins/x.tazel.tar.xz\")")
    {
        var module = new TemporaryModule().Write("plugin/tazel-plugin.json", manifest);
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(module.Root, "plugin", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        ModuleArchive.Write(Path.Combine(module.Root, "plugin"), "com.example.x-1.0.0", Path.Combine(module.Root, "plugins", "x.tazel.tar.xz"),
            ["tazel-plugin.json", .. files.Keys.Order(StringComparer.Ordinal)]);
        module.Write("MODULE.csx", TemporaryModule.ModuleFile + $"\nplugins = Plugins.Define({plugins});")
            .Write("BUILD.csx", "using Progmasoft.Tazel.Dsl.Build.Prelude;\n");
        return module;
    }

    private static async Task<string> RefusedAsync(TemporaryModule module) =>
        (await Assert.ThrowsAsync<ProjectException>(() => module.LoadAsync())).Message;

    [Theory]
    [InlineData("""{"group":"com.example","name":"x","version":"1.0.0","pluginApi":"0.1.0","entryAssembly":"lib/X.dll"}""", "is not a .dll at the top")]
    [InlineData("""{"group":"com.example","name":"x y","version":"1.0.0","pluginApi":"0.1.0","entryAssembly":"X.dll"}""", "is not a name")]
    [InlineData("""{"group":"com.example","name":"x","version":"1.0.0","pluginApi":"one","entryAssembly":"X.dll"}""", "is not the version of a plugin API")]
    [InlineData("null", "tazel-plugin.json is empty")]
    public async Task AManifestThatIsWrongIsRefused(string manifest, string said)
    {
        using var module = ModuleWith(manifest, new Dictionary<string, byte[]>());
        Assert.Contains(said, await RefusedAsync(module), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEntryAssemblyThatIsNotDotNetIsRefused()
    {
        using var module = ModuleWith(Manifest, new Dictionary<string, byte[]> { ["X.dll"] = "MZ not really"u8.ToArray() });
        Assert.Contains("X.dll is not a .NET assembly", await RefusedAsync(module), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""
        using Progmasoft.Tazel.PluginApi;
        public sealed class One : ITazelPlugin { public void Apply(IPluginContext context) { } }
        public sealed class Two : ITazelPlugin { public void Apply(IPluginContext context) { } }
        """, "has 2 public classes that implement ITazelPlugin")]
    [InlineData("""
        public sealed class NotAPlugin { }
        """, "has 0 public classes that implement ITazelPlugin")]
    [InlineData("""
        using Progmasoft.Tazel.PluginApi;
        public sealed class Plugin : ITazelPlugin { public Plugin(int x) { } public void Apply(IPluginContext context) { } }
        """, "has no public constructor without parameters")]
    [InlineData("""
        using Progmasoft.Tazel.PluginApi;
        public sealed class Plugin : ITazelPlugin { public Plugin() { throw new System.InvalidOperationException("broken"); } public void Apply(IPluginContext context) { } }
        """, "failed when it was applied: InvalidOperationException: broken")]
    [InlineData("""
        using Progmasoft.Tazel.PluginApi;
        public sealed class Plugin : ITazelPlugin { public void Apply(IPluginContext context) => throw new System.IO.IOException("disk"); }
        """, "failed when it was applied: IOException: disk")]
    [InlineData("""
        using Progmasoft.Tazel.PluginApi;
        public sealed class Plugin : ITazelPlugin { public void Apply(IPluginContext context) => throw new PluginException("it says no"); }
        """, "the plugin com.example.x: it says no")]
    public async Task APluginThatCannotBeAppliedSaysWhy(string source, string said)
    {
        using var module = ModuleWith(Manifest, new Dictionary<string, byte[]> { ["X.dll"] = Compile("X", source) });
        Assert.Contains(said, await RefusedAsync(module), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginWhoseDependencyIsMissingCannotBeLoaded()
    {
        var dependency = Compile("Dep", "public class Base { }");
        var entry = Compile("X", """
            using Progmasoft.Tazel.PluginApi;
            public sealed class Plugin : Base, ITazelPlugin { public void Apply(IPluginContext context) { } }
            """, MetadataReference.CreateFromImage(dependency));
        using var module = ModuleWith(Manifest, new Dictionary<string, byte[]> { ["X.dll"] = entry });
        Assert.Contains("the plugin com.example.x cannot be loaded", await RefusedAsync(module), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginGetsItsContextAndIsUnpackedOnce()
    {
        using var module = ModuleWith(Manifest, new Dictionary<string, byte[]> { ["X.dll"] = Compile("X", Works) });
        var (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        Assert.Contains($"com.example.x 1.0.0 for {Driver.Host.Platform} Debug in {Path.GetFileName(module.Root)}", output,
            StringComparison.Ordinal);

        // Unpacked once: the second build takes the directory; a directory without its marker is unpacked again.
        (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
        var unpacked = Directory.GetDirectories(Path.Combine(module.Root, ".tazel", "plugins")).Single();
        foreach (var marker in Directory.GetFiles(unpacked, ".unpacked*"))
        {
            File.Delete(marker);
        }

        (status, output) = await module.TazelAsync(".", "build", "//...");
        Assert.True(status == 0, output);
    }

    [Theory]
    [InlineData("LocalPlugin.From(\"plugins/x.tazel.tar.xz\"), LocalPlugin.From(\"plugins/x.tazel.tar.xz\")", "is named twice")]
    [InlineData("LocalPlugin.From(\"plugins/x.zip\")", "is not a .tazel.tar.xz of this module")]
    [InlineData("LocalPlugin.From(\"/plugins/x.tazel.tar.xz\")", "is not a .tazel.tar.xz of this module")]
    [InlineData("LocalPlugin.From(\"plugins/missing.tazel.tar.xz\")", "is not a file of this module")]
    public async Task APluginMustBeAnArchiveOfTheModuleNamedOnce(string plugins, string said)
    {
        using var module = ModuleWith(Manifest, new Dictionary<string, byte[]> { ["X.dll"] = Compile("X", Works) }, plugins);
        Assert.Contains(said, await RefusedAsync(module), StringComparison.Ordinal);
    }
}
