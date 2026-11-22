namespace SharpS3.Streams;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;

/// <summary>
/// Stream wrapper that decodes AWS-chunked streaming payload (STREAMING-AWS4-HMAC-SHA256-PAYLOAD).
/// Validates chunk HMAC signatures on the fly when an AwsChunkSigner is attached.
/// Protocol format: &lt;hex-size&gt;[;chunk-signature=...] \r\n &lt;data&gt; \r\n ... 0[;chunk-signature=...] \r\n \r\n
/// </summary>
public class AwsChunkedDecodingStream : Stream
{
    private readonly Stream _Inner;
    private AwsChunkSigner? _Signer;
    private byte[]? _CurrentChunkBuffer;
    private int _CurrentChunkOffset;
    private int _CurrentChunkAvailable;
    private long _RemainingInUnbufferedChunk;
    private bool _EndOfStream;

    /// <summary>
    /// Collected trailer headers, if any.
    /// </summary>
    public SortedDictionary<string, string> Trailers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default maximum chunk size allowed to be buffered into memory (64 MB).
    /// </summary>
    public const long DefaultMaxChunkSize = 64 * 1024 * 1024;

    /// <summary>
    /// Default maximum length for a chunk header or trailer line (8 KB).
    /// </summary>
    public const int DefaultMaxLineLength = 8192;

    /// <summary>
    /// Default maximum number of trailer headers permitted.
    /// </summary>
    public const int DefaultMaxTrailersCount = 100;

    private long _maxChunkSize = DefaultMaxChunkSize;
    private int _maxLineLength = DefaultMaxLineLength;
    private int _maxTrailersCount = DefaultMaxTrailersCount;

