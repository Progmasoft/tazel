// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Lexer;
using Progmasoft.Tazel.Output;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>The kind of a text file, which names its Include directory: Options, Custom or Envs.</summary>
public enum ConfigurationFamily
{
    /// <summary><c>tazel_options.txt</c>.</summary>
    Options,

    /// <summary><c>tazel_custom.txt</c>.</summary>
    Custom,

    /// <summary><c>tazel_envs.txt</c>.</summary>
    Envs,

    /// <summary><c>.tazelignore</c>.</summary>
    Ignore,
}

/// <summary>
/// What the conditions of a configuration see: this machine, the platform once it is chosen, and one snapshot of the
/// environment for every file and phase of a command.
/// </summary>
/// <param name="Host">This machine.</param>
/// <param name="Target">The platform being built for; null while it is chosen.</param>
/// <param name="Environment">The environment variables.</param>
public sealed record ConfigurationContext(SystemFacts Host, SystemFacts? Target, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>This machine and the environment of this process, with no platform chosen yet.</summary>
    public static ConfigurationContext ForThisMachine()
    {
        var environment = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            environment[(string)entry.Key] = entry.Value as string ?? "";
        }

        return new ConfigurationContext(SystemFacts.Of(Driver.Host.Platform), null, environment);
    }

    /// <summary>This context with this machine as the platform built for.</summary>
    public ConfigurationContext ForItself() => this with { Target = Host };
}

/// <summary>
/// The tree-walking interpreter of the text files: from a file's tree it gives the tokens of the entries its taken
/// branches hold, with those of the files it includes in their places, each token naming the file it comes from.
/// </summary>
public static class ConfigurationResolver
{
    /// <summary>The most files one configuration reads.</summary>
    public static int MaxFiles { get; } = 4096;

    /// <summary>The most tokens one configuration holds.</summary>
    public static int MaxTokens { get; } = 1_000_000;

    /// <summary>
    /// The tokens of <paramref name="text"/>, the file at <paramref name="path"/> that messages call
    /// <paramref name="shown"/>, with its directives resolved for <paramref name="context"/>. With no target in the context, a branch that depends on <c>target_system</c> and
    /// what follows it in its <c>.if</c> are left out, so only what holds on every platform chooses the platform. A
    /// <c>platform</c> entry in such a branch is an error in either case.
    /// </summary>
    public static IReadOnlyList<Token> Resolve(
        string path, string shown, string text, ConfigurationFamily family, ConfigurationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Walk(family, context).Root(path, shown, text);
    }

    /// <summary>The file a path of a file of <paramref name="family"/> names, checked against its root.</summary>
    internal static string IncludeRoot(ConfigurationFamily family) =>
        Path.Combine(TazelHome.Directory(), "Include", family.ToString());

    // Why a path is not taken as written, or null when it is.
    internal static string? Refusal(string path, bool global)
    {
        if (path.IndexOfAny(['*', '?', '[', ']']) >= 0)
        {
            return "has a wildcard; name one file";
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return "is a URL; Tazel includes files of this machine only";
        }

        if (Path.IsPathRooted(path) || (path.Length >= 2 && path[1] == ':'))
        {
            return global ? "is absolute; a path in <> is below the Include directory" : "is absolute; a path in quotes is relative to this file";
        }

        return global && path.Replace('\\', '/').Split('/').Any(part => part is ".." or ".")
            ? "has . or .. parts; a path in <> stays below the Include directory"
            : null;
    }

    private sealed class Walk(ConfigurationFamily family, ConfigurationContext context)
    {
        private readonly List<Token> _output = [];
        private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
        private readonly HashSet<string> _once = new(StringComparer.Ordinal);
        private readonly List<(string Canonical, string Shown)> _chain = [];
        private int _files;

        public IReadOnlyList<Token> Root(string path, string shown, string text)
        {
            var canonical = Canonical(Path.GetFullPath(path));
            Read(canonical, shown, text, origin: null, onTarget: false);
            return _output;
        }

