// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Progmasoft.Tazel.Dsl.Build.Prelude;

namespace Progmasoft.Tazel.Project;

/// <summary>What <c>tazel limited-time C#</c> changed, or would have changed and could not.</summary>
internal sealed record MigrationResult(IReadOnlyList<string> Renamed, IReadOnlyList<string> Rewritten, IReadOnlyList<string> Problems);

/// <summary>
/// <c>tazel limited-time C#</c>, until Tazel 1.0.0: renames the project files of earlier names to <c>BUILD.csx</c>
/// and writes the targets that <c>Depends</c> names by their variables as labels. It reads the files with Roslyn and
/// runs none of them. Everything is checked before anything is written, so a problem changes no file; a second run
/// finds nothing to do.
/// </summary>
internal static class CsxMigration
{
    /// <summary>The names a package's project file had before only <c>BUILD.csx</c> was one.</summary>
    public static IReadOnlyList<string> LegacyNames { get; } = ["BUILD.tzl", "BUILD.tazel", "BUILD", "Tazel.build"];

    // The families whose declarations return a Target, and so may be named by a variable in Depends.
    private static readonly HashSet<string> Families = new(StringComparer.Ordinal) { "CC", "ObjectiveC", "Zig" };

    /// <summary>Migrates every package below <paramref name="directory"/>, in every module there.</summary>
    public static MigrationResult Run(string directory, bool write = true)
    {
        var start = Path.GetFullPath(directory);
        var problems = new List<string>();
        var plans = new List<(string? Legacy, string Target, string Text, bool Changed)>();
        foreach (var packageDirectory in Directories(start))
        {
            var names = new HashSet<string>(
                Directory.EnumerateFiles(packageDirectory).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal);
            var legacy = LegacyNames.Where(names.Contains).ToList();
            var shown = Shown(start, packageDirectory);
            var csx = Path.Combine(packageDirectory, "BUILD.csx");
            if (legacy.Count > 1)
            {
                problems.Add($"{shown}: {string.Join(" and ", legacy)} are both here; keep one, and run this again");
                continue;
            }

            var source = legacy.Count == 1 ? Path.Combine(packageDirectory, legacy[0]) : names.Contains("BUILD.csx") ? csx : null;
            if (source is null)
            {
                continue;
            }

            var text = File.ReadAllText(source);
            var rewritten = Rewrite(text, Package(packageDirectory), Shown(start, source), problems);
            if (legacy.Count == 1 && names.Contains("BUILD.csx"))
            {
                // What an interrupted run leaves: BUILD.csx already holds what this file becomes.
                if (rewritten is not null && File.ReadAllText(csx) == rewritten)
                {
                    plans.Add((source, csx, rewritten, Changed: false));
                }
                else
                {
                    problems.Add($"{shown}: {legacy[0]} and BUILD.csx are both here; keep one, and run this again");
                }

                continue;
            }

            if (rewritten is not null && (legacy.Count == 1 || rewritten != text))
            {
                plans.Add((legacy.Count == 1 ? source : null, csx, rewritten, rewritten != text || legacy.Count == 1));
            }
        }

        if (problems.Count > 0 || !write)
        {
            return Result(start, plans, problems);
        }

        foreach (var (legacy, target, text, changed) in plans)
        {
            if (changed)
            {
                var partial = target + ".partial";
                File.WriteAllText(partial, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(partial, target, overwrite: legacy is null);
            }

            if (legacy is not null)
            {
                File.Delete(legacy);
            }
        }

        return Result(start, plans, problems);
    }

    private static MigrationResult Result(
        string start, List<(string? Legacy, string Target, string Text, bool Changed)> plans, List<string> problems) =>
        new(plans.Where(plan => plan.Legacy is not null).Select(plan => $"{Shown(start, plan.Legacy!)} -> BUILD.csx").ToList(),
            plans.Where(plan => plan.Legacy is null).Select(plan => Shown(start, plan.Target)).ToList(),
            problems);

    // The directories below start, without hidden ones (.tazel, .git), the stores of fetched modules, or links.
    private static IEnumerable<string> Directories(string start)
    {
        var pending = new Stack<string>([start]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            yield return directory;
            foreach (var child in new DirectoryInfo(directory).EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal))
            {
                if (!child.Name.StartsWith('.') && child.LinkTarget is null)
                {
                    pending.Push(child.FullName);
                }
            }
        }
    }

