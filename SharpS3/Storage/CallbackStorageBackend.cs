namespace SharpS3.Storage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

/// <summary>
/// A delegate-driven storage backend that allows operations to be provided via individual callbacks,
/// providing direct parity for applications migrating from callback-based S3 servers.
/// </summary>
public class CallbackStorageBackend : IS3StorageBackend
{
    #region Service-Callbacks

    /// <summary>
    /// Lists all buckets.
    /// </summary>
    public Func<IS3Request, CancellationToken, Task<ListAllMyBucketsResult>>? ListBuckets { get; set; }

    #endregion

    #region Bucket-Callbacks

    /// <summary>
    /// Checks whether a bucket exists.
    /// </summary>
    public Func<string, CancellationToken, Task<bool>>? BucketExists { get; set; }

    /// <summary>
    /// Creates a bucket.
    /// </summary>
    public Func<string, CreateBucketConfiguration?, CancellationToken, Task>? CreateBucket { get; set; }

    /// <summary>
    /// Deletes a bucket.
    /// </summary>
    public Func<string, CancellationToken, Task>? DeleteBucket { get; set; }

    /// <summary>
    /// Gets bucket location constraint.
    /// </summary>
    public Func<string, CancellationToken, Task<LocationConstraint>>? GetBucketLocation { get; set; }

    /// <summary>
    /// Lists objects in a bucket.
    /// </summary>
    public Func<string, S3Request, CancellationToken, Task<ListBucketResult>>? ListObjects { get; set; }

    /// <summary>
    /// Lists object versions in a bucket.
    /// </summary>
    public Func<string, CancellationToken, Task<ListVersionsResult>>? ListObjectVersions { get; set; }

    /// <summary>
    /// Lists in-progress multipart uploads.
    /// </summary>
    public Func<string, CancellationToken, Task<ListMultipartUploadsResult>>? ListMultipartUploads { get; set; }

    /// <summary>
    /// Gets a bucket's ACL.
    /// </summary>
    public Func<string, CancellationToken, Task<AccessControlPolicy>>? GetBucketAcl { get; set; }

    /// <summary>
    /// Sets a bucket's ACL.
    /// </summary>
    public Func<string, AccessControlPolicy, CancellationToken, Task>? SetBucketAcl { get; set; }

    /// <summary>
    /// Deletes a bucket's ACL.
    /// </summary>
    public Func<string, CancellationToken, Task>? DeleteBucketAcl { get; set; }

    /// <summary>
    /// Gets a bucket's tags.
    /// </summary>
    public Func<string, CancellationToken, Task<Tagging>>? GetBucketTags { get; set; }

    /// <summary>
    /// Sets a bucket's tags.
    /// </summary>
    public Func<string, Tagging, CancellationToken, Task>? SetBucketTags { get; set; }

    /// <summary>
    /// Deletes a bucket's tags.
    /// </summary>
    public Func<string, CancellationToken, Task>? DeleteBucketTags { get; set; }

    /// <summary>
    /// Gets a bucket's versioning configuration.
    /// </summary>
    public Func<string, CancellationToken, Task<VersioningConfiguration>>? GetBucketVersioning { get; set; }

    /// <summary>
    /// Sets a bucket's versioning configuration.
    /// </summary>
    public Func<string, VersioningConfiguration, CancellationToken, Task>? SetBucketVersioning { get; set; }

    /// <summary>
    /// Gets a bucket's website configuration.
    /// </summary>
    public Func<string, CancellationToken, Task<WebsiteConfiguration>>? GetBucketWebsite { get; set; }

    /// <summary>
    /// Sets a bucket's website configuration.
    /// </summary>
    public Func<string, WebsiteConfiguration, CancellationToken, Task>? SetBucketWebsite { get; set; }

    /// <summary>
    /// Deletes a bucket's website configuration.
    /// </summary>
    public Func<string, CancellationToken, Task>? DeleteBucketWebsite { get; set; }

    /// <summary>
    /// Gets a bucket's logging status.
    /// </summary>
    public Func<string, CancellationToken, Task<BucketLoggingStatus>>? GetBucketLogging { get; set; }

    /// <summary>
    /// Sets a bucket's logging status.
    /// </summary>
    public Func<string, BucketLoggingStatus, CancellationToken, Task>? SetBucketLogging { get; set; }

    #endregion

    #region Object-Callbacks

