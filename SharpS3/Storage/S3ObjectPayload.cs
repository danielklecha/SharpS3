namespace SharpS3.Storage;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Represents an object retrieved from an S3 storage backend, including its data stream and headers.
/// </summary>
public class S3ObjectPayload : IDisposable
{
    /// <summary>
    /// Content data stream.
    /// </summary>
    public Stream Stream { get; set; } = Stream.Null;

    /// <summary>
    /// Content length in bytes.
    /// </summary>
    public long ContentLength { get; set; }

    /// <summary>
    /// MIME content type.
    /// </summary>
    public string? ContentType { get; set; } = "binary/octet-stream";

    /// <summary>
    /// Entity tag (ETag).
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Last modified timestamp in UTC.
    /// </summary>
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total size of the underlying object (relevant for HTTP 206 Partial Content range responses).
    /// </summary>
    public long? TotalSize { get; set; }

    /// <summary>
    /// Range start byte offset, if a range was requested.
    /// </summary>
    public long? RangeStart { get; set; }

    /// <summary>
    /// Range end byte offset, if a range was requested.
    /// </summary>
    public long? RangeEnd { get; set; }

    /// <summary>
    /// User metadata and custom headers.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Disposes the underlying data stream.
    /// </summary>
    public void Dispose()
    {
        if (Stream != Stream.Null)
        {
            Stream.Dispose();
        }
    }
}
