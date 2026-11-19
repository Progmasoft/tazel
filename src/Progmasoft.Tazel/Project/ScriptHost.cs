// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
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
internal static class ScriptHost
{
    /// <summary>The namespace every project file sees without a <c>using</c>.</summary>
    public const string CommonNamespace = "Progmasoft.Tazel.Dsl.Common";

    private static readonly ScriptOptions BaseOptions = CreateOptions();

    /// <summary>
    /// Compiles and runs <paramref name="code"/> with <paramref name="globals"/>; <paramref name="shownPath"/> names
    /// the file in messages. A compile error or an exception of the script becomes a <see cref="ProjectException"/>.
    /// </summary>
    public static async Task RunAsync<TGlobals>(string code, string shownPath, TGlobals globals)
        where TGlobals : class
    {
        var script = CSharpScript.Create(code, BaseOptions.WithFilePath(shownPath), typeof(TGlobals));
        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new ProjectException(Describe(errors, shownPath));
        }

        try
        {
            await script.RunAsync(globals).ConfigureAwait(false);
        }
        catch (DslException error)
        {
            throw new ProjectException(error.Message, error);
        }
        catch (CompilationErrorException error)
        {
            throw new ProjectException(Describe(error.Diagnostics, shownPath), error);
        }
        catch (Exception error)
        {
            throw new ProjectException($"{shownPath}: {Where(error, shownPath)}{error.GetType().Name}: {error.Message}", error);
        }
    }

    private static ScriptOptions CreateOptions()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>();
        foreach (var name in new[] { "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "netstandard.dll" })
        {
            var file = Path.Combine(runtime, name);
            if (File.Exists(file))
            {
                references.Add(MetadataReference.CreateFromFile(file));
            }
        }

        foreach (var assembly in new[] { typeof(object).Assembly, typeof(Enumerable).Assembly, typeof(DslException).Assembly })
        {
            references.Add(MetadataReference.CreateFromFile(assembly.Location));
        }

        return ScriptOptions.Default
            .WithReferences(references)
            .WithImports(CommonNamespace)
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithEmitDebugInformation(false)
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

    /// <summary>Whether a type is from the DSL, for messages that name what a script returned.</summary>
    public static bool IsDslType(Type type) => type.Assembly == typeof(DslException).Assembly;

    /// <summary>The assembly of the DSL, for diagnostics.</summary>
    public static Assembly DslAssembly => typeof(DslException).Assembly;
}