    /// <summary>
    /// Gets object metadata without downloading payload.
    /// </summary>
    public Func<string, string, CancellationToken, Task<ObjectMetadata?>>? GetObjectMetadata { get; set; }

    /// <summary>
    /// Downloads an object or byte range.
    /// </summary>
    public Func<string, string, long?, long?, CancellationToken, Task<S3ObjectPayload>>? GetObject { get; set; }

    /// <summary>
    /// Uploads an object.
    /// </summary>
    public Func<string, string, Stream, long, string?, Dictionary<string, string>?, CancellationToken, Task<string>>? PutObject { get; set; }

    /// <summary>
    /// Deletes an object.
    /// </summary>
    public Func<string, string, string?, CancellationToken, Task>? DeleteObject { get; set; }

    /// <summary>
    /// Copies an object.
    /// </summary>
    public Func<string, string, string, string, CancellationToken, Task<CopyObjectResult>>? CopyObject { get; set; }

    /// <summary>
    /// Deletes multiple objects.
    /// </summary>
    public Func<string, DeleteMultiple, CancellationToken, Task<DeleteResult>>? DeleteMultipleObjects { get; set; }

    /// <summary>
    /// Gets object tags.
    /// </summary>
    public Func<string, string, CancellationToken, Task<Tagging>>? GetObjectTags { get; set; }

    /// <summary>
    /// Sets object tags.
    /// </summary>
    public Func<string, string, Tagging, CancellationToken, Task>? SetObjectTags { get; set; }

    /// <summary>
    /// Deletes object tags.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? DeleteObjectTags { get; set; }

    /// <summary>
    /// Gets object legal hold.
    /// </summary>
    public Func<string, string, CancellationToken, Task<LegalHold>>? GetLegalHold { get; set; }

    /// <summary>
    /// Sets object legal hold.
    /// </summary>
    public Func<string, string, LegalHold, CancellationToken, Task>? SetLegalHold { get; set; }

    /// <summary>
    /// Gets object retention.
    /// </summary>
    public Func<string, string, CancellationToken, Task<Retention>>? GetRetention { get; set; }

    /// <summary>
    /// Sets object retention.
    /// </summary>
    public Func<string, string, Retention, CancellationToken, Task>? SetRetention { get; set; }

    /// <summary>
    /// Gets object ACL.
    /// </summary>
    public Func<string, string, CancellationToken, Task<AccessControlPolicy>>? GetObjectAcl { get; set; }

    /// <summary>
    /// Sets object ACL.
    /// </summary>
    public Func<string, string, AccessControlPolicy, CancellationToken, Task>? SetObjectAcl { get; set; }

    /// <summary>
    /// Deletes object ACL.
    /// </summary>
    public Func<string, string, CancellationToken, Task>? DeleteObjectAcl { get; set; }

    /// <summary>
    /// Restores an archived object.
    /// </summary>
    public Func<string, string, RestoreRequest, CancellationToken, Task<RestoreObjectResult>>? RestoreObject { get; set; }

    #endregion

    #region Multipart-Callbacks

    /// <summary>
    /// Initiates a multipart upload.
    /// </summary>
    public Func<string, string, CancellationToken, Task<InitiateMultipartUploadResult>>? CreateMultipartUpload { get; set; }

    /// <summary>
    /// Uploads a multipart upload part.
    /// </summary>
    public Func<string, string, string, int, Stream, long, CancellationToken, Task<string>>? UploadPart { get; set; }

    /// <summary>
    /// Uploads a part by copying from another object.
    /// </summary>
    public Func<string, string, string, string, string, int, long?, long?, CancellationToken, Task<CopyPartResult>>? UploadPartCopy { get; set; }

    /// <summary>
    /// Completes a multipart upload.
    /// </summary>
    public Func<string, string, string, CompleteMultipartUpload, CancellationToken, Task<CompleteMultipartUploadResult>>? CompleteMultipartUpload { get; set; }

    /// <summary>
    /// Aborts a multipart upload.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task>? AbortMultipartUpload { get; set; }

    /// <summary>
    /// Lists parts of a multipart upload.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<ListPartsResult>>? ListParts { get; set; }

    #endregion

    #region IS3StorageBackend-Implementation

