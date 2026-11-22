namespace SharpS3.Auth;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Native AWS Signature Version 4 calculation and verification engine.
/// Provides canonical request construction, signing key derivation, and signature validation.
/// </summary>
public class AwsSignatureV4 : IDisposable
{
    public const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly HashSet<string> HeaderIgnoreList = new(StringComparer.OrdinalIgnoreCase)
    {
        "amz-sdk-invocation-id",
        "amz-sdk-request",
        "authorization",
        "connection",
        "expect",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
        "user-agent",
        "x-amzn-trace-id"
    };

    /// <summary>
    /// Gets the ISO 8601 basic format timestamp (e.g., "20130524T000000Z") of the request.
    /// </summary>
    public string Timestamp { get; }

    /// <summary>
    /// Gets the date stamp (YYYYMMDD) component of the credential scope.
    /// </summary>
    public string DateStamp { get; }

    /// <summary>
    /// Gets the HTTP request method (e.g., GET, PUT, POST, DELETE).
    /// </summary>
    public string HttpMethod { get; }

    /// <summary>
    /// Gets the full URL of the request.
    /// </summary>
    public string FullUrl { get; }

    /// <summary>
    /// Gets the AWS access key ID used for signing.
    /// </summary>
    public string AccessKey { get; }

    /// <summary>
    /// Gets the AWS secret access key used for signing.
    /// </summary>
    public string SecretKey { get; }

    /// <summary>
    /// Gets the AWS region (e.g., "us-east-1").
    /// </summary>
    public string Region { get; }

    /// <summary>
    /// Gets the AWS service name (e.g., "s3").
    /// </summary>
    public string Service { get; }

    /// <summary>
    /// Gets the credential scope formatted as "date/region/service/aws4_request".
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Gets the canonical URI path derived from the request.
    /// </summary>
    public string CanonicalUri { get; }

    /// <summary>
    /// Gets the canonical query string sorted and URI-encoded.
    /// </summary>
    public string CanonicalQuerystring { get; }

    /// <summary>
    /// Gets the canonical headers string formatted according to the AWS Signature Version 4 specification.
    /// </summary>
    public string CanonicalHeaders { get; }

    /// <summary>
    /// Gets the semicolon-separated list of signed header names in lowercase.
    /// </summary>
    public string SignedHeaders { get; }

    /// <summary>
    /// Gets the hex-encoded SHA-256 hash of the request payload or payload hash literal.
    /// </summary>
    public string HashedPayload { get; }

    /// <summary>
    /// Gets the canonical request string constructed according to AWS SigV4 rules.
    /// </summary>
    public string CanonicalRequest { get; }

    /// <summary>
    /// Gets the string-to-sign constructed from the algorithm, timestamp, credential scope, and canonical request hash.
    /// </summary>
    public string StringToSign { get; }

    /// <summary>
    /// Gets the derived cryptographic signing key bytes.
    /// </summary>
    public byte[] SigningKeyBytes { get; }

    /// <summary>
    /// Gets the hex-encoded signature calculated by signing the string-to-sign.
    /// </summary>
    public string Signature { get; }

    /// <summary>
    /// Gets the complete Authorization header value.
    /// </summary>
    public string AuthorizationHeader { get; }

    public AwsSignatureV4(
        string timestamp,
        string httpMethod,
        string fullUrl,
        string accessKey,
        string secretKey,
        string region,
        string service,
        IEnumerable<KeyValuePair<string, string[]>>? headers,
        IEnumerable<string>? signedHeaders = null,
        object? requestBody = null,
        AwsPayloadHashMode payloadHashMode = AwsPayloadHashMode.Signed,
        string? precomputedPayloadHash = null,
        bool requireHostHeader = false)
        : this(
            timestamp,
            httpMethod,
            fullUrl,
            accessKey,
            secretKey,
            region,
            service,
            FlattenHeaders(headers),
            signedHeaders,
            requestBody,
            payloadHashMode,
            precomputedPayloadHash,
            requireHostHeader)
    {
    }

