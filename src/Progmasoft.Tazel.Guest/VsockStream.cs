// SPDX-FileCopyrightText: 2026 Progmasoft <support@progmasoft.com>
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Progmasoft.Tazel.Guest;

/// <summary>A connected vsock socket as a stream, over read and write; .NET has no vsock address family.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class VsockStream(int fd) : Stream
{
    private readonly Lock _writing = new();
    private int _fd = fd;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        fixed (byte* start = &buffer[offset])
        {
            while (true)
            {
                var read = Libc.Read(_fd, start, (nuint)count);
                if (read >= 0)
                {
                    return (int)read;
                }

                if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 4)
                {
                    throw new IOException($"reading the connection failed: {Libc.LastError()}");
                }
            }
        }
    }

    // A blocking read on a thread of its own, so the agent serves other connections meanwhile.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.Factory.StartNew(() => Read(buffer, offset, count), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var array = new byte[buffer.Length];
        return new ValueTask<int>(ReadAsync(array, 0, array.Length, cancellationToken).ContinueWith(
            task =>
            {
                array.AsSpan(0, task.Result).CopyTo(buffer.Span);
                return task.Result;
            },
            cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    public override unsafe void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        lock (_writing)
        {
            fixed (byte* start = buffer)
            {
                var done = 0;
                while (done < count)
                {
                    var written = Libc.Write(_fd, start + offset + done, (nuint)(count - done));
                    if (written < 0)
                    {
                        if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 4)
                        {
                            continue;
                        }

                        throw new IOException($"writing the connection failed: {Libc.LastError()}");
                    }

                    done += (int)written;
                }
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var array = buffer.ToArray();
        Write(array, 0, array.Length);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        var fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            Libc.Close(fd);
        }

        base.Dispose(disposing);
    }
}
