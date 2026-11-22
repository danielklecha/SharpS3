# FileSystem Storage Backend

`FileSystemStorageBackend` is the production-grade disk storage engine included with SharpS3. It maps Amazon S3 buckets and object keys directly to directories and files on local or networked filesystems.

---

## 1. Directory Structure

Given a root directory (e.g. `C:\Data\S3Storage` or `/var/s3data`):

```
C:\Data\S3Storage\
├── bucket-alpha\
│   ├── .multipart\
│   │   └── <upload-id>\
│   │       ├── 1.part
│   │       └── 2.part
│   ├── documents\
│   │   ├── report.pdf
│   │   ├── report.pdf.s3meta.xml
│   │   └── invoice.xml
│   │   └── invoice.xml.s3meta.xml
│   └── .bucket.s3meta.xml
└── bucket-beta\
    └── images\
        ├── photo.jpg
        └── photo.jpg.s3meta.xml
```

* **Buckets**: Each bucket corresponds to a top-level directory under the storage root.
* **Objects**: Keys with forward slashes (e.g. `documents/2026/report.pdf`) map to native subdirectories on disk.
* **Sidecar Metadata (`.s3meta.xml`)**: Object and bucket metadata (Content-Type, ETag, user-defined `x-amz-meta-*` tags, ACLs, legal holds, and retention policies) are stored alongside object files in compact XML sidecars.
* **Multipart Staging (`.multipart/`)**: Temporary multipart uploads are isolated in hidden `.multipart` directories under each bucket.

---

## 2. Key Features & Guarantees

### Atomic Streaming Writes
When writing an object via `PutObjectAsync`, data is first written to a temporary file (`<file>.<guid>.tmp`). Once the upload finishes and the MD5/SHA-256 checksum is verified, the file is atomically moved to its permanent path. This prevents corrupted or partial files from being exposed if a connection drops.

### Path Traversal Protection
All bucket names and object keys are strictly validated and normalized against the configured root directory. Any attempt to traverse directory boundaries (e.g. using `..`, absolute paths, or special characters) is caught and rejected with an `InvalidBucketName` or `InvalidArgument` S3 error.

### Automatic Directory Pruning
When an object is deleted via `DeleteObjectAsync` or `DeleteMultipleObjectsAsync`, empty parent directories are automatically removed recursively up to the bucket root. This prevents empty orphaned directory trees from cluttering the filesystem.

### Multipart Lifecycle
* Parts are uploaded directly to the `.multipart/<upload-id>/` directory.
* Upon `CompleteMultipartUploadAsync`, the engine concatenates parts sequentially into the destination object file, writes the sidecar, and deletes the temporary upload directory.
* Calling `AbortMultipartUploadAsync` instantly purges all staging files for that upload session.

---

## 3. Usage Example

Register `FileSystemStorageBackend` in your ASP.NET Core `Program.cs`:

```csharp
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Storage;

var builder = WebApplication.CreateBuilder(args);

// Register FileSystemStorageBackend pointing to a storage path
string storageRoot = builder.Configuration["SharpS3:StoragePath"] ?? @"C:\Data\S3Storage";
builder.Services.AddSingleton<IS3StorageBackend>(_ => new FileSystemStorageBackend(storageRoot));

builder.Services.AddSingleton<ISharpS3Server, SharpS3Server>();
builder.Services.AddSingleton<S3RequestDispatcher>();

var app = builder.Build();

// Map S3 endpoint (see ASP.NET Core Integration guide for MapSharpS3 extension)
app.MapSharpS3("{**path}");

app.Run();
```
