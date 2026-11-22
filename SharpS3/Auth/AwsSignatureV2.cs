namespace SharpS3.Auth;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

/// <summary>
/// Native AWS Signature Version 2 calculation and verification engine.
/// Supports both Authorization header (AWS AccessKey:Signature) and Presigned URL (?AWSAccessKeyId=...&amp;Expires=...&amp;Signature=...).
/// </summary>
public static class AwsSignatureV2
{
    private static readonly Dictionary<string, string> InterestingSubresourcesMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "acl", "acl" },
        { "analytics", "analytics" },
        { "cors", "cors" },
        { "delete", "delete" },
        { "encryption", "encryption" },
        { "inventory", "inventory" },
        { "legal-hold", "legal-hold" },
        { "lifecycle", "lifecycle" },
        { "location", "location" },
        { "logging", "logging" },
        { "metrics", "metrics" },
        { "notification", "notification" },
        { "object-lock", "object-lock" },
        { "partNumber", "partNumber" },
        { "policy", "policy" },
        { "publicAccessBlock", "publicAccessBlock" },
        { "replication", "replication" },
        { "requestPayment", "requestPayment" },
        { "response-cache-control", "response-cache-control" },
        { "response-content-disposition", "response-content-disposition" },
        { "response-content-encoding", "response-content-encoding" },
        { "response-content-language", "response-content-language" },
        { "response-content-type", "response-content-type" },
        { "response-expires", "response-expires" },
        { "restore", "restore" },
        { "retention", "retention" },
        { "select", "select" },
        { "select-type", "select-type" },
        { "tagging", "tagging" },
        { "torrent", "torrent" },
        { "uploadId", "uploadId" },
        { "uploads", "uploads" },
        { "versionId", "versionId" },
        { "versioning", "versioning" },
        { "versions", "versions" },
        { "website", "website" }
    };

    /// <summary>
    /// Calculates the AWS Signature Version 2 authorization signature for header-based authentication.
    /// </summary>
    public static string CalculateHeaderSignature(
        string httpMethod,
        string rawPath,
        string? bucket,
        S3RequestStyle requestStyle,
        IReadOnlyDictionary<string, string[]> headers,
        IReadOnlyDictionary<string, string> queryParameters,
        string secretKey)
    {
        string stringToSign = BuildStringToSign(httpMethod, rawPath, bucket, requestStyle, headers, queryParameters, expires: null);
        return ComputeHmacSha1(secretKey, stringToSign);
    }

    /// <summary>
    /// Calculates the AWS Signature Version 2 signature for presigned query string URLs.
    /// </summary>
    public static string CalculateSignedUrlSignature(
        string httpMethod,
        string rawPath,
        string? bucket,
        S3RequestStyle requestStyle,
        IReadOnlyDictionary<string, string[]> headers,
        IReadOnlyDictionary<string, string> queryParameters,
        long expires,
        string secretKey)
    {
        string stringToSign = BuildStringToSign(httpMethod, rawPath, bucket, requestStyle, headers, queryParameters, expires.ToString());
        return ComputeHmacSha1(secretKey, stringToSign);
    }

    /// <summary>
    /// Generates a complete AWS Signature Version 2 signed URL.
    /// </summary>
    /// <param name="httpMethod">HTTP method (e.g. GET, PUT).</param>
    /// <param name="fullUrl">Full URL before signing.</param>
    /// <param name="accessKey">AWS access key ID.</param>
    /// <param name="secretKey">AWS secret access key.</param>
    /// <param name="expires">Expiration time in Unix epoch seconds.</param>
    /// <param name="headers">Optional request headers participating in signature.</param>
    /// <param name="bucket">Optional explicit bucket name for virtual-hosted style.</param>
    /// <returns>Signed URL with AWSAccessKeyId, Expires, and Signature query parameters.</returns>
    public static string GenerateSignedUrl(
        string httpMethod,
        string fullUrl,
        string accessKey,
        string secretKey,
        long expires,
        IReadOnlyDictionary<string, string[]>? headers = null,
        string? bucket = null)
    {
        if (string.IsNullOrEmpty(httpMethod)) throw new ArgumentNullException(nameof(httpMethod));
        if (string.IsNullOrEmpty(fullUrl)) throw new ArgumentNullException(nameof(fullUrl));
        if (string.IsNullOrEmpty(accessKey)) throw new ArgumentNullException(nameof(accessKey));
        if (string.IsNullOrEmpty(secretKey)) throw new ArgumentNullException(nameof(secretKey));
        if (expires < 1) throw new ArgumentOutOfRangeException(nameof(expires), "Expiration time must be greater than zero.");

        if (!Uri.TryCreate(fullUrl, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The supplied full URL is not an absolute HTTP or HTTPS URI: " + fullUrl, nameof(fullUrl));
        }

        string rawPath = parsedUri.AbsolutePath;
        string rawQuery = parsedUri.Query;

        var queryParams = ParseQueryString(rawQuery);
        if (queryParams.ContainsKey("AWSAccessKeyId") || queryParams.ContainsKey("Expires") || queryParams.ContainsKey("Signature"))
        {
            throw new ArgumentException("The supplied URL already contains SigV2 authentication parameters.", nameof(fullUrl));
        }

        S3RequestStyle requestStyle = S3RequestStyle.PathStyle;
        if (!string.IsNullOrEmpty(bucket))
        {
            requestStyle = S3RequestStyle.VirtualHostedStyle;
        }

        string sig = CalculateSignedUrlSignature(
            httpMethod,
            rawPath,
            bucket,
            requestStyle,
            headers ?? new Dictionary<string, string[]>(),
            queryParams,
            expires,
            secretKey);

        string encodedSig = Uri.EscapeDataString(sig);
        string sep;
        if (fullUrl.EndsWith("?", StringComparison.Ordinal) || fullUrl.EndsWith("&", StringComparison.Ordinal))
        {
            sep = string.Empty;
        }
        else if (string.IsNullOrEmpty(rawQuery))
        {
            sep = "?";
        }
        else
        {
            sep = "&";
        }

        return fullUrl + sep + "AWSAccessKeyId=" + Uri.EscapeDataString(accessKey) + "&Expires=" + expires + "&Signature=" + encodedSig;
    }

    /// <summary>
    /// Generates a complete AWS Signature Version 2 signed URL.
    /// </summary>
    public static string GenerateSignedUrl(
        string httpMethod,
        string fullUrl,
        string accessKey,
        string secretKey,
        DateTimeOffset expiresAt,
        IReadOnlyDictionary<string, string[]>? headers = null,
        string? bucket = null)
        => GenerateSignedUrl(httpMethod, fullUrl, accessKey, secretKey, expiresAt.ToUnixTimeSeconds(), headers, bucket);

    /// <summary>
    /// Validates the AWS Signature Version 2 on an incoming S3 request against the provided secret key.
    /// </summary>
    public static void Validate(S3Request request, string secretKey)
    {
        if (string.IsNullOrEmpty(request.Signature))
        {
            throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
        }

        if (string.IsNullOrEmpty(request.Authorization))
        {
            // Presigned URL
            if (!long.TryParse(request.Expires, out long expires))
            {
                throw new S3Exception(new Error(ErrorCode.AccessDenied));
            }

            if (expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                throw new S3Exception(new Error(ErrorCode.AccessDenied));
            }

            string expected = CalculateSignedUrlSignature(
                request.HttpMethod,
                request.RawPath,
                request.Bucket,
                request.RequestStyle,
                request.Headers,
                request.QueryParameters,
                expires,
                secretKey);

            if (!ConstantTimeHelper.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.Signature)))
            {
                throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
            }
        }
        else
        {
            // Authorization Header
            bool hasAmzDate = request.HeaderExists("x-amz-date");
            bool hasDate = request.HeaderExists("date");
            if (!hasAmzDate && !hasDate)
            {
                throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
            }

            string expected = CalculateHeaderSignature(
                request.HttpMethod,
                request.RawPath,
                request.Bucket,
                request.RequestStyle,
                request.Headers,
                request.QueryParameters,
                secretKey);

            if (!ConstantTimeHelper.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(request.Signature)))
            {
                throw new S3Exception(new Error(ErrorCode.SignatureDoesNotMatch));
            }
        }
    }

    private static string BuildStringToSign(
        string httpMethod,
        string rawPath,
        string? bucket,
        S3RequestStyle requestStyle,
        IReadOnlyDictionary<string, string[]> headers,
        IReadOnlyDictionary<string, string> queryParameters,
        string? expires)
    {
        var sb = new StringBuilder();

        // 1. HTTP-Verb
        sb.Append(httpMethod.ToUpperInvariant()).Append('\n');

        // 2. Content-MD5
        string contentMd5 = GetFirstHeaderValue(headers, "Content-MD5");
        sb.Append(contentMd5).Append('\n');

        // 3. Content-Type
        string contentType = GetFirstHeaderValue(headers, "Content-Type");
        sb.Append(contentType).Append('\n');

        // 4. Date or Expires
        if (!string.IsNullOrEmpty(expires))
        {
            sb.Append(expires).Append('\n');
        }
        else
        {
            bool hasAmzDate = headers.Any(h => h.Key.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase));
            if (hasAmzDate)
            {
                sb.Append('\n');
            }
            else
            {
                string date = GetFirstHeaderValue(headers, "Date");
                sb.Append(date).Append('\n');
            }
        }

        // 5. CanonicalizedAmzHeaders
        var amzHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in headers)
        {
            string lowerKey = kvp.Key.ToLowerInvariant();
            if (lowerKey.StartsWith("x-amz-", StringComparison.Ordinal))
            {
                if (kvp.Value == null || kvp.Value.Length == 0)
                {
                    amzHeaders[lowerKey] = string.Empty;
                }
                else
                {
                    var values = new string[kvp.Value.Length];
                    for (int i = 0; i < kvp.Value.Length; i++)
                    {
                        values[i] = NormalizeHeaderValue(kvp.Value[i]);
                    }
                    amzHeaders[lowerKey] = string.Join(",", values).Trim();
                }
            }
        }

        foreach (var kvp in amzHeaders)
        {
            sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('\n');
        }

        // 6. CanonicalizedResource
        sb.Append(BuildCanonicalizedResource(rawPath, bucket, requestStyle, queryParameters));

        return sb.ToString();
    }

    private static string BuildCanonicalizedResource(
        string rawPath,
        string? bucket,
        S3RequestStyle requestStyle,
        IReadOnlyDictionary<string, string> queryParameters)
    {
        var sb = new StringBuilder();

        if (requestStyle == S3RequestStyle.VirtualHostedStyle && !string.IsNullOrEmpty(bucket))
        {
            sb.Append('/').Append(bucket);
        }

        string path = rawPath;
        if (string.IsNullOrEmpty(path))
        {
            sb.Append('/');
        }
        else
        {
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                sb.Append('/');
            }

            sb.Append(path);
        }

        // Subresources in query parameters
        var subresources = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var kvp in queryParameters)
        {
            if (InterestingSubresourcesMap.TryGetValue(kvp.Key, out string? canonicalName))
            {
                subresources[canonicalName] = kvp.Value;
            }
        }

        if (subresources.Count > 0)
        {
            bool first = true;
            foreach (var kvp in subresources)
            {
                sb.Append(first ? '?' : '&');
                first = false;
                sb.Append(kvp.Key);
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    sb.Append('=').Append(kvp.Value);
                }
            }
        }

        return sb.ToString();
    }

    private static string GetFirstHeaderValue(IReadOnlyDictionary<string, string[]> headers, string name)
    {
        foreach (var kvp in headers)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && kvp.Value != null && kvp.Value.Length > 0)
            {
                return kvp.Value[0] ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string ComputeHmacSha1(string secretKey, string stringToSign)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(secretKey);
        using var hmac = new HMACSHA1(keyBytes);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Normalizes a header value by unfolding multiline values and compressing internal whitespace.
    /// </summary>
    internal static string NormalizeHeaderValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        string unfolded = value!.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
        string trimmed = unfolded.Trim();
        var sb = new StringBuilder(trimmed.Length);
        bool prevSpace = false;
        foreach (char c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a raw URL query string into a case-insensitive dictionary of key-value pairs.
    /// </summary>
    internal static Dictionary<string, string> ParseQueryString(string? rawQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(rawQuery)) return result;

        string query = rawQuery!.TrimStart('?');
        if (string.IsNullOrEmpty(query))
            return result;

        string[] pairs = query.Split('&');
        foreach (var pair in pairs)
        {
            if (string.IsNullOrEmpty(pair))
                continue;
            int eq = pair.IndexOf('=');
            if (eq >= 0)
            {
                string key = Uri.UnescapeDataString(pair.Substring(0, eq).Replace("+", " "));
                string val = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace("+", " "));
                result[key] = val;
            }
            else
            {
                string key = Uri.UnescapeDataString(pair.Replace("+", " "));
                result[key] = string.Empty;
            }
        }
        return result;
    }
}
