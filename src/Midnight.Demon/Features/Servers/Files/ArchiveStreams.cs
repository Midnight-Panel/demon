using System.Security.Cryptography;

namespace Midnight.Demon.Features.Servers.Files;

/// <summary>Thrown when an archive operation would write more than allowed (disk limit), shown as 413</summary>
public sealed class SizeLimitExceededException(string message) : IOException(message);

/// <summary>
/// Reads exactly <see cref="Length"/> bytes of a file that may change while it is read (log files of a running server):
/// data appended later is cut off, a file that shrank is padded with zeros. Tar headers are written with the length
/// before the data, so the data must match it exactly.
/// </summary>
public sealed class FixedLengthReadStream(Stream inner, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    // TarWriter only takes the length of seekable data streams, seeking itself is not supported
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value != _position)
                throw new NotSupportedException();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var remaining = length - _position;

        if (remaining <= 0)
            return 0;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        var read = inner.Read(buffer);

        if (read == 0)
        {
            // the file got shorter: pad
            buffer.Clear();
            read = buffer.Length;
        }

        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var remaining = length - _position;

        if (remaining <= 0)
            return 0;

        if (buffer.Length > remaining)
            buffer = buffer[..(int)remaining];

        var read = await inner.ReadAsync(buffer, cancellationToken);

        if (read == 0)
        {
            buffer.Span.Clear();
            read = buffer.Length;
        }

        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };

        Position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>Write-only stream that counts (and optionally hashes) what passes through and enforces a size limit</summary>
public sealed class CountingWriteStream(Stream inner, long maxBytes = long.MaxValue, IncrementalHash? hash = null, bool leaveOpen = false) : Stream
{
    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Count(buffer);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Count(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private void Count(ReadOnlySpan<byte> buffer)
    {
        if (BytesWritten + buffer.Length > maxBytes)
            throw new SizeLimitExceededException("Not enough disk space left for the archive");

        BytesWritten += buffer.Length;
        hash?.AppendData(buffer);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
            inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
            await inner.DisposeAsync();

        await base.DisposeAsync();
    }
}
