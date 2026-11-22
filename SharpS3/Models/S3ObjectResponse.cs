namespace SharpS3.Models;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Response wrapper for an S3 object download (GetObject).
/// Implements IDisposable and IAsyncDisposable to dispose underlying HTTP content/response stream.
/// </summary>
#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
public sealed class S3ObjectResponse : IDisposable, IAsyncDisposable
#else
public sealed class S3ObjectResponse : IDisposable
#endif
{
    private readonly HttpResponseMessage _httpResponse;
    private readonly Stream _stream;
    private bool _disposed;

    public S3ObjectResponse(
        HttpResponseMessage httpResponse,
        Stream stream,
        long contentLength,
        string? contentType,
        string? eTag,
        DateTimeOffset? lastModified,
        IDictionary<string, string> metadata)
    {
        _httpResponse = httpResponse ?? throw new ArgumentNullException(nameof(httpResponse));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        ContentLength = contentLength;
        ContentType = contentType;
        ETag = eTag;
        LastModified = lastModified;
        Metadata = metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Object payload stream. Caller is responsible for reading and disposing.
    /// </summary>
    public Stream Stream => _stream;

    /// <summary>
    /// Content length in bytes.
    /// </summary>
    public long ContentLength { get; }

    /// <summary>
    /// Content-Type header.
    /// </summary>
    public string? ContentType { get; }

    /// <summary>
    /// ETag header (usually MD5 hex wrapped in quotes).
    /// </summary>
    public string? ETag { get; }

    /// <summary>
    /// Last-Modified timestamp.
    /// </summary>
    public DateTimeOffset? LastModified { get; }

    /// <summary>
    /// User metadata (headers starting with x-amz-meta-).
    /// </summary>
    public IDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Reads the entire object stream into a byte array.
    /// </summary>
    public async Task<byte[]> ReadAsByteArrayAsync(CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await _stream.CopyToAsync(ms, 81920, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>
    /// Reads the entire object stream as a UTF-8 string.
    /// </summary>
    public async Task<string> ReadAsStringAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _httpResponse.Dispose();
            _disposed = true;
        }
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
#if NET8_0_OR_GREATER
            await _stream.DisposeAsync().ConfigureAwait(false);
#else
            _stream.Dispose();
#endif
            _httpResponse.Dispose();
            _disposed = true;
        }
    }
#endif
}
