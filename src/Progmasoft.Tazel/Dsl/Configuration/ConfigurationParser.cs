// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using Progmasoft.Tazel.Dsl.Custom;
using Progmasoft.Tazel.Dsl.Custom.Lexer;
using Progmasoft.Tazel.Output;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>A part of a text file: tokens of its entries, or a directive.</summary>
/// <param name="At">Where it starts.</param>
public abstract record Node(Token At);

/// <summary>Tokens of entries, as the reader of the file's kind takes them.</summary>
/// <param name="Tokens">The tokens, in order.</param>
public sealed record TokensNode(IReadOnlyList<Token> Tokens) : Node(Tokens[0]);

/// <summary><c>.include</c> or <c>.import</c> of a file.</summary>
/// <param name="At">The directive.</param>
/// <param name="Path">The path as written, without its quotes or brackets.</param>
/// <param name="Global">Whether it was written <c>&lt;path&gt;</c>, below the Include directory of TAZEL_HOME.</param>
/// <param name="Import">Whether it is <c>.import</c>, which reads a file at most once.</param>
public sealed record IncludeNode(Token At, string Path, bool Global, bool Import) : Node(At);

/// <summary>One branch of an <c>.if</c>: its condition, null for <c>.else</c>, and what it holds.</summary>
/// <param name="At">The <c>.if</c>, <c>.elif</c> or <c>.else</c>.</param>
/// <param name="Condition">The condition, or null for <c>.else</c>.</param>
/// <param name="Body">What the branch holds.</param>
public sealed record Branch(Token At, Condition? Condition, IReadOnlyList<Node> Body);

/// <summary><c>.if</c> ... <c>.end</c>: the first branch whose condition holds is taken.</summary>
/// <param name="At">The <c>.if</c>.</param>
/// <param name="Branches">Its branches, in order.</param>
public sealed record IfNode(Token At, IReadOnlyList<Branch> Branches) : Node(At)
{
    /// <summary>Whether any branch's condition names <c>target_system</c>.</summary>
    public bool OnTarget => Branches.Any(branch => branch.Condition?.UsesTarget == true);
}

/// <summary>A text file read: what it holds and whether it said <c>.pragma once</c>.</summary>
/// <param name="Nodes">Its parts, in order.</param>
/// <param name="Once">Whether it starts with <c>.pragma once</c>.</param>
public sealed record ConfigurationDocument(IReadOnlyList<Node> Nodes, bool Once);

/// <summary>
/// The parser of <c>tazel_options.txt</c>, <c>tazel_custom.txt</c> and <c>tazel_envs.txt</c>: it puts the tokens of
/// <see cref="CustomLexer"/> into a tree of entries and directives. It reads every branch of every <c>.if</c>, so a
/// mistake in a branch not taken is found too, and it opens no file and runs nothing.
/// </summary>
public static class ConfigurationParser
{
    /// <summary>The deepest <c>.if</c> may nest, and the deepest includes may go.</summary>
    public const int MaxDepth = 64;

    private sealed class Frame(Token at)
    {
        public Token At { get; } = at;

        public List<Branch> Branches { get; } = [];

        public Token BranchAt { get; set; } = at;

        public Condition? Condition { get; set; }

        public List<Node> Body { get; set; } = [];

        public bool SawElse { get; set; }
    }

    /// <summary>The tree of <paramref name="tokens"/>, read from <paramref name="text"/> of <paramref name="file"/>.</summary>
    public static ConfigurationDocument Parse(IReadOnlyList<Token> tokens, string file, string text)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var root = new List<Node>();
        var frames = new Stack<Frame>();
        var pending = new List<Token>();
        var once = false;
        List<Node> Current() => frames.Count == 0 ? root : frames.Peek().Body;
        void Flush()
        {
            if (pending.Count > 0)
            {
                Current().Add(new TokensNode(pending.ToList()));
                pending.Clear();
            }
        }

