namespace SharpS3.Streams;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;

/// <summary>
/// A read-only stream wrapper that incrementally computes SHA-256 while payload is read
/// and verifies that the computed hash matches the expected 'x-amz-content-sha256' value.
/// </summary>
public class Sha256PayloadVerifyingStream : Stream
{
    private readonly Stream _InnerStream;
    private readonly string _ExpectedSha256;
    private readonly IncrementalHash _Hasher;
    private readonly bool _LeaveOpen;
    private bool _Verified;
    private long _BytesRead;

    /// <summary>
    /// Gets the expected SHA-256 hex string.
    /// </summary>
    public string ExpectedSha256 => _ExpectedSha256;

    /// <summary>
    /// Gets the total bytes read through this verifying stream.
    /// </summary>
    public long BytesRead => _BytesRead;

    /// <summary>
    /// Initializes a new instance of Sha256PayloadVerifyingStream.
    /// </summary>
    public Sha256PayloadVerifyingStream(Stream innerStream, string expectedSha256, bool leaveOpen = false)
    {
        _InnerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _ExpectedSha256 = expectedSha256 ?? throw new ArgumentNullException(nameof(expectedSha256));
        _LeaveOpen = leaveOpen;
        _Hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
        if (count <= 0) return 0;
        int read = _InnerStream.Read(buffer, offset, count);
        if (read > 0)
        {
            _Hasher.AppendData(buffer, offset, read);
            _BytesRead += read;
        }
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0) return 0;
        int read = await _InnerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _Hasher.AppendData(buffer, offset, read);
            _BytesRead += read;
        }
        return read;
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length <= 0) return 0;
        int read = _InnerStream.Read(buffer);
        if (read > 0)
        {
            _Hasher.AppendData(buffer.Slice(0, read));
            _BytesRead += read;
        }
        return read;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length <= 0) return 0;
        int read = await _InnerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _Hasher.AppendData(buffer.Span.Slice(0, read));
            _BytesRead += read;
        }
        return read;
    }
#endif

    /// <summary>
    /// Finalizes hash computation and verifies it matches the expected SHA-256 in constant time.
    /// Throws <see cref="S3Exception"/> with ErrorCode.XAmzContentSHA256Mismatch if validation fails.
    /// </summary>
    public void Verify()
    {
        if (_Verified) return;
        _Verified = true;
        byte[] hashBytes = _Hasher.GetHashAndReset();
        string computedHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        if (!ConstantTimeHelper.FixedTimeEqualsHex(computedHex, _ExpectedSha256))
        {
            throw new S3Exception(new Error(ErrorCode.XAmzContentSHA256Mismatch));
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _Hasher.Dispose();
            if (!_LeaveOpen)
            {
                _InnerStream.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
