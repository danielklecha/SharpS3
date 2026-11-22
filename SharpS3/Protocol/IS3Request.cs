namespace SharpS3.Protocol;

using System.IO;
using System.Collections.Generic;
using SharpS3.Models;

/// <summary>
/// Base interface for all typed S3 requests.
/// </summary>
public interface IS3Request
{
    /// <summary>
    /// Unique identifier for this request.
    /// </summary>
    string RequestId { get; }

    /// <summary>
    /// Trace identifier.
    /// </summary>
    string TraceId { get; }

    /// <summary>
    /// HTTP method (GET, PUT, POST, DELETE, HEAD).
    /// </summary>
    string HttpMethod { get; }

    /// <summary>
    /// Raw requested path.
    /// </summary>
    string RawPath { get; }

    /// <summary>
    /// The classified S3 request type.
    /// </summary>
    S3RequestType RequestType { get; }

    /// <summary>
    /// Style of request (PathStyle or VirtualHostedStyle).
    /// </summary>
    S3RequestStyle RequestStyle { get; }

    /// <summary>
    /// Target bucket name, if any.
    /// </summary>
    string? Bucket { get; }

    /// <summary>
    /// Target object key, if any.
    /// </summary>
    string? Key { get; }

    /// <summary>
    /// Extracted AWS Access Key, if signed.
    /// </summary>
    string? AccessKey { get; }

    /// <summary>
    /// AWS Signature Version detected on the request.
    /// </summary>
    S3SignatureVersion SignatureVersion { get; }

    /// <summary>
    /// HTTP headers received with the request.
    /// </summary>
    IReadOnlyDictionary<string, string[]> Headers { get; }

    /// <summary>
    /// Query parameters received with the request.
    /// </summary>
    IReadOnlyDictionary<string, string> QueryParameters { get; }

    /// <summary>
    /// Raw payload stream (unbuffered for streaming uploads).
    /// </summary>
    Stream BodyStream { get; }

    /// <summary>
    /// User metadata or contextual object attached to this request.
    /// </summary>
    object? Metadata { get; set; }
}
