// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progmasoft.Tazel.Dsl.Common;

/// <summary>
/// A pattern of files below the directory of the project file: <c>*</c> stands for any part of one name,
/// <c>?</c> for one character, and <c>**</c> for any number of directories.
/// </summary>
/// <param name="pattern">The pattern, with <c>/</c> between directories.</param>
/// <param name="exclude">Patterns of files to leave out.</param>
public sealed class Glob(string pattern, IReadOnlyList<string>? exclude = null)
{
    /// <summary>The pattern.</summary>
    public string Pattern { get; } = pattern;

    /// <summary>Patterns of files the glob leaves out.</summary>
    public IReadOnlyList<string> Exclude { get; } = exclude ?? [];

    /// <inheritdoc/>
    public override string ToString() => $"new Glob(\"{Pattern}\")";
}

/// <summary>One path, or a <see cref="Common.Glob"/>; a list of files may mix both.</summary>
public sealed class FilePattern
{
    private FilePattern(string? path, Glob? glob)
    {
        Path = path;
        Glob = glob;
    }

    /// <summary>The path, when this is one path.</summary>
    public string? Path { get; }

    /// <summary>The glob, when this is a pattern.</summary>
    public Glob? Glob { get; }

    /// <summary>One path.</summary>
    public static implicit operator FilePattern(string path) => FromPath(path);

    /// <summary>A pattern.</summary>
    public static implicit operator FilePattern(Glob glob) => FromGlob(glob);

    /// <summary>One path.</summary>
    public static FilePattern FromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FilePattern(path, null);
    }

    /// <summary>A pattern.</summary>
    public static FilePattern FromGlob(Glob glob)
    {
        ArgumentNullException.ThrowIfNull(glob);
        return new FilePattern(null, glob);
    }

    /// <inheritdoc/>
    public override string ToString() => Path is null ? Glob!.ToString() : $"\"{Path}\"";
}

/// <summary>
/// Files of a target: <c>["a.c", "b.c"]</c>, <c>new Glob("src/*.c")</c>, or both in one list.
/// </summary>
[CollectionBuilder(typeof(Files), nameof(Create))]
public sealed class Files : IEnumerable<FilePattern>
{
    private readonly FilePattern[] _items;

    private Files(FilePattern[] items) => _items = items;

    /// <summary>No files.</summary>
    public static Files None { get; } = new([]);

    /// <summary>The patterns, in their order.</summary>
    public IReadOnlyList<FilePattern> Items => _items;

    /// <summary>The files of one glob.</summary>
    public static implicit operator Files(Glob glob) => FromGlob(glob);

    /// <summary>The files of one glob.</summary>
    public static Files FromGlob(Glob glob) => new([FilePattern.FromGlob(glob)]);

    /// <summary>Builds the files of a collection expression.</summary>
    public static Files Create(ReadOnlySpan<FilePattern> items) => new(items.ToArray());

    /// <inheritdoc/>
    public IEnumerator<FilePattern> GetEnumerator() => ((IEnumerable<FilePattern>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An amount of memory.</summary>
/// <param name="Bytes">The amount in bytes.</param>
public readonly record struct ByteSize(long Bytes);

/// <summary>Amounts of memory written as <c>16L.Gibibytes()</c>.</summary>
public static class ByteSizes
{
    /// <summary><paramref name="amount"/> bytes.</summary>
    public static ByteSize Bytes(this long amount) => new(amount);

    /// <summary><paramref name="amount"/> KiB.</summary>
    public static ByteSize Kibibytes(this long amount) => new(checked(amount * 1024));

    /// <summary><paramref name="amount"/> MiB.</summary>
    public static ByteSize Mebibytes(this long amount) => new(checked(amount * 1024 * 1024));

    /// <summary><paramref name="amount"/> GiB.</summary>
    public static ByteSize Gibibytes(this long amount) => new(checked(amount * 1024 * 1024 * 1024));
}
