// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Progmasoft.Tazel.Dsl.Common;

namespace Progmasoft.Tazel.Dsl.Configuration;

/// <summary>What a condition or a part of one stands for.</summary>
public enum ConditionType
{
    /// <summary>true or false.</summary>
    Boolean,

    /// <summary>A string.</summary>
    String,
}

/// <summary>A condition, or a part of one, of <c>.if</c> and <c>.elif</c>.</summary>
public abstract record Condition
{
    /// <summary>What it stands for.</summary>
    public abstract ConditionType Type { get; }

    /// <summary>Whether it names <c>target_system</c>, so it holds only once the platform is chosen.</summary>
    public abstract bool UsesTarget { get; }
}

/// <summary><c>true</c>, <c>false</c> or a string in quotes.</summary>
/// <param name="Value">A bool or a string.</param>
public sealed record LiteralCondition(object Value) : Condition
{
    /// <inheritdoc/>
    public override ConditionType Type => Value is bool ? ConditionType.Boolean : ConditionType.String;

    /// <inheritdoc/>
    public override bool UsesTarget => false;
}

/// <summary><c>host_system.os</c> and the like.</summary>
/// <param name="System"><c>host_system</c> or <c>target_system</c>.</param>
/// <param name="Field"><c>os</c>, <c>arch</c>, <c>abi</c> or <c>platform</c>.</param>
public sealed record MemberCondition(string System, string Field) : Condition
{
    /// <inheritdoc/>
    public override ConditionType Type => ConditionType.String;

    /// <inheritdoc/>
    public override bool UsesTarget => System == "target_system";
}

/// <summary><c>defined("NAME")</c> or <c>env("NAME")</c>.</summary>
/// <param name="Function"><c>defined</c> or <c>env</c>.</param>
/// <param name="Name">The name of the environment variable.</param>
public sealed record CallCondition(string Function, string Name) : Condition
{
    /// <inheritdoc/>
    public override ConditionType Type => Function == "defined" ? ConditionType.Boolean : ConditionType.String;

    /// <inheritdoc/>
    public override bool UsesTarget => false;
}

/// <summary><c>!</c> of a condition.</summary>
/// <param name="Operand">The condition.</param>
public sealed record NotCondition(Condition Operand) : Condition
{
    /// <inheritdoc/>
    public override ConditionType Type => ConditionType.Boolean;

    /// <inheritdoc/>
    public override bool UsesTarget => Operand.UsesTarget;
}

/// <summary><c>==</c>, <c>!=</c>, <c>&amp;&amp;</c> or <c>||</c> of two conditions.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Left">Its left side.</param>
/// <param name="Right">Its right side.</param>
public sealed record BinaryCondition(string Operator, Condition Left, Condition Right) : Condition
{
    /// <inheritdoc/>
    public override ConditionType Type => ConditionType.Boolean;

    /// <inheritdoc/>
    public override bool UsesTarget => Left.UsesTarget || Right.UsesTarget;
}

/// <summary>A condition that cannot be read.</summary>
public sealed class ConditionException : Exception
{
    /// <summary>What is wrong.</summary>
    public ConditionException(string message)
        : base(message)
    {
    }

    /// <summary>Nothing to say.</summary>
    public ConditionException()
    {
    }

    /// <summary>What is wrong, and why.</summary>
    public ConditionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>What a condition sees of a system: <c>host_system</c> or <c>target_system</c>.</summary>
/// <param name="Os"><c>linux</c>, <c>windows</c>, <c>macos</c>, <c>freebsd</c>, <c>openbsd</c> or <c>dragonfly</c>.</param>
/// <param name="Arch"><c>amd64</c> (x86-64) or <c>aarch64</c> (ARM64), the same on every system.</param>
/// <param name="Abi"><c>gnu</c>, <c>musl</c>, <c>msvc</c> or <c>default</c>.</param>
/// <param name="Platform">The name of the platform, as <c>--platform</c> takes it, such as <c>linux-amd64</c>.</param>
public sealed record SystemFacts(string Os, string Arch, string Abi, string Platform)
{
    /// <summary>The facts of <paramref name="platform"/>.</summary>
    public static SystemFacts Of(Platform platform) => new(
        platform.GetOS() switch
        {
            OS.Linux => "linux",
            OS.Windows => "windows",
            OS.MacOS => "macos",
            OS.FreeBSD => "freebsd",
            OS.OpenBSD => "openbsd",
            _ => "dragonfly",
        },
        platform.GetArch() == Common.Arch.X64 ? "amd64" : "aarch64",
        platform.GetOS() switch
        {
            OS.Linux => "gnu",
            OS.Windows => "msvc",
            _ => "default",
        },
        platform.Name());

