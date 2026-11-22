namespace SharpS3.Protocol;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Serialization;

/// <summary>
/// Parsed Amazon S3 request containing all extracted metadata, query parameters, headers, and body stream.
/// </summary>
public class S3Request : IS3Request
{
    #region Public-Properties

    /// <summary>
    /// Unique identifier for this request.
    /// </summary>
    public string RequestId { get; set; } = "req_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Trace identifier.
    /// </summary>
    public string TraceId { get; set; } = "trace_" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// HTTP method (GET, PUT, POST, DELETE, HEAD).
    /// </summary>
    public string HttpMethod { get; private set; }

    /// <summary>
    /// Raw requested path.
    /// </summary>
    public string RawPath { get; private set; }

    /// <summary>
    /// Classified S3 request type.
    /// </summary>
    public S3RequestType RequestType { get; internal set; } = S3RequestType.Unknown;

    /// <summary>
    /// Style of request (PathStyle or VirtualHostedStyle).
    /// </summary>
    public S3RequestStyle RequestStyle { get; internal set; } = S3RequestStyle.Unknown;

    /// <summary>
    /// Indicates if payload transfer is chunked.
    /// </summary>
    public bool Chunked { get; set; } = false;

    /// <summary>
    /// AWS region extracted from auth header.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Request hostname.
    /// </summary>
    public string? Hostname { get; set; }

    /// <summary>
    /// Host header value.
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// Base domain for virtual hosted style.
    /// </summary>
    public string? BaseDomain { get; set; }

    /// <summary>
    /// Target bucket name.
    /// </summary>
    public string? Bucket { get; internal set; }

    /// <summary>
    /// Target object key.
    /// </summary>
    public string? Key { get; internal set; }

    /// <summary>
    /// Prefix query parameter.
    /// </summary>
    public string? Prefix { get; set; }

    /// <summary>
    /// Delimiter query parameter.
    /// </summary>
    public string? Delimiter { get; set; }

    /// <summary>
    /// Marker query parameter.
    /// </summary>
    public string? Marker { get; set; }

    /// <summary>
    /// Part number query parameter.
    /// </summary>
    public int PartNumber { get; set; } = 0;

    /// <summary>
    /// Part number marker query parameter.
    /// </summary>
    public int PartNumberMarker { get; set; } = 0;

    /// <summary>
    /// Max keys query parameter.
    /// </summary>
    public int MaxKeys { get; set; } = 1000;

    /// <summary>
    /// Max parts query parameter.
    /// </summary>
    public int MaxParts { get; set; } = 1000;

    /// <summary>
    /// Max uploads query parameter.
    /// </summary>
    public int MaxUploads { get; set; } = 1000;

    /// <summary>
    /// Version ID query parameter.
    /// </summary>
    public string? VersionId { get; set; }

    /// <summary>
    /// Upload ID query parameter.
    /// </summary>
    public string? UploadId { get; set; }

    /// <summary>
    /// Continuation token query parameter (ListObjectsV2).
    /// </summary>
    public string? ContinuationToken { get; set; }

    /// <summary>
    /// Start after query parameter (ListObjectsV2).
    /// </summary>
    public string? StartAfter { get; set; }

    /// <summary>
    /// Key marker query parameter.
    /// </summary>
    public string? KeyMarker { get; set; }

    /// <summary>
    /// Version ID marker query parameter.
    /// </summary>
    public string? VersionIdMarker { get; set; }

    /// <summary>
    /// Upload ID marker query parameter.
    /// </summary>
    public string? UploadIdMarker { get; set; }

    /// <summary>
    /// Fetch owner boolean (ListObjectsV2).
    /// </summary>
    public bool FetchOwner { get; set; } = false;

    /// <summary>
    /// Encoding type query parameter.
    /// </summary>
    public string? EncodingType { get; set; }

    /// <summary>
    /// Authorization header value.
    /// </summary>
    public string? Authorization { get; set; }

    /// <summary>
    /// Extracted AWS Access Key ID.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// Extracted signature.
    /// </summary>
    public string? Signature { get; set; }

    /// <summary>
    /// Extracted expiration for signed URLs.
    /// </summary>
    public string? Expires { get; set; }

    /// <summary>
    /// Signature version.
    /// </summary>
    public S3SignatureVersion SignatureVersion { get; set; } = S3SignatureVersion.Unknown;

