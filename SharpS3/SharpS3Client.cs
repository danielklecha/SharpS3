namespace SharpS3;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Serialization;

/// <summary>
/// Transport-agnostic Amazon S3 compatible client.
/// </summary>
public class SharpS3Client : ISharpS3Client
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly SharpS3ClientOptions _options;
    private bool _disposed;

    public SharpS3Client(SharpS3ClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _disposeHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient { Timeout = _options.Timeout };
            _disposeHttpClient = true;
        }
    }

    // --- Service Operations ---

    /// <inheritdoc />
    public async Task<ListAllMyBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendRequestAsync(HttpMethod.Get, bucket: null, key: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<ListAllMyBucketsResult>(xml);
    }

    // --- Bucket Operations ---

    /// <inheritdoc />
    public async Task CreateBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        using var response = await SendRequestAsync(HttpMethod.Delete, bucket, key: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        try
        {
            using var response = await SendRequestAsync(HttpMethod.Head, bucket, key: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SharpS3ClientException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));

        var query = new Dictionary<string, string> { { "location", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<LocationConstraint>(xml);
    }

    /// <inheritdoc />
    public async Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "acl", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<AccessControlPolicy>(xml);
    }

    /// <inheritdoc />
    public async Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (acl == null) throw new ArgumentNullException(nameof(acl));
        var query = new Dictionary<string, string> { { "acl", "" } };
        string xml = SerializationHelper.SerializeXml(acl, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "tagging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<Tagging>(xml);
    }

    /// <inheritdoc />
    public async Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (tags == null) throw new ArgumentNullException(nameof(tags));
        var query = new Dictionary<string, string> { { "tagging", "" } };
        string xml = SerializationHelper.SerializeXml(tags, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "tagging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Delete, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "versioning", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<VersioningConfiguration>(xml);
    }

    /// <inheritdoc />
    public async Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        var query = new Dictionary<string, string> { { "versioning", "" } };
        string xml = SerializationHelper.SerializeXml(configuration, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "website", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<WebsiteConfiguration>(xml);
    }

    /// <inheritdoc />
    public async Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        var query = new Dictionary<string, string> { { "website", "" } };
        string xml = SerializationHelper.SerializeXml(configuration, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "website", "" } };
        using var response = await SendRequestAsync(HttpMethod.Delete, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        var query = new Dictionary<string, string> { { "logging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<BucketLoggingStatus>(xml);
    }

    /// <inheritdoc />
    public async Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus loggingStatus, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (loggingStatus == null) throw new ArgumentNullException(nameof(loggingStatus));
        var query = new Dictionary<string, string> { { "logging", "" } };
        string xml = SerializationHelper.SerializeXml(loggingStatus, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key: null, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // --- Object Operations ---

    /// <inheritdoc />
    public async Task<S3ObjectResponse> GetObjectAsync(
        string bucket,
        string key,
        ByteRange? range = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (range != null)
        {
            headers["Range"] = range.ToString()!;
        }

        var response = await SendRequestAsync(
            HttpMethod.Get,
            bucket,
            key,
            headers: headers,
            completionOption: HttpCompletionOption.ResponseHeadersRead,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

#if NET8_0_OR_GREATER
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        long contentLength = response.Content.Headers.ContentLength ?? 0;
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        string? eTag = response.Headers.ETag?.Tag ?? GetHeaderValue(response.Headers, "ETag");
        DateTimeOffset? lastModified = response.Content.Headers.LastModified;

        var metadata = ExtractMetadata(response);

        return new S3ObjectResponse(response, stream, contentLength, contentType, eTag, lastModified, metadata);
    }

    /// <inheritdoc />
    public Task PutObjectAsync(
        string bucket,
        string key,
        byte[] content,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        var ms = new MemoryStream(content, writable: false);
        return PutObjectAsync(bucket, key, ms, content.Length, contentType, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public Task PutObjectAsync(
        string bucket,
        string key,
        string content,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return PutObjectAsync(bucket, key, bytes, contentType ?? "text/plain; charset=utf-8", metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (content == null) throw new ArgumentNullException(nameof(content));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMetadataHeaders(headers, metadata);

        using var httpContent = new StreamContent(new NonDisposingStream(content));
        httpContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
        httpContent.Headers.ContentLength = contentLength;

        using var response = await SendRequestAsync(
            HttpMethod.Put,
            bucket,
            key,
            headers: headers,
            content: httpContent,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ObjectMetadataResponse?> HeadObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        try
        {
            using var response = await SendRequestAsync(HttpMethod.Head, bucket, key, cancellationToken: cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

            return new ObjectMetadataResponse
            {
                ContentLength = response.Content.Headers.ContentLength ?? 0,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                ETag = response.Headers.ETag?.Tag ?? GetHeaderValue(response.Headers, "ETag"),
                LastModified = response.Content.Headers.LastModified,
                Metadata = ExtractMetadata(response)
            };
        }
        catch (SharpS3ClientException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ObjectExistsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var meta = await HeadObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
        return meta != null;
    }

    /// <inheritdoc />
    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        using var response = await SendRequestAsync(HttpMethod.Delete, bucket, key, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DeleteResult> DeleteObjectsAsync(string bucket, DeleteMultiple delete, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (delete == null) throw new ArgumentNullException(nameof(delete));

        string xmlBody = SerializationHelper.SerializeXml(delete);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(xmlBody);

        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/xml");

        var query = new Dictionary<string, string> { { "delete", "" } };
        using var response = await SendRequestAsync(
            HttpMethod.Post,
            bucket,
            key: null,
            queryParams: query,
            content: content,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string respXml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<DeleteResult>(respXml);
    }

    /// <inheritdoc />
    public async Task<CopyObjectResult> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceBucket)) throw new ArgumentNullException(nameof(sourceBucket));
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentNullException(nameof(sourceKey));
        if (string.IsNullOrWhiteSpace(destinationBucket)) throw new ArgumentNullException(nameof(destinationBucket));
        if (string.IsNullOrWhiteSpace(destinationKey)) throw new ArgumentNullException(nameof(destinationKey));

        string copySource = $"/{Uri.EscapeDataString(sourceBucket)}/{Uri.EscapeDataString(sourceKey)}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "x-amz-copy-source", copySource }
        };

        using var response = await SendRequestAsync(
            HttpMethod.Put,
            destinationBucket,
            destinationKey,
            headers: headers,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<CopyObjectResult>(xml);
    }

    /// <inheritdoc />
    public async Task<ListBucketResult> ListObjectsV2Async(
        string bucket,
        string? prefix = null,
        string? continuationToken = null,
        int? maxKeys = null,
        string? delimiter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));

        var query = new Dictionary<string, string>
        {
            { "list-type", "2" }
        };

        if (!string.IsNullOrEmpty(prefix)) query["prefix"] = prefix;
        if (!string.IsNullOrEmpty(continuationToken)) query["continuation-token"] = continuationToken;
        if (maxKeys.HasValue) query["max-keys"] = maxKeys.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(delimiter)) query["delimiter"] = delimiter;

        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key: null, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<ListBucketResult>(xml);
    }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    public async IAsyncEnumerable<ObjectMetadata> ListAllObjectsAsync(
        string bucket,
        string? prefix = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? continuationToken = null;
        do
        {
            var result = await ListObjectsV2Async(bucket, prefix, continuationToken, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Contents != null)
            {
                foreach (var item in result.Contents)
                {
                    yield return item;
                }
            }

            continuationToken = result.IsTruncated ? result.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuationToken));
    }
#endif

    // --- Object Tagging Operations ---

    /// <inheritdoc />
    public async Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var query = new Dictionary<string, string> { { "tagging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<Tagging>(xml);
    }

    /// <inheritdoc />
    public async Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (tags == null) throw new ArgumentNullException(nameof(tags));

        string xmlBody = SerializationHelper.SerializeXml(tags);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(xmlBody);

        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/xml");

        var query = new Dictionary<string, string> { { "tagging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var query = new Dictionary<string, string> { { "tagging", "" } };
        using var response = await SendRequestAsync(HttpMethod.Delete, bucket, key, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // --- Object Governance Operations ---

    /// <inheritdoc />
    public async Task<LegalHold> GetObjectLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var query = new Dictionary<string, string> { { "legal-hold", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<LegalHold>(xml);
    }

    /// <inheritdoc />
    public async Task SetObjectLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (legalHold == null) throw new ArgumentNullException(nameof(legalHold));

        var query = new Dictionary<string, string> { { "legal-hold", "" } };
        string xml = SerializationHelper.SerializeXml(legalHold, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Retention> GetObjectRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var query = new Dictionary<string, string> { { "retention", "" } };
        using var response = await SendRequestAsync(HttpMethod.Get, bucket, key, queryParams: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<Retention>(xml);
    }

    /// <inheritdoc />
    public async Task SetObjectRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (retention == null) throw new ArgumentNullException(nameof(retention));

        var query = new Dictionary<string, string> { { "retention", "" } };
        string xml = SerializationHelper.SerializeXml(retention, true);
        using var content = new StringContent(xml, Encoding.UTF8, "application/xml");
        using var response = await SendRequestAsync(HttpMethod.Put, bucket, key, queryParams: query, content: content, cancellationToken: cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    // --- High-Level Convenience Methods ---

    /// <inheritdoc />
    public async Task UploadFileAsync(
        string bucket,
        string key,
        string filePath,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("File to upload was not found", filePath);
        var fi = new FileInfo(filePath);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await PutObjectAsync(bucket, key, fs, fi.Length, contentType, metadata, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DownloadFileAsync(
        string bucket,
        string key,
        string destinationFilePath,
        CancellationToken cancellationToken = default)
    {
        string? dir = Path.GetDirectoryName(destinationFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var obj = await GetObjectAsync(bucket, key, null, cancellationToken).ConfigureAwait(false);
        try
        {
            using (var fs = new FileStream(destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await obj.Stream.CopyToAsync(fs, 81920, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(destinationFilePath))
                {
                    File.Delete(destinationFilePath);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    /// <inheritdoc />
    public Uri GeneratePresignedUrl(string bucket, string key, HttpMethod method, TimeSpan expiresIn)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (expiresIn <= TimeSpan.Zero || expiresIn > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(expiresIn), "Presigned URL expiration must be between 1 second and 7 days.");

        var endpoint = _options.Endpoint;
        var builder = new UriBuilder(endpoint);

        var pathBuilder = new StringBuilder();
        if (_options.ForcePathStyle)
        {
            string basePath = endpoint.AbsolutePath.TrimEnd('/');
            if (!string.IsNullOrEmpty(basePath)) pathBuilder.Append(basePath);
            pathBuilder.Append('/').Append(Uri.EscapeDataString(bucket));
        }
        else
        {
            builder.Host = $"{bucket}.{endpoint.Host}";
        }

        if (!string.IsNullOrEmpty(key))
        {
            string[] segments = key!.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                pathBuilder.Append('/').Append(Uri.EscapeDataString(segments[i]));
            }
        }
        builder.Path = pathBuilder.ToString();

        // If no credentials configured, return unauthenticated URL
        if (string.IsNullOrEmpty(_options.AccessKey) || string.IsNullOrEmpty(_options.SecretKey))
        {
            return builder.Uri;
        }

        DateTime utcNow = DateTime.UtcNow;
        string timestamp = utcNow.ToString("yyyyMMddTHHmmssZ");
        string date = utcNow.ToString("yyyyMMdd");
        string region = _options.Region;
        string credential = $"{_options.AccessKey}/{date}/{region}/s3/aws4_request";
        int expiresSeconds = (int)expiresIn.TotalSeconds;

        string hostHeader = builder.Uri.IsDefaultPort
            ? builder.Host
            : $"{builder.Host}:{builder.Port}";

        // Sorted query parameters for AWS SigV4 presigned URL
        var qParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            { "X-Amz-Algorithm", "AWS4-HMAC-SHA256" },
            { "X-Amz-Credential", credential },
            { "X-Amz-Date", timestamp },
            { "X-Amz-Expires", expiresSeconds.ToString(CultureInfo.InvariantCulture) },
            { "X-Amz-SignedHeaders", "host" }
        };

        var canonicalQueryBuilder = new StringBuilder();
        bool first = true;
        foreach (var kvp in qParams)
        {
            if (!first) canonicalQueryBuilder.Append('&');
            first = false;
            canonicalQueryBuilder.Append(Uri.EscapeDataString(kvp.Key)).Append('=').Append(Uri.EscapeDataString(kvp.Value));
        }
        string canonicalQuery = canonicalQueryBuilder.ToString();

        // Canonical Request
        string canonicalUri = builder.Path;
        string canonicalHeaders = $"host:{hostHeader}\n";
        string signedHeaders = "host";
        string payloadHash = "UNSIGNED-PAYLOAD";

        string canonicalRequest = $"{method.Method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        // String to sign
        byte[] canonicalRequestHash;
        using (var sha = SHA256.Create())
        {
            canonicalRequestHash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalRequest));
        }
        string hexCanonicalHash = BitConverter.ToString(canonicalRequestHash).Replace("-", "").ToLowerInvariant();

        string stringToSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{date}/{region}/s3/aws4_request\n{hexCanonicalHash}";

        // Calculate signature
        byte[] kSigning = GetSignatureKey(_options.SecretKey, date, region, "s3");
        byte[] signatureBytes;
        using (var hmac = new HMACSHA256(kSigning))
        {
            signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign));
        }
        string signature = BitConverter.ToString(signatureBytes).Replace("-", "").ToLowerInvariant();

        builder.Query = $"{canonicalQuery}&X-Amz-Signature={signature}";
        return builder.Uri;
    }

    // --- Multipart Upload Operations ---

    /// <inheritdoc />
    public async Task<InitiateMultipartUploadResult> InitiateMultipartUploadAsync(
        string bucket,
        string key,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMetadataHeaders(headers, metadata);

        var query = new Dictionary<string, string> { { "uploads", "" } };
        using var response = await SendRequestAsync(
            HttpMethod.Post,
            bucket,
            key,
            queryParams: query,
            headers: headers,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<InitiateMultipartUploadResult>(xml);
    }

    /// <inheritdoc />
    public Task<UploadPartResult> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        var ms = new MemoryStream(content, writable: false);
        return UploadPartAsync(bucket, key, uploadId, partNumber, ms, content.Length, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UploadPartResult> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentNullException(nameof(uploadId));
        if (content == null) throw new ArgumentNullException(nameof(content));

        var query = new Dictionary<string, string>
        {
            { "partNumber", partNumber.ToString(CultureInfo.InvariantCulture) },
            { "uploadId", uploadId }
        };

        using var httpContent = new StreamContent(new NonDisposingStream(content));
        httpContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        httpContent.Headers.ContentLength = contentLength;

        using var response = await SendRequestAsync(
            HttpMethod.Put,
            bucket,
            key,
            queryParams: query,
            content: httpContent,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        string? eTag = response.Headers.ETag?.Tag ?? GetHeaderValue(response.Headers, "ETag");
        if (string.IsNullOrEmpty(eTag))
        {
            eTag = string.Empty;
        }

        return new UploadPartResult
        {
            PartNumber = partNumber,
            ETag = eTag!
        };
    }

    /// <inheritdoc />
    public async Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CompleteMultipartUpload complete,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentNullException(nameof(uploadId));
        if (complete == null) throw new ArgumentNullException(nameof(complete));

        string xmlBody = SerializationHelper.SerializeXml(complete);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(xmlBody);

        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/xml");

        var query = new Dictionary<string, string>
        {
            { "uploadId", uploadId }
        };

        using var response = await SendRequestAsync(
            HttpMethod.Post,
            bucket,
            key,
            queryParams: query,
            content: content,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string respXml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<CompleteMultipartUploadResult>(respXml);
    }

    /// <inheritdoc />
    public async Task AbortMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentNullException(nameof(uploadId));

        var query = new Dictionary<string, string>
        {
            { "uploadId", uploadId }
        };

        using var response = await SendRequestAsync(
            HttpMethod.Delete,
            bucket,
            key,
            queryParams: query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<CopyPartResult> UploadPartCopyAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        string uploadId,
        int partNumber,
        string? sourceVersionId = null,
        ByteRange? sourceRange = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceBucket)) throw new ArgumentNullException(nameof(sourceBucket));
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentNullException(nameof(sourceKey));
        if (string.IsNullOrWhiteSpace(destinationBucket)) throw new ArgumentNullException(nameof(destinationBucket));
        if (string.IsNullOrWhiteSpace(destinationKey)) throw new ArgumentNullException(nameof(destinationKey));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentNullException(nameof(uploadId));
        if (partNumber < 1 || partNumber > 10000)
            throw new ArgumentOutOfRangeException(nameof(partNumber), "Part number must be between 1 and 10000.");

        string copySource = $"/{Uri.EscapeDataString(sourceBucket)}/{Uri.EscapeDataString(sourceKey)}";
        if (!string.IsNullOrEmpty(sourceVersionId))
        {
            copySource += $"?versionId={Uri.EscapeDataString(sourceVersionId)}";
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "x-amz-copy-source", copySource }
        };

        if (sourceRange != null)
        {
            headers["x-amz-copy-source-range"] = sourceRange.ToString()!;
        }

        var query = new Dictionary<string, string>
        {
            { "partNumber", partNumber.ToString(CultureInfo.InvariantCulture) },
            { "uploadId", uploadId }
        };

        using var response = await SendRequestAsync(
            HttpMethod.Put,
            destinationBucket,
            destinationKey,
            queryParams: query,
            headers: headers,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<CopyPartResult>(xml);
    }

    /// <inheritdoc />
    public async Task<ListPartsResult> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        int? maxParts = null,
        int? partNumberMarker = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(uploadId)) throw new ArgumentNullException(nameof(uploadId));

        var query = new Dictionary<string, string>
        {
            { "uploadId", uploadId }
        };

        if (maxParts.HasValue)
            query["max-parts"] = maxParts.Value.ToString(CultureInfo.InvariantCulture);
        if (partNumberMarker.HasValue)
            query["part-number-marker"] = partNumberMarker.Value.ToString(CultureInfo.InvariantCulture);

        using var response = await SendRequestAsync(
            HttpMethod.Get,
            bucket,
            key,
            queryParams: query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<ListPartsResult>(xml);
    }

    /// <inheritdoc />
    public async Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(
        string bucket,
        string? prefix = null,
        string? delimiter = null,
        string? keyMarker = null,
        string? uploadIdMarker = null,
        int? maxUploads = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentNullException(nameof(bucket));

        var query = new Dictionary<string, string>
        {
            { "uploads", "" }
        };

        if (!string.IsNullOrEmpty(prefix)) query["prefix"] = prefix;
        if (!string.IsNullOrEmpty(delimiter)) query["delimiter"] = delimiter;
        if (!string.IsNullOrEmpty(keyMarker)) query["key-marker"] = keyMarker;
        if (!string.IsNullOrEmpty(uploadIdMarker)) query["upload-id-marker"] = uploadIdMarker;
        if (maxUploads.HasValue)
            query["max-uploads"] = maxUploads.Value.ToString(CultureInfo.InvariantCulture);

        using var response = await SendRequestAsync(
            HttpMethod.Get,
            bucket,
            key: null,
            queryParams: query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return SerializationHelper.DeserializeXml<ListMultipartUploadsResult>(xml);
    }

    // --- Internal HTTP & Signing Helpers ---

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpMethod method,
        string? bucket,
        string? key,
        IDictionary<string, string>? queryParams = null,
        IDictionary<string, string>? headers = null,
        HttpContent? content = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        Uri requestUri = BuildUri(bucket, key, queryParams);
        var request = new HttpRequestMessage(method, requestUri);

        if (content != null)
        {
            request.Content = content;
        }

        string hostHeader = requestUri.IsDefaultPort
            ? requestUri.Host
            : $"{requestUri.Host}:{requestUri.Port}";

        request.Headers.Host = hostHeader;

        if (headers != null)
        {
            foreach (var kvp in headers)
            {
                if (kvp.Key.Equals("Range", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation("Range", kvp.Value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value);
                }
            }
        }

        // Apply SigV4 authentication if credentials are provided
        if (!string.IsNullOrEmpty(_options.AccessKey) && !string.IsNullOrEmpty(_options.SecretKey))
        {
            SignRequest(request, requestUri, method);
        }

        return await _httpClient.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
    }

    private Uri BuildUri(string? bucket, string? key, IDictionary<string, string>? queryParams)
    {
        var baseUri = _options.Endpoint;
        var builder = new UriBuilder(baseUri);

        var pathBuilder = new StringBuilder();
        if (_options.ForcePathStyle || string.IsNullOrEmpty(bucket))
        {
            string basePath = baseUri.AbsolutePath.TrimEnd('/');
            if (!string.IsNullOrEmpty(basePath)) pathBuilder.Append(basePath);

            if (!string.IsNullOrEmpty(bucket))
            {
                pathBuilder.Append('/').Append(Uri.EscapeDataString(bucket));
            }
        }
        else
        {
            // Virtual-hosted style: bucket.host
            builder.Host = $"{bucket}.{baseUri.Host}";
        }

        if (!string.IsNullOrEmpty(key))
        {
            string[] segments = key!.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                pathBuilder.Append('/').Append(Uri.EscapeDataString(segments[i]));
            }
        }

        if (pathBuilder.Length == 0)
        {
            pathBuilder.Append('/');
        }

        builder.Path = pathBuilder.ToString();

        if (queryParams != null && queryParams.Count > 0)
        {
            var qb = new StringBuilder();
            bool first = true;
            foreach (var kvp in queryParams)
            {
                if (!first) qb.Append('&');
                first = false;
                qb.Append(Uri.EscapeDataString(kvp.Key));
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    qb.Append('=').Append(Uri.EscapeDataString(kvp.Value));
                }
            }
            builder.Query = qb.ToString();
        }

        return builder.Uri;
    }

    private void SignRequest(HttpRequestMessage request, Uri uri, HttpMethod method)
    {
        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);

        string contentSha256 = "UNSIGNED-PAYLOAD";
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", contentSha256);

        var nvc = new NameValueCollection();
        var signedHeadersList = new List<string>();

        foreach (var header in request.Headers)
        {
            string keyLower = header.Key.ToLowerInvariant();
            signedHeadersList.Add(keyLower);
            foreach (var val in header.Value)
            {
                nvc.Add(keyLower, val);
            }
        }

        if (request.Content != null)
        {
            foreach (var header in request.Content.Headers)
            {
                string keyLower = header.Key.ToLowerInvariant();
                signedHeadersList.Add(keyLower);
                foreach (var val in header.Value)
                {
                    nvc.Add(keyLower, val);
                }
            }
        }

        signedHeadersList.Sort(StringComparer.Ordinal);

        var sigResult = new AwsSignatureV4(
            timestamp,
            method.Method,
            uri.ToString(),
            _options.AccessKey!,
            _options.SecretKey!,
            _options.Region,
            "s3",
            nvc,
            signedHeadersList,
            null,
            AwsPayloadHashMode.Unsigned);

        request.Headers.TryAddWithoutValidation("Authorization", sigResult.AuthorizationHeader);
    }

    private static byte[] GetSignatureKey(string key, string dateStamp, string regionName, string serviceName)
    {
        byte[] kSecret = Encoding.UTF8.GetBytes("AWS4" + key);
        byte[] kDate = HmacSha256(kSecret, dateStamp);
        byte[] kRegion = HmacSha256(kDate, regionName);
        byte[] kService = HmacSha256(kRegion, serviceName);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// Ensures that the HTTP response indicates success, throwing a <see cref="SharpS3ClientException"/> if an error occurred.
    /// </summary>
    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode) return;

        string content = string.Empty;
        if (response.Content != null)
        {
#if NET5_0_OR_GREATER
            content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
            content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            Error? error = null;
            try
            {
                error = SerializationHelper.DeserializeXml<Error>(content);
            }
            catch
            {
                // Fall through
            }

            if (error != null)
            {
                throw new SharpS3ClientException(response.StatusCode, error);
            }
        }

        throw new SharpS3ClientException(response.StatusCode, $"S3 request failed with status {(int)response.StatusCode} ({response.StatusCode}): {content}");
    }

    private static void AddMetadataHeaders(IDictionary<string, string> headers, IDictionary<string, string>? metadata)
    {
        if (metadata == null) return;

        foreach (var kvp in metadata)
        {
            string headerName = kvp.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? kvp.Key
                : "x-amz-meta-" + kvp.Key;
            headers[headerName] = kvp.Value;
        }
    }

    private static Dictionary<string, string> ExtractMetadata(HttpResponseMessage response)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in response.Headers)
        {
            if (h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                string key = h.Key.Substring("x-amz-meta-".Length);
                meta[key] = string.Join(",", h.Value);
            }
        }
        if (response.Content != null)
        {
            foreach (var h in response.Content.Headers)
            {
                if (h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                {
                    string key = h.Key.Substring("x-amz-meta-".Length);
                    meta[key] = string.Join(",", h.Value);
                }
            }
        }
        return meta;
    }

    /// <summary>
    /// Gets the first value for the specified header name from the HTTP headers collection.
    /// </summary>
    internal static string? GetHeaderValue(HttpHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_disposeHttpClient)
            {
                _httpClient.Dispose();
            }
            _disposed = true;
        }
    }

    internal sealed class NonDisposingStream : Stream
    {
        private readonly Stream _inner;
        public NonDisposingStream(Stream inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
#endif
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
#endif
        protected override void Dispose(bool disposing)
        {
            // Do not dispose inner stream
        }
    }
}