    /// <summary>The value of <paramref name="field"/>.</summary>
    public string this[string field] => field switch
    {
        "os" => Os,
        "arch" => Arch,
        "abi" => Abi,
        _ => Platform,
    };
}

/// <summary>Reads the text of a condition: the lexer and parser of the condition language in one.</summary>
internal sealed class ConditionReader(string text)
{
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "os", "arch", "abi", "platform" };

    private int _position;

    public Condition Read()
    {
        if (text.Length == 0)
        {
            throw new ConditionException("the condition is missing");
        }

        var condition = Or();
        Space();
        if (_position < text.Length)
        {
            // The usual slips of other languages get their own words.
            var rest = text[_position..];
            throw new ConditionException(rest[0] switch
            {
                '=' => "'=' is not an operator of conditions; compare with ==",
                '&' => "'&' is not an operator of conditions; write &&",
                '|' => "'|' is not an operator of conditions; write ||",
                _ => $"'{rest}' cannot follow '{text[.._position].TrimEnd()}'",
            });
        }

        return condition;
    }

    private Condition Or()
    {
        var left = And();
        while (Take("||"))
        {
            left = new BinaryCondition("||", Booleans(left, "||"), Booleans(And(), "||"));
        }

        return left;
    }

    private Condition And()
    {
        var left = Equality();
        while (Take("&&"))
        {
            left = new BinaryCondition("&&", Booleans(left, "&&"), Booleans(Equality(), "&&"));
        }

        return left;
    }

    private Condition Equality()
    {
        var left = Unary();
        while (true)
        {
            var op = Take("==") ? "==" : Take("!=") ? "!=" : null;
            if (op is null)
            {
                return left;
            }

            var right = Unary();
            if (left.Type != right.Type)
            {
                throw new ConditionException($"{op} compares a {Name(left.Type)} with a {Name(right.Type)}");
            }

            left = new BinaryCondition(op, left, right);
        }
    }

    private Condition Unary()
    {
        Space();
        if (_position < text.Length && text[_position] == '!' && !At("!="))
        {
            _position++;
            return new NotCondition(Booleans(Unary(), "!"));
        }

        return Primary();
    }

    private Condition Primary()
    {
        Space();
        if (_position >= text.Length)
        {
            throw new ConditionException("the condition ends where a value should be");
        }

        var letter = text[_position];
        if (letter == '(')
        {
            _position++;
            var inner = Or();
            return Take(")") ? inner : throw new ConditionException("a '(' has no ')'");
        }

        if (letter is '"' or '\'' or '`')
        {
            return new LiteralCondition(Quoted());
        }

        var name = Word();
        return name switch
        {
            "" => throw new ConditionException($"'{letter}' is not an operator of conditions"),
            "true" => new LiteralCondition(true),
            "false" => new LiteralCondition(false),
            "host_system" or "target_system" => Member(name),
            "defined" or "env" => Call(name),
            _ => throw new ConditionException($"'{name}' is not a name conditions know" +
                (Cli.Suggestions.Nearest(name, ["true", "false", "host_system", "target_system", "defined", "env"]) is { } near ? $"; did you mean {near}?" : "")),
        };
    }

