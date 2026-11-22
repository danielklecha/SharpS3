# SharpS3

[![NuGet](https://img.shields.io/nuget/v/SharpS3.svg)](https://www.nuget.org/packages/SharpS3)
[![NuGet downloads](https://img.shields.io/nuget/dt/SharpS3.svg)](https://www.nuget.org/packages/SharpS3)
[![Docs](https://img.shields.io/badge/project-docs-blue)](https://danielklecha.github.io/SharpS3/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](https://github.com/danielklecha/SharpS3/blob/master/LICENSE)
[![Coverage Status](https://coveralls.io/repos/github/danielklecha/SharpS3/badge.svg?branch=master)](https://coveralls.io/github/danielklecha/SharpS3?branch=master)

A transport-agnostic .NET library for building Amazon S3 compatible clients, servers, and storage backends.

## Features

- **Single-Port In-Process Server**: Zero subprocesses or separate server ports. Mounts directly into any web framework pipeline or custom endpoints on the same port as your application.
- **Transport-Agnostic Protocol Engine**: Operates entirely on raw HTTP metadata (`method`, `path`, `headers`, `query`) and standard `System.IO.Stream`, with zero coupling to any web server or socket layer.
- **Client & Server SDK**: Strongly-typed interfaces to build S3 client applications (`ISharpS3Client`) and S3-compatible service hosts (`ISharpS3Server`).
- **Comprehensive S3 API Support**:
  - Service operations (`ListBuckets`)
  - Bucket operations (`CreateBucket`, `DeleteBucket`, `BucketExists`, `GetBucketLocation`, `Get/PutBucketAcl`, `Get/PutBucketTagging`)
  - Object operations (`GetObject`, `PutObject`, `DeleteObject`, `DeleteObjects`, `HeadObject`, `CopyObject`, `Get/PutObjectAcl`, `Get/PutObjectTagging`, `Get/PutObjectLegalHold`, `Get/PutObjectRetention`)
  - Multipart upload workflow (`CreateMultipartUpload`, `UploadPart`, `UploadPartCopy`, `CompleteMultipartUpload`, `AbortMultipartUpload`, `ListParts`, `ListMultipartUploads`)
  - Byte-range streaming (`Range: bytes=start-end` and suffix ranges)
- **AOT & Trimming**: Full support for Native AOT compilation and assembly trimming.
- **No Reflection**: Uses compile-time Roslyn source generators (`SharpS3.Generators`) for XML serialization and deserialization with zero runtime reflection for optimal performance and trim safety.
- **AWS SDK Compatible**: Fully verified against the official Amazon AWS SDK for .NET (`AWSSDK.S3`).
- **Streaming AWS-Chunked Decoding & SigV4 Verification**: Native streaming decode for `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` payloads with on-the-fly chunk and trailer HMAC verification via `AwsChunkedDecodingStream`.
- **Presigned URLs**: Built-in support for generating and validating SigV4 presigned URLs with custom expirations.
- **Pluggable Storage Backends (`IS3StorageBackend`)**:
  - `InMemoryStorageBackend`: Fast, thread-safe in-memory backend for unit tests and local development.
  - `FileSystemStorageBackend`: Production-grade disk storage with directory-based buckets, file-based objects, atomic streaming writes, XML sidecar metadata (`.s3meta.xml`), path-traversal protection, and directory pruning.
- **Verb-Specific & Catch-All Routing**: Clean `ReceiveGetRequestAsync`, `ReceiveHeadRequestAsync`, `ReceivePutRequestAsync`, `ReceivePostRequestAsync`, and `ReceiveDeleteRequestAsync` methods alongside catch-all `ReceiveRequestAsync`.
- **Built-in Observability & OpenTelemetry**: Instruments metric counters, histograms, and distributed tracing spans via `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource`.
- **Target Frameworks**: Targets **`.NET Standard 2.0`** and **`.NET 10.0`** for broad cross-platform and runtime support.

## Installation

Install the library via NuGet:

```bash
dotnet add package SharpS3
```

## Getting Started

### 1. S3 Client Quick Start

Initialize `SharpS3Client` and perform standard S3 operations:

```csharp
using SharpS3;
using SharpS3.Models;

// 1. Initialize client
var options = new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "test-access-key",
    SecretKey = "test-secret-key",
    ForcePathStyle = true
};

using ISharpS3Client client = new SharpS3Client(options);

// 2. Create a bucket
await client.CreateBucketAsync("my-bucket");

// 3. Upload an object
await client.PutObjectAsync("my-bucket", "documents/sample.txt", "Hello S3 from SharpS3!");

// 4. Download an object
using var response = await client.GetObjectAsync("my-bucket", "documents/sample.txt");
string content = await response.ReadAsStringAsync();
Console.WriteLine($"Downloaded: {content}");

// 5. Generate a presigned URL (valid for 1 hour)
Uri presignedUrl = client.GeneratePresignedUrl("my-bucket", "documents/sample.txt", HttpMethod.Get, TimeSpan.FromHours(1));
Console.WriteLine($"Presigned URL: {presignedUrl}");
```

### 2. S3 Server & Protocol Engine

SharpS3 is transport-agnostic and decoupled from any web server. Receive and dispatch requests in any framework using `ISharpS3Server` and `S3RequestDispatcher`:

```csharp
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Storage;

// 1. Initialize SharpS3 protocol server and storage backend
ISharpS3Server s3Server = new SharpS3Server();
IS3StorageBackend backend = new FileSystemStorageBackend(@"C:\Data\S3Storage");
var dispatcher = new S3RequestDispatcher(backend);

// 2. Parse incoming HTTP request metadata and body stream
var s3Request = await s3Server.ReceiveRequestAsync(
    httpMethod: "GET",
    path: "/my-bucket/documents/sample.txt",
    queryParameters: queryDictionary,
    headers: headerDictionary,
    bodyStream: requestStream);

// 3. Dispatch to storage backend and obtain response stream
using var s3Response = await dispatcher.DispatchAsync(s3Request);
Console.WriteLine($"Status: {s3Response.StatusCode}, Content-Type: {s3Response.ContentType}");
```

### 3. Using Official AWS SDK (`AWSSDK.S3`)

Any standard AWS SDK client can connect directly to your SharpS3 server:

```csharp
using Amazon.S3;
using Amazon.S3.Model;

var s3Config = new AmazonS3Config
{
    ServiceURL = "http://localhost:8000",
    ForcePathStyle = true,
    UseHttp = true
};

using var awsClient = new AmazonS3Client("test-key", "test-secret", s3Config);

// List buckets using AWS SDK
var response = await awsClient.ListBucketsAsync();
foreach (var b in response.Buckets)
{
    Console.WriteLine($"Bucket: {b.BucketName}");
}
```

## Storage Backends

SharpS3 separates the S3 wire protocol from persistence through the `IS3StorageBackend` interface:

| Backend | Implementation | Best Used For |
|---|---|---|
| **In-Memory** | `InMemoryStorageBackend` | Unit testing, integration tests, mock services, ephemeral cache |
| **File System** | `FileSystemStorageBackend` | Local S3 storage, edge appliances, NAS/SAN storage, on-prem S3 |
| **Custom** | `IS3StorageBackend` | Azure Blob, Google Cloud Storage, SQLite, or database backends |

### Custom Storage Backend Example

Implement `IS3StorageBackend` to back S3 requests with any custom store:

```csharp
public class CustomStorageBackend : IS3StorageBackend
{
    public Task<ListAllMyBucketsResult> ListBucketsAsync(IS3Request request, CancellationToken ct = default)
    {
        // Custom bucket listing logic...
    }

    public Task<S3ObjectPayload> ReadObjectAsync(string bucket, string key, IS3Request request, CancellationToken ct = default)
    {
        // Custom stream streaming logic...
    }

    // Implement remaining bucket and object operations...
}
```

## Contributing

Contributions are welcome! Please feel free to submit issues, pull requests, or feature proposals.

## License

`SharpS3` is provided as-is under the [MIT license](LICENSE).
