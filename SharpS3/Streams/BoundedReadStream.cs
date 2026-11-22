namespace SharpS3.Streams;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Exceptions;
using SharpS3.Models;

/// <summary>
/// A read-only stream wrapper that enforces an upper bound on the number of bytes that can be read.
/// If reading exceeds the specified maximum allowed bytes, throws an S3Exception with ErrorCode.EntityTooLarge.
/// </summary>
public class BoundedReadStream : Stream
{
    private readonly Stream _InnerStream;
    private readonly long _MaxBytes;
    private readonly bool _LeaveOpen;
    private long _BytesRead;

    /// <summary>
    /// Gets the maximum allowed bytes that can be read from this stream.
    /// </summary>
    public long MaxBytes => _MaxBytes;

    /// <summary>
    /// Gets the total number of bytes read from this stream so far.
    /// </summary>
    public long BytesRead => _BytesRead;

    /// <summary>
    /// Initializes a new instance of BoundedReadStream.
    /// </summary>
    /// <param name="innerStream">The underlying stream to wrap.</param>
    /// <param name="maxBytes">The maximum allowed bytes.</param>
    /// <param name="leaveOpen">Whether to leave the inner stream open when disposing.</param>
    public BoundedReadStream(Stream innerStream, long maxBytes, bool leaveOpen = false)
    {
        _InnerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _MaxBytes = maxBytes;
        _LeaveOpen = leaveOpen;

        if (_InnerStream.CanSeek && (_InnerStream.Length - _InnerStream.Position) > _MaxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }
    }

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => _InnerStream.CanRead;

    /// <summary>
    /// Gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the length in bytes of the stream if the inner stream supports seeking.
    /// </summary>
    public override long Length => _InnerStream.CanSeek ? _InnerStream.Length : throw new NotSupportedException();

    /// <summary>
    /// Gets or sets the position within the current stream.
    /// </summary>
    public override long Position
    {
        get => _BytesRead;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _InnerStream.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _InnerStream.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
            return 0;

        if (_BytesRead >= _MaxBytes)
        {
            int check = _InnerStream.Read(buffer, offset, 1);
            if (check > 0)
            {
                _BytesRead += check;
                throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
            }
            return 0;
        }

        long remaining = _MaxBytes - _BytesRead;
        int toRead = (remaining >= count) ? count : (int)(remaining + 1);
        int read = _InnerStream.Read(buffer, offset, toRead);
        if (read <= 0) return 0;

        _BytesRead += read;
        if (_BytesRead > _MaxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return 0;

        if (_BytesRead >= _MaxBytes)
        {
            int check = await _InnerStream.ReadAsync(buffer, offset, 1, cancellationToken).ConfigureAwait(false);
            if (check > 0)
            {
                _BytesRead += check;
                throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
            }
            return 0;
        }

        long remaining = _MaxBytes - _BytesRead;
        int toRead = (remaining >= count) ? count : (int)(remaining + 1);
        int read = await _InnerStream.ReadAsync(buffer, offset, toRead, cancellationToken).ConfigureAwait(false);
        if (read <= 0)
            return 0;

        _BytesRead += read;
        if (_BytesRead > _MaxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        return read;
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length <= 0) return 0;

        if (_BytesRead >= _MaxBytes)
        {
            int check = _InnerStream.Read(buffer.Slice(0, 1));
            if (check > 0)
            {
                _BytesRead += check;
                throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
            }
            return 0;
        }

        long remaining = _MaxBytes - _BytesRead;
        int toRead = (remaining >= buffer.Length) ? buffer.Length : (int)(remaining + 1);
        int read = _InnerStream.Read(buffer.Slice(0, toRead));
        if (read <= 0) return 0;

        _BytesRead += read;
        if (_BytesRead > _MaxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length <= 0) return 0;

        if (_BytesRead >= _MaxBytes)
        {
            int check = await _InnerStream.ReadAsync(buffer.Slice(0, 1), cancellationToken).ConfigureAwait(false);
            if (check > 0)
            {
                _BytesRead += check;
                throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
            }
            return 0;
        }

        long remaining = _MaxBytes - _BytesRead;
        int toRead = (remaining >= buffer.Length) ? buffer.Length : (int)(remaining + 1);
        int read = await _InnerStream.ReadAsync(buffer.Slice(0, toRead), cancellationToken).ConfigureAwait(false);
        if (read <= 0) return 0;

        _BytesRead += read;
        if (_BytesRead > _MaxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        return read;
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_LeaveOpen)
        {
            await _InnerStream.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
#endif

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_LeaveOpen)
        {
            _InnerStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
