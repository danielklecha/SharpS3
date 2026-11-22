namespace SharpS3;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;

/// <summary>
/// Default implementation of the transport-agnostic SharpS3 protocol engine.
/// Decoupled from any web server or socket layer.
/// </summary>
public class SharpS3Server : ISharpS3Server
{
    private static readonly IReadOnlyDictionary<string, string> _emptyQuery = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string[]> _emptyHeaders = new Dictionary<string, string[]>();

    private readonly SharpS3Settings _Settings;

    /// <summary>
    /// Gets the telemetry manager for tracking metrics and diagnostic events.
    /// </summary>
    internal S3ServerTelemetry Telemetry { get; }

    /// <summary>
    /// Configuration settings for this S3 server engine.
    /// </summary>
    public SharpS3Settings Settings => _Settings;

    /// <summary>
    /// Initializes a new instance of SharpS3Server.
    /// </summary>
    public SharpS3Server(SharpS3Settings? settings = null)
    {
        _Settings = settings ?? new SharpS3Settings();
        Telemetry = new S3ServerTelemetry(_Settings.Telemetry, () => _Settings, () => true);
    }

    /// <inheritdoc />
    public async Task<IS3Request> ReceiveRequestAsync(
        string httpMethod,
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        Stream? bodyStream = null,
        CancellationToken cancellationToken = default)
    {
        RequestTelemetry? rt = Telemetry.BeginRequest();
        RequestTelemetry.Current = rt;

        S3Request request;
        using (TelemetryStage parseStage = rt?.StartStage(S3ServerTelemetryNames.StageParse))
        {
            try
            {
                request = new S3Request(httpMethod, path, queryParameters ?? _emptyQuery, headers ?? _emptyHeaders, bodyStream ?? Stream.Null, _Settings);
            }
            catch (Exception ex)
            {
                TelemetryStage.Fail(parseStage, ex);
                throw;
            }
        }

        rt?.Describe(request);

        if (request.ValidationError != null)
        {
            rt?.MarkException(request.ValidationError, S3ServerTelemetryNames.StageParse);
            throw request.ValidationError;
        }

        if (!_Settings.RouteSuffixRangesToReadRange)
        {
            request.RouteSuffixRangeAsObjectRead();
        }

        // Validate signatures if enabled, unless deferred to DispatchAsync for PreRequestHandler
        if (_Settings.EnableSignatures && (_Settings.PreRequestHandler == null || _Settings.ValidateSignaturesBeforePreRequestHandler))
        {
            using (TelemetryStage sigStage = rt?.StartStage(S3ServerTelemetryNames.StageSignatureValidation))
            {
                try
                {
                    await ValidateSignatureAsync(request, _Settings, rt, cancellationToken).ConfigureAwait(false);
                    request.IsSignatureValidated = true;
                }
                catch (Exception ex)
                {
                    TelemetryStage.Fail(sigStage, ex);
                    throw;
                }
            }
        }

        return request;
    }

