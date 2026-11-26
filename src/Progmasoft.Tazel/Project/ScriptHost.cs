// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Progmasoft.Tazel.Dsl;

namespace Progmasoft.Tazel.Project;

/// <summary>What is wrong with a module, its project files or what they name.</summary>
public sealed class ProjectException : Exception
{
    /// <summary>A problem of the project.</summary>
    public ProjectException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public ProjectException()
    {
    }

    /// <summary>A problem and its cause.</summary>
    public ProjectException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Runs <c>MODULE.csx</c> and <c>BUILD.csx</c> files with the C# scripting of Roslyn.</summary>
/// <remarks>
/// A file is compiled to an assembly with its debugging information embedded, and that assembly is what runs, whether
/// it was compiled now or kept from an earlier run (<see cref="ScriptCache"/>). The compiler is loaded only when a file
/// is compiled: only <see cref="Emit"/> and <see cref="CreateOptions"/> use its types.
/// </remarks>
internal static class ScriptHost
{
    /// <summary>The namespace every project file sees without a <c>using</c>.</summary>
    public const string CommonNamespace = "Progmasoft.Tazel.Dsl.Common";

    private static readonly Lazy<ScriptOptions> BaseOptionsValue = new(CreateOptions);

    /// <summary>The options every project file is compiled with.</summary>
    internal static ScriptOptions BaseOptions => BaseOptionsValue.Value;

    /// <summary>
    /// Compiles and runs <paramref name="code"/> with <paramref name="globals"/>; <paramref name="shownPath"/> names
    /// the file in messages. The compiled file is kept in the state directory of <paramref name="mainRoot"/>, the root
    /// of the main module; null keeps nothing. A compile error or an exception of the script becomes a
    /// <see cref="ProjectException"/>.
    /// </summary>
    public static async Task RunAsync<TGlobals>(
        string code, string shownPath, TGlobals globals, string? mainRoot, ScriptEnvironment? environment = null)
        where TGlobals : class
    {
        var used = environment ?? ScriptEnvironment.Default;
        var key = ScriptCache.Key(code, shownPath, typeof(TGlobals), used);
        var directory = mainRoot is null ? null : Path.Combine(mainRoot, ModuleFiles.StateDirectory, ScriptCache.Directory);
        LoadedScript loaded;
        try
        {
            loaded = used.Entry(key, () => Image(code, shownPath, typeof(TGlobals), used, directory, key, reuse: true));
        }
        catch (BadImageFormatException) when (directory is not null)
        {
            // A kept file that does not load is compiled again and replaced.
            loaded = used.Entry(key, () => Image(code, shownPath, typeof(TGlobals), used, directory, key, reuse: false));
        }

        // Loaded before in this process, perhaps for another module: this module keeps it too.
        if (directory is not null && !ScriptCache.IsKept(directory, key))
        {
            ScriptCache.TryWrite(directory, key, loaded.Image);
        }

        try
        {
            await loaded.Entry([globals, null]).ConfigureAwait(false);
        }
        catch (DslException error)
        {
            throw new ProjectException(error.Message, error);
        }
        catch (Exception error)
        {
            throw new ProjectException($"{shownPath}: {Where(error, shownPath)}{error.GetType().Name}: {error.Message}", error);
        }
    }

    // The compiled file: the one kept under its key, else compiled now and kept.
    private static byte[] Image(
        string code, string shownPath, Type globals, ScriptEnvironment environment, string? directory, string key, bool reuse)
    {
        if (reuse && directory is not null && ScriptCache.TryRead(directory, key) is { } kept)
        {
            return kept;
        }

        var image = Emit(code, shownPath, globals, environment);
        if (directory is not null)
        {
            ScriptCache.TryWrite(directory, key, image);
        }

        return image;
    }

    private static byte[] Emit(string code, string shownPath, Type globals, ScriptEnvironment environment)
    {
        var options = BaseOptions
            .AddReferences(environment.Plugins.Select(assembly => MetadataReference.CreateFromFile(assembly.Location)))
            .AddImports(environment.Imports)
            .WithFilePath(shownPath)
            .WithFileEncoding(Encoding.UTF8);
        var compilation = CSharpScript.Create(code, options, globals).GetCompilation();
        using var image = new MemoryStream();
        // The debugging information names the lines of the file in the stack trace of an exception it throws.
        var result = compilation.Emit(image, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new ProjectException(Describe(result.Diagnostics, shownPath));
        }

        // A file of nothing but using directives has no code, and Roslyn emits no assembly for it: it is kept as an
        // empty file, whose entry does nothing.
        return result.Success ? image.ToArray() : [];
    }

    private static ScriptOptions CreateOptions()
    {
        // The whole base class library: every managed assembly of the .NET runtime Tazel runs on, as the host lists
        // them. A project file still names what it uses with using; nothing of it is imported for it.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>();
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator);
        foreach (var file in trusted.Where(file => file.Length > 0 &&
                     string.Equals(Path.GetDirectoryName(file), runtime, StringComparison.Ordinal)))
        {
            references.Add(MetadataReference.CreateFromFile(file));
        }

        foreach (var assembly in new[]
                 {
                     typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(DslException).Assembly,
                     typeof(PluginApi.ITazelPlugin).Assembly,
                 })
        {
            references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(CommonNamespace)
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithAllowUnsafe(false)
            .WithCheckOverflow(true);
    }

    private static string Describe(IEnumerable<Diagnostic> diagnostics, string shownPath)
    {
        var text = new StringBuilder();
        foreach (var diagnostic in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20))
        {
            var span = diagnostic.Location.GetLineSpan();
            var position = span.StartLinePosition;
            text.Append(shownPath).Append(':').Append(position.Line + 1).Append(':').Append(position.Character + 1)
                .Append(": ").Append(diagnostic.Id).Append(": ")
                .Append(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        return text.ToString().TrimEnd('\n');
    }

    // The line of the script an exception came from, when its stack trace has one.
    private static string Where(Exception error, string shownPath)
    {
        var trace = new System.Diagnostics.StackTrace(error, fNeedFileInfo: true);
        foreach (var frame in trace.GetFrames())
        {
            if (frame.GetFileName() == shownPath && frame.GetFileLineNumber() > 0)
            {
                return $"{frame.GetFileLineNumber()}: ";
            }
        }

        return "";
    }
}