    /// <summary>
    /// Maximum chunk size allowed to be buffered into memory.
    /// </summary>
    public long MaxChunkSize
    {
        get => _maxChunkSize;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxChunkSize cannot be negative.");
            _maxChunkSize = value;
        }
    }

    /// <summary>
    /// Maximum character length for a chunk header or trailer line.
    /// </summary>
    public int MaxLineLength
    {
        get => _maxLineLength;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxLineLength must be positive.");
            _maxLineLength = value;
        }
    }

    /// <summary>
    /// Maximum number of trailer headers permitted.
    /// </summary>
    public int MaxTrailersCount
    {
        get => _maxTrailersCount;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), "MaxTrailersCount cannot be negative.");
            _maxTrailersCount = value;
        }
    }

    /// <summary>
    /// Attached chunk signer for SigV4 validation.
    /// </summary>
    public AwsChunkSigner? Signer => _Signer;

    /// <summary>
    /// Initializes a new instance wrapping an inner stream.
    /// </summary>
    public AwsChunkedDecodingStream(Stream inner, AwsChunkSigner? signer = null)
    {
        _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _Signer = signer;
    }

    /// <summary>
    /// Attaches or updates the SigV4 chunk signer.
    /// </summary>
    public void AttachSigner(AwsChunkSigner signer)
    {
        _Signer = signer ?? throw new ArgumentNullException(nameof(signer));
    }

    /// <summary>
    /// Gets a value indicating whether the current stream supports reading.
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Gets a value indicating whether the current stream supports seeking.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets a value indicating whether the current stream supports writing.
    /// </summary>
    public override bool CanWrite => false;

    /// <summary>
    /// Gets the length in bytes of the stream. Not supported for chunked decoding streams.
    /// </summary>
    public override long Length => throw new NotSupportedException();

    /// <summary>
    /// Gets or sets the position within the current stream. Not supported for chunked decoding streams.
    /// </summary>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _Inner.Flush();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_EndOfStream)
            return 0;
        if (count <= 0)
            return 0;

        // Mode A: With SigV4 verification - read from buffered verified chunk
        if (_Signer != null)
        {
            while (_CurrentChunkAvailable == 0)
            {
                if (_EndOfStream) break;
                await ReadAndVerifyNextChunkAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_EndOfStream)
                return 0;

            int toCopy = Math.Min(count, _CurrentChunkAvailable);
            Buffer.BlockCopy(_CurrentChunkBuffer!, _CurrentChunkOffset, buffer, offset, toCopy);
            _CurrentChunkOffset += toCopy;
            _CurrentChunkAvailable -= toCopy;

            if (_CurrentChunkAvailable == 0)
            {
                _CurrentChunkBuffer = null;
            }

            return toCopy;
        }

        // Mode B: Fast unbuffered streaming without signature verification
        while (_RemainingInUnbufferedChunk == 0)
        {
            string headerLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
            {
                _EndOfStream = true;
                return 0;
            }

            long chunkSize = ParseChunkSize(headerLine);

            if (chunkSize == 0)
            {
                await ReadTrailersAsync(cancellationToken).ConfigureAwait(false);
                _EndOfStream = true;
                return 0;
            }

            _RemainingInUnbufferedChunk = chunkSize;
        }

        int toRead = (int)Math.Min(count, _RemainingInUnbufferedChunk);
        int bytesRead = await _Inner.ReadAsync(buffer, offset, toRead, cancellationToken).ConfigureAwait(false);
        if (bytesRead <= 0)
        {
            _EndOfStream = true;
            return 0;
        }

        _RemainingInUnbufferedChunk -= bytesRead;
        if (_RemainingInUnbufferedChunk == 0)
        {
            await ConsumeCrLfAsync(cancellationToken).ConfigureAwait(false);
        }

        return bytesRead;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_EndOfStream || count <= 0)
            return 0;

        if (_Signer != null)
        {
            while (_CurrentChunkAvailable == 0)
            {
                if (_EndOfStream) break;
                ReadAndVerifyNextChunkSync();
            }

            if (_EndOfStream)
                return 0;

            int toCopy = Math.Min(count, _CurrentChunkAvailable);
            Buffer.BlockCopy(_CurrentChunkBuffer!, _CurrentChunkOffset, buffer, offset, toCopy);
            _CurrentChunkOffset += toCopy;
            _CurrentChunkAvailable -= toCopy;

            if (_CurrentChunkAvailable == 0)
            {
                _CurrentChunkBuffer = null;
            }

            return toCopy;
        }

        while (_RemainingInUnbufferedChunk == 0)
        {
            string headerLine = ReadLineSync();
            if (string.IsNullOrEmpty(headerLine))
            {
                _EndOfStream = true;
                return 0;
            }

            long chunkSize = ParseChunkSize(headerLine);

            if (chunkSize == 0)
            {
                ReadTrailersSync();
                _EndOfStream = true;
                return 0;
            }

            _RemainingInUnbufferedChunk = chunkSize;
        }

        int toRead = (int)Math.Min(count, _RemainingInUnbufferedChunk);
        int bytesRead = _Inner.Read(buffer, offset, toRead);
        if (bytesRead <= 0)
        {
            _EndOfStream = true;
            return 0;
        }

        _RemainingInUnbufferedChunk -= bytesRead;
        if (_RemainingInUnbufferedChunk == 0)
        {
            ConsumeCrLfSync();
        }

        return bytesRead;
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        if (_EndOfStream || buffer.Length <= 0)
            return 0;

        if (_Signer != null)
        {
            while (_CurrentChunkAvailable == 0)
            {
                if (_EndOfStream) break;
                ReadAndVerifyNextChunkSync();
            }

            if (_EndOfStream)
                return 0;

            int toCopy = Math.Min(buffer.Length, _CurrentChunkAvailable);
            _CurrentChunkBuffer.AsSpan(_CurrentChunkOffset, toCopy).CopyTo(buffer);
            _CurrentChunkOffset += toCopy;
            _CurrentChunkAvailable -= toCopy;

            if (_CurrentChunkAvailable == 0)
            {
                _CurrentChunkBuffer = null;
            }

            return toCopy;
        }

        while (_RemainingInUnbufferedChunk == 0)
        {
            string headerLine = ReadLineSync();
            if (string.IsNullOrEmpty(headerLine))
            {
                _EndOfStream = true;
                return 0;
            }

            long chunkSize = ParseChunkSize(headerLine);

            if (chunkSize == 0)
            {
                ReadTrailersSync();
                _EndOfStream = true;
                return 0;
            }

            _RemainingInUnbufferedChunk = chunkSize;
        }

        int toRead = (int)Math.Min(buffer.Length, _RemainingInUnbufferedChunk);
        int bytesRead = _Inner.Read(buffer.Slice(0, toRead));
        if (bytesRead <= 0)
        {
            _EndOfStream = true;
            return 0;
        }

        _RemainingInUnbufferedChunk -= bytesRead;
        if (_RemainingInUnbufferedChunk == 0)
        {
            ConsumeCrLfSync();
        }

        return bytesRead;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_EndOfStream || buffer.Length <= 0)
            return 0;

        if (_Signer != null)
        {
            while (_CurrentChunkAvailable == 0)
            {
                if (_EndOfStream) break;
                await ReadAndVerifyNextChunkAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_EndOfStream)
                return 0;

            int toCopy = Math.Min(buffer.Length, _CurrentChunkAvailable);
            _CurrentChunkBuffer.AsMemory(_CurrentChunkOffset, toCopy).CopyTo(buffer);
            _CurrentChunkOffset += toCopy;
            _CurrentChunkAvailable -= toCopy;

            if (_CurrentChunkAvailable == 0)
            {
                _CurrentChunkBuffer = null;
            }

            return toCopy;
        }

        while (_RemainingInUnbufferedChunk == 0)
        {
            string headerLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(headerLine))
            {
                _EndOfStream = true;
                return 0;
            }

            long chunkSize = ParseChunkSize(headerLine);

            if (chunkSize == 0)
            {
                await ReadTrailersAsync(cancellationToken).ConfigureAwait(false);
                _EndOfStream = true;
                return 0;
            }

            _RemainingInUnbufferedChunk = chunkSize;
        }

        int toRead = (int)Math.Min(buffer.Length, _RemainingInUnbufferedChunk);
        int bytesRead = await _Inner.ReadAsync(buffer.Slice(0, toRead), cancellationToken).ConfigureAwait(false);
        if (bytesRead <= 0)
        {
            _EndOfStream = true;
            return 0;
        }

        _RemainingInUnbufferedChunk -= bytesRead;
        if (_RemainingInUnbufferedChunk == 0)
        {
            await ConsumeCrLfAsync(cancellationToken).ConfigureAwait(false);
        }

        return bytesRead;
    }