    /// <inheritdoc />
    public Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken cancellationToken = default)
        => ListBuckets != null ? ListBuckets(request, cancellationToken) : Task.FromResult(new ListAllMyBucketsResult());

    /// <inheritdoc />
    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
        => BucketExists != null ? BucketExists(bucket, cancellationToken) : Task.FromResult(false);

    /// <inheritdoc />
    public Task CreateBucketAsync(string bucket, CreateBucketConfiguration? configuration, CancellationToken cancellationToken = default)
        => CreateBucket != null ? CreateBucket(bucket, configuration, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
        => DeleteBucket != null ? DeleteBucket(bucket, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketLocation != null ? GetBucketLocation(bucket, cancellationToken) : Task.FromResult(new LocationConstraint());

    /// <inheritdoc />
    public Task<ListBucketResult> ListObjectsAsync(string bucket, S3Request request, CancellationToken cancellationToken = default)
        => ListObjects != null ? ListObjects(bucket, request, cancellationToken) : Task.FromResult(new ListBucketResult { Name = bucket });

    /// <inheritdoc />
    public Task<ListVersionsResult> ListObjectVersionsAsync(string bucket, CancellationToken cancellationToken = default)
        => ListObjectVersions != null ? ListObjectVersions(bucket, cancellationToken) : Task.FromResult(new ListVersionsResult { Name = bucket });

    /// <inheritdoc />
    public Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucket, CancellationToken cancellationToken = default)
        => ListMultipartUploads != null ? ListMultipartUploads(bucket, cancellationToken) : Task.FromResult(new ListMultipartUploadsResult { Bucket = bucket });

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketAcl != null ? GetBucketAcl(bucket, cancellationToken) : Task.FromResult(new AccessControlPolicy());

    /// <inheritdoc />
    public Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default)
        => SetBucketAcl != null ? SetBucketAcl(bucket, acl, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteBucketAclAsync(string bucket, CancellationToken cancellationToken = default)
        => DeleteBucketAcl != null ? DeleteBucketAcl(bucket, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketTags != null ? GetBucketTags(bucket, cancellationToken) : throw new S3Exception(new Error(ErrorCode.NoSuchTagSet));

    /// <inheritdoc />
    public Task SetBucketTagsAsync(string bucket, Tagging tagging, CancellationToken cancellationToken = default)
        => SetBucketTags != null ? SetBucketTags(bucket, tagging, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default)
        => DeleteBucketTags != null ? DeleteBucketTags(bucket, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketVersioning != null ? GetBucketVersioning(bucket, cancellationToken) : Task.FromResult(new VersioningConfiguration());

    /// <inheritdoc />
    public Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default)
        => SetBucketVersioning != null ? SetBucketVersioning(bucket, configuration, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketWebsite != null ? GetBucketWebsite(bucket, cancellationToken) : throw new S3Exception(new Error(ErrorCode.NoSuchWebsiteConfiguration));

    /// <inheritdoc />
    public Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default)
        => SetBucketWebsite != null ? SetBucketWebsite(bucket, configuration, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default)
        => DeleteBucketWebsite != null ? DeleteBucketWebsite(bucket, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default)
        => GetBucketLogging != null ? GetBucketLogging(bucket, cancellationToken) : Task.FromResult(new BucketLoggingStatus());

    /// <inheritdoc />
    public Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus loggingStatus, CancellationToken cancellationToken = default)
        => SetBucketLogging != null ? SetBucketLogging(bucket, loggingStatus, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => GetObjectMetadata != null ? GetObjectMetadata(bucket, key, cancellationToken) : Task.FromResult<ObjectMetadata?>(null);

    /// <inheritdoc />
    public Task<S3ObjectPayload> GetObjectAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
        => GetObject != null ? GetObject(bucket, key, rangeStart, rangeEnd, cancellationToken) : throw new S3Exception(new Error(ErrorCode.NoSuchKey));

    /// <inheritdoc />
    public Task<string> PutObjectAsync(string bucket, string key, Stream data, long contentLength, string? contentType, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => PutObject != null ? PutObject(bucket, key, data, contentLength, contentType, metadata, cancellationToken) : Task.FromResult("\"" + Guid.NewGuid().ToString("N") + "\"");

    /// <inheritdoc />
    public Task DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
        => DeleteObject != null ? DeleteObject(bucket, key, versionId, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<CopyObjectResult> CopyObjectAsync(string srcBucket, string srcKey, string destBucket, string destKey, CancellationToken cancellationToken = default)
        => CopyObject != null ? CopyObject(srcBucket, srcKey, destBucket, destKey, cancellationToken) : Task.FromResult(new CopyObjectResult { ETag = "\"" + Guid.NewGuid().ToString("N") + "\"", LastModified = DateTime.UtcNow });

    /// <inheritdoc />
    public Task<DeleteResult> DeleteMultipleObjectsAsync(string bucket, DeleteMultiple objects, CancellationToken cancellationToken = default)
        => DeleteMultipleObjects != null ? DeleteMultipleObjects(bucket, objects, cancellationToken) : Task.FromResult(new DeleteResult());

    /// <inheritdoc />
    public Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => GetObjectTags != null ? GetObjectTags(bucket, key, cancellationToken) : Task.FromResult(new Tagging());

    /// <inheritdoc />
    public Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default)
        => SetObjectTags != null ? SetObjectTags(bucket, key, tags, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => DeleteObjectTags != null ? DeleteObjectTags(bucket, key, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<LegalHold> GetLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => GetLegalHold != null ? GetLegalHold(bucket, key, cancellationToken) : Task.FromResult(new LegalHold { Status = "OFF" });

    /// <inheritdoc />
    public Task SetLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default)
        => SetLegalHold != null ? SetLegalHold(bucket, key, legalHold, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<Retention> GetRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => GetRetention != null ? GetRetention(bucket, key, cancellationToken) : Task.FromResult(new Retention());

    /// <inheritdoc />
    public Task SetRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default)
        => SetRetention != null ? SetRetention(bucket, key, retention, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<AccessControlPolicy> GetObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => GetObjectAcl != null ? GetObjectAcl(bucket, key, cancellationToken) : Task.FromResult(new AccessControlPolicy());

    /// <inheritdoc />
    public Task SetObjectAclAsync(string bucket, string key, AccessControlPolicy acl, CancellationToken cancellationToken = default)
        => SetObjectAcl != null ? SetObjectAcl(bucket, key, acl, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => DeleteObjectAcl != null ? DeleteObjectAcl(bucket, key, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<RestoreObjectResult> RestoreObjectAsync(string bucket, string key, RestoreRequest request, CancellationToken cancellationToken = default)
        => RestoreObject != null ? RestoreObject(bucket, key, request, cancellationToken) : Task.FromResult(new RestoreObjectResult());

    /// <inheritdoc />
    public Task<InitiateMultipartUploadResult> CreateMultipartUploadAsync(string bucket, string key, CancellationToken cancellationToken = default)
        => CreateMultipartUpload != null ? CreateMultipartUpload(bucket, key, cancellationToken) : Task.FromResult(new InitiateMultipartUploadResult { Bucket = bucket, Key = key, UploadId = Guid.NewGuid().ToString("N") });

    /// <inheritdoc />
    public Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream data, long contentLength, CancellationToken cancellationToken = default)
        => UploadPart != null ? UploadPart(bucket, key, uploadId, partNumber, data, contentLength, cancellationToken) : Task.FromResult("\"" + Guid.NewGuid().ToString("N") + "\"");

    /// <inheritdoc />
    public Task<CopyPartResult> UploadPartCopyAsync(string srcBucket, string srcKey, string destBucket, string destKey, string uploadId, int partNumber, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default)
        => UploadPartCopy != null ? UploadPartCopy(srcBucket, srcKey, destBucket, destKey, uploadId, partNumber, rangeStart, rangeEnd, cancellationToken) : Task.FromResult(new CopyPartResult { ETag = "\"" + Guid.NewGuid().ToString("N") + "\"", LastModified = DateTime.UtcNow });

    /// <inheritdoc />
    public Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, CompleteMultipartUpload parts, CancellationToken cancellationToken = default)
        => CompleteMultipartUpload != null ? CompleteMultipartUpload(bucket, key, uploadId, parts, cancellationToken) : Task.FromResult(new CompleteMultipartUploadResult { Bucket = bucket, Key = key, ETag = "\"" + Guid.NewGuid().ToString("N") + "\"" });

    /// <inheritdoc />
    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
        => AbortMultipartUpload != null ? AbortMultipartUpload(bucket, key, uploadId, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task<ListPartsResult> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default)
        => ListParts != null ? ListParts(bucket, key, uploadId, cancellationToken) : Task.FromResult(new ListPartsResult { Bucket = bucket, Key = key, UploadId = uploadId });

    #endregion
}
