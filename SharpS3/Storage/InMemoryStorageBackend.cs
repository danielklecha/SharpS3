namespace SharpS3.Storage;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

/// <summary>
/// Thread-safe in-memory S3 storage backend implementing IS3StorageBackend.
/// Suitable for unit tests, local development, and mock services.
/// </summary>
public class InMemoryStorageBackend : IS3StorageBackend
{
    /// <summary>
    /// Represents an object stored in memory.
    /// </summary>
    internal class StoredObject
    {
        /// <summary>
        /// Gets or sets the binary payload of the stored object.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Gets or sets the entity tag (ETag) of the stored object.
        /// </summary>
        public string ETag { get; set; } = "\"\"";

        /// <summary>
        /// Gets or sets the timestamp when the object was last modified.
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the MIME content type of the stored object.
        /// </summary>
        public string ContentType { get; set; } = "binary/octet-stream";

        /// <summary>
        /// Gets or sets the user-defined metadata dictionary.
        /// </summary>
        public Dictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Gets or sets the object tagging set.
        /// </summary>
        public Tagging? Tags { get; set; }

        /// <summary>
        /// Gets or sets the object legal hold status.
        /// </summary>
        public LegalHold? LegalHold { get; set; }

        /// <summary>
        /// Gets or sets the object retention configuration.
        /// </summary>
        public Retention? Retention { get; set; }

        /// <summary>
        /// Gets or sets the access control policy for the stored object.
        /// </summary>
        public AccessControlPolicy? Acl { get; set; }
    }

    /// <summary>
    /// Represents an active multipart upload tracked in memory.
    /// </summary>
    internal class StoredUpload
    {
        /// <summary>
        /// Gets or sets the multipart upload identifier.
        /// </summary>
        public string UploadId { get; set; } = "";

        /// <summary>
        /// Gets or sets the destination bucket name.
        /// </summary>
        public string Bucket { get; set; } = "";

        /// <summary>
        /// Gets or sets the object key.
        /// </summary>
        public string Key { get; set; } = "";

        /// <summary>
        /// Gets or sets the timestamp when the upload was initiated.
        /// </summary>
        public DateTime Initiated { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the collection of uploaded parts indexed by part number.
        /// </summary>
        public ConcurrentDictionary<int, StoredPart> Parts { get; } = new();
    }

    /// <summary>
    /// Represents an individual part of a multipart upload stored in memory.
    /// </summary>
    internal class StoredPart
    {
        /// <summary>
        /// Gets or sets the part number.
        /// </summary>
        public int PartNumber { get; set; }

        /// <summary>
        /// Gets or sets the binary payload of the part.
        /// </summary>
        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Gets or sets the entity tag (ETag) of the part.
        /// </summary>
        public string ETag { get; set; } = "\"\"";

        /// <summary>
        /// Gets or sets the timestamp when the part was uploaded.
        /// </summary>
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Represents bucket-level configuration and metadata stored in memory.
    /// </summary>
    private class StoredBucketMeta
    {
        /// <summary>
        /// Gets or sets the access control policy for the bucket.
        /// </summary>
        public AccessControlPolicy? Acl { get; set; }

        /// <summary>
        /// Gets or sets the bucket tagging configuration.
        /// </summary>
        public Tagging? Tags { get; set; }

        /// <summary>
        /// Gets or sets the bucket versioning configuration.
        /// </summary>
        public VersioningConfiguration? Versioning { get; set; }

        /// <summary>
        /// Gets or sets the bucket website configuration.
        /// </summary>
        public WebsiteConfiguration? Website { get; set; }

        /// <summary>
        /// Gets or sets the bucket logging configuration.
        /// </summary>
        public BucketLoggingStatus? Logging { get; set; }
    }

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StoredObject>> _Buckets =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, StoredBucketMeta> _BucketMeta =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, StoredUpload> _Uploads =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the dictionary of active multipart uploads in progress.
    /// </summary>
    internal ConcurrentDictionary<string, StoredUpload> Uploads => _Uploads;

