// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Progmasoft.Tazel.Dsl;
using Progmasoft.Tazel.Label;
using Progmasoft.Tazel.Project;

namespace Progmasoft.Tazel.Cli;

/// <summary>
/// The query language of <c>tazel query</c>, a part of Bazel's: target patterns, <c>deps(x)</c>, <c>deps(x, depth)</c>,
/// <c>rdeps(universe, x)</c>, <c>rdeps(universe, x, depth)</c>, <c>kind(k, x)</c>, <c>tests(x)</c>, and <c>+</c>,
/// <c>-</c>, <c>^</c> (or <c>union</c>, <c>except</c>, <c>intersect</c>) with parentheses. An operator stands apart, with
/// spaces around it, as a label may hold <c>-</c> and <c>+</c>.
/// </summary>
internal sealed class Query(Workspace workspace, PackageId current)
{
    private List<string> _tokens = [];
    private int _position;

    /// <summary>The name of a target's kind, as <c>kind()</c> matches it and <c>--output label_kind</c> prints it.</summary>
    public static string KindOf(ProjectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return $"{target.Family}.{target.Kind}";
    }

    /// <summary>The targets <paramref name="expression"/> names, in label order.</summary>
    public IReadOnlyList<ProjectTarget> Evaluate(string expression)
    {
        _tokens = Tokens(expression);
        _position = 0;
        if (_tokens.Count == 0)
        {
            throw new ProjectException("the query is empty");
        }

        var result = Expression();
        if (_position < _tokens.Count)
        {
            throw new ProjectException($"the query has '{_tokens[_position]}' where it should end");
        }

        return result.OrderBy(target => target.Label.ToString(), StringComparer.Ordinal).ToList();
    }

    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (text[i] is '(' or ')' or ',')
            {
                tokens.Add(text[i++].ToString());
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ','))
                {
                    i++;
                }

                tokens.Add(text[start..i]);
            }
        }

        return tokens;
    }

    private string? Peek => _position < _tokens.Count ? _tokens[_position] : null;

    private string Take(string? wanted = null)
    {
        if (_position >= _tokens.Count)
        {
            throw new ProjectException($"the query ends where it needs {(wanted is null ? "more" : $"'{wanted}'")}");
        }

        var token = _tokens[_position++];
        if (wanted is not null && token != wanted)
        {
            throw new ProjectException($"the query has '{token}' where it needs '{wanted}'");
        }

        return token;
    }

    private HashSet<ProjectTarget> Expression()
    {
        var result = Term();
        while (Peek is "+" or "-" or "^" or "union" or "except" or "intersect")
        {
            var op = Take();
            var right = Term();
            switch (op)
            {
                case "+" or "union":
                    result.UnionWith(right);
                    break;
                case "-" or "except":
                    result.ExceptWith(right);
                    break;
                default:
                    result.IntersectWith(right);
                    break;
            }
        }

        return result;
    }

    private HashSet<ProjectTarget> Term()
    {
        var token = Take();
        if (token == "(")
        {
            var inner = Expression();
            Take(")");
            return inner;
        }

        if (Peek == "(" && token is "deps" or "rdeps" or "kind" or "tests")
        {
            Take("(");
            HashSet<ProjectTarget> result;
            switch (token)
            {
                case "deps":
                    var of = Expression();
                    result = Closure(of, Depth(), target => target.Dependencies.Concat(target.ZigImports.Values));
                    break;
                case "rdeps":
                    var universe = Expression();
                    Take(",");
                    var targets = Expression();
                    var reverse = universe.SelectMany(target => target.Dependencies.Concat(target.ZigImports.Values)
                            .Select(dependency => (dependency, target)))
                        .ToLookup(pair => pair.dependency, pair => pair.target);
                    result = Closure(targets, Depth(), target => reverse[target]);
                    result.IntersectWith(universe.Union(targets));
                    break;
                case "kind":
                    var kind = Take();
                    Take(",");
                    result = Expression().Where(target => KindOf(target).Contains(kind, StringComparison.OrdinalIgnoreCase))
                        .ToHashSet();
                    break;
                default:
                    result = Expression().Where(target => target.Kind == TargetKind.Test).ToHashSet();
                    break;
            }

            Take(")");
            return result;
        }

        return Pattern(token);
    }

    private int? Depth()
    {
        if (Peek != ",")
        {
            return null;
        }

        Take(",");
        var text = Take();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var depth)
            ? depth
            : throw new ProjectException($"'{text}' is not a depth");
    }

    private static HashSet<ProjectTarget> Closure(
        HashSet<ProjectTarget> start, int? depth, Func<ProjectTarget, IEnumerable<ProjectTarget>> next)
    {
        var result = new HashSet<ProjectTarget>(start);
        var frontier = start.ToList();
        for (var level = 0; frontier.Count > 0 && (depth is null || level < depth); level++)
        {
            frontier = frontier.SelectMany(next).Where(result.Add).ToList();
        }

        return result;
    }

    private HashSet<ProjectTarget> Pattern(string text)
    {
        Selection selection;
        try
        {
            selection = Labels.Selection(text, current);
        }
        catch (LabelException error)
        {
            throw new ProjectException(error.Message, error);
        }

        return selection switch
        {
            TargetLabel label => workspace.Targets.TryGetValue(label, out var target)
                ? [target]
                : throw new ProjectException($"{label} is not a target"),
            PackagePattern pattern => workspace.Targets.Values.Where(target => pattern.Covers(target.Label.Package)).ToHashSet(),
            _ => [],
        };
    }
}