        CustomCommandsException Fail(string code, Token at, string message, string help, string joke) =>
            new(Diagnostic.AtToken(code, text, file, at with { Text = "." + at.Text }, message, "this directive", help, joke));

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.Directive)
            {
                pending.Add(token);
                continue;
            }

            Flush();
            var argument = token.Argument ?? "";
            if (argument.EndsWith(';'))
            {
                throw Fail(ErrorCodes.DirectiveUnknown, token, $".{token.Text} takes no ';'", "leave the ';' out",
                    "Directives are terse. They do not even say goodbye with a ';'.");
            }

            switch (token.Text)
            {
                case "include" or "import":
                    var (path, global) = IncludePath(argument) ?? throw Fail(ErrorCodes.IncludePathRefused, token,
                        $".{token.Text} needs one path, in quotes or in angle brackets, not '{argument}'",
                        $"write .{token.Text} \"file\" for a file beside this one, or .{token.Text} <file> for one of TAZEL_HOME's Include directory",
                        "Tazel looked for a path and found an opinion instead.");
                    Current().Add(new IncludeNode(token, path, global, token.Text == "import"));
                    break;
                case "pragma":
                    if (argument != "once")
                    {
                        throw Fail(ErrorCodes.DirectiveUnknown, token, $"'.pragma {argument}' is not a pragma Tazel knows",
                            ".pragma once is the only pragma", "Tazel has exactly one pragma, and it is used once.");
                    }

                    if (i != 0)
                    {
                        throw Fail(ErrorCodes.DirectiveUnknown, token, ".pragma once must come first in its file",
                            "move it to the top, above everything but comments", "Once is a word that only works at the start.");
                    }

                    once = true;
                    break;
                case "if":
                    if (frames.Count >= MaxDepth)
                    {
                        throw Fail(ErrorCodes.ConditionalUnbalanced, token, $".if blocks nest more than {MaxDepth} deep",
                            "fold the conditions together with && and ||", "Somewhere, deep in the ifs, a condition got lost.");
                    }

                    frames.Push(new Frame(token) { Condition = Condition(token, file, text) });
                    break;
                case "elif" or "else":
                    var frame = frames.Count > 0 ? frames.Peek() : throw Fail(ErrorCodes.ConditionalUnbalanced, token,
                        $".{token.Text} without an .if", "open the block with .if", "An answer, and nobody asked.");
                    if (frame.SawElse)
                    {
                        throw Fail(ErrorCodes.ConditionalUnbalanced, token, $".{token.Text} after .else",
                            ".else is the last branch of its .if", "After else, there is nothing else.");
                    }

                    if (token.Text == "else" && argument.Length > 0)
                    {
                        throw Fail(ErrorCodes.ConditionalUnbalanced, token, ".else takes no condition",
                            "write .elif and the condition, or .else alone", "Else is for everything that is left; it does not choose.");
                    }

                    frame.Branches.Add(new Branch(frame.BranchAt, frame.Condition, frame.Body));
                    frame.BranchAt = token;
                    frame.Condition = token.Text == "elif" ? Condition(token, file, text) : null;
                    frame.Body = [];
                    frame.SawElse = token.Text == "else";
                    break;
                case "end":
                    var closing = frames.Count > 0 ? frames.Pop() : throw Fail(ErrorCodes.ConditionalUnbalanced, token,
                        ".end without an .if", "remove it, or open the block with .if", "Tazel found an ending without a story.");
                    if (argument.Length > 0)
                    {
                        throw Fail(ErrorCodes.ConditionalUnbalanced, token, ".end takes nothing after it", "write .end alone",
                            "The end is the end. Nothing comes after it on the line.");
                    }

                    closing.Branches.Add(new Branch(closing.BranchAt, closing.Condition, closing.Body));
                    Current().Add(new IfNode(closing.At, closing.Branches));
                    break;
                default:
                    var near = Cli.Suggestions.Nearest(token.Text, ["include", "import", "pragma", "if", "elif", "else", "end"]);
                    throw Fail(ErrorCodes.DirectiveUnknown, token, $".{token.Text} is not a directive",
                        near is null ? "the directives are .include, .import, .pragma once, .if, .elif, .else and .end; a name that starts with a dot goes in quotes"
                            : $"did you mean .{near}?",
                        "Tazel only answers to the directives it was introduced to.");
            }
        }

        Flush();
        if (frames.Count > 0)
        {
            throw Fail(ErrorCodes.ConditionalUnbalanced, frames.Peek().At, ".if without its .end", "close the block with .end",
                "This .if is still waiting for the end of the story.");
        }

        return new ConfigurationDocument(root, once);
    }

    // "file", 'file' or `file`, relative to the file that names it; <file>, below the Include directory.
    private static (string Path, bool Global)? IncludePath(string argument)
    {
        if (argument.Length < 3)
        {
            return null;
        }

        var (first, last) = (argument[0], argument[^1]);
        var inner = argument[1..^1];
        if (inner.Length == 0 || inner.IndexOfAny(['"', '\'', '`', '<', '>']) >= 0)
        {
            return null;
        }

        return first switch
        {
            '"' or '\'' or '`' when last == first => (inner, false),
            '<' when last == '>' => (inner, true),
            _ => null,
        };
    }

    private static Condition Condition(Token token, string file, string text)
    {
        try
        {
            var condition = new ConditionReader(token.Argument ?? "").Read();
            if (condition.Type != ConditionType.Boolean)
            {
                throw new ConditionException("the condition is a string, not true or false; compare it with == or !=");
            }

            return condition;
        }
        catch (ConditionException problem)
        {
            throw new CustomCommandsException(Diagnostic.AtToken(ErrorCodes.ConditionInvalid, text, file,
                token with { Text = "." + token.Text }, problem.Message, "in this condition",
                "conditions take true, false, strings, host_system.*, target_system.*, defined(\"NAME\"), env(\"NAME\"), == != ! && || and ( )",
                "Tazel reads conditions. It does not guess them."));
        }
    }
}
