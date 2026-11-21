// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Progmasoft.Tazel.Cache;

/// <summary>A file of a cached result: where it goes, the SHA-256 and size of its bytes, and whether it runs.</summary>
internal sealed record OutputFile(string Path, string Hash, long Size, bool IsExecutable);

/// <summary>
/// The part of the Remote Execution API's <c>ActionResult</c> that Tazel writes to and reads from the action cache of a
/// remote cache: its output files (field 2) and its exit code (field 4). The cache keeps it as the protocol buffer the
/// API defines, so a cache that checks what it is given, such as bazel-remote, takes it.
/// </summary>
internal static class ActionResultMessage
{
    private const int WireVarint = 0;
    private const int WireLength = 2;

    /// <summary>The message of <paramref name="files"/>, with exit code 0.</summary>
    public static byte[] Encode(IEnumerable<OutputFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        using var message = new MemoryStream();
        foreach (var file in files)
        {
            using var digest = new MemoryStream();
            WriteString(digest, 1, file.Hash);
            WriteVarintField(digest, 2, (ulong)file.Size);

            using var output = new MemoryStream();
            WriteString(output, 1, file.Path);
            WriteBytes(output, 2, digest.ToArray());
            if (file.IsExecutable)
            {
                WriteVarintField(output, 4, 1);
            }

            WriteBytes(message, 2, output.ToArray());
        }

        return message.ToArray();
    }

    /// <summary>The output files of <paramref name="bytes"/>; null when it is not a message Tazel can use.</summary>
    public static List<OutputFile>? Decode(ReadOnlySpan<byte> bytes)
    {
        if (Fields(bytes) is not { } fields)
        {
            return null;
        }

        var files = new List<OutputFile>();
        foreach (var field in fields)
        {
            // A result that did not exit with 0 is not one Tazel caches.
            if (field is { Number: 4, Wire: WireVarint } && field.Value != 0)
            {
                return null;
            }

            if (field is { Number: 2, Wire: WireLength })
            {
                if (DecodeOutput(field.Body!) is not { } file)
                {
                    return null;
                }

                files.Add(file);
            }
        }

        return files;
    }

    private static OutputFile? DecodeOutput(byte[] bytes)
    {
        string? path = null, hash = null;
        long size = -1;
        var executable = false;
        if (Fields(bytes) is not { } fields)
        {
            return null;
        }

        // Fields Tazel does not read are passed over.
        foreach (var field in fields)
        {
            if (field is { Number: 1, Wire: WireLength })
            {
                path = Encoding.UTF8.GetString(field.Body!);
            }
            else if (field is { Number: 2, Wire: WireLength })
            {
                (hash, size) = DecodeDigest(field.Body!);
            }
            else if (field is { Number: 4, Wire: WireVarint })
            {
                executable = field.Value != 0;
            }
        }

        return path is null || hash is null || size < 0 ? null : new OutputFile(path, hash, size, executable);
    }

    /// <summary>The hash and size of a <c>Digest</c>; a null hash when it is not one.</summary>
    private static (string? Hash, long Size) DecodeDigest(byte[] bytes)
    {
        string? hash = null;
        long size = 0;
        foreach (var field in Fields(bytes) ?? [])
        {
            if (field is { Number: 1, Wire: WireLength })
            {
                hash = Encoding.ASCII.GetString(field.Body!);
            }
            else if (field is { Number: 2, Wire: WireVarint, Value: <= long.MaxValue })
            {
                size = (long)field.Value;
            }
        }

        return (hash is { Length: 64 } ? hash : null, size);
    }

    /// <summary>One field of a message: its number, its wire type, and its value or its bytes.</summary>
    private sealed record Field(int Number, int Wire, ulong Value, byte[]? Body);

    /// <summary>The fields of a message in order; null when it runs past its end or has a wire type Tazel does not read.</summary>
    private static List<Field>? Fields(ReadOnlySpan<byte> bytes)
    {
        var fields = new List<Field>();
        var reader = new Reader(bytes);
        while (!reader.AtEnd)
        {
            if (!reader.TryTag(out var number, out var wire))
            {
                return null;
            }

            if (wire == WireLength && reader.TryLength(out var body))
            {
                fields.Add(new Field(number, wire, 0, body.ToArray()));
            }
            else if (wire == WireVarint && reader.TryVarint(out var value))
            {
                fields.Add(new Field(number, wire, value, null));
            }
            else if (wire is WireLength or WireVarint || !reader.TrySkip(wire))
            {
                return null;
            }
        }

        return fields;
    }

    private static void WriteTag(Stream stream, int field, int wire) => WriteVarint(stream, (ulong)((field << 3) | wire));

    private static void WriteVarintField(Stream stream, int field, ulong value)
    {
        WriteTag(stream, field, WireVarint);
        WriteVarint(stream, value);
    }

    private static void WriteString(Stream stream, int field, string text) => WriteBytes(stream, field, Encoding.UTF8.GetBytes(text));

    private static void WriteBytes(Stream stream, int field, byte[] bytes)
    {
        WriteTag(stream, field, WireLength);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    /// <summary>Reads protocol buffer fields, refusing anything that runs past the end.</summary>
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool AtEnd => _at >= _bytes.Length;

        public bool TryTag(out int field, out int wire)
        {
            field = wire = 0;
            if (!TryVarint(out var tag) || tag >> 3 is 0 or > int.MaxValue)
            {
                return false;
            }

            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            return true;
        }

        public bool TryVarint(out ulong value)
        {
            value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_at >= _bytes.Length)
                {
                    return false;
                }

                var next = _bytes[_at++];
                value |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryLength(out ReadOnlySpan<byte> body)
        {
            body = default;
            if (!TryVarint(out var length) || length > (ulong)(_bytes.Length - _at))
            {
                return false;
            }

            body = _bytes.Slice(_at, (int)length);
            _at += (int)length;
            return true;
        }

        public bool TrySkip(int wire)
        {
            switch (wire)
            {
                case WireVarint:
                    return TryVarint(out _);
                case WireLength:
                    return TryLength(out _);
                case 1:
                    return Advance(8);
                case 5:
                    return Advance(4);
                default:
                    return false;
            }
        }

        private bool Advance(int count)
        {
            if (count > _bytes.Length - _at)
            {
                return false;
            }

            _at += count;
            return true;
        }
    }
}