    /// <inheritdoc />
    public Task<IS3Request> ReceiveGetRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return ReceiveRequestAsync("GET", path, queryParameters, headers, Stream.Null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IS3Request> ReceiveHeadRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return ReceiveRequestAsync("HEAD", path, queryParameters, headers, Stream.Null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IS3Request> ReceivePutRequestAsync(
        string path,
        Stream bodyStream,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return ReceiveRequestAsync("PUT", path, queryParameters, headers, bodyStream, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IS3Request> ReceivePostRequestAsync(
        string path,
        Stream bodyStream,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default)
    {
        return ReceiveRequestAsync("POST", path, queryParameters, headers, bodyStream, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IS3Request> ReceiveDeleteRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        Stream? bodyStream = null,
        CancellationToken cancellationToken = default)
    {
        return ReceiveRequestAsync("DELETE", path, queryParameters, headers, bodyStream ?? Stream.Null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<S3ServerResponse> CreateResponseAsync(
        IS3Response response,
        CancellationToken cancellationToken = default)
    {
        var serverResponse = new S3ServerResponse
        {
            StatusCode = response.StatusCode,
            ContentType = response.ContentType
        };

        if (response.Headers != null)
        {
            foreach (var kvp in response.Headers)
            {
                serverResponse.Headers[kvp.Key] = kvp.Value;
            }
        }

        if (response.BodyStream != null)
        {
            serverResponse.BodyStream = response.BodyStream;
        }

        return Task.FromResult(serverResponse);
    }

    /// <inheritdoc />
    public S3ServerResponse CreateErrorResponse(S3Exception exception, string? requestId = null)
    {
        Error error = exception?.Error ?? new Error(ErrorCode.InternalError);
        if (!string.IsNullOrEmpty(requestId))
        {
            error.RequestId = requestId;
        }

        string xml = SerializationHelper.SerializeXml(error, true);
        byte[] bytes = Encoding.UTF8.GetBytes(xml);

        var response = new S3ServerResponse
        {
            StatusCode = exception?.HttpStatusCode ?? 500,
            ContentType = "application/xml",
            BodyStream = new MemoryStream(bytes)
        };

        response.Headers["Content-Length"] = bytes.Length.ToString();
        if (!string.IsNullOrEmpty(requestId))
        {
            response.Headers["x-amz-request-id"] = requestId;
            response.Headers["x-amz-id-2"] = requestId;
        }

        return response;
    }

    /// <summary>
    /// Validates the AWS signature on an incoming request using the configured authentication settings.
    /// </summary>
    internal static async Task ValidateSignatureAsync(S3Request request, SharpS3Settings settings, RequestTelemetry? rt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Authorization) && string.IsNullOrEmpty(request.Signature))
        {
            // Check if anonymous access is permitted
            if (settings.IsAnonymousRequestAllowedAsync != null &&
                await settings.IsAnonymousRequestAllowedAsync(request).ConfigureAwait(false))
            {
                rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeAnonymous);
                return;
            }

            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeUnsigned);
            throw new S3Exception(new Error(ErrorCode.AccessDenied));
        }

        string? accessKey = request.AccessKey;
        if (string.IsNullOrEmpty(accessKey))
        {
            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeUnknownKey);
            throw new S3Exception(new Error(ErrorCode.AccessDenied));
        }

        string? secretKey = null;
        if (settings.GetSecretKeyAsync != null)
        {
            secretKey = await settings.GetSecretKeyAsync(accessKey).ConfigureAwait(false);
        }
        else if (settings.Credentials.TryGetValue(accessKey, out var configuredSecret))
        {
            secretKey = configuredSecret;
        }

        if (string.IsNullOrEmpty(secretKey))
        {
            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeUnknownKey);
            throw new S3Exception(new Error(ErrorCode.AccessDenied));
        }

        switch (request.SignatureVersion)
        {
            case S3SignatureVersion.Version4:
                ValidateV4Signature(request, secretKey, settings, rt);
                break;

            case S3SignatureVersion.Version2:
                if (!settings.EnableSignatureV2)
                {
                    rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeUnsupported);
                    throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
                }
                ValidateV2Signature(request, secretKey, rt);
                break;

            default:
                rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeUnsupported);
                throw new S3Exception(new Error(ErrorCode.AccessDenied));
        }
    }

    private static void ValidateV4Signature(S3Request request, string secretKey, SharpS3Settings settings, RequestTelemetry? rt)
    {
        string timestamp = request.Date ?? DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);

        // 1. Presigned URL expiration validation
        if (!string.IsNullOrEmpty(request.Expires))
        {
            if (!long.TryParse(request.Expires, out long expiresSeconds) || expiresSeconds < 0)
            {
                rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeMismatch);
                throw new S3Exception(new Error(ErrorCode.AccessDenied) { Message = "Invalid expiration." });
            }

            if (TryParseSigV4Timestamp(timestamp, out DateTime reqTime))
            {
                if (DateTime.UtcNow > reqTime.AddSeconds(expiresSeconds))
                {
                    rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeMismatch);
                    throw new S3Exception(new Error(ErrorCode.AccessDenied) { Message = "Request has expired." });
                }
            }
        }
        else if (settings.ValidateTimestampSkew)
        {
            // 2. Clock skew verification for header-authenticated requests
            if (TryParseSigV4Timestamp(timestamp, out DateTime reqTime))
            {
                if (Math.Abs((DateTime.UtcNow - reqTime).TotalMinutes) > settings.MaxClockSkew.TotalMinutes)
                {
                    rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeMismatch);
                    throw new S3Exception(new Error(ErrorCode.RequestTimeTooSkewed));
                }
            }
        }

        string? contentSha256 = request.ContentSha256;
        AwsPayloadHashMode payloadHashMode = AwsPayloadHashMode.Signed;
        string? precomputedHash = null;

        if (!string.IsNullOrEmpty(contentSha256))
        {
            if (contentSha256.Equals("STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER", StringComparison.OrdinalIgnoreCase))
                payloadHashMode = AwsPayloadHashMode.StreamingSignedTrailer;
            else if (contentSha256.Equals("STREAMING-AWS4-HMAC-SHA256-PAYLOAD", StringComparison.OrdinalIgnoreCase))
                payloadHashMode = AwsPayloadHashMode.StreamingSigned;
            else if (contentSha256.Equals("STREAMING-UNSIGNED-PAYLOAD-TRAILER", StringComparison.OrdinalIgnoreCase))
                payloadHashMode = AwsPayloadHashMode.IsStreaming;
            else if (contentSha256.Equals("UNSIGNED-PAYLOAD", StringComparison.OrdinalIgnoreCase))
                payloadHashMode = AwsPayloadHashMode.Unsigned;
            else
                precomputedHash = contentSha256;
        }
        else if (!string.IsNullOrEmpty(request.Expires) || string.IsNullOrEmpty(request.Authorization))
        {
            // SigV4 presigned URL payload hash default
            payloadHashMode = AwsPayloadHashMode.Unsigned;
            precomputedHash = "UNSIGNED-PAYLOAD";
        }

        string sigFullUrl = BuildSignatureUrl(request);

        var result = new AwsSignatureV4(
            timestamp,
            request.HttpMethod,
            sigFullUrl,
            request.AccessKey,
            secretKey,
            request.Region ?? "us-east-1",
            "s3",
            request.Headers,
            request.SignedHeaders,
            null,
            payloadHashMode,
            precomputedHash);

        if (!ConstantTimeHelper.FixedTimeEqualsHex(result.Signature, request.Signature))
        {
            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeMismatch);
            throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
        }

        rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeSuccess);

        if (request.Chunked && request.BodyStream is Streams.AwsChunkedDecodingStream chunkStream && result.SigningKeyBytes != null)
        {
            string dateStr = timestamp.Length >= 8 ? timestamp.Substring(0, 8) : timestamp;
            var chunkSigner = new AwsChunkSigner(
                dateStr,
                request.Region ?? "us-east-1",
                "s3",
                result.SigningKeyBytes,
                request.Signature!);
            chunkStream.AttachSigner(chunkSigner);
        }
    }

    private static bool TryParseSigV4Timestamp(string timestamp, out DateTime dt)
    {
        if (DateTime.TryParseExact(timestamp, Constants.AmazonTimestampFormatCompact, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
            return true;
        if (DateTime.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out dt))
            return true;
        dt = default;
        return false;
    }

    private static void ValidateV2Signature(S3Request request, string secretKey, RequestTelemetry? rt)
    {
        try
        {
            AwsSignatureV2.Validate(request, secretKey);
            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeSuccess);
        }
        catch (S3Exception)
        {
            rt?.MarkSignatureOutcome(S3ServerTelemetryNames.OutcomeMismatch);
            throw;
        }
    }

    private static string BuildSignatureUrl(S3Request request)
    {
        string host = request.Host ?? request.Hostname ?? "localhost";
        string path = request.RawPath;
        if (!path.StartsWith("/")) path = "/" + path;

        var sb = new StringBuilder();
        sb.Append("http://").Append(host).Append(path);

        if (request.QueryParameters.Count > 0)
        {
            sb.Append('?');
            bool first = true;
            foreach (var kvp in request.QueryParameters)
            {
                if (kvp.Key.Equals("X-Amz-Signature", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!first) sb.Append('&');
                first = false;
                sb.Append(kvp.Key);
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    sb.Append('=').Append(Uri.EscapeDataString(kvp.Value));
                }
            }
        }

        return sb.ToString();
    }
}
