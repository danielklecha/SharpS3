namespace SharpS3.Streams;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// A read-only stream wrapper that exposes a specific byte slice [start, start + length)
/// of an underlying seekable Stream, enabling zero-copy range requests.
/// </summary>
public class SubStream : Stream
{
    private readonly Stream _BaseStream;
    private readonly long _Offset;
    private readonly long _Length;
    private long _Position;

    public SubStream(Stream baseStream, long offset, long length)
    {
        _BaseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        if (!baseStream.CanSeek)
            throw new ArgumentException("Base stream must be seekable.", nameof(baseStream));
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));

        _Offset = offset;
        _Length = length;
        _Position = 0;
        _BaseStream.Seek(_Offset, SeekOrigin.Begin);
    }

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => true;

    /// <summary>
    /// Gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the length of the sub-stream in bytes.
    /// </summary>
    public override long Length => _Length;

    /// <summary>
    /// Gets or sets the position within the sub-stream.
    /// </summary>
    public override long Position
    {
        get => _Position;
        set
        {
            if (value < 0 || value > _Length) throw new ArgumentOutOfRangeException(nameof(value));
            _Position = value;
            _BaseStream.Seek(_Offset + _Position, SeekOrigin.Begin);
        }
    }

    /// <inheritdoc />
    public override void Flush() => _BaseStream.Flush();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _Position + offset,
            SeekOrigin.End => _Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = target;
        return _Position;
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_Position >= _Length) return 0;

        int toRead = (int)Math.Min(count, _Length - _Position);
        _BaseStream.Seek(_Offset + _Position, SeekOrigin.Begin);
        int read = _BaseStream.Read(buffer, offset, toRead);
        _Position += read;
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_Position >= _Length) return 0;

        int toRead = (int)Math.Min(count, _Length - _Position);
        _BaseStream.Seek(_Offset + _Position, SeekOrigin.Begin);
        int read = await _BaseStream.ReadAsync(buffer, offset, toRead, cancellationToken).ConfigureAwait(false);
        _Position += read;
        return read;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _BaseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}
