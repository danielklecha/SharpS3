# SharpS3 Storage Backends

SharpS3 separates the Amazon S3 wire protocol engine (`ISharpS3Server` and `S3RequestDispatcher`) from actual physical data storage through the `IS3StorageBackend` interface.

This architectural decoupling provides total flexibility: you can run the exact same S3 REST API in front of local disk directories, an in-memory test store, relational/NoSQL databases, or hybrid cloud object stores.

---

## 1. Storage Architecture

When a request arrives, `SharpS3Server` decodes the HTTP verb, headers, query string, and payload into a strongly-typed `S3Request`. The `S3RequestDispatcher` validates authentication and route constraints, and then invokes the corresponding method on `IS3StorageBackend`.

```
                    ┌───────────────────────────────────────────┐
                    │            HTTP Client / AWS SDK          │
                    └─────────────────────┬─────────────────────┘
                                          │ HTTP REST Request
                                          ▼
                    ┌───────────────────────────────────────────┐
                    │               SharpS3Server               │
                    │  (SigV4 / Headers / Query / Stream Parse) │
                    └─────────────────────┬─────────────────────┘
                                          │ S3Request
                                          ▼
                    ┌───────────────────────────────────────────┐
                    │            S3RequestDispatcher            │
                    │      (Auth, Hooks, Routing Engine)        │
                    └─────────────────────┬─────────────────────┘
                                          │ Operation Dispatch
                                          ▼
                    ┌───────────────────────────────────────────┐
                    │             IS3StorageBackend             │
                    └──────┬──────────────┬──────────────┬──────┘
                           │              │              │
                           ▼              ▼              ▼
                 ┌──────────────────┐ ┌─────────┐ ┌───────────────┐
                 │FileSystemBackend │ │InMemory │ │ Custom Backend│
                 │ (Disk / Sidecars)│ │ (Tests) │ │ (Azure / DB)  │
                 └──────────────────┘ └─────────┘ └───────────────┘
```

---

## 2. The `IS3StorageBackend` Interface

The `IS3StorageBackend` interface defines methods across five operational categories:

### Service Operations
* `ListBucketsAsync`: Lists all buckets owned by the caller.

### Bucket Operations
* `BucketExistsAsync`: Checks whether a bucket exists.
* `CreateBucketAsync`: Creates a new bucket with optional location constraint.
* `DeleteBucketAsync`: Deletes an empty bucket.
* `GetBucketLocationAsync`: Returns the region where the bucket resides.
* `ListObjectsAsync`: Lists objects in a bucket (supporting ListObjects v1 and v2 pagination, prefix, delimiter, and continuation tokens).
* `ListObjectVersionsAsync`: Lists versioned objects and delete markers.
* `ListMultipartUploadsAsync`: Lists in-progress multipart upload sessions.
* `GetBucketAclAsync`, `SetBucketAclAsync`, `DeleteBucketAclAsync`: Manages bucket access control policies.
* `GetBucketTagsAsync`, `SetBucketTagsAsync`, `DeleteBucketTagsAsync`: Manages bucket key-value tags.
* `GetBucketVersioningAsync`, `SetBucketVersioningAsync`: Configures bucket versioning status.
* `GetBucketWebsiteAsync`, `SetBucketWebsiteAsync`, `DeleteBucketWebsiteAsync`: Manages static website hosting configuration.
* `GetBucketLoggingAsync`, `SetBucketLoggingAsync`: Manages server access logging status.

### Object Operations
* `GetObjectMetadataAsync`: Retrieves metadata without body content (`HEAD` request).
* `GetObjectAsync`: Downloads object content with optional byte-range streaming (`GET` request).
* `PutObjectAsync`: Stores an object stream along with metadata, returning the calculated ETag (`PUT` request).
* `DeleteObjectAsync`: Removes an object from storage.
* `DeleteMultipleObjectsAsync`: Deletes multiple objects in a single batch request.
* `CopyObjectAsync`: Performs a server-side copy between keys and buckets.

### Multipart Upload Operations
* `CreateMultipartUploadAsync`: Initiates a new multipart session and returns an `UploadId`.
* `UploadPartAsync`: Stores an individual numbered chunk for a multipart session.
* `UploadPartCopyAsync`: Stores a part by copying byte ranges from an existing object.
* `ListPartsAsync`: Lists parts successfully uploaded for an active session.
* `CompleteMultipartUploadAsync`: Assembles all parts in sequential order into the final object.
* `AbortMultipartUploadAsync`: Cancels the upload session and cleans up all temporary part files.

### Metadata, Locking & ACL Operations
* `GetObjectTagsAsync`, `SetObjectTagsAsync`, `DeleteObjectTagsAsync`: Object tagging.
* `GetLegalHoldAsync`, `SetLegalHoldAsync`: Object Lock legal hold management.
* `GetRetentionAsync`, `SetRetentionAsync`: Object Lock retention configuration.
* `GetObjectAclAsync`, `SetObjectAclAsync`, `DeleteObjectAclAsync`: Object access control policies.
* `RestoreObjectAsync`: Archival restoration workflows.

---

## 3. Built-In Implementations

| Backend | Class | Recommended Use Case |
|---|---|---|
| **File System** | `FileSystemStorageBackend` | Production standalone servers, edge appliances, NAS/SAN storage, on-prem S3 storage. |
| **In-Memory** | `InMemoryStorageBackend` | Unit tests, integration tests, CI pipelines, local development, ephemeral mock servers. |
| **Callback** | `CallbackStorageBackend` | Mocking specific backend behaviors or intercepting calls in tests. |
| **Custom** | Custom implementation | Azure Blob Storage, Google Cloud Storage, PostgreSQL/SQLite large object stores. |

For detailed implementation guides, see:
* [FileSystem Backend](FileSystemBackend.md)
* [InMemory Backend](InMemoryBackend.md)
* [Custom Storage Backend](CustomBackend.md)
