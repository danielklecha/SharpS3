namespace SharpS3.Exceptions;

using System;
using System.Net;
using SharpS3.Models;

/// <summary>
/// Exception thrown by SharpS3Client when an S3 request fails.
/// </summary>
public class SharpS3ClientException : Exception
{
    /// <summary>
    /// HTTP status code returned by the server.
    /// </summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>
    /// S3 error code (e.g. NoSuchBucket, NoSuchKey, AccessDenied).
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// S3 error message from the response XML.
    /// </summary>
    public string? S3ErrorMessage { get; }

    /// <summary>
    /// Resource on which the error occurred.
    /// </summary>
    public string? Resource { get; }

    /// <summary>
    /// S3 Request ID.
    /// </summary>
    public string? RequestId { get; }

    public SharpS3ClientException(HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public SharpS3ClientException(HttpStatusCode statusCode, Error error)
        : base($"S3 operation failed with status {(int)statusCode} ({statusCode}): {error.Code} - {error.Message}")
    {
        StatusCode = statusCode;
        ErrorCode = error.Code.ToString();
        S3ErrorMessage = error.Message;
        Resource = error.Resource;
        RequestId = error.RequestId;
    }
}
