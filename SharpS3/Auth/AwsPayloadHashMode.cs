namespace SharpS3.Auth;

/// <summary>
/// Specifies the payload hashing mode used for AWS Signature Version 4 calculations.
/// </summary>
public enum AwsPayloadHashMode
{
    /// <summary>
    /// Computes the SHA-256 hash of the request body.
    /// </summary>
    Signed,

    /// <summary>
    /// Unsigned payload (uses literal "UNSIGNED-PAYLOAD").
    /// </summary>
    Unsigned,

    /// <summary>
    /// Streaming unsigned payload (uses literal "STREAMING-UNSIGNED-PAYLOAD-TRAILER").
    /// </summary>
    IsStreaming,

    /// <summary>
    /// Streaming signed payload without trailers (uses literal "STREAMING-AWS4-HMAC-SHA256-PAYLOAD").
    /// </summary>
    StreamingSigned,

    /// <summary>
    /// Streaming signed payload with trailers (uses literal "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER").
    /// </summary>
    StreamingSignedTrailer
}