        private void Read(string canonical, string shown, string text, Source? origin, bool onTarget)
        {
            if (++_files > MaxFiles)
            {
                throw Failure(ErrorCodes.IncludeTooDeep, null, $"this configuration reads more than {MaxFiles} files", Chain());
            }

            var document = ConfigurationParser.Parse(new CustomLexer(text, shown).Tokens(), shown, text);
            _loaded.Add(canonical);
            if (document.Once)
            {
                _once.Add(canonical);
            }

            _chain.Add((canonical, shown));
            var first = _output.Count;
            Emit(document.Nodes, Path.GetDirectoryName(canonical)!, shown, text, origin, onTarget);
            _chain.RemoveAt(_chain.Count - 1);

            // The last entry of a file may leave out its ';': it is put back, so the next file starts afresh.
            if (_output.Count > first && _output[^1].Kind != TokenKind.Semicolon)
            {
                var last = _output[^1];
                _output.Add(new Token(TokenKind.Semicolon, ";", last.Line, last.Column + last.Text.Length, Origin: last.Origin));
            }
        }

        private void Emit(IReadOnlyList<Node> nodes, string directory, string shown, string text, Source? origin, bool onTarget)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case TokensNode tokens:
                        if (onTarget)
                        {
                            NoPlatform(tokens.Tokens, shown, text, origin);
                        }

                        if (_output.Count + tokens.Tokens.Count > MaxTokens)
                        {
                            throw Failure(ErrorCodes.IncludeTooDeep, null, $"this configuration holds more than {MaxTokens} tokens", Chain());
                        }