    /// <summary>
    /// Default S3 owner for mock listings.
    /// </summary>
    public Owner DefaultOwner { get; set; } = new("in-memory-owner-id", "in-memory");

    /// <summary>
    /// When true, verifies that each part's ETag specified in CompleteMultipartUpload matches the uploaded part's ETag.
    /// Default is false for compatibility with test mocks, but recommended true in production.
    /// </summary>
    public bool ValidatePartETags { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum allowed size (in bytes) for a single object or part in memory.
    /// If null, size is unlimited (subject only to host memory and dispatcher limits).
    /// </summary>
    public long? MaxObjectSize { get; set; }

    /// <inheritdoc />
    public Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken cancellationToken = default)
    {
        var result = new ListAllMyBucketsResult
        {
            Owner = DefaultOwner,
            Buckets = new Buckets(_Buckets.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => new Bucket(k, DateTime.UtcNow))
                .ToList())
        };

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_Buckets.ContainsKey(bucket));
    }

    /// <inheritdoc />
    public Task CreateBucketAsync(string bucket, CreateBucketConfiguration? configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bucket) ||
            bucket.StartsWith(".") ||
            bucket.EndsWith(".") ||
            bucket.Contains('/') || bucket.Contains('\\'))
        {
            throw new S3Exception(new Error(ErrorCode.InvalidBucketName));
        }

        _Buckets.TryAdd(bucket, new ConcurrentDictionary<string, StoredObject>(StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.IsEmpty)
        {
            throw new S3Exception(new Error(ErrorCode.BucketNotEmpty));
        }

        _Buckets.TryRemove(bucket, out _);
        _BucketMeta.TryRemove(bucket, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        return Task.FromResult(new LocationConstraint("us-east-1"));
    }

    /// <inheritdoc />
    public Task<ListBucketResult> ListObjectsAsync(string bucket, S3Request request, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        bool isV2 = request.RetrieveQueryValue("list-type") == "2";
        string prefix = request.Prefix ?? "";
        string? delimiter = request.Delimiter;
        string? after = isV2 ? (request.ContinuationToken ?? request.StartAfter) : request.Marker;
        int max = Math.Min(Math.Max(1, request.MaxKeys), 1000);

        var r = new ListBucketResult
        {
            Name = bucket,
            Prefix = prefix,
            Delimiter = delimiter,
            MaxKeys = max,
            Marker = isV2 ? null : request.Marker,
            ContinuationToken = isV2 ? request.ContinuationToken : null,
            StartAfter = isV2 ? request.StartAfter : null
        };

        string? last = null;
        var seenPrefixes = new HashSet<string>(StringComparer.Ordinal);

        foreach (string key in b.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
        {
            string entry = key;
            bool isPrefix = false;

            if (!string.IsNullOrEmpty(delimiter))
            {
                int i = key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
                if (i >= 0)
                {
                    entry = key.Substring(0, i + delimiter.Length);
                    isPrefix = true;
                }
            }

            if (after != null && string.CompareOrdinal(entry, after) <= 0) continue;
            if (isPrefix && seenPrefixes.Contains(entry)) continue;

            if (r.Contents.Count + seenPrefixes.Count >= max)
            {
                r.IsTruncated = true;
                break;
            }

            if (isPrefix)
            {
                seenPrefixes.Add(entry);
                r.CommonPrefixes.Add(new CommonPrefixes(entry));
            }
            else
            {
                var o = b[key];
                r.Contents.Add(new ObjectMetadata(key, o.LastModified, o.ETag, o.Data.Length, DefaultOwner));
            }

            last = entry;
        }

        if (r.IsTruncated)
        {
            if (isV2) r.NextContinuationToken = last;
            else r.NextMarker = last;
        }

        if (isV2) r.KeyCount = r.Contents.Count + r.CommonPrefixes.Count;

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<ListVersionsResult> ListObjectVersionsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        var r = new ListVersionsResult
        {
            Name = bucket,
            MaxKeys = 1000
        };

        foreach (var kvp in b.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            r.Versions.Add(new ObjectVersion(kvp.Key, "null", true, kvp.Value.LastModified, kvp.Value.ETag, kvp.Value.Data.Length, DefaultOwner));
        }

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var r = new ListMultipartUploadsResult
        {
            Bucket = bucket,
            MaxUploads = 1000
        };

        foreach (var u in _Uploads.Values.Where(u => u.Bucket == bucket).OrderBy(u => u.Key, StringComparer.Ordinal))
        {
            r.Uploads.Add(new Upload
            {
                Key = u.Key,
                UploadId = u.UploadId,
                Owner = DefaultOwner,
                Initiator = DefaultOwner,
                StorageClass = StorageClassEnum.STANDARD,
                Initiated = u.Initiated
            });
        }

        return Task.FromResult(r);
    }

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m) && m.Acl != null)
            return Task.FromResult(m.Acl);
        return Task.FromResult(new AccessControlPolicy(DefaultOwner, new AccessControlList()));
    }

    /// <inheritdoc />
    public Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var meta = _BucketMeta.GetOrAdd(bucket, _ => new StoredBucketMeta());
        meta.Acl = acl;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m)) m.Acl = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m) && m.Tags != null)
            return Task.FromResult(m.Tags);
        throw new S3Exception(new Error(ErrorCode.NoSuchTagSet));
    }

    /// <inheritdoc />
    public Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var meta = _BucketMeta.GetOrAdd(bucket, _ => new StoredBucketMeta());
        meta.Tags = tags;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m)) m.Tags = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m) && m.Versioning != null)
            return Task.FromResult(m.Versioning);
        return Task.FromResult(new VersioningConfiguration());
    }

    /// <inheritdoc />
    public Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var meta = _BucketMeta.GetOrAdd(bucket, _ => new StoredBucketMeta());
        meta.Versioning = configuration;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m) && m.Website != null)
            return Task.FromResult(m.Website);
        throw new S3Exception(new Error(ErrorCode.NoSuchWebsiteConfiguration));
    }

    /// <inheritdoc />
    public Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var meta = _BucketMeta.GetOrAdd(bucket, _ => new StoredBucketMeta());
        meta.Website = configuration;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m)) m.Website = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (_BucketMeta.TryGetValue(bucket, out var m) && m.Logging != null)
            return Task.FromResult(m.Logging);
        return Task.FromResult(new BucketLoggingStatus());
    }

    /// <inheritdoc />
    public Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus logging, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        var meta = _BucketMeta.GetOrAdd(bucket, _ => new StoredBucketMeta());
        meta.Logging = logging;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o))
            return Task.FromResult<ObjectMetadata?>(null);

        var md = new ObjectMetadata(key, o.LastModified, o.ETag, o.Data.Length, DefaultOwner)
        {
            ContentType = o.ContentType
        };
        if (o.Metadata != null)
        {
            foreach (var kvp in o.Metadata)
            {
                md.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return Task.FromResult<ObjectMetadata?>(md);
    }

    /// <inheritdoc />
    public Task<S3ObjectPayload> GetObjectAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o))
            throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        byte[] data = o.Data;
        long total = data.Length;

        S3ObjectPayload payload;
        if (rangeStart != null || rangeEnd != null)
        {
            long start = rangeStart ?? 0;
            long end = rangeEnd ?? (total - 1);
            if (end >= total) end = total - 1;
            if (start > end || start >= total)
            {
                throw new S3Exception(new Error(ErrorCode.InvalidRange));
            }

            long length = end - start + 1;
            byte[] slice = new byte[length];
            Buffer.BlockCopy(data, (int)start, slice, 0, (int)length);

            payload = new S3ObjectPayload
            {
                Stream = new MemoryStream(slice),
                ContentLength = length,
                ContentType = o.ContentType,
                ETag = o.ETag,
                LastModified = o.LastModified,
                TotalSize = total,
                RangeStart = start,
                RangeEnd = end
            };
        }
        else
        {
            payload = new S3ObjectPayload
            {
                Stream = new MemoryStream(data, writable: false),
                ContentLength = total,
                ContentType = o.ContentType,
                ETag = o.ETag,
                LastModified = o.LastModified,
                TotalSize = total
            };
        }

        PopulateMetadataHeaders(payload, o.Metadata);
        return Task.FromResult(payload);
    }

    /// <inheritdoc />
    public async Task<string> PutObjectAsync(
        string bucket,
        string key,
        Stream data,
        long contentLength,
        string? contentType,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        var b = GetBucket(bucket);
        byte[] bytes = await ReadStreamBytesAsync(data, contentLength, cancellationToken).ConfigureAwait(false);
        string etag = ComputeMd5ETag(bytes);

        var o = new StoredObject
        {
            Data = bytes,
            ETag = etag,
            LastModified = DateTime.UtcNow,
            ContentType = string.IsNullOrEmpty(contentType) ? "binary/octet-stream" : contentType,
            Metadata = metadata != null ? new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase) : null
        };

        b[key] = o;
        return etag;
    }

    /// <inheritdoc />
    public Task DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        b.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<DeleteResult> DeleteMultipleObjectsAsync(string bucket, DeleteMultiple request, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        var result = new DeleteResult();

        if (request?.Objects != null)
        {
            foreach (var item in request.Objects)
            {
                if (!string.IsNullOrEmpty(item.Key))
                {
                    b.TryRemove(item.Key, out _);
                    result.DeletedObjects.Add(new Deleted(item.Key, "null", false));
                }
            }
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<CopyObjectResult> CopyObjectAsync(string srcBucket, string srcKey, string destBucket, string destKey, CancellationToken cancellationToken = default)
    {
        var srcB = GetBucket(srcBucket);
        if (!srcB.TryGetValue(srcKey, out var srcObj))
            throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        if (MaxObjectSize.HasValue && srcObj.Data.Length > MaxObjectSize.Value)
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));

        var destB = GetBucket(destBucket);
        var destObj = new StoredObject
        {
            Data = (byte[])srcObj.Data.Clone(),
            ETag = srcObj.ETag,
            LastModified = DateTime.UtcNow,
            ContentType = srcObj.ContentType,
            Metadata = srcObj.Metadata != null ? new Dictionary<string, string>(srcObj.Metadata, StringComparer.OrdinalIgnoreCase) : null
        };

        destB[destKey] = destObj;

        var result = new CopyObjectResult(destObj.ETag, destObj.LastModified);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<InitiateMultipartUploadResult> CreateMultipartUploadAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        string uploadId = Guid.NewGuid().ToString("N");

        var upload = new StoredUpload
        {
            UploadId = uploadId,
            Bucket = bucket,
            Key = key,
            Initiated = DateTime.UtcNow
        };

        _Uploads[uploadId] = upload;

        var result = new InitiateMultipartUploadResult(bucket, key, uploadId);
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream data, long contentLength, CancellationToken cancellationToken = default)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        if (partNumber < 1 || partNumber > 10000)
            throw new S3Exception(new Error(ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000."));

        if (!_Uploads.TryGetValue(uploadId, out var upload) ||
            !string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) ||
            !string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        byte[] bytes = await ReadStreamBytesAsync(data, contentLength, cancellationToken).ConfigureAwait(false);
        string etag = ComputeMd5ETag(bytes);

        var part = new StoredPart
        {
            PartNumber = partNumber,
            Data = bytes,
            ETag = etag,
            LastModified = DateTime.UtcNow
        };

        upload.Parts[partNumber] = part;
        return etag;
    }

    /// <inheritdoc />
    public Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, CompleteMultipartUpload parts, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (!_Uploads.TryGetValue(uploadId, out var upload) ||
            !string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) ||
            !string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        List<StoredPart> orderedParts;
        if (parts?.Parts != null && parts.Parts.Count > 0)
        {
            orderedParts = new List<StoredPart>(parts.Parts.Count);
            int lastPartNum = 0;
            foreach (var p in parts.Parts)
            {
                if (p.PartNumber <= lastPartNum || p.PartNumber < 1 || p.PartNumber > 10000)
                {
                    throw new S3Exception(new Error(ErrorCode.InvalidPartOrder, "Parts must be in ascending order."));
                }
                lastPartNum = p.PartNumber;

                if (!upload.Parts.TryGetValue(p.PartNumber, out var sp))
                {
                    throw new S3Exception(new Error(ErrorCode.InvalidPart, $"Part {p.PartNumber} does not exist."));
                }

                if (ValidatePartETags && !string.IsNullOrEmpty(p.ETag) && !string.Equals(sp.ETag.Trim('"'), p.ETag.Trim('"'), StringComparison.OrdinalIgnoreCase))
                {
                    throw new S3Exception(new Error(ErrorCode.InvalidPart, $"Part {p.PartNumber} ETag does not match."));
                }

                orderedParts.Add(sp);
            }
        }
        else
        {
            orderedParts = upload.Parts.OrderBy(p => p.Key).Select(p => p.Value).ToList();
        }

        long totalSizeLong = orderedParts.Sum(p => (long)p.Data.Length);
        if (totalSizeLong > int.MaxValue || (MaxObjectSize.HasValue && totalSizeLong > MaxObjectSize.Value))
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));

        int totalSize = (int)totalSizeLong;
        byte[] completeData = new byte[totalSize];

        int offset = 0;
        foreach (var p in orderedParts)
        {
            Buffer.BlockCopy(p.Data, 0, completeData, offset, p.Data.Length);
            offset += p.Data.Length;
        }

        var partDigests = new List<byte[]>(orderedParts.Count);
        foreach (var p in orderedParts)
        {
            byte[] d = ETagHelper.HexToBytes(p.ETag);
            if (d.Length != 16)
            {
                using var md5 = MD5.Create();
                d = md5.ComputeHash(p.Data);
            }
            partDigests.Add(d);
        }

        string etag = ETagHelper.ComputeMultipartETag(partDigests);

        var destB = GetBucket(upload.Bucket);
        destB[upload.Key] = new StoredObject
        {
            Data = completeData,
            ETag = etag,
            LastModified = DateTime.UtcNow
        };

        _Uploads.TryRemove(uploadId, out _);

        var result = new CompleteMultipartUploadResult
        {
            Bucket = upload.Bucket,
            Key = upload.Key,
            ETag = etag,
            Location = "/" + upload.Bucket + "/" + upload.Key
        };

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (!_Uploads.TryGetValue(uploadId, out var upload) ||
            !string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) ||
            !string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        _Uploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ListPartsResult> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
    {
        GetBucket(bucket);
        if (!_Uploads.TryGetValue(uploadId, out var upload) ||
            !string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) ||
            !string.Equals(upload.Key, key, StringComparison.Ordinal))
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        var partsList = upload.Parts.Values.OrderBy(p => p.PartNumber).Select(p => new Part
        {
            PartNumber = p.PartNumber,
            ETag = p.ETag,
            Size = p.Data.Length,
            LastModified = p.LastModified
        }).ToList();

        var result = new ListPartsResult
        {
            Bucket = upload.Bucket,
            Key = upload.Key,
            UploadId = upload.UploadId,
            Parts = partsList,
            MaxParts = 1000,
            IsTruncated = false
        };

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return Task.FromResult(o.Tags ?? new Tagging());
    }

    /// <inheritdoc />
    public Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.Tags = tags;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.Tags = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LegalHold> GetLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return Task.FromResult(o.LegalHold ?? new LegalHold("OFF"));
    }

    /// <inheritdoc />
    public Task SetLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.LegalHold = legalHold;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Retention> GetRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return Task.FromResult(o.Retention ?? new Retention());
    }

    /// <inheritdoc />
    public Task SetRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.Retention = retention;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return Task.FromResult(o.Acl ?? new AccessControlPolicy(DefaultOwner, new AccessControlList()));
    }

    /// <inheritdoc />
    public Task SetObjectAclAsync(string bucket, string key, AccessControlPolicy acl, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.Acl = acl;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out var o)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        o.Acl = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<CopyPartResult> UploadPartCopyAsync(string srcBucket, string srcKey, string destBucket, string destKey, string uploadId, int partNumber, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
    {
        var srcB = GetBucket(srcBucket);
        if (!srcB.TryGetValue(srcKey, out var srcObj))
            throw new S3Exception(new Error(ErrorCode.NoSuchKey));

        GetBucket(destBucket);

        if (!_Uploads.TryGetValue(uploadId, out var upload))
            throw new S3Exception(new Error(ErrorCode.NoSuchUpload));

        byte[] srcData = srcObj.Data;
        long start = rangeStart ?? 0;
        long end = rangeEnd ?? (srcData.Length - 1);
        if (start < 0 || start > end || start >= srcData.Length)
            throw new S3Exception(new Error(ErrorCode.InvalidRange));
        if (end >= srcData.Length) end = srcData.Length - 1;

        long lengthLong = end - start + 1;
        if (MaxObjectSize.HasValue && lengthLong > MaxObjectSize.Value)
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));

        int length = (int)lengthLong;
        byte[] partBytes = new byte[length];
        Buffer.BlockCopy(srcData, (int)start, partBytes, 0, length);

        string etag = ComputeMd5ETag(partBytes);
        var part = new StoredPart
        {
            PartNumber = partNumber,
            Data = partBytes,
            ETag = etag,
            LastModified = DateTime.UtcNow
        };
        upload.Parts[partNumber] = part;

        return Task.FromResult(new CopyPartResult(etag, DateTime.UtcNow));
    }

    /// <inheritdoc />
    public Task<RestoreObjectResult> RestoreObjectAsync(string bucket, string key, RestoreRequest request, CancellationToken cancellationToken = default)
    {
        var b = GetBucket(bucket);
        if (!b.TryGetValue(key, out _)) throw new S3Exception(new Error(ErrorCode.NoSuchKey));
        return Task.FromResult(new RestoreObjectResult { AlreadyRestored = false });
    }

    /// <summary>
    /// Populates custom user metadata headers (x-amz-meta-*) onto the S3 object payload.
    /// </summary>
    internal static void PopulateMetadataHeaders(S3ObjectPayload payload, Dictionary<string, string>? metadata)
    {
        if (metadata == null)
            return;

        foreach (var kvp in metadata)
        {
            string headerKey = kvp.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                ? kvp.Key
                : "x-amz-meta-" + kvp.Key;
            payload.Headers[headerKey] = kvp.Value;
        }
    }

    private async Task<byte[]> ReadStreamBytesAsync(Stream data, long contentLength, CancellationToken cancellationToken)
    {
        if (contentLength > int.MaxValue || (MaxObjectSize.HasValue && contentLength > MaxObjectSize.Value))
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        if (contentLength > 0)
        {
            byte[] bytes = new byte[contentLength];
            int read = 0;
            while (read < contentLength)
            {
                int n = await data.ReadAsync(bytes, read, (int)contentLength - read, cancellationToken).ConfigureAwait(false);
                if (n <= 0) break;
                read += n;
            }

            if (read < contentLength)
            {
                Array.Resize(ref bytes, read);
            }

            return bytes;
        }
        else
        {
            using var ms = new MemoryStream();
            byte[] buffer = new byte[81920];
            long totalRead = 0;
            while (true)
            {
                int n = await data.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (n <= 0) break;
                totalRead += n;
                if ((MaxObjectSize.HasValue && totalRead > MaxObjectSize.Value) || totalRead > int.MaxValue)
                {
                    throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
                }
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
    }

    private ConcurrentDictionary<string, StoredObject> GetBucket(string bucket)
    {
        if (string.IsNullOrEmpty(bucket) || !_Buckets.TryGetValue(bucket, out var b))
            throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
        return b;
    }

    private static string ComputeMd5ETag(byte[] data)
    {
        return "\"" + ComputeMd5Raw(data) + "\"";
    }

    private static string ComputeMd5Raw(byte[] data)
    {
        using var md5 = MD5.Create();
        byte[] hash = md5.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}