    // host_system.os and the like, after its name.
    private MemberCondition Member(string name)
    {
        if (!Take("."))
        {
            throw new ConditionException($"{name} needs a field: {name}.os, .arch, .abi or .platform");
        }

        var field = Word();
        return Fields.Contains(field) ? new MemberCondition(name, field)
            : throw new ConditionException($"{name}.{field} is not a field; the fields are os, arch, abi and platform");
    }

    // defined("NAME") or env("NAME"), after its name.
    private CallCondition Call(string name)
    {
        if (!Take("("))
        {
            throw new ConditionException($"{name} is called: {name}(\"NAME\")");
        }

        Space();
        if (_position >= text.Length || text[_position] is not ('"' or '\'' or '`'))
        {
            throw new ConditionException($"{name} takes the name of a variable in quotes");
        }

        var variable = Quoted();
        return Take(")") ? new CallCondition(name, variable) : throw new ConditionException($"{name}(\"{variable}\" has no ')'");
    }

    private static Condition Booleans(Condition condition, string op) => condition.Type == ConditionType.Boolean
        ? condition
        : throw new ConditionException($"{op} takes true or false, and a string stands beside it; compare the string with == or !=");

    private static string Name(ConditionType type) => type == ConditionType.Boolean ? "boolean" : "string";

    private string Quoted()
    {
        var quote = text[_position++];
        var end = text.IndexOf(quote, _position);
        if (end < 0)
        {
            throw new ConditionException($"a string has no closing {quote}");
        }

        var value = text[_position..end];
        _position = end + 1;
        return value;
    }

    private string Word()
    {
        var begin = _position;
        while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
        {
            _position++;
        }

        return text[begin.._position];
    }

    private bool At(string what) => string.CompareOrdinal(text, _position, what, 0, what.Length) == 0;

    private bool Take(string what)
    {
        Space();
        if (!At(what))
        {
            return false;
        }

        _position += what.Length;
        return true;
    }

    private void Space()
    {
        while (_position < text.Length && char.IsWhiteSpace(text[_position]))
        {
            _position++;
        }
    }
}

/// <summary>Evaluates conditions against what the configuration sees.</summary>
public static class ConditionEvaluator
{
    /// <summary>
    /// The value of <paramref name="condition"/>: true, false, or null when it needs <c>target_system</c> and
    /// <paramref name="target"/> is not chosen yet. <c>&amp;&amp;</c> and <c>||</c> stop at their first side when it
    /// decides, so the second may need nothing that is unknown.
    /// </summary>
    public static bool? Evaluate(
        Condition condition, SystemFacts host, SystemFacts? target, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return new Evaluator(host, target, environment).Value(condition) as bool?;
    }

    private sealed class Evaluator(SystemFacts host, SystemFacts? target, IReadOnlyDictionary<string, string> environment)
    {
        // A bool, a string, or null for unknown.
        public object? Value(Condition condition) => condition switch
        {
            LiteralCondition literal => literal.Value,
            MemberCondition member => member.System == "host_system" ? host[member.Field] : target?[member.Field],
            CallCondition call => call.Function == "defined" ? environment.ContainsKey(call.Name) : environment.GetValueOrDefault(call.Name, ""),
            NotCondition not => Value(not.Operand) is bool truth ? !truth : null,
            BinaryCondition { Operator: "&&" or "||" } logical => Logical(logical),
            BinaryCondition binary => Compare(binary),
            _ => throw new ArgumentException($"no condition {condition.GetType().Name}", nameof(condition)),
        };

        // && and ||: the first side decides when it can; an unknown first side decides only with the second.
        private bool? Logical(BinaryCondition binary)
        {
            var decides = binary.Operator == "||";
            var left = Value(binary.Left) as bool?;
            if (left == decides)
            {
                return decides;
            }

            var right = Value(binary.Right) as bool?;
            return left is null ? (right == decides ? decides : null) : right;
        }

        private bool? Compare(BinaryCondition binary)
        {
            var (left, right) = (Value(binary.Left), Value(binary.Right));
            return left is null || right is null ? null : Equals(left, right) == (binary.Operator == "==");
        }
    }
}
