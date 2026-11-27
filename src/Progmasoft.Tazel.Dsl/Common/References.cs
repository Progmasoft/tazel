// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progmasoft.Tazel.Dsl.Common;

/// <summary>Which targets may depend on a target.</summary>
public enum Visibility
{
    /// <summary>Only the targets of the same project file.</summary>
    Private,

    /// <summary>Every package of the module.</summary>
    Module,

    /// <summary>Every package of the module and the modules that depend on it.</summary>
    Public,
}

/// <summary>How much a test may take; it sets the default timeout.</summary>
public enum TestSize
{
    /// <summary>One minute.</summary>
    Small,

    /// <summary>Five minutes.</summary>
    Medium,

    /// <summary>Fifteen minutes.</summary>
    Large,

    /// <summary>Sixty minutes.</summary>
    Enormous,
}

/// <summary>A target a project file declared. <c>Depends</c> takes its label, not this value.</summary>
public sealed class Target
{
    internal Target(string label) => Label = label;

    /// <summary>The label of the target, such as <c>//core:math</c>.</summary>
    public string Label { get; }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>A target that another depends on, by its label.</summary>
public sealed class TargetReference
{
    private TargetReference(string label) => Label = label;

    /// <summary>The label as written, such as <c>//:math</c>, <c>:math</c> or <c>@dep//:x</c>.</summary>
    public string Label { get; }

    /// <summary>A label.</summary>
    public static implicit operator TargetReference(string label) => FromLabel(label);

    /// <summary>A label.</summary>
    public static TargetReference FromLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return new TargetReference(label);
    }

    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>The targets a target depends on, by their labels: <c>["//:math", ":util", "@dep//:x"]</c>.</summary>
[CollectionBuilder(typeof(TargetReferences), nameof(Create))]
public sealed class TargetReferences : IEnumerable<TargetReference>
{
    private readonly TargetReference[] _items;

    private TargetReferences(TargetReference[] items) => _items = items;

    /// <summary>No targets.</summary>
    public static TargetReferences None { get; } = new([]);

    /// <summary>The references, in their order.</summary>
    public IReadOnlyList<TargetReference> Items => _items;

    /// <summary>Builds the references of a collection expression.</summary>
    public static TargetReferences Create(ReadOnlySpan<TargetReference> items) => new(items.ToArray());

    /// <inheritdoc/>
    public IEnumerator<TargetReference> GetEnumerator() =>
        ((IEnumerable<TargetReference>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