    /// <summary>
    /// List of signed header names.
    /// </summary>
    public List<string> SignedHeaders { get; set; } = new();

    /// <summary>
    /// Content type header.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Content MD5 header.
    /// </summary>
    public string? ContentMd5 { get; set; }

    /// <summary>
    /// Content SHA256 header (SigV4).
    /// </summary>
    public string? ContentSha256 { get; set; }

    /// <summary>
    /// Date header.
    /// </summary>
    public string? Date { get; set; }

    /// <summary>
    /// Range start offset.
    /// </summary>
    public long? RangeStart { get; set; }

    /// <summary>
    /// Range end offset.
    /// </summary>
    public long? RangeEnd { get; set; }

    /// <summary>
    /// Range suffix length.
    /// </summary>
    public long? RangeSuffixLength { get; set; }

    /// <summary>
    /// Copy source bucket.
    /// </summary>
    public string? CopySourceBucket { get; set; }

    /// <summary>
    /// Copy source key.
    /// </summary>
    public string? CopySourceKey { get; set; }

    /// <summary>
    /// Copy source version ID.
    /// </summary>
    public string? CopySourceVersionId { get; set; }

    /// <summary>
    /// Copy source range start.
    /// </summary>
    public long? CopySourceRangeStart { get; set; }

    /// <summary>
    /// Copy source range end.
    /// </summary>
    public long? CopySourceRangeEnd { get; set; }

    /// <summary>
    /// Conditional headers parsed from the request.
    /// </summary>
    public List<string>? IfMatch { get; set; }

    /// <summary>
    /// Gets or sets the entity tags from the If-None-Match header.
    /// </summary>
    public List<string>? IfNoneMatch { get; set; }

    /// <summary>
    /// Gets or sets the timestamp from the If-Modified-Since header.
    /// </summary>
    public DateTime? IfModifiedSince { get; set; }

    /// <summary>
    /// Gets or sets the timestamp from the If-Unmodified-Since header.
    /// </summary>
    public DateTime? IfUnmodifiedSince { get; set; }

    /// <summary>
    /// Copy-source conditional headers parsed from x-amz-copy-source-if-* headers.
    /// </summary>
    public List<string>? CopySourceIfMatch { get; set; }

    /// <summary>
    /// Gets or sets the entity tags from the x-amz-copy-source-if-none-match header.
    /// </summary>
    public List<string>? CopySourceIfNoneMatch { get; set; }

    /// <summary>
    /// Gets or sets the timestamp from the x-amz-copy-source-if-modified-since header.
    /// </summary>
    public DateTime? CopySourceIfModifiedSince { get; set; }

    /// <summary>
    /// Gets or sets the timestamp from the x-amz-copy-source-if-unmodified-since header.
    /// </summary>
    public DateTime? CopySourceIfUnmodifiedSince { get; set; }

    /// <summary>
    /// User metadata or contextual object attached to this request.
    /// </summary>
    public object? Metadata { get; set; }

    /// <summary>
    /// Indicates whether signature validation has already completed successfully for this request.
    /// </summary>
    public bool IsSignatureValidated { get; internal set; }

    /// <summary>
    /// Deferred validation error.
    /// </summary>
    public S3Exception? ValidationError { get; private set; }

    /// <summary>
    /// Read-only dictionary of incoming HTTP headers.
    /// </summary>
    public IReadOnlyDictionary<string, string[]> Headers { get; }

    /// <summary>
    /// Decoded content length when aws-chunked transfer encoding is used (x-amz-decoded-content-length).
    /// </summary>
    public long? DecodedContentLength { get; set; }

    /// <summary>
    /// Read-only dictionary of incoming query string parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string> QueryParameters { get; }

    /// <summary>
    /// Unbuffered body stream.
    /// </summary>
    public Stream BodyStream { get; private set; }

    #endregion

    #region Private-Members

    private readonly SharpS3Settings _Settings;
    private bool _CopySourceHeaderPresent = false;

    #endregion

    #region Constructor

    /// <summary>
    /// Instantiates an S3Request from raw HTTP components.
    /// </summary>
    public S3Request(
        string httpMethod,
        string path,
        IReadOnlyDictionary<string, string> queryParameters,
        IReadOnlyDictionary<string, string[]> headers,
        Stream bodyStream,
        SharpS3Settings? settings = null)
    {
        HttpMethod = httpMethod?.ToUpperInvariant() ?? throw new ArgumentNullException(nameof(httpMethod));
        RawPath = path ?? "/";
        QueryParameters = queryParameters ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Headers = headers ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        BodyStream = bodyStream ?? Stream.Null;
        _Settings = settings ?? new SharpS3Settings();

        Parse();
    }