    public AwsSignatureV4(
        string timestamp,
        string httpMethod,
        string fullUrl,
        string accessKey,
        string secretKey,
        string region,
        string service,
        NameValueCollection? headers,
        IEnumerable<string>? signedHeaders = null,
        object? requestBody = null,
        AwsPayloadHashMode payloadHashMode = AwsPayloadHashMode.Signed,
        string? precomputedPayloadHash = null,
        bool requireHostHeader = false)
        : this(
            timestamp,
            httpMethod,
            fullUrl,
            accessKey,
            secretKey,
            region,
            service,
            ConvertHeaders(headers),
            signedHeaders,
            requestBody,
            payloadHashMode,
            precomputedPayloadHash,
            requireHostHeader)
    {
    }

    public AwsSignatureV4(
        string timestamp,
        string httpMethod,
        string fullUrl,
        string accessKey,
        string secretKey,
        string region,
        string service,
        IEnumerable<KeyValuePair<string, string>>? headers,
        IEnumerable<string>? signedHeaders = null,
        object? requestBody = null,
        AwsPayloadHashMode payloadHashMode = AwsPayloadHashMode.Signed,
        string? precomputedPayloadHash = null,
        bool requireHostHeader = false)
    {
        if (timestamp == null) throw new ArgumentNullException(nameof(timestamp));
        if (httpMethod == null) throw new ArgumentNullException(nameof(httpMethod));
        if (fullUrl == null) throw new ArgumentNullException(nameof(fullUrl));
        if (accessKey == null) throw new ArgumentNullException(nameof(accessKey));
        if (secretKey == null) throw new ArgumentNullException(nameof(secretKey));

        if (requireHostHeader)
        {
            bool hasHost = false;
            if (headers != null)
            {
                foreach (var h in headers)
                {
                    if (string.Equals(h.Key, "host", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHost = true;
                        break;
                    }
                }
            }

            if (!hasHost && signedHeaders != null)
            {
                foreach (var h in signedHeaders)
                {
                    if (string.Equals(h, "host", StringComparison.OrdinalIgnoreCase))
                    {
                        hasHost = true;
                        break;
                    }
                }
            }

            if (!hasHost)
            {
                throw new ArgumentException("Supplied headers does not include 'host' header.", nameof(headers));
            }
        }

        Timestamp = timestamp;
        DateStamp = timestamp.Length >= 8 ? timestamp.Substring(0, 8) : timestamp;
        HttpMethod = httpMethod.ToUpperInvariant();
        FullUrl = fullUrl;
        AccessKey = accessKey;
        SecretKey = secretKey;
        Region = region ?? "us-east-1";
        Service = service ?? "s3";
        Scope = $"{DateStamp}/{Region}/{Service}/aws4_request";

        CanonicalUri = BuildCanonicalUri(FullUrl);
        CanonicalQuerystring = BuildCanonicalQueryString(FullUrl);

        var (canHeaders, sHeaders) = BuildCanonicalHeaders(headers, signedHeaders);
        CanonicalHeaders = canHeaders;
        SignedHeaders = sHeaders;

        if (!string.IsNullOrEmpty(precomputedPayloadHash))
        {
            if (precomputedPayloadHash.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(precomputedPayloadHash, "UNSIGNED-PAYLOAD", StringComparison.OrdinalIgnoreCase))
            {
                HashedPayload = precomputedPayloadHash.ToUpperInvariant();
            }
            else
            {
                HashedPayload = precomputedPayloadHash.ToLowerInvariant();
            }
        }
        else
        {
            HashedPayload = ComputePayloadHash(requestBody, payloadHashMode);
        }

        CanonicalRequest = $"{HttpMethod}\n{CanonicalUri}\n{CanonicalQuerystring}\n{CanonicalHeaders}\n{SignedHeaders}\n{HashedPayload}";

        string canonicalRequestHash = Sha256Hex(Encoding.UTF8.GetBytes(CanonicalRequest));
        StringToSign = $"AWS4-HMAC-SHA256\n{Timestamp}\n{Scope}\n{canonicalRequestHash}";

        SigningKeyBytes = DeriveSigningKey(SecretKey, DateStamp, Region, Service);
        byte[] sigBytes = HmacSha256(SigningKeyBytes, Encoding.UTF8.GetBytes(StringToSign));
        Signature = BytesToHex(sigBytes).ToLowerInvariant();

        AuthorizationHeader = $"AWS4-HMAC-SHA256 Credential={AccessKey}/{Scope}, SignedHeaders={SignedHeaders}, Signature={Signature}";
    }

    /// <summary>
    /// Derives the AWS Signature Version 4 signing key using the secret key, date stamp, region, and service name.
    /// </summary>
    public static byte[] DeriveSigningKey(string secretKey, string dateStamp, string region, string service)
    {
        byte[] kSecret = Encoding.UTF8.GetBytes("AWS4" + secretKey);
        byte[] kDate = HmacSha256(kSecret, Encoding.UTF8.GetBytes(dateStamp));
        byte[] kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        byte[] kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes(service));
        return HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    /// <summary>
    /// Constructs the canonical URI from a full URL according to AWS SigV4 specification.
    /// </summary>
    public static string BuildCanonicalUri(string fullUrl)
    {
        if (string.IsNullOrEmpty(fullUrl)) return "/";

        string path;
        int schemeIdx = fullUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            int pathStart = fullUrl.IndexOf('/', schemeIdx + 3);
            path = pathStart >= 0 ? fullUrl.Substring(pathStart) : string.Empty;
        }
        else
        {
            path = fullUrl;
        }

        int qIdx = path.IndexOf('?');
        if (qIdx >= 0)
        {
            path = path.Substring(0, qIdx);
        }

        if (string.IsNullOrEmpty(path))
            return "/";

        if (!path.StartsWith("/"))
        {
            path = "/" + path;
        }

        path = Uri.UnescapeDataString(path);
        return UriEncode(path, encodeSlash: false);
    }

