namespace SharpS3.Protocol;

using System;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Transport-level S3 response structure returned to the hosting web server.
/// Contains HTTP status, response headers, and output stream to write directly to the client.
/// </summary>
public class S3ServerResponse : IS3Response, IDisposable
{
    /// <summary>
    /// HTTP status code.
    /// </summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>
    /// HTTP response headers.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stream containing the response body (XML document, object payload, etc.).
    /// </summary>
    public Stream BodyStream { get; set; } = Stream.Null;

    /// <summary>
    /// Response MIME content type.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Disposes the body stream if disposable and not Stream.Null.
    /// </summary>
    public void Dispose()
    {
        if (BodyStream != Stream.Null)
        {
            BodyStream.Dispose();
        }
    }
}