                        _output.AddRange(origin is null ? tokens.Tokens : tokens.Tokens.Select(token => token with { Origin = origin }));
                        break;
                    case IncludeNode include:
                        Include(include, directory, shown, text, origin, onTarget);
                        break;
                    case IfNode chain:
                        If(chain, directory, shown, text, origin, onTarget);
                        break;
                    default:
                        throw new ArgumentException($"no node {node.GetType().Name}", nameof(nodes));
                }
            }
        }

        private void If(IfNode chain, string directory, string shown, string text, Source? origin, bool onTarget)
        {
            var dependent = onTarget || chain.OnTarget;
            if (chain.OnTarget)
            {
                // Every branch, taken or not, is looked at: a platform here would choose the platform from itself.
                foreach (var branch in chain.Branches)
                {
                    foreach (var tokens in Flat(branch.Body))
                    {
                        NoPlatform(tokens.Tokens, shown, text, origin);
                    }
                }
            }

            foreach (var branch in chain.Branches)
            {
                var holds = branch.Condition is null ? true
                    : ConditionEvaluator.Evaluate(branch.Condition, context.Host, context.Target, context.Environment);
                if (holds is null)
                {
                    // While the platform is chosen, a branch on target_system decides nothing, and nor do those after it.
                    return;
                }

                if (holds == true)
                {
                    Emit(branch.Body, directory, shown, text, origin, dependent);
                    return;
                }
            }
        }

        private static IEnumerable<TokensNode> Flat(IReadOnlyList<Node> nodes) => nodes.SelectMany(node => node switch
        {
            TokensNode tokens => [tokens],
            IfNode chain => chain.Branches.SelectMany(branch => Flat(branch.Body)),
            _ => [],
        });

        private void NoPlatform(IReadOnlyList<Token> tokens, string shown, string text, Source? origin)
        {
            if (family != ConfigurationFamily.Options)
            {
                return;
            }

            for (var i = 0; i + 1 < tokens.Count; i++)
            {
                var token = tokens[i];
                var startsEntry = i == 0 || tokens[i - 1].Kind == TokenKind.Semicolon;
                if (startsEntry && token is { Kind: TokenKind.Name, Text: "platform" } && tokens[i + 1].Kind == TokenKind.Colon)
                {
                    var at = origin is null ? token : token with { Origin = origin };
                    throw new CustomCommandsException(Diagnostic.AtToken(ErrorCodes.PlatformDependsOnTarget, text, shown, at,
                        "the platform is chosen inside a condition on target_system, which depends on the platform",
                        "this platform", "choose the platform outside such a condition, or with a condition on host_system",
                        "Tazel cannot pick the platform by asking which platform it picked."));
                }
            }
        }

        private void Include(IncludeNode node, string directory, string shown, string text, Source? origin, bool onTarget)
        {
            var include = node;
            var at = origin is null ? include.At : include.At with { Origin = origin };
            var target = Target(include, directory, at, shown, text);
            var canonical = Canonical(target);
            if (_once.Contains(canonical) || (include.Import && _loaded.Contains(canonical)))
            {
                return;
            }

            if (_chain.Any(entry => entry.Canonical == canonical))
            {
                throw Failure(ErrorCodes.IncludeTooDeep, (at, shown, text), $"{include.Path} includes itself",
                    Chain() + $" -> {include.Path}");
            }

            if (_chain.Count >= ConfigurationParser.MaxDepth)
            {
                throw Failure(ErrorCodes.IncludeTooDeep, (at, shown, text), $"includes nest more than {ConfigurationParser.MaxDepth} deep", Chain());
            }

            string included;
            try
            {
                included = File.ReadAllText(canonical);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw Failure(ErrorCodes.IncludeMissing, (at, shown, text), $"{include.Path} cannot be read: {error.Message}", null);
            }

            if (include.Import)
            {
                _once.Add(canonical);
            }

            var name = Shown(canonical);
            Read(canonical, name, included, new Source(name, included), onTarget);
        }

        // The file an include names: beside the file that names it, or below the family's Include directory.
        private string Target(IncludeNode include, string directory, Token at, string shown, string text)
        {
            var path = include.Path;
            var refusal = Refusal(path, include.Global);
            if (refusal is not null)
            {
                throw Failure(ErrorCodes.IncludePathRefused, (at, shown, text), $"'{path}' {refusal}", null);
            }

            if (!include.Global)
            {
                var full = Path.GetFullPath(Path.Combine(directory, path));
                return File.Exists(full) ? full
                    : throw Failure(ErrorCodes.IncludeMissing, (at, shown, text), $"{path} is not a file beside {shown}", null);
            }

            return Global(path, at, shown, text);
        }

        // A file below the family's Include directory of TAZEL_HOME, which no link may leave.
        private string Global(string path, Token at, string shown, string text)
        {
            CustomCommandsException Refused(string why) => Failure(ErrorCodes.IncludePathRefused, (at, shown, text),
                $"'{path}' {why}", null);

            string root;
            try
            {
                root = Canonical(IncludeRoot(family));
            }
            catch (ConfigurationHomeException error)
            {
                throw Failure(ErrorCodes.IncludePathRefused, (at, shown, text), error.Message, null);
            }

            var file = Path.GetFullPath(Path.Combine(root, path));
            if (!File.Exists(file))
            {
                throw Failure(ErrorCodes.IncludeMissing, (at, shown, text), $"<{path}> is not a file of {root}", null);
            }

            var real = Canonical(file);
            if (!real.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw Refused($"leaves {root} through a link");
            }

            return real;
        }

        private string Chain() => string.Join(" -> ", _chain.Select(entry => entry.Shown));

        private static string Shown(string canonical)
        {
            var relative = Path.GetRelativePath(System.Environment.CurrentDirectory, canonical);
            return relative.StartsWith("..", StringComparison.Ordinal) ? canonical : relative.Replace('\\', '/');
        }

        private static CustomCommandsException Failure(string code, (Token At, string File, string Text)? where, string message, string? chain)
        {
            var full = chain is null ? message : $"{message} ({chain})";
            if (where is not { } place)
            {
                return new CustomCommandsException(full);
            }

            return new CustomCommandsException(Diagnostic.AtToken(code, place.Text, place.File, place.At with { Text = "." + place.At.Text },
                full, "this directive", code == ErrorCodes.IncludeTooDeep ? "say .pragma once at the top of the file, or .import it" : null,
                "Tazel followed the includes all the way down, and then some."));
        }
    }

    /// <summary>
    /// <paramref name="path"/> with every link of it followed, so two names of one file are one file and a link
    /// cannot lead out of a directory; on Windows each part is written as the directory holds it, so a path typed in
    /// another case is the same path.
    /// </summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            if (OperatingSystem.IsWindows() && System.IO.Directory.Exists(current))
            {
                name = System.IO.Directory.EnumerateFileSystemEntries(current, part).Select(Path.GetFileName)
                    .FirstOrDefault(entry => string.Equals(entry, part, StringComparison.OrdinalIgnoreCase)) ?? part;
            }

            var next = Path.Combine(current, name);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            current = info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved
                ? Path.GetFullPath(resolved.FullName)
                : next;
        }

        return current;
    }
}