    #endregion

    #region Query-and-Header-Helpers

    /// <summary>
    /// Determines whether the specified query parameter key exists in the request.
    /// </summary>
    public bool QuerystringExists(string key) =>
        QueryParameters.ContainsKey(key);

    /// <summary>
    /// Determines whether any of the specified query parameter keys exist in the request.
    /// </summary>
    public bool QuerystringExistsAny(params string[] keys)
    {
        foreach (var k in keys)
            if (QueryParameters.ContainsKey(k)) return true;
        return false;
    }

    /// <summary>
    /// Retrieves the value of the specified query parameter, or null if not found.
    /// </summary>
    public string? RetrieveQueryValue(string key) =>
        QueryParameters.TryGetValue(key, out var val) ? val : null;

    /// <summary>
    /// Retrieves the value of the first matching query parameter from the provided keys, or null if none match.
    /// </summary>
    public string? RetrieveFirstQueryValue(params string[] keys)
    {
        foreach (var k in keys)
            if (QueryParameters.TryGetValue(k, out var val)) return val;
        return null;
    }

    /// <summary>
    /// Attempts to parse and retrieve an integer from the first matching query parameter key.
    /// </summary>
    public bool TryRetrieveQueryInt(out int value, params string[] keys)
    {
        string? raw = RetrieveFirstQueryValue(keys);
        if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0;
        return false;
    }

    /// <summary>
    /// Determines whether the specified header exists in the request.
    /// </summary>
    public bool HeaderExists(string key) =>
        Headers.ContainsKey(key);

    /// <summary>
    /// Retrieves the first value of the specified header, or null if not present.
    /// </summary>
    public string? RetrieveHeaderValue(string key)
    {
        if (Headers.TryGetValue(key, out var vals) && vals.Length > 0)
            return vals[0];
        return null;
    }

    /// <summary>
    /// Retrieves the first value of the specified header if it exists, or null.
    /// </summary>
    public string? RetrieveHeaderValueIfExists(string key) =>
        RetrieveHeaderValue(key);

    /// <summary>
    /// Reroutes a suffix-only byte range request to an object read request when backend resolution requires full object context.
    /// </summary>
    internal void RouteSuffixRangeAsObjectRead()
    {
        if (RequestType == S3RequestType.ObjectReadRange
            && RangeStart == null
            && RangeSuffixLength != null)
        {
            RequestType = S3RequestType.ObjectRead;
        }
    }

    #endregion

    #region Parsing-Pipeline

