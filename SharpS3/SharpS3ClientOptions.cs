namespace SharpS3;

using System;

/// <summary>
/// Configuration options for SharpS3Client.
/// </summary>
public class SharpS3ClientOptions
{
    /// <summary>
    /// S3 service endpoint URI (e.g. "http://localhost:8000" or "https://s3.amazonaws.com").
    /// </summary>
    public Uri Endpoint { get; set; } = new Uri("http://localhost:8000");

    /// <summary>
    /// AWS access key ID for signing requests.
    /// If null or empty, requests are sent anonymously without SigV4 authorization.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// AWS secret access key for signing requests.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// AWS region (defaults to "us-east-1").
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Whether to force path-style URLs (http://endpoint/bucket/key) rather than virtual-hosted URLs (http://bucket.endpoint/key).
    /// Defaults to true for local/custom S3 compatible services.
    /// </summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// Request timeout (defaults to 100 seconds).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
}
