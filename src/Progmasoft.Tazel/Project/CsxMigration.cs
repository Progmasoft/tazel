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
    public static MigrationResult Run(string directory) => Run(directory, write: true);

    /// <summary>Migrates every package below <paramref name="directory"/>, or with <paramref name="write"/> false only says what it would do.</summary>
    public static MigrationResult Run(string directory, bool write)
    {
        var start = Path.GetFullPath(directory);
        var problems = new List<string>();
        var plans = new List<Plan>();
        foreach (var packageDirectory in Directories(start))
        {
            if (PlanOf(start, packageDirectory, problems) is { } plan)
            {
                plans.Add(plan);
            }
        }

        if (problems.Count == 0 && write)
        {
            plans.ForEach(Apply);
        }

        return Result(start, plans, problems);
    }

    // What becomes of one package: Legacy is the file to remove, or null when BUILD.csx is rewritten in place.
    private sealed record Plan(string? Legacy, string Target, string Text, bool Changed);

    // The plan of one directory, or null when it has nothing to change or a problem was added.
    private static Plan? PlanOf(string start, string packageDirectory, List<string> problems)
    {
        var names = new HashSet<string>(
            Directory.EnumerateFiles(packageDirectory).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal);
        var legacy = LegacyNames.Where(names.Contains).ToList();
        var shown = Shown(start, packageDirectory);
        var csx = Path.Combine(packageDirectory, "BUILD.csx");
        if (legacy.Count > 1)
        {
            problems.Add($"{shown}: {string.Join(" and ", legacy)} are both here; keep one, and run this again");
            return null;
        }

        var source = legacy.Count == 1 ? Path.Combine(packageDirectory, legacy[0]) : csx;
        if (!File.Exists(source))
        {
            return null;
        }

        var text = File.ReadAllText(source);
        var rewritten = Rewrite(text, Package(packageDirectory), Shown(start, source), problems);
        if (rewritten is null)
        {
            return null;
        }

        if (legacy.Count == 0)
        {
            return rewritten == text ? null : new Plan(null, csx, rewritten, Changed: true);
        }

        if (!names.Contains("BUILD.csx"))
        {
            return new Plan(source, csx, rewritten, Changed: true);
        }

        // What an interrupted run leaves: BUILD.csx already holds what this file becomes.
        if (File.ReadAllText(csx) == rewritten)
        {
            return new Plan(source, csx, rewritten, Changed: false);
        }

        problems.Add($"{shown}: {legacy[0]} and BUILD.csx are both here; keep one, and run this again");
        return null;
    }

    private static void Apply(Plan plan)
    {
        if (plan.Changed)
        {
            var partial = plan.Target + ".partial";
            File.WriteAllText(partial, plan.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(partial, plan.Target, overwrite: plan.Legacy is null);
        }

        if (plan.Legacy is not null)
        {
            File.Delete(plan.Legacy);
        }
    }

    private static MigrationResult Result(string start, List<Plan> plans, List<string> problems) =>
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
        var before = problems.Count;
        foreach (var element in DependsElements(root))
        {
            if (Label(element, model, package, shown, problems) is { } label)
            {
                changes.Add(new TextChange(element.Span, Literal(label.Text)));
                replaced[label.Symbol] = replaced.GetValueOrDefault(label.Symbol) + 1;
            }
        }

        if (problems.Count > before)
        {
            return null;
        }

        changes.AddRange(UnusedDeclarations(root, model, replaced));
        return changes.Count == 0 ? text : SourceText.From(text).WithChanges(changes).ToString();
    }

    // The label an element of Depends becomes, with its variable; null for an element that is no Target, or when it
    // cannot be resolved, with the reason added to problems.
    private static (string Text, ISymbol Symbol)? Label(
        ExpressionSyntax element, SemanticModel model, string package, string shown, List<string> problems)
    {
        var type = model.GetTypeInfo(element).Type;
        if (type is null || type.SpecialType == SpecialType.System_String || type.Name != "Target")
        {
            return null;
        }

        if (element is IdentifierNameSyntax identifier && model.GetSymbolInfo(identifier).Symbol is { } symbol &&
            DeclaredName(symbol) is { } name)
        {
            return ($"//{package}:{name}", symbol);
        }

        var line = element.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        problems.Add($"{shown}:{line}: Depends names '{element}', which is not a variable a declaration of this " +
            "file set; write the label of the target instead");
        return null;
    }

    // A variable that only Depends used is left out: its declaration stays, as a statement of its own.
    private static IEnumerable<TextChange> UnusedDeclarations(SyntaxNode root, SemanticModel model, Dictionary<ISymbol, int> replaced)
    {
        foreach (var (symbol, count) in replaced)
        {
            var uses = root.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Count(name => name.Identifier.ValueText == symbol.Name &&
                    SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(name).Symbol, symbol));
            if (uses == count && Declarator(symbol) is { Parent: VariableDeclarationSyntax { Variables.Count: 1 } declaration } declarator)
            {
                yield return new TextChange(TextSpan.FromBounds(declaration.SpanStart, declarator.Initializer!.Value.SpanStart), "");
            }
        }
    }

    // The elements of every Depends = [...] (and Depends = new() {...}) in object initializers.
    private static IEnumerable<ExpressionSyntax> DependsElements(SyntaxNode root) =>
        root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(assignment => assignment.Left is IdentifierNameSyntax { Identifier.ValueText: "Depends" })
            .SelectMany(assignment => assignment.Right is CollectionExpressionSyntax collection
                ? collection.Elements.OfType<ExpressionElementSyntax>().Select(e => e.Expression)
                : []);

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
