// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.Project;

/// <summary>
/// The compiled project files of a module, in <c>.tazel/scripts/</c>: a file is compiled once and its assembly is
/// loaded on later runs, so a run whose project files did not change does not start the compiler.
/// </summary>
/// <remarks>
/// The name of an assembly is the SHA-256 of everything its code depends on: the format of this cache, the build of
/// Tazel (the options a file is compiled with are part of it), the .NET runtime, the type of the globals, the path the
/// file is shown as, the plugins' assemblies, the imports and the text. Nothing else changes what a file compiles to.
/// </remarks>
internal static class ScriptCache
{
    /// <summary>The directory of the compiled files, below the state directory of the main module.</summary>
    public const string Directory = "scripts";

    // Changes when what is cached or how it is named changes.
    private const string Format = "tazel-script/1";

    /// <summary>The name of the compiled form of <paramref name="code"/>.</summary>
    public static string Key(string code, string shownPath, Type globals, ScriptEnvironment environment)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Span<byte> length = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        Add(Format);
        foreach (var assembly in new[] { typeof(ScriptCache).Assembly, typeof(Dsl.DslException).Assembly, typeof(object).Assembly })
        {
            Add(assembly.ManifestModule.ModuleVersionId.ToString("N"));
        }

        Add(Environment.Version.ToString());
        Add(globals.AssemblyQualifiedName ?? globals.FullName ?? globals.Name);
        Add(shownPath);
        foreach (var assembly in environment.Plugins)
        {
            Add(assembly.GetName().FullName);
            Add(assembly.ManifestModule.ModuleVersionId.ToString("N"));
        }

        foreach (var import in environment.Imports)
        {
            Add(import);
        }

        Add(code);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    // A file of the cache directory: a key is the hexadecimal SHA-256 that Key returns, so its name is never a path.
    private static string FileOf(string directory, string key, string suffix)
    {
        if (key.Length != 64 || !key.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"'{key}' is not a key of the script cache", nameof(key));
        }

        return Path.Combine(directory, Path.GetFileName(key + suffix));
    }

    /// <summary>Whether <paramref name="directory"/> keeps the compiled file named <paramref name="key"/>.</summary>
    public static bool IsKept(string directory, string key) => File.Exists(FileOf(directory, key, ".dll"));

    /// <summary>The compiled file named <paramref name="key"/> in <paramref name="directory"/>, or null.</summary>
    public static byte[]? TryRead(string directory, string key)
    {
        try
        {
            var file = FileOf(directory, key, ".dll");
            return File.Exists(file) && new FileInfo(file).LinkTarget is null ? File.ReadAllBytes(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps <paramref name="image"/> as <paramref name="key"/>: written beside it and moved into place, so a reader
    /// sees the whole file or none. A failure leaves the file uncached; the run goes on.
    /// </summary>
    public static void TryWrite(string directory, string key, byte[] image)
    {
        var partial = FileOf(directory, key, $".{Guid.NewGuid():N}.partial");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllBytes(partial, image);
            File.Move(partial, FileOf(directory, key, ".dll"), overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do: a leftover .partial file is never read.
            }
        }
    }
}

/// <summary>What a project file is compiled with beyond the DSL: the plugins' assemblies and imports.</summary>
internal sealed class ScriptEnvironment
{
    private readonly Lazy<ScriptLoadContext> context;
    private readonly ConcurrentDictionary<string, LoadedScript> loaded = new(StringComparer.Ordinal);

    private ScriptEnvironment(IReadOnlyList<Assembly> plugins, IReadOnlyList<string> imports)
    {
        Plugins = plugins;
        Imports = imports;
        context = new Lazy<ScriptLoadContext>(() => new ScriptLoadContext(plugins));
    }

    /// <summary>The DSL alone.</summary>
    public static ScriptEnvironment Default { get; } = new([], []);

    /// <summary>The assemblies of the plugins, which project files may use.</summary>
    public IReadOnlyList<Assembly> Plugins { get; }

    /// <summary>The namespaces of the plugins that project files see without a <c>using</c>.</summary>
    public IReadOnlyList<string> Imports { get; }

    /// <summary>The DSL with the assemblies and imports of plugins.</summary>
    public static ScriptEnvironment With(IEnumerable<Assembly> plugins, IEnumerable<string> imports) =>
        new(plugins.ToList(), imports.ToList());

    /// <summary>
    /// The compiled file named <paramref name="key"/>, loaded once per environment, so running a file again in the
    /// same process does not load its assembly twice.
    /// </summary>
    public LoadedScript Entry(string key, Func<byte[]> image) =>
        loaded.GetOrAdd(key, _ =>
        {
            var bytes = image();
            return new LoadedScript(bytes, context.Value.Entry(bytes));
        });
}

/// <summary>A compiled project file: its assembly and its entry, which takes the submission array.</summary>
internal sealed record LoadedScript(byte[] Image, Func<object?[], Task<object>> Entry);

/// <summary>Where compiled project files are loaded: the plugins' assemblies resolve to those already loaded.</summary>
internal sealed class ScriptLoadContext : AssemblyLoadContext
{
    // The class and the method Roslyn compiles the first submission of a script to.
    private const string SubmissionType = "Submission#0";
    private const string FactoryMethod = "<Factory>";

    private readonly IReadOnlyList<Assembly> plugins;

    public ScriptLoadContext(IReadOnlyList<Assembly> plugins)
        : base("tazel-project-files")
    {
        this.plugins = plugins;
    }

    /// <summary>
    /// Loads <paramref name="image"/> and returns its entry, which takes the submission array: the globals first, a
    /// place for the submission itself second.
    /// </summary>
    public Func<object?[], Task<object>> Entry(byte[] image)
    {
        if (image.Length == 0)
        {
            return _ => Task.FromResult<object>(null!);
        }

        using var stream = new MemoryStream(image, writable: false);
        var assembly = LoadFromStream(stream);
        var factory = assembly.GetType(SubmissionType, throwOnError: true)!
            .GetMethod(FactoryMethod, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new BadImageFormatException($"a compiled project file has no {FactoryMethod}");
        return factory.CreateDelegate<Func<object?[], Task<object>>>();
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName) =>
        plugins.FirstOrDefault(plugin => string.Equals(plugin.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
}
