# SharpS3 Operations

The `ISharpS3Client` interface exposes the following strongly-typed S3 operations. Each operation below links to a dedicated example page.

## Service Operations

| Operation | Method | Example |
| --- | --- | --- |
| List-Buckets | `ListBucketsAsync` | [ListBuckets.md](ListBuckets.md) |

## Bucket Operations

| Operation | Method | Example |
| --- | --- | --- |
| Create-Bucket | `CreateBucketAsync` | [CreateBucket.md](CreateBucket.md) |
| Delete-Bucket | `DeleteBucketAsync` | [DeleteBucket.md](DeleteBucket.md) |
| Bucket-Exists | `BucketExistsAsync` | [BucketExists.md](BucketExists.md) |
| Get-Bucket-Location | `GetBucketLocationAsync` | [GetBucketLocation.md](GetBucketLocation.md) |
| Get-Bucket-Acl | `GetBucketAclAsync` | [GetBucketAcl.md](GetBucketAcl.md) |
| Set-Bucket-Acl | `SetBucketAclAsync` | [SetBucketAcl.md](SetBucketAcl.md) |
| Get-Bucket-Tags | `GetBucketTagsAsync` | [GetBucketTags.md](GetBucketTags.md) |
| Set-Bucket-Tags | `SetBucketTagsAsync` | [SetBucketTags.md](SetBucketTags.md) |
| Delete-Bucket-Tags | `DeleteBucketTagsAsync` | [DeleteBucketTags.md](DeleteBucketTags.md) |
| Get-Bucket-Versioning | `GetBucketVersioningAsync` | [GetBucketVersioning.md](GetBucketVersioning.md) |
| Set-Bucket-Versioning | `SetBucketVersioningAsync` | [SetBucketVersioning.md](SetBucketVersioning.md) |
| Get-Bucket-Website | `GetBucketWebsiteAsync` | [GetBucketWebsite.md](GetBucketWebsite.md) |
| Set-Bucket-Website | `SetBucketWebsiteAsync` | [SetBucketWebsite.md](SetBucketWebsite.md) |
| Delete-Bucket-Website | `DeleteBucketWebsiteAsync` | [DeleteBucketWebsite.md](DeleteBucketWebsite.md) |
| Get-Bucket-Logging | `GetBucketLoggingAsync` | [GetBucketLogging.md](GetBucketLogging.md) |
| Set-Bucket-Logging | `SetBucketLoggingAsync` | [SetBucketLogging.md](SetBucketLogging.md) |

## Object Operations

| Operation | Method | Example |
| --- | --- | --- |
| Get-Object | `GetObjectAsync` | [GetObject.md](GetObject.md) |
| Put-Object | `PutObjectAsync` | [PutObject.md](PutObject.md) |
| Head-Object | `HeadObjectAsync` | [HeadObject.md](HeadObject.md) |
| Object-Exists | `ObjectExistsAsync` | [ObjectExists.md](ObjectExists.md) |
| Delete-Object | `DeleteObjectAsync` | [DeleteObject.md](DeleteObject.md) |
| Delete-Objects | `DeleteObjectsAsync` | [DeleteObjects.md](DeleteObjects.md) |
| Copy-Object | `CopyObjectAsync` | [CopyObject.md](CopyObject.md) |
| List-Objects-V2 | `ListObjectsV2Async` | [ListObjectsV2.md](ListObjectsV2.md) |
| List-All-Objects | `ListAllObjectsAsync` | [ListAllObjects.md](ListAllObjects.md) |
| Get-Object-Tags | `GetObjectTagsAsync` | [GetObjectTags.md](GetObjectTags.md) |
| Set-Object-Tags | `SetObjectTagsAsync` | [SetObjectTags.md](SetObjectTags.md) |
| Delete-Object-Tags | `DeleteObjectTagsAsync` | [DeleteObjectTags.md](DeleteObjectTags.md) |

## High-Level Convenience & Presigned URLs

| Operation | Method | Example |
| --- | --- | --- |
| Upload-File | `UploadFileAsync` | [UploadFile.md](UploadFile.md) |
| Download-File | `DownloadFileAsync` | [DownloadFile.md](DownloadFile.md) |
| Generate-Presigned-Url | `GeneratePresignedUrl` | [GeneratePresignedUrl.md](GeneratePresignedUrl.md) |

## Multipart Upload Operations

| Operation | Method | Example |
| --- | --- | --- |
| Initiate-Multipart-Upload | `InitiateMultipartUploadAsync` | [InitiateMultipartUpload.md](InitiateMultipartUpload.md) |
| Upload-Part | `UploadPartAsync` | [UploadPart.md](UploadPart.md) |
| Complete-Multipart-Upload | `CompleteMultipartUploadAsync` | [CompleteMultipartUpload.md](CompleteMultipartUpload.md) |
| Abort-Multipart-Upload | `AbortMultipartUploadAsync` | [AbortMultipartUpload.md](AbortMultipartUpload.md) |
