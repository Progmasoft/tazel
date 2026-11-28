// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;

namespace Progmasoft.Tazel.Packaging.Spdx;

/// <summary>
/// An SPDX license expression, as <see cref="SpdxExpressionParser"/> reads it: a license, a license with an exception,
/// or two expressions joined by <c>AND</c> or <c>OR</c>. <see cref="object.ToString"/> writes it back with the
/// parentheses its structure needs and no others.
/// </summary>
public abstract record SpdxExpression
{
    private protected SpdxExpression()
    {
    }

    /// <summary>Every license and license reference of the expression, left to right.</summary>
    public IEnumerable<SpdxExpression> Leaves()
    {
        var pending = new Stack<SpdxExpression>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            switch (node)
            {
                case SpdxAnd and:
                    pending.Push(and.Right);
                    pending.Push(and.Left);
                    break;
                case SpdxOr or:
                    pending.Push(or.Right);
                    pending.Push(or.Left);
                    break;
                case SpdxWith with:
                    yield return with.License;
                    break;
                default:
                    yield return node;
                    break;
            }
        }
    }

    /// <summary>How tightly the expression binds: OR 1, AND 2, everything else 3.</summary>
    private protected abstract int Precedence { get; }

    /// <summary>The expression, in parentheses when it binds more loosely than its place needs.</summary>
    private protected static string Within(SpdxExpression part, int place)
    {
        ArgumentNullException.ThrowIfNull(part);
        var text = part.ToString();
        return part.Precedence < place ? $"({text})" : text;
    }
}

/// <summary>A license of the SPDX License List, such as <c>Apache-2.0</c>, or that version or later with <c>+</c>.</summary>
/// <param name="Id">The identifier as written.</param>
/// <param name="OrLater">Whether <c>+</c> follows it.</param>
public sealed record SpdxLicense(string Id, bool OrLater) : SpdxExpression
{
    private protected override int Precedence => 3;

    /// <inheritdoc />
    public override string ToString() => OrLater ? Id + "+" : Id;
}

/// <summary>A license of one's own: <c>LicenseRef-name</c>, or <c>DocumentRef-document:LicenseRef-name</c>.</summary>
/// <param name="Document">The <c>DocumentRef-</c> part, or null.</param>
/// <param name="License">The <c>LicenseRef-</c> part.</param>
public sealed record SpdxLicenseReference(string? Document, string License) : SpdxExpression
{
    private protected override int Precedence => 3;

    /// <inheritdoc />
    public override string ToString() => Document is null ? License : $"{Document}:{License}";
}

/// <summary>A license with an exception, such as <c>GPL-2.0-only WITH Classpath-exception-2.0</c>.</summary>
/// <param name="License">The license: an <see cref="SpdxLicense"/> or an <see cref="SpdxLicenseReference"/>.</param>
/// <param name="Exception">The identifier of the exception.</param>
public sealed record SpdxWith(SpdxExpression License, string Exception) : SpdxExpression
{
    private protected override int Precedence => 3;

    /// <inheritdoc />
    public override string ToString() => $"{License} WITH {Exception}";
}

/// <summary>Both expressions apply.</summary>
/// <param name="Left">The first.</param>
/// <param name="Right">The second.</param>
public sealed record SpdxAnd(SpdxExpression Left, SpdxExpression Right) : SpdxExpression
{
    private protected override int Precedence => 2;

    /// <inheritdoc />
    public override string ToString() => $"{Within(Left, 2)} AND {Within(Right, 3)}";
}

/// <summary>Either expression applies, as the user chooses.</summary>
/// <param name="Left">The first.</param>
/// <param name="Right">The second.</param>
public sealed record SpdxOr(SpdxExpression Left, SpdxExpression Right) : SpdxExpression
{
    private protected override int Precedence => 1;

    /// <inheritdoc />
    public override string ToString() => $"{Within(Left, 1)} OR {Within(Right, 2)}";
}