    // The package path of a directory: from the nearest MODULE.csx at or above it.
    private static string Package(string packageDirectory)
    {
        var root = ModuleFiles.FindRoot(packageDirectory);
        var relative = Path.GetRelativePath(root, packageDirectory).Replace('\\', '/');
        return relative == "." ? "" : relative;
    }

    private static string Shown(string start, string path)
    {
        var relative = Path.GetRelativePath(start, path).Replace('\\', '/');
        return relative == "." ? "./" : relative;
    }

    /// <summary>
    /// <paramref name="text"/> with every target that <c>Depends</c> names by a variable named by its label, and the
    /// variables nothing else uses left out; null, with the reasons in <paramref name="problems"/>, when a reference
    /// cannot be resolved.
    /// </summary>
    internal static string? Rewrite(string text, string package, string shown, List<string> problems)
    {
        var options = ScriptHost.BaseOptions.WithFilePath(shown).WithFileEncoding(Encoding.UTF8);
        var compilation = CSharpScript.Create(text, options, typeof(BuildGlobals)).GetCompilation();
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var changes = new List<TextChange>();
        var replaced = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        var failed = false;

        foreach (var element in DependsElements(root))
        {
            var type = model.GetTypeInfo(element).Type;
            if (type is null || type.SpecialType == SpecialType.System_String || type.Name != "Target")
            {
                continue;
            }

            var line = element.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (element is not IdentifierNameSyntax identifier ||
                model.GetSymbolInfo(identifier).Symbol is not { } symbol ||
                DeclaredName(symbol) is not { } name)
            {
                problems.Add($"{shown}:{line}: Depends names '{element}', which is not a variable a declaration of this " +
                    "file set; write the label of the target instead");
                failed = true;
                continue;
            }

            changes.Add(new TextChange(element.Span, Literal($"//{package}:{name}")));
            replaced[symbol] = replaced.GetValueOrDefault(symbol) + 1;
        }

        if (failed)
        {
            return null;
        }

        // A variable that only Depends used is left out: its declaration stays, as a statement of its own.
        foreach (var (symbol, count) in replaced)
        {
            var uses = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Count(name => name.Identifier.ValueText == symbol.Name &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, symbol));
            if (uses == count && Declarator(symbol) is { Parent: VariableDeclarationSyntax { Variables.Count: 1 } declaration } declarator)
            {
                changes.Add(new TextChange(TextSpan.FromBounds(declaration.SpanStart, declarator.Initializer!.Value.SpanStart), ""));
            }
        }

        return changes.Count == 0 ? text : SourceText.From(text).WithChanges(changes).ToString();
    }

    // The elements of every Depends = [...] (and Depends = new() {...}) in object initializers.
    private static IEnumerable<ExpressionSyntax> DependsElements(SyntaxNode root) =>
        root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "Depends" })
            .SelectMany(assignment => assignment.Right switch
            {
                CollectionExpressionSyntax collection => collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression),
                _ => [],
            });

    // The name in the first argument of the declaration a variable was set to: CC.StaticLibrary("math", ...).
    private static string? DeclaredName(ISymbol symbol)
    {
        if (Declarator(symbol)?.Initializer?.Value is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax family },
                ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax literal }, ..],
            } || !Families.Contains(family.Identifier.ValueText) || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return null;
        }

        return literal.Token.ValueText;
    }

    private static VariableDeclaratorSyntax? Declarator(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault();

    private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);
}