    private void Parse()
    {
        #region Headers

        Host = RetrieveHeaderValue("host");
        ContentType = RetrieveHeaderValue("content-type");
        ContentMd5 = RetrieveHeaderValue("content-md5");
        Date = RetrieveHeaderValue("x-amz-date") ?? RetrieveHeaderValue("date");
        RequestId = RetrieveHeaderValue("x-amz-request-id") ?? RequestId;
        TraceId = RetrieveHeaderValue("x-amz-id-2") ?? TraceId;

        ContentSha256 = RetrieveHeaderValue("x-amz-content-sha256");
        string? contentEncoding = RetrieveHeaderValue("content-encoding");
        if ((!string.IsNullOrEmpty(ContentSha256) && ContentSha256.IndexOf("streaming", StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrEmpty(contentEncoding) && contentEncoding.IndexOf("aws-chunked", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            Chunked = true;
            BodyStream = new SharpS3.Streams.AwsChunkedDecodingStream(BodyStream);
        }

        string? decodedCl = RetrieveHeaderValue("x-amz-decoded-content-length");
        if (!string.IsNullOrEmpty(decodedCl) && long.TryParse(decodedCl, NumberStyles.Integer, CultureInfo.InvariantCulture, out long dcl))
        {
            DecodedContentLength = dcl;
        }

        Authorization = RetrieveHeaderValue("authorization");
        ParseAuthorizationHeader();

        string? rangeHeader = RetrieveHeaderValue("range");
        if (!string.IsNullOrEmpty(rangeHeader))
        {
            if (TryParseRangeHeader(rangeHeader, out var start, out var end, out var suffix))
            {
                RangeStart = start;
                RangeEnd = end;
                RangeSuffixLength = suffix;
            }
        }

        string? copySource = RetrieveHeaderValue("x-amz-copy-source");
        if (!string.IsNullOrEmpty(copySource) && HttpMethod == "PUT")
        {
            _CopySourceHeaderPresent = true;
            try
            {
                ParseCopySourceHeader(copySource);
                string? copySourceRange = RetrieveHeaderValue("x-amz-copy-source-range");
                if (!string.IsNullOrEmpty(copySourceRange))
                    ParseCopySourceRangeHeader(copySourceRange);
            }
            catch (S3Exception ex)
            {
                ValidationError = ex;
            }
        }

        IfMatch = ConditionalHeaderParser.ParseEntityTagList(RetrieveHeaderValueIfExists("if-match"));
        IfNoneMatch = ConditionalHeaderParser.ParseEntityTagList(RetrieveHeaderValueIfExists("if-none-match"));
        IfModifiedSince = ConditionalHeaderParser.ParseHttpDate(RetrieveHeaderValueIfExists("if-modified-since"));
        IfUnmodifiedSince = ConditionalHeaderParser.ParseHttpDate(RetrieveHeaderValueIfExists("if-unmodified-since"));

        CopySourceIfMatch = ConditionalHeaderParser.ParseEntityTagList(RetrieveHeaderValueIfExists("x-amz-copy-source-if-match"));
        CopySourceIfNoneMatch = ConditionalHeaderParser.ParseEntityTagList(RetrieveHeaderValueIfExists("x-amz-copy-source-if-none-match"));
        CopySourceIfModifiedSince = ConditionalHeaderParser.ParseHttpDate(RetrieveHeaderValueIfExists("x-amz-copy-source-if-modified-since"));
        CopySourceIfUnmodifiedSince = ConditionalHeaderParser.ParseHttpDate(RetrieveHeaderValueIfExists("x-amz-copy-source-if-unmodified-since"));

        #endregion

        #region Query-Parameters

        string? amzAlgo = RetrieveFirstQueryValue("x-amz-algorithm", "X-Amz-Algorithm");
        string? amzCred = RetrieveFirstQueryValue("x-amz-credential", "X-Amz-Credential");
        string? amzDate = RetrieveFirstQueryValue("x-amz-date", "X-Amz-Date");
        string? amzSignedHeaders = RetrieveFirstQueryValue("x-amz-signedheaders", "X-Amz-SignedHeaders");

        Date = Date ?? amzDate;

        if (SignatureVersion == S3SignatureVersion.Unknown && (!string.IsNullOrEmpty(amzAlgo) || !string.IsNullOrEmpty(amzCred)))
        {
            SignatureVersion = S3SignatureVersion.Version4;
            if (!string.IsNullOrEmpty(amzCred))
            {
                string[] parts = amzCred.Split('/');
                AccessKey = parts[0].Trim();
                if (parts.Length >= 5)
                {
                    Region = parts[2].Trim();
                }
            }
            if (!string.IsNullOrEmpty(amzSignedHeaders))
            {
                SignedHeaders = amzSignedHeaders.Split(';').Select(s => s.Trim().ToLowerInvariant()).ToList();
            }
        }

        AccessKey = AccessKey ?? RetrieveFirstQueryValue("awsaccesskeyid", "AWSAccessKeyId");
        Signature = Signature ?? RetrieveFirstQueryValue("signature", "Signature", "X-Amz-Signature");
        string? expires = RetrieveFirstQueryValue("expires", "Expires", "X-Amz-Expires");
        Expires = expires;
        if (SignatureVersion == S3SignatureVersion.Unknown && !string.IsNullOrEmpty(AccessKey) && !string.IsNullOrEmpty(expires))
        {
            SignatureVersion = S3SignatureVersion.Version2;
        }

        ContinuationToken = RetrieveQueryValue("continuation-token");
        StartAfter = RetrieveQueryValue("start-after");
        FetchOwner = string.Equals(RetrieveQueryValue("fetch-owner"), "true", StringComparison.OrdinalIgnoreCase);
        Delimiter = RetrieveQueryValue("delimiter");
        Marker = RetrieveQueryValue("marker");
        Prefix = RetrieveQueryValue("prefix");
        UploadId = RetrieveFirstQueryValue("uploadid", "uploadId", "UploadId");
        VersionId = RetrieveFirstQueryValue("versionid", "versionId", "VersionId");

        if (TryRetrieveQueryInt(out int maxKeys, "max-keys")) MaxKeys = maxKeys;
        if (TryRetrieveQueryInt(out int maxParts, "max-parts", "maxParts", "MaxParts")) MaxParts = maxParts;
        if (TryRetrieveQueryInt(out int partNum, "partnumber", "partNumber", "PartNumber")) PartNumber = partNum;
        if (TryRetrieveQueryInt(out int partNumMarker, "part-number-marker", "partNumberMarker", "PartNumberMarker")) PartNumberMarker = partNumMarker;
        if (TryRetrieveQueryInt(out int maxUploads, "max-uploads")) MaxUploads = maxUploads;

        KeyMarker = RetrieveQueryValue("key-marker");
        VersionIdMarker = RetrieveQueryValue("version-id-marker");
        UploadIdMarker = RetrieveQueryValue("upload-id-marker");
        EncodingType = RetrieveQueryValue("encoding-type");

        #endregion

        #region Hostname-and-Path

        ParseHostnameAndPath();

        #endregion

        #region Request-Type

        SetRequestType();

        #endregion
    }

    private void ParseHostnameAndPath()
    {
        string hostOnly = Host ?? "localhost";
        if (hostOnly.StartsWith("["))
        {
            int closeBracket = hostOnly.IndexOf(']');
            if (closeBracket > 0)
            {
                hostOnly = hostOnly.Substring(0, closeBracket + 1);
            }
        }
        else if (hostOnly.Contains(':'))
        {
            hostOnly = hostOnly.Split(':')[0];
        }

        Hostname = hostOnly;

        string cleanHost = Hostname.Trim('[', ']');
        bool isIp = IPAddress.TryParse(cleanHost, out _) || Hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        string? matchedBaseDomain = !isIp
            ? _Settings.GetMatchingBaseDomain(Hostname)
            : null;

        if (!isIp && !string.IsNullOrEmpty(matchedBaseDomain) && Hostname.EndsWith(matchedBaseDomain, StringComparison.OrdinalIgnoreCase) && Hostname.Length > matchedBaseDomain.Length)
        {
            RequestStyle = S3RequestStyle.VirtualHostedStyle;
            BaseDomain = matchedBaseDomain;
            string prefix = Hostname.Substring(0, Hostname.Length - matchedBaseDomain.Length).TrimEnd('.');
            Bucket = prefix;

            string path = RawPath.TrimStart('/');
            Key = string.IsNullOrEmpty(path) ? null : Uri.UnescapeDataString(path);
        }
        else
        {
            RequestStyle = S3RequestStyle.PathStyle;
            string pathWithoutLeading = RawPath.TrimStart('/');
            if (string.IsNullOrEmpty(pathWithoutLeading))
            {
                Bucket = null;
                Key = null;
            }
            else
            {
                int slash = pathWithoutLeading.IndexOf('/');
                if (slash < 0)
                {
                    Bucket = pathWithoutLeading.TrimEnd('/');
                    Key = null;
                }
                else
                {
                    Bucket = pathWithoutLeading.Substring(0, slash);
                    string rawKey = pathWithoutLeading.Substring(slash + 1);
                    Key = string.IsNullOrEmpty(rawKey) ? null : Uri.UnescapeDataString(rawKey);
                }
            }
        }
    }

    private void SetRequestType()
    {
        switch (HttpMethod)
        {
            case "HEAD":
                if (string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                    RequestType = S3RequestType.ServiceExists;
                else if (!string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                    RequestType = S3RequestType.BucketExists;
                else if (!string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(Key))
                    RequestType = S3RequestType.ObjectExists;
                break;

            case "GET":
                if (string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                {
                    RequestType = S3RequestType.ListBuckets;
                }
                else if (!string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                {
                    if (QuerystringExists("acl"))
                        RequestType = S3RequestType.BucketReadAcl;
                    else if (QuerystringExists("location"))
                        RequestType = S3RequestType.BucketReadLocation;
                    else if (QuerystringExists("logging"))
                        RequestType = S3RequestType.BucketReadLogging;
                    else if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.BucketReadTags;
                    else if (QuerystringExists("uploads"))
                        RequestType = S3RequestType.BucketReadMultipartUploads;
                    else if (QuerystringExists("versions"))
                        RequestType = S3RequestType.BucketReadVersions;
                    else if (QuerystringExists("versioning"))
                        RequestType = S3RequestType.BucketReadVersioning;
                    else if (QuerystringExists("website"))
                        RequestType = S3RequestType.BucketReadWebsite;
                    else
                        RequestType = S3RequestType.BucketRead;
                }
                else if (!string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(Key))
                {
                    if (HeaderExists("range") && (RangeStart != null || RangeSuffixLength != null))
                        RequestType = S3RequestType.ObjectReadRange;
                    else if (QuerystringExists("acl"))
                        RequestType = S3RequestType.ObjectReadAcl;
                    else if (QuerystringExists("legal-hold"))
                        RequestType = S3RequestType.ObjectReadLegalHold;
                    else if (QuerystringExistsAny("uploadid", "uploadId", "UploadId"))
                        RequestType = S3RequestType.ObjectReadParts;
                    else if (QuerystringExists("retention"))
                        RequestType = S3RequestType.ObjectReadRetention;
                    else if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.ObjectReadTags;
                    else
                        RequestType = S3RequestType.ObjectRead;
                }
                break;

            case "PUT":
                if (!string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                {
                    if (QuerystringExists("acl"))
                        RequestType = S3RequestType.BucketWriteAcl;
                    else if (QuerystringExists("logging"))
                        RequestType = S3RequestType.BucketWriteLogging;
                    else if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.BucketWriteTags;
                    else if (QuerystringExists("versioning"))
                        RequestType = S3RequestType.BucketWriteVersioning;
                    else if (QuerystringExists("website"))
                        RequestType = S3RequestType.BucketWriteWebsite;
                    else
                        RequestType = S3RequestType.BucketWrite;
                }
                else if (!string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(Key))
                {
                    if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.ObjectWriteTags;
                    else if (QuerystringExists("acl"))
                        RequestType = S3RequestType.ObjectWriteAcl;
                    else if (QuerystringExists("legal-hold"))
                        RequestType = S3RequestType.ObjectWriteLegalHold;
                    else if (QuerystringExists("retention"))
                        RequestType = S3RequestType.ObjectWriteRetention;
                    else if (QuerystringExistsAny("partnumber", "partNumber", "PartNumber") && QuerystringExistsAny("uploadid", "uploadId", "UploadId"))
                        RequestType = _CopySourceHeaderPresent ? S3RequestType.ObjectUploadPartCopy : S3RequestType.ObjectUploadPart;
                    else if (_CopySourceHeaderPresent)
                        RequestType = S3RequestType.ObjectCopy;
                    else
                        RequestType = S3RequestType.ObjectWrite;
                }
                break;

            case "POST":
                if (!string.IsNullOrEmpty(Bucket))
                {
                    if (QuerystringExists("delete"))
                        RequestType = S3RequestType.ObjectDeleteMultiple;

                    if (!string.IsNullOrEmpty(Key))
                    {
                        if (QuerystringExists("restore"))
                            RequestType = S3RequestType.ObjectRestore;
                        if (QuerystringExists("select") && QuerystringExists("select-type") && RetrieveQueryValue("select-type") == "2")
                            RequestType = S3RequestType.ObjectSelectContent;
                        if (QuerystringExistsAny("uploadid", "uploadId", "UploadId"))
                            RequestType = S3RequestType.ObjectCompleteMultipartUpload;
                        if (QuerystringExists("uploads"))
                            RequestType = S3RequestType.ObjectCreateMultipartUpload;
                    }
                }
                break;

            case "DELETE":
                if (!string.IsNullOrEmpty(Bucket) && string.IsNullOrEmpty(Key))
                {
                    if (QuerystringExists("acl"))
                        RequestType = S3RequestType.BucketDeleteAcl;
                    else if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.BucketDeleteTags;
                    else if (QuerystringExists("website"))
                        RequestType = S3RequestType.BucketDeleteWebsite;
                    else
                        RequestType = S3RequestType.BucketDelete;
                }
                else if (!string.IsNullOrEmpty(Bucket) && !string.IsNullOrEmpty(Key))
                {
                    if (QuerystringExists("acl"))
                        RequestType = S3RequestType.ObjectDeleteAcl;
                    else if (QuerystringExists("tagging"))
                        RequestType = S3RequestType.ObjectDeleteTags;
                    else if (QuerystringExistsAny("uploadid", "uploadId", "UploadId"))
                        RequestType = S3RequestType.ObjectAbortMultipartUpload;
                    else
                        RequestType = S3RequestType.ObjectDelete;
                }
                break;
        }
    }

    private void ParseAuthorizationHeader()
    {
        if (string.IsNullOrEmpty(Authorization))
            return;

        string[] outer = Authorization.Split(new[] { ' ' }, 2);
        if (outer.Length < 2) return;

        if (outer[0].Equals("AWS", StringComparison.OrdinalIgnoreCase))
        {
            SignatureVersion = S3SignatureVersion.Version2;
            string[] inner = outer[1].Split(':');
            if (inner.Length == 2)
            {
                AccessKey = inner[0].Trim();
                Signature = inner[1].Trim();
            }
        }
        else if (outer[0].Equals("AWS4-HMAC-SHA256", StringComparison.OrdinalIgnoreCase))
        {
            SignatureVersion = S3SignatureVersion.Version4;
            string[] pairs = outer[1].Split(',');
            foreach (var rawPair in pairs)
            {
                string pair = rawPair.Trim();
                if (pair.StartsWith("Credential=", StringComparison.OrdinalIgnoreCase))
                {
                    string cred = pair.Substring("Credential=".Length).Trim();
                    string[] parts = cred.Split('/');
                    if (parts.Length >= 5)
                    {
                        AccessKey = parts[0].Trim();
                        Region = parts[2].Trim();
                    }
                }
                else if (pair.StartsWith("SignedHeaders=", StringComparison.OrdinalIgnoreCase))
                {
                    string sh = pair.Substring("SignedHeaders=".Length).Trim();
                    SignedHeaders = sh.Split(';').Select(s => s.Trim().ToLowerInvariant()).ToList();
                }
                else if (pair.StartsWith("Signature=", StringComparison.OrdinalIgnoreCase))
                {
                    Signature = pair.Substring("Signature=".Length).Trim();
                }
            }
        }
    }

    private void ParseCopySourceHeader(string header)
    {
        string trimmed = header.Trim().TrimStart('/');
        int slash = trimmed.IndexOf('/');
        if (slash < 0)
        {
            CopySourceBucket = trimmed;
            CopySourceKey = null;
        }
        else
        {
            CopySourceBucket = trimmed.Substring(0, slash);
            string remainder = trimmed.Substring(slash + 1);
            int question = remainder.IndexOf('?');
            if (question >= 0)
            {
                CopySourceKey = Uri.UnescapeDataString(remainder.Substring(0, question));
                string query = remainder.Substring(question + 1);
                if (query.StartsWith("versionId=", StringComparison.OrdinalIgnoreCase))
                    CopySourceVersionId = query.Substring("versionId=".Length);
            }
            else
            {
                CopySourceKey = Uri.UnescapeDataString(remainder);
            }
        }
    }

    private void ParseCopySourceRangeHeader(string header)
    {
        if (TryParseRangeHeader(header, out var start, out var end, out _))
        {
            CopySourceRangeStart = start;
            CopySourceRangeEnd = end;
        }
        else
        {
            throw new S3Exception(new Error(ErrorCode.InvalidArgument));
        }
    }

    /// <summary>
    /// Attempts to parse a standard HTTP Range header (e.g. "bytes=0-499") into start, end, or suffix length components.
    /// </summary>
    internal static bool TryParseRangeHeader(string header, out long? start, out long? end, out long? suffixLength)
    {
        start = null;
        end = null;
        suffixLength = null;

        if (string.IsNullOrWhiteSpace(header)) return false;

        string value = header.Trim();
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;

        value = value.Substring(6).Trim();
        if (value.IndexOf(',') >= 0) return false;

        int dash = value.IndexOf('-');
        if (dash < 0 || dash != value.LastIndexOf('-')) return false;

        string first = value.Substring(0, dash).Trim();
        string last = value.Substring(dash + 1).Trim();

        if (first.Length == 0 && last.Length == 0) return false;

        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix)) return false;
            suffixLength = suffix;
            return true;
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out long firstPos)) return false;

        if (last.Length == 0)
        {
            start = firstPos;
            return true;
        }

        if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long lastPos)) return false;
        if (lastPos < firstPos) return false;

        start = firstPos;
        end = lastPos;
        return true;
    }

    #endregion
}
