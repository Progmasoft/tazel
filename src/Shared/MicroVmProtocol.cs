// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.MicroVm;

/// <summary>The kinds of message between Tazel and the agent of its MicroVM.</summary>
internal enum MessageKind : byte
{
    /// <summary>Host to guest: run a command; <see cref="RunRequest"/>.</summary>
    Run = 1,

    /// <summary>Host to guest: stop the command of this connection.</summary>
    Stop = 2,

    /// <summary>Host to guest: write everything out and power off.</summary>
    PowerOff = 3,

    /// <summary>Host to guest: are you there? The agent answers <see cref="Ready"/>.</summary>
    Hello = 4,

    /// <summary>Guest to host: the agent is up; the payload is its version.</summary>
    Ready = 10,

    /// <summary>Guest to host: output of the command; the first byte is 1 for stdout or 2 for stderr.</summary>
    Output = 11,

    /// <summary>Guest to host: the command ended; the payload is its exit status, a signal as 128 + its number.</summary>
    Exit = 12,

    /// <summary>Guest to host: the command could not run; the payload is why.</summary>
    Error = 13,
}

/// <summary>A command for the guest: its arguments, its whole environment and where it runs.</summary>
/// <param name="Arguments">The program and its arguments; the program is found on the PATH of <paramref name="Environment"/>.</param>
/// <param name="Environment">Every variable the command gets, and nothing else.</param>
/// <param name="Directory">The working directory, inside a shared directory.</param>
internal sealed record RunRequest(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string Directory);

/// <summary>
/// The messages of a MicroVM connection: a kind byte, a little-endian 32-bit length, and the payload. Strings are a
/// length and UTF-8. It is the same file in Tazel and in the agent; no JSON library is needed on either side.
/// </summary>
internal static class MicroVmProtocol
{
    /// <summary>The vsock port the agent listens on.</summary>
    public const uint Port = 1024;

    /// <summary>The PATH of commands inside, and the one the agent searches when a command brings none.</summary>
    public const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    /// <summary>A path as <c>tazel.share=</c> writes it on the kernel command line: base64url without padding.</summary>
    public static string EncodePath(string path) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(path)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads a path <see cref="EncodePath"/> wrote.</summary>
    public static string DecodePath(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4)));
    }

    /// <summary>The most a payload may hold; a larger length means the stream is broken.</summary>
    public const int MaxPayload = 16 * 1024 * 1024;

    /// <summary>Writes one message.</summary>
    public static async Task WriteAsync(Stream stream, MessageKind kind, ReadOnlyMemory<byte> payload, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[5];
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancel).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancel).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Reads one message, or null at the end of the stream.</summary>
    public static async Task<(MessageKind Kind, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, cancel).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxPayload)
        {
            throw new InvalidDataException($"a message says it holds {length} bytes");
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancel).ConfigureAwait(false))
        {
            throw new EndOfStreamException("the connection ended inside a message");
        }

        return ((MessageKind)header[0], payload);
    }

    /// <summary>The payload of <see cref="MessageKind.Run"/>.</summary>
    public static byte[] Encode(RunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var buffer = new MemoryStream();
        WriteInt(buffer, request.Arguments.Count);
        foreach (var argument in request.Arguments)
        {
            WriteString(buffer, argument);
        }

        WriteInt(buffer, request.Environment.Count);
        foreach (var (name, value) in request.Environment)
        {
            WriteString(buffer, name);
            WriteString(buffer, value);
        }

        WriteString(buffer, request.Directory);
        return buffer.ToArray();
    }

    /// <summary>Reads the payload of <see cref="MessageKind.Run"/>.</summary>
    public static RunRequest DecodeRun(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var position = 0;
        var count = ReadCount(payload, ref position);
        var arguments = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            arguments.Add(ReadString(payload, ref position));
        }

        var variables = ReadCount(payload, ref position);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < variables; i++)
        {
            var name = ReadString(payload, ref position);
            environment[name] = ReadString(payload, ref position);
        }

        var directory = ReadString(payload, ref position);
        if (position != payload.Length)
        {
            throw new InvalidDataException("a run message has bytes after its directory");
        }

        return new RunRequest(arguments, environment, directory);
    }

    /// <summary>The payload of <see cref="MessageKind.Exit"/>.</summary>
    public static byte[] EncodeStatus(int status)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, status);
        return payload;
    }

    /// <summary>Reads the payload of <see cref="MessageKind.Exit"/>.</summary>
    public static int DecodeStatus(byte[] payload) =>
        payload is { Length: 4 } ? BinaryPrimitives.ReadInt32LittleEndian(payload) : throw new InvalidDataException("an exit message is not 4 bytes");

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static int ReadCount(byte[] payload, ref int position)
    {
        if (payload.Length - position < 4)
        {
            throw new InvalidDataException("a message ends inside a number");
        }

        var value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(position));
        position += 4;
        return value < 0 || value > payload.Length ? throw new InvalidDataException($"a message holds the count {value}") : value;
    }

    private static string ReadString(byte[] payload, ref int position)
    {
        var length = ReadCount(payload, ref position);
        if (payload.Length - position < length)
        {
            throw new InvalidDataException("a message ends inside a string");
        }

        var value = Encoding.UTF8.GetString(payload, position, length);
        position += length;
        return value;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancel).ConfigureAwait(false);
            if (count == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("the connection ended inside a message");
            }

            read += count;
        }

        return true;
    }
}
