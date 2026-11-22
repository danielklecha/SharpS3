# Custom Storage Backend

SharpS3 makes it straightforward to bridge Amazon S3 requests to any custom persistence mechanism—such as Azure Blob Storage, Google Cloud Storage, MongoDB GridFS, SQLite, or relational databases—by implementing the `IS3StorageBackend` interface.

---

## 1. Implementing `IS3StorageBackend`

To create a custom backend, implement `IS3StorageBackend` and provide your own logic for bucket and object operations:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

public class AzureBlobStorageBackend : IS3StorageBackend
{
    // Inject Azure BlobServiceClient or database connection
    public AzureBlobStorageBackend(/* Azure clients */)
    {
    }

    #region Service Operations

    public async Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken cancellationToken = default)
    {
        var result = new ListAllMyBucketsResult
        {
            Owner = new Owner { Id = "custom-owner", DisplayName = "Custom" },
            Buckets = new Buckets()
        };

        // Enumerate containers / buckets
        result.Buckets.BucketList.Add(new Bucket { Name = "my-bucket", CreationDate = DateTime.UtcNow });
        return result;
    }

    #endregion

    #region Bucket Operations

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        // Check container existence
        return Task.FromResult(true);
    }

    public Task CreateBucketAsync(string bucket, CreateBucketConfiguration? configuration, CancellationToken cancellationToken = default)
    {
        // Create container
        return Task.CompletedTask;
    }

    public Task DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        // Delete container
        return Task.CompletedTask;
    }

    public Task<LocationConstraint> GetBucketLocationAsync(string bucket, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new LocationConstraint { Value = "us-east-1" });
    }

    public Task<ListBucketResult> ListObjectsAsync(string bucket, S3Request request, CancellationToken cancellationToken = default)
    {
        // List blobs under prefix
        return Task.FromResult(new ListBucketResult { Name = bucket, KeyCount = 0 });
    }

    // ... Implement remaining bucket operations (ACL, Tags, Versioning, Website, Logging) ...

    #endregion

    #region Object Operations

    public async Task<S3ObjectPayload> GetObjectAsync(
        string bucket,
        string key,
        long? rangeStart = null,
        long? rangeEnd = null,
        CancellationToken cancellationToken = default)
    {
        // Download blob stream or range
        Stream blobStream = /* download from blob storage */ Stream.Null;
        
        return new S3ObjectPayload(
            blobStream,
            contentLength: 1024,
            contentType: "application/octet-stream",
            etag: "\"etag-12345\"",
            lastModifiedUtc: DateTime.UtcNow);
    }

    public async Task<string> PutObjectAsync(
        string bucket,
        string key,
        Stream data,
        long contentLength,
        string? contentType,
        Dictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        // Upload data stream to blob storage
        string computedEtag = "\"computed-md5-or-etag\"";
        return computedEtag;
    }

    public Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        // Return blob properties without downloading payload
        return Task.FromResult<ObjectMetadata?>(new ObjectMetadata
        {
            Key = key,
            Size = 1024,
            ETag = "\"etag-12345\"",
            LastModified = DateTime.UtcNow
        });
    }

    public Task DeleteObjectAsync(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        // Delete blob
        return Task.CompletedTask;
    }

    public Task<DeleteResult> DeleteMultipleObjectsAsync(string bucket, DeleteMultiple request, CancellationToken cancellationToken = default)
    {
        var result = new DeleteResult();
        foreach (var obj in request.Objects)
        {
            result.Deleted.Add(new Deleted { Key = obj.Key });
        }
        return Task.FromResult(result);
    }

    public Task<CopyObjectResult> CopyObjectAsync(string srcBucket, string srcKey, string destBucket, string destKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new CopyObjectResult
        {
            ETag = "\"copy-etag\"",
            LastModified = DateTime.UtcNow
        });
    }

    // ... Implement Multipart, Tagging, and Locking operations ...
    #endregion

    // Implement default/empty responses for unsupported optional S3 features
    public Task<ListVersionsResult> ListObjectVersionsAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new ListVersionsResult { Name = bucket });
    public Task<ListMultipartUploadsResult> ListMultipartUploadsAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new ListMultipartUploadsResult { Bucket = bucket });
    public Task<AccessControlPolicy> GetBucketAclAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new AccessControlPolicy());
    public Task SetBucketAclAsync(string bucket, AccessControlPolicy acl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteBucketAclAsync(string bucket, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<Tagging> GetBucketTagsAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new Tagging());
    public Task SetBucketTagsAsync(string bucket, Tagging tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteBucketTagsAsync(string bucket, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<VersioningConfiguration> GetBucketVersioningAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new VersioningConfiguration());
    public Task SetBucketVersioningAsync(string bucket, VersioningConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<WebsiteConfiguration> GetBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new WebsiteConfiguration());
    public Task SetBucketWebsiteAsync(string bucket, WebsiteConfiguration configuration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteBucketWebsiteAsync(string bucket, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<BucketLoggingStatus> GetBucketLoggingAsync(string bucket, CancellationToken cancellationToken = default) => Task.FromResult(new BucketLoggingStatus());
    public Task SetBucketLoggingAsync(string bucket, BucketLoggingStatus logging, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<InitiateMultipartUploadResult> CreateMultipartUploadAsync(string bucket, string key, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task<string> UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream data, long contentLength, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task<CompleteMultipartUploadResult> CompleteMultipartUploadAsync(string bucket, string key, string uploadId, CompleteMultipartUpload parts, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task AbortMultipartUploadAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task<ListPartsResult> ListPartsAsync(string bucket, string key, string uploadId, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task<CopyPartResult> UploadPartCopyAsync(string srcBucket, string srcKey, string destBucket, string destKey, string uploadId, int partNumber, long? rangeStart = null, long? rangeEnd = null, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
    public Task<Tagging> GetObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(new Tagging());
    public Task SetObjectTagsAsync(string bucket, string key, Tagging tags, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteObjectTagsAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<LegalHold> GetLegalHoldAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(new LegalHold { Status = "OFF" });
    public Task SetLegalHoldAsync(string bucket, string key, LegalHold legalHold, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<Retention> GetRetentionAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(new Retention());
    public Task SetRetentionAsync(string bucket, string key, Retention retention, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<AccessControlPolicy> GetObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(new AccessControlPolicy());
    public Task SetObjectAclAsync(string bucket, string key, AccessControlPolicy acl, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteObjectAclAsync(string bucket, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<RestoreObjectResult> RestoreObjectAsync(string bucket, string key, RestoreRequest request, CancellationToken cancellationToken = default) => throw new S3Exception(new Error(ErrorCode.NotImplemented));
}
```

---

## 2. Registering in Dependency Injection

Once implemented, register your custom backend in your DI container:

```csharp
builder.Services.AddSingleton<IS3StorageBackend, AzureBlobStorageBackend>();
```

Your SharpS3 server will now persist and serve all incoming Amazon S3 requests directly from Azure Blob Storage without modifying any client application code!