#endif

    private async Task ReadAndVerifyNextChunkAsync(CancellationToken cancellationToken)
    {
        string headerLine = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(headerLine))
        {
            _EndOfStream = true;
            return;
        }

        long chunkSize = ParseChunkSize(headerLine);
        string? signature = ExtractSignature(headerLine);

        if (chunkSize == 0)
        {
            // Validate final 0-length chunk
            if (!string.IsNullOrEmpty(signature))
            {
                bool validFinal = _Signer!.ValidateChunk(Array.Empty<byte>(), signature);
                if (!validFinal)
                {
                    throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The chunk signature does not match the computed signature for final chunk."));
                }
            }

            // Consume trailers
            string? trailerSig = await ReadTrailersAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(trailerSig))
            {
                bool validTrailer = _Signer!.ValidateTrailer(Trailers, trailerSig);
                if (!validTrailer)
                {
                    throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The trailer signature does not match the computed trailer signature."));
                }
            }

            _EndOfStream = true;
            return;
        }

#if NET6_0_OR_GREATER
        if (chunkSize > Array.MaxLength)
#else
        if (chunkSize > 0x7FFFFFC7) // Array.MaxLength
#endif
            throw new InvalidDataException($"Chunk size {chunkSize} exceeds supported in-memory buffer limit.");

        // Buffer the chunk data for verification
        byte[] chunkData = new byte[(int)chunkSize];
        int totalRead = 0;
        while (totalRead < chunkSize)
        {
            int n = await _Inner.ReadAsync(chunkData, totalRead, (int)chunkSize - totalRead, cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new EndOfStreamException($"Premature end of stream while reading chunk data (expected {chunkSize}, got {totalRead}).");
            }
            totalRead += n;
        }

        // Consume trailing CRLF
        await ConsumeCrLfAsync(cancellationToken).ConfigureAwait(false);

        // Verify chunk signature
        if (!string.IsNullOrEmpty(signature))
        {
            bool valid = _Signer!.ValidateChunk(chunkData, signature);
            if (!valid)
            {
                throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The chunk signature does not match the computed signature."));
            }
        }

        _CurrentChunkBuffer = chunkData;
        _CurrentChunkOffset = 0;
        _CurrentChunkAvailable = totalRead;
    }

    private long ParseChunkSize(string headerLine)
    {
        int semi = headerLine.IndexOf(';');
        string hexSize = semi >= 0 ? headerLine.Substring(0, semi).Trim() : headerLine.Trim();

        if (!long.TryParse(hexSize, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long chunkSize) || chunkSize < 0)
        {
            throw new InvalidDataException($"Invalid aws-chunked size header: '{headerLine}'");
        }

        if (chunkSize > MaxChunkSize)
        {
            throw new InvalidDataException($"Chunk size {chunkSize} exceeds maximum allowed limit of {MaxChunkSize} bytes.");
        }

        return chunkSize;
    }

    private static string? ExtractSignature(string headerLine)
    {
        int idx = headerLine.IndexOf("chunk-signature=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        int start = idx + "chunk-signature=".Length;
        int end = headerLine.IndexOf(';', start);
        return (end >= 0 ? headerLine.Substring(start, end - start) : headerLine.Substring(start)).Trim();
    }

    private async Task<string?> ReadTrailersAsync(CancellationToken cancellationToken)
    {
        string? trailerSig = null;
        int trailerCount = 0;
        while (true)
        {
            string line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line)) break;

            trailerCount++;
            if (trailerCount > MaxTrailersCount)
            {
                throw new InvalidDataException($"Number of trailers exceeds maximum limit of {MaxTrailersCount}.");
            }

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();
                if (name.Equals("x-amz-trailer-signature", StringComparison.OrdinalIgnoreCase))
                {
                    trailerSig = val;
                }
                else
                {
                    Trailers[name] = val;
                }
            }
        }
        return trailerSig;
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        byte[] b = new byte[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = await _Inner.ReadAsync(b, 0, 1, cancellationToken).ConfigureAwait(false);
            if (n <= 0) break;

            if (b[0] == '\r')
            {
                int next = await _Inner.ReadAsync(b, 0, 1, cancellationToken).ConfigureAwait(false);
                if (next > 0 && b[0] == '\n') break;
                if (next > 0)
                {
                    sb.Append((char)b[0]);
                    if (sb.Length > MaxLineLength)
                        throw new InvalidDataException($"Header line exceeds maximum length of {MaxLineLength} characters.");
                }
                break;
            }
            if (b[0] == '\n')
                break;

            sb.Append((char)b[0]);
            if (sb.Length > MaxLineLength)
            {
                throw new InvalidDataException($"Header line exceeds maximum length of {MaxLineLength} characters.");
            }
        }

        return sb.ToString();
    }

    private async Task ConsumeCrLfAsync(CancellationToken cancellationToken)
    {
        byte[] b = new byte[1];
        int n = await _Inner.ReadAsync(b, 0, 1, cancellationToken).ConfigureAwait(false);
        if (n > 0 && b[0] == '\r')
        {
            await _Inner.ReadAsync(b, 0, 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ReadAndVerifyNextChunkSync()
    {
        string headerLine = ReadLineSync();
        if (string.IsNullOrEmpty(headerLine))
        {
            _EndOfStream = true;
            return;
        }

        long chunkSize = ParseChunkSize(headerLine);
        string? signature = ExtractSignature(headerLine);

        if (chunkSize == 0)
        {
            if (!string.IsNullOrEmpty(signature))
            {
                bool validFinal = _Signer!.ValidateChunk(Array.Empty<byte>(), signature);
                if (!validFinal)
                {
                    throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The chunk signature does not match the computed signature for final chunk."));
                }
            }

            string? trailerSig = ReadTrailersSync();
            if (!string.IsNullOrEmpty(trailerSig))
            {
                bool validTrailer = _Signer!.ValidateTrailer(Trailers, trailerSig);
                if (!validTrailer)
                {
                    throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The trailer signature does not match the computed trailer signature."));
                }
            }

            _EndOfStream = true;
            return;
        }

#if NET6_0_OR_GREATER
        if (chunkSize > Array.MaxLength)
#else
        if (chunkSize > 0x7FFFFFC7)
#endif
            throw new InvalidDataException($"Chunk size {chunkSize} exceeds supported in-memory buffer limit.");

        byte[] chunkData = new byte[(int)chunkSize];
        int totalRead = 0;
        while (totalRead < chunkSize)
        {
            int n = _Inner.Read(chunkData, totalRead, (int)chunkSize - totalRead);
            if (n <= 0)
            {
                throw new EndOfStreamException($"Premature end of stream while reading chunk data (expected {chunkSize}, got {totalRead}).");
            }
            totalRead += n;
        }

        ConsumeCrLfSync();

        if (!string.IsNullOrEmpty(signature))
        {
            bool valid = _Signer!.ValidateChunk(chunkData, signature);
            if (!valid)
            {
                throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch, "The chunk signature does not match the computed signature."));
            }
        }

        _CurrentChunkBuffer = chunkData;
        _CurrentChunkOffset = 0;
        _CurrentChunkAvailable = totalRead;
    }

    private string? ReadTrailersSync()
    {
        string? trailerSig = null;
        int trailerCount = 0;
        while (true)
        {
            string line = ReadLineSync();
            if (string.IsNullOrEmpty(line)) break;

            trailerCount++;
            if (trailerCount > MaxTrailersCount)
            {
                throw new InvalidDataException($"Number of trailers exceeds maximum limit of {MaxTrailersCount}.");
            }

            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                string name = line.Substring(0, colon).Trim();
                string val = line.Substring(colon + 1).Trim();
                if (name.Equals("x-amz-trailer-signature", StringComparison.OrdinalIgnoreCase))
                {
                    trailerSig = val;
                }
                else
                {
                    Trailers[name] = val;
                }
            }
        }
        return trailerSig;
    }

    private string ReadLineSync()
    {
        var sb = new StringBuilder();
        byte[] b = new byte[1];

        while (true)
        {
            int n = _Inner.Read(b, 0, 1);
            if (n <= 0) break;

            if (b[0] == '\r')
            {
                int next = _Inner.Read(b, 0, 1);
                if (next > 0 && b[0] == '\n') break;
                if (next > 0)
                {
                    sb.Append((char)b[0]);
                    if (sb.Length > MaxLineLength)
                        throw new InvalidDataException($"Header line exceeds maximum length of {MaxLineLength} characters.");
                }
                break;
            }
            if (b[0] == '\n')
                break;

            sb.Append((char)b[0]);
            if (sb.Length > MaxLineLength)
            {
                throw new InvalidDataException($"Header line exceeds maximum length of {MaxLineLength} characters.");
            }
        }

        return sb.ToString();
    }

    private void ConsumeCrLfSync()
    {
        int b = _Inner.ReadByte();
        if (b == '\r')
        {
            _ = _Inner.ReadByte();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _Signer?.Dispose();
            _Inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