    /// <summary>
    /// Constructs the sorted and URI-encoded canonical query string from a full URL.
    /// </summary>
    public static string BuildCanonicalQueryString(string fullUrl)
    {
        if (string.IsNullOrEmpty(fullUrl)) return string.Empty;

        int qIdx = fullUrl.IndexOf('?');
        if (qIdx < 0) return string.Empty;

        string queryString = fullUrl.Substring(qIdx + 1);
        if (string.IsNullOrEmpty(queryString))
            return string.Empty;

        string[] parts = queryString.Split('&');
        var list = new List<KeyValuePair<string, string>>(parts.Length);

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;

            int eqIdx = part.IndexOf('=');
            string key;
            string val;
            if (eqIdx >= 0)
            {
                key = part.Substring(0, eqIdx);
                val = part.Substring(eqIdx + 1);
            }
            else
            {
                key = part;
                val = string.Empty;
            }

            string unescapedKey = Uri.UnescapeDataString(key.Replace("+", " "));
            string unescapedVal = Uri.UnescapeDataString(val.Replace("+", " "));

            if (unescapedKey.Equals("X-Amz-Signature", StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add(new KeyValuePair<string, string>(
                UriEncode(unescapedKey, encodeSlash: true),
                UriEncode(unescapedVal, encodeSlash: true)
            ));
        }

        list.Sort((a, b) =>
        {
            int cmp = string.CompareOrdinal(a.Key, b.Key);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Value, b.Value);
        });

        var sb = new StringBuilder();
        for (int i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(list[i].Key).Append('=').Append(list[i].Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// URI-encodes the specified string in accordance with RFC 3986 and AWS SigV4 rules.
    /// </summary>
    public static string UriEncode(string input, bool encodeSlash = true)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sb = new StringBuilder();
        byte[] utf8 = Encoding.UTF8.GetBytes(input);
        foreach (byte b in utf8)
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '_' || c == '-' || c == '~' || c == '.')
            {
                sb.Append(c);
            }
            else if (c == '/' && !encodeSlash)
            {
                sb.Append('/');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }
        return sb.ToString();
    }

    private static (string CanonicalHeaders, string SignedHeaders) BuildCanonicalHeaders(
        IEnumerable<KeyValuePair<string, string>>? headers,
        IEnumerable<string>? signedHeadersFilter)
    {
        if (headers == null)
        {
            return (string.Empty, string.Empty);
        }

        HashSet<string>? signedSet = null;
        if (signedHeadersFilter != null)
        {
            signedSet = new HashSet<string>(signedHeadersFilter.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        }

        // Group header values by lowercase key, preserving multiple values separated by commas
        var grouped = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var h in headers)
        {
            if (string.IsNullOrEmpty(h.Key))
                continue;
            string keyLower = h.Key.ToLowerInvariant();

            if (signedSet != null)
            {
                if (!signedSet.Contains(keyLower))
                    continue;
            }
            else
            {
                if (HeaderIgnoreList.Contains(keyLower))
                    continue;
            }

            if (!grouped.TryGetValue(keyLower, out var list))
            {
                list = new List<string>();
                grouped[keyLower] = list;
            }

            string val = h.Value ?? string.Empty;
            val = val.Trim();
            while (val.Contains("  "))
            {
                val = val.Replace("  ", " ");
            }
            list.Add(val);
        }

        var sbHeaders = new StringBuilder();
        var signedHeadersList = new List<string>(grouped.Count);

        foreach (var kvp in grouped)
        {
            signedHeadersList.Add(kvp.Key);
            string combinedVal = string.Join(",", kvp.Value);
            sbHeaders.Append(kvp.Key).Append(':').Append(combinedVal).Append('\n');
        }

        string signedHeadersString = string.Join(";", signedHeadersList);
        return (sbHeaders.ToString(), signedHeadersString);
    }

    /// <summary>
    /// Computes the payload hash string for the specified request body and payload hash mode.
    /// </summary>
    public static string ComputePayloadHash(object? body, AwsPayloadHashMode mode)
    {
        switch (mode)
        {
            case AwsPayloadHashMode.Unsigned:
                return "UNSIGNED-PAYLOAD";
            case AwsPayloadHashMode.IsStreaming:
                return "STREAMING-UNSIGNED-PAYLOAD-TRAILER";
            case AwsPayloadHashMode.StreamingSigned:
                return "STREAMING-AWS4-HMAC-SHA256-PAYLOAD";
            case AwsPayloadHashMode.StreamingSignedTrailer:
                return "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER";
            case AwsPayloadHashMode.Signed:
                if (body == null)
                {
                    return EmptySha256;
                }
                if (body is byte[] bytes)
                {
                    return bytes.Length == 0 ? EmptySha256 : Sha256Hex(bytes);
                }
                if (body is string s)
                {
                    return string.IsNullOrEmpty(s) ? EmptySha256 : Sha256Hex(Encoding.UTF8.GetBytes(s));
                }
                if (body is Stream stream)
                {
                    long pos = stream.CanSeek ? stream.Position : -1;
                    using var sha = SHA256.Create();
                    byte[] hash = sha.ComputeHash(stream);
                    if (pos >= 0)
                    {
                        stream.Position = pos;
                    }
                    return BytesToHex(hash).ToLowerInvariant();
                }
                throw new ArgumentException($"Unsupported body type: {body.GetType().FullName}");
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> FlattenHeaders(IEnumerable<KeyValuePair<string, string[]>>? headers)
    {
        if (headers == null)
            yield break;
        foreach (var kvp in headers)
        {
            if (kvp.Value == null)
                continue;
            foreach (var val in kvp.Value)
            {
                yield return new KeyValuePair<string, string>(kvp.Key, val);
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ConvertHeaders(NameValueCollection? nvc)
    {
        if (nvc == null)
            yield break;
        foreach (string? key in nvc.AllKeys)
        {
            if (key == null)
                continue;
            string[]? values = nvc.GetValues(key);
            if (values != null)
            {
                foreach (string val in values)
                {
                    yield return new KeyValuePair<string, string>(key, val);
                }
            }
        }
    }

    /// <summary>
    /// Computes an HMAC-SHA256 hash for the given byte array and key.
    /// </summary>
    public static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the input data and returns it as a lowercase hexadecimal string.
    /// </summary>
    public static string Sha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return BytesToHex(sha.ComputeHash(data)).ToLowerInvariant();
    }

    /// <summary>
    /// Converts a byte array to its lowercase hexadecimal string representation.
    /// </summary>
    public static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
