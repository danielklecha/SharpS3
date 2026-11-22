namespace SharpS3.Storage;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Models;
using SharpS3.Protocol;

/// <summary>
/// Pluggable storage backend interface for S3 operations.
/// Consumers implement this interface to back S3 with memory, filesystem, databases, or blob stores.
/// </summary>
public interface IS3StorageBackend
{
    #region Service

    /// <summary>
    /// Lists all buckets owned by the authenticated caller.
    /// </summary>
    Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken cancellationToken = default);

    #endregion

    #region Bucket

    /// <summary>
    /// Checks if a bucket exists.
    /// </summary>
    Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new bucket.
    /// </summary>
    Task CreateBucketAsync(string bucket, CreateBucketConfiguration? configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an empty bucket.
    /// </summary>
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the location constraint (region) of a bucket.
    /// </summary>
    Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists objects within a bucket (ListObjects v1 and v2).
    /// </summary>
    Task<ListBucketResult> ListObjectsAsync(string bucket, S3Request request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists object versions and delete markers in a bucket.
    /// </summary>
    Task<ListVersionsResult> ListObjectVersionsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists in-progress multipart uploads in a bucket.
    /// </summary>
    Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves bucket ACL.
    /// </summary>
    Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets bucket ACL.
    /// </summary>
    Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes bucket ACL.
    /// </summary>
    Task DeleteBucketAclAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves bucket tags.
    /// </summary>
    Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets bucket tags.
    /// </summary>
    Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes bucket tags.
    /// </summary>
    Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves bucket versioning configuration.
    /// </summary>
    Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets bucket versioning configuration.
    /// </summary>
    Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves bucket website configuration.
    /// </summary>
    Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets bucket website configuration.
    /// </summary>
    Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes bucket website configuration.
    /// </summary>
    Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves bucket logging status.
    /// </summary>
    Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets bucket logging status.
    /// </summary>
    Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus logging, CancellationToken cancellationToken = default);

    #endregion

    #region Object

    /// <summary>
    /// Retrieves object metadata (HeadObject).
    /// Returns null if the object does not exist.
    /// </summary>
    Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an object or byte range payload (GetObject).
    /// </summary>
    Task<S3ObjectPayload> GetObjectAsync(string bucket, string key, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an object payload (PutObject).
    /// Returns the ETag of the stored object.
    /// </summary>
    Task<string> PutObjectAsync(string bucket, string key, Stream data, long contentLength, string? contentType, Dictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an object.
    /// </summary>
    Task DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple objects in a single batch operation.
    /// </summary>
    Task<DeleteResult> DeleteMultipleObjectsAsync(string bucket, DeleteMultiple request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs a server-side copy of an object.
    /// </summary>
    Task<CopyObjectResult> CopyObjectAsync(string srcBucket, string srcKey, string destBucket, string destKey, CancellationToken cancellationToken = default);

    #endregion

    #region Multipart-Uploads

    /// <summary>
    /// Initiates a multipart upload session.
    /// </summary>
    Task<InitiateMultipartUploadResult> CreateMultipartUploadAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an individual part in a multipart upload session.
    /// Returns the ETag of the uploaded part.
    /// </summary>
    Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream data, long contentLength, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a multipart upload by assembling previously uploaded parts.
    /// </summary>
    Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, CompleteMultipartUpload parts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts an in-progress multipart upload and releases uploaded part data.
    /// </summary>
    Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists uploaded parts for an in-progress multipart upload.
    /// </summary>
    Task<ListPartsResult> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a part by copying data from an existing object.
    /// </summary>
    Task<CopyPartResult> UploadPartCopyAsync(string srcBucket, string srcKey, string destBucket, string destKey, string uploadId, int partNumber, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default);

    #endregion

    #region Metadata-and-Locking

    /// <summary>
    /// Retrieves object tags.
    /// </summary>
    Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets object tags.
    /// </summary>
    Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes object tags.
    /// </summary>
    Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves legal hold status.
    /// </summary>
    Task<LegalHold> GetLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets legal hold status.
    /// </summary>
    Task SetLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves retention settings.
    /// </summary>
    Task<Retention> GetRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets retention settings.
    /// </summary>
    Task SetRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves object ACL.
    /// </summary>
    Task<AccessControlPolicy> GetObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets object ACL.
    /// </summary>
    Task SetObjectAclAsync(string bucket, string key, AccessControlPolicy acl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes object ACL.
    /// </summary>
    Task DeleteObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an archived object.
    /// </summary>
    Task<RestoreObjectResult> RestoreObjectAsync(string bucket, string key, RestoreRequest request, CancellationToken cancellationToken = default);

    #endregion
}
