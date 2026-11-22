namespace SharpS3.Protocol;

using System.IO;
using System.Collections.Generic;

/// <summary>
/// Base interface for all typed S3 responses.
/// </summary>
public interface IS3Response
{
    /// <summary>
    /// HTTP status code.
    /// </summary>
    int StatusCode { get; }

    /// <summary>
    /// Custom response headers.
    /// </summary>
    Dictionary<string, string> Headers { get; }

    /// <summary>
    /// Content type, if applicable.
    /// </summary>
    string? ContentType { get; }

    /// <summary>
    /// Payload stream, if any (e.g. for ObjectRead, ObjectReadRange).
    /// </summary>
    Stream? BodyStream { get; }
}
