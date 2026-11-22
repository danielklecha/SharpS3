# SharpS3 Server

SharpS3 provides high-performance, transport-agnostic server-side Amazon S3 support through `ISharpS3Server`, `SharpS3Server`, and `S3RequestDispatcher`. This enables building custom S3 servers, mock services, edge object stores, or S3-to-cloud gateways in .NET and ASP.NET Core.

---

## 1. Overview and Architecture

The SharpS3 server pipeline performs three primary functions:

1. **Request Deserialization & Protocol Parsing (`ISharpS3Server`):** Reads incoming raw HTTP metadata (`method`, `path`, `query`, `headers`, and standard `System.IO.Stream`), performs SigV4/SigV2 signature validation, resolves virtual-hosted or path-style routing, and packages the request into a strongly-typed `S3Request`.
2. **Operation Dispatching (`S3RequestDispatcher`):** Runs pre-request and authenticated hooks, checks permissions, and maps the `S3Request` to the corresponding `IS3StorageBackend` operation.
3. **Response Serialization:** Takes typed `IS3Response` models (or `S3Exception` error details) and serializes them into standard Amazon S3 XML payloads, HTTP status codes, and HTTP headers (`S3ServerResponse`).

```
HTTP Request (Headers, Query, Stream)
               │
               ▼
   SharpS3Server.ReceiveRequestAsync
               │
               ▼  (S3Request parsed & SigV4 verified)
   S3RequestDispatcher.DispatchAsync
               │
               ▼  (Routed to storage operation)
   IS3StorageBackend (e.g., FileSystemStorageBackend)
               │
               ▼  (Returns S3ServerResponse / XML / Stream)
HTTP Response (Status, S3 Headers, Response Body)
```

---

## 2. Dependency Injection Setup

Register `ISharpS3Server`, your desired `IS3StorageBackend`, and `S3RequestDispatcher` in your ASP.NET Core `Program.cs`:

```csharp
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Extensions;
using SharpS3.Storage;

var builder = WebApplication.CreateBuilder(args);

// Configure SharpS3 settings
var settings = new SharpS3Settings
{
    EnableSignatures = true
};
settings.Credentials["AKIAIOSFODNN7EXAMPLE"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ISharpS3Server, SharpS3Server>();
builder.Services.AddSingleton<IS3StorageBackend>(_ => new FileSystemStorageBackend(@"C:\Data\S3Storage"));
builder.Services.AddSingleton<S3RequestDispatcher>();

var app = builder.Build();
```

---

## 3. ASP.NET Core Minimal API Integration

Mount the S3 API directly into your ASP.NET Core routing table using `MapSharpS3` (see the [ASP.NET Core Integration](AspNetCoreIntegration.md) guide for the extension method implementation):

```csharp
// Map S3 REST API catch-all endpoint on standard route pattern (single port)
app.MapSharpS3("{**path}");

app.Run();
```

`MapSharpS3` binds `GET`, `PUT`, `POST`, `DELETE`, and `HEAD` verbs, passing incoming streams and metadata to `SharpS3Server` and piping output streams to Kestrel. See [ASP.NET Core Integration](AspNetCoreIntegration.md) for full instructions and copy-pasteable extension code.

---

## 4. Verb-Specific Routing Methods

If you are not using ASP.NET Core or need fine-grained control over individual HTTP verbs, `ISharpS3Server` provides verb-specific receiver methods alongside the catch-all `ReceiveRequestAsync`:

```csharp
// GET (Buckets, Objects, Queries)
IS3Request getReq = await server.ReceiveGetRequestAsync("/my-bucket/sample.txt", query, headers, ct);

// HEAD (Object or Bucket Existence & Metadata)
IS3Request headReq = await server.ReceiveHeadRequestAsync("/my-bucket/sample.txt", query, headers, ct);

// PUT (Bucket creation, Object upload, ACLs, Tagging)
IS3Request putReq = await server.ReceivePutRequestAsync("/my-bucket/sample.txt", bodyStream, query, headers, ct);

// POST (Multipart upload init/complete, Delete multiple)
IS3Request postReq = await server.ReceivePostRequestAsync("/my-bucket", bodyStream, query, headers, ct);

// DELETE (Bucket deletion, Object deletion, Tags)
IS3Request delReq = await server.ReceiveDeleteRequestAsync("/my-bucket/sample.txt", query, headers, bodyStream, ct);
```

---

## 5. Error Handling and S3 XML Envelopes

When errors occur, SharpS3 generates compliant Amazon S3 XML error envelopes matching the AWS S3 specification:

```csharp
catch (Exception ex)
{
    var s3Ex = ex as S3Exception ?? new S3Exception(new Error(ErrorCode.InternalError));
    using S3ServerResponse error = s3Server.CreateErrorResponse(s3Ex, context.TraceIdentifier);

    context.Response.StatusCode = error.StatusCode;
    context.Response.ContentType = error.ContentType ?? "application/xml";

    foreach (var h in error.Headers)
    {
        context.Response.Headers[h.Key] = h.Value;
    }

    if (error.BodyStream != Stream.Null)
    {
        await error.BodyStream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
```

Produces:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Error>
  <Code>NoSuchKey</Code>
  <Message>The specified key does not exist.</Message>
  <Key>sample.txt</Key>
  <BucketName>my-bucket</BucketName>
  <RequestId>17F3D08A1B2C3D4E</RequestId>
  <HostId>sharps3-host</HostId>
</Error>
```

---

## 6. Official AWS SDK Compatibility

Clients using the official AWS SDK (`AWSSDK.S3`) can connect directly:

```csharp
using Amazon.S3;
using Amazon.S3.Model;

var s3Config = new AmazonS3Config
{
    ServiceURL = "http://localhost:5000",
    ForcePathStyle = true,
    UseHttp = true
};

using var awsClient = new AmazonS3Client("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", s3Config);

var response = await awsClient.ListBucketsAsync();
foreach (var bucket in response.Buckets)
{
    Console.WriteLine($"Bucket: {bucket.BucketName}");
}
```
