namespace SharpS3;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Models;

/// <summary>
/// Transport-agnostic Amazon S3 compatible client interface.
/// </summary>
public interface ISharpS3Client : IDisposable
{
    // --- Service Operations ---

    /// <summary>
    /// Lists all buckets owned by the authenticated sender.
    /// </summary>
    Task<ListAllMyBucketsResult> ListBucketsAsync(CancellationToken cancellationToken = default);

    // --- Bucket Operations ---

    /// <summary>
    /// Creates a new bucket.
    /// </summary>
    Task CreateBucketAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an empty bucket.
    /// </summary>
    Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a bucket exists and is accessible.
    /// </summary>
    Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the location constraint (region) of a bucket.
    /// </summary>
    Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the access control policy of a bucket.
    /// </summary>
    Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the access control policy of a bucket.
    /// </summary>
    Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves tags of a bucket.
    /// </summary>
    Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets tags of a bucket.
    /// </summary>
    Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes tags of a bucket.
    /// </summary>
    Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves versioning configuration of a bucket.
    /// </summary>
    Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets versioning configuration of a bucket.
    /// </summary>
    Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves website configuration of a bucket.
    /// </summary>
    Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets website configuration of a bucket.
    /// </summary>
    Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes website configuration of a bucket.
    /// </summary>
    Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves logging status of a bucket.
    /// </summary>
    Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets logging status of a bucket.
    /// </summary>
    Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus loggingStatus, CancellationToken cancellationToken = default);

    // --- Object Operations ---

    /// <summary>
    /// Downloads an object or byte range of an object.
    /// Caller is responsible for disposing the returned S3ObjectResponse.
    /// </summary>
    Task<S3ObjectResponse> GetObjectAsync(string bucket, string key, ByteRange? range = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an object from a stream.
    /// </summary>
    Task PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an object from a byte array.
    /// </summary>
    Task PutObjectAsync(
        string bucket,
        string key,
        byte[] content,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads an object from a string using UTF-8 encoding.
    /// </summary>
    Task PutObjectAsync(
        string bucket,
        string key,
        string content,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves object metadata and HTTP headers without downloading the body (HEAD request).
    /// Returns null if the object does not exist.
    /// </summary>
    Task<ObjectMetadataResponse?> HeadObjectAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether an object exists.
    /// </summary>
    Task<bool> ObjectExistsAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single object.
    /// </summary>
    Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes multiple objects in a single batch request.
    /// </summary>
    Task<DeleteResult> DeleteObjectsAsync(string bucket, DeleteMultiple delete, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies an object from a source bucket/key to destination bucket/key.
    /// </summary>
    Task<CopyObjectResult> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists objects in a bucket using ListObjectsV2.
    /// </summary>
    Task<ListBucketResult> ListObjectsV2Async(
        string bucket,
        string? prefix = null,
        string? continuationToken = null,
        int? maxKeys = null,
        string? delimiter = null,
        CancellationToken cancellationToken = default);

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
    /// <summary>
    /// Automatically paginates through all objects under a prefix via asynchronous enumerable.
    /// </summary>
    IAsyncEnumerable<ObjectMetadata> ListAllObjectsAsync(
        string bucket,
        string? prefix = null,
        CancellationToken cancellationToken = default);
#endif

    // --- Object Tagging Operations ---

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

    // --- Object Governance Operations ---

    /// <summary>
    /// Retrieves legal hold status of an object.
    /// </summary>
    Task<LegalHold> GetObjectLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets legal hold status of an object.
    /// </summary>
    Task SetObjectLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves retention configuration of an object.
    /// </summary>
    Task<Retention> GetObjectRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets retention configuration of an object.
    /// </summary>
    Task SetObjectRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default);

    // --- High-Level Convenience Methods ---

    /// <summary>
    /// Uploads a local file from disk.
    /// </summary>
    Task UploadFileAsync(
        string bucket,
        string key,
        string filePath,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads an S3 object directly to a local file on disk.
    /// </summary>
    Task DownloadFileAsync(
        string bucket,
        string key,
        string destinationFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an AWS SigV4 pre-signed URL for temporary authenticated access.
    /// </summary>
    Uri GeneratePresignedUrl(
        string bucket,
        string key,
        HttpMethod method,
        TimeSpan expiresIn);

    // --- Multipart Upload Operations ---

    /// <summary>
    /// Initiates a multipart upload.
    /// </summary>
    Task<InitiateMultipartUploadResult> InitiateMultipartUploadAsync(
        string bucket,
        string key,
        string? contentType = null,
        IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a single part in a multipart upload from a stream.
    /// </summary>
    Task<UploadPartResult> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a single part in a multipart upload from a byte array.
    /// </summary>
    Task<UploadPartResult> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a multipart upload by assembling previously uploaded parts.
    /// </summary>
    Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CompleteMultipartUpload complete,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aborts a multipart upload, freeing stored parts.
    /// </summary>
    Task AbortMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a part by copying data from an existing object as data source.
    /// </summary>
    Task<CopyPartResult> UploadPartCopyAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        string uploadId,
        int partNumber,
        string? sourceVersionId = null,
        ByteRange? sourceRange = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the parts that have been uploaded for a specific multipart upload.
    /// </summary>
    Task<ListPartsResult> ListPartsAsync(
        string bucket,
        string key,
        string uploadId,
        int? maxParts = null,
        int? partNumberMarker = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists in-progress multipart uploads in a bucket.
    /// </summary>
    Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(
        string bucket,
        string? prefix = null,
        string? delimiter = null,
        string? keyMarker = null,
        string? uploadIdMarker = null,
        int? maxUploads = null,
        CancellationToken cancellationToken = default);
}
