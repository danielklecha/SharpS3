# Client Configuration & Best Practices

While initializing `new SharpS3Client(options)` is sufficient for quick tests and development, production applications benefit from fine-grained configuration of HTTP lifetimes, SigV4 authentication, error handling, and streaming performance.

---

## 1. Client Options (`SharpS3ClientOptions`)

Configure your client instance using `SharpS3ClientOptions`:

```csharp
using System;
using SharpS3;

var options = new SharpS3ClientOptions
{
    // Target endpoint (e.g., local server, AWS S3, MinIO, Wasabi)
    Endpoint = new Uri("http://localhost:8000"),

    // AWS credentials for SigV4 signing. If null/empty, requests are sent anonymously.
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",

    // Target AWS region
    Region = "us-east-1",

    // Use path-style URLs (http://host/bucket/key) vs virtual-hosted (http://bucket.host/key)
    ForcePathStyle = true,

    // HTTP timeout
    Timeout = TimeSpan.FromSeconds(60)
};

using ISharpS3Client client = new SharpS3Client(options);
```

---

## 2. Custom HttpClient Injection & `IHttpClientFactory`

To prevent socket exhaustion in high-throughput .NET services, manage your `HttpClient` lifecycle using `IHttpClientFactory`:

```csharp
// Program.cs
builder.Services.AddHttpClient("SharpS3Client", client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddSingleton<ISharpS3Client>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("SharpS3Client");

    var options = new SharpS3ClientOptions
    {
        Endpoint = new Uri(builder.Configuration["S3:Endpoint"]!),
        AccessKey = builder.Configuration["S3:AccessKey"],
        SecretKey = builder.Configuration["S3:SecretKey"],
        ForcePathStyle = true
    };

    // SharpS3Client will NOT dispose injected HttpClients
    return new SharpS3Client(options, httpClient);
});
```

---

## 3. Streaming and Memory Optimization

SharpS3 is designed to process gigabyte-scale objects with minimal memory allocation:

* **Use Stream Overloads:** Prefer `PutObjectAsync(bucket, key, stream, contentLength)` over byte arrays or strings when uploading large payloads.
* **Dispose S3ObjectResponse:** `GetObjectAsync` returns an `S3ObjectResponse` wrapping the live network response stream. Always wrap it in a `using` statement:

```csharp
using S3ObjectResponse response = await client.GetObjectAsync("my-bucket", "large-archive.zip");
using var fileStream = File.Create(@"C:\Downloads\large-archive.zip");
await response.Content.CopyToAsync(fileStream);
```

* **Byte Range Streaming:** For partial downloads, video streaming, or resume workflows, pass a `ByteRange`:

```csharp
// Download only bytes 0 to 1023 (first 1 KB)
var range = new ByteRange(0, 1023);
using var response = await client.GetObjectAsync("my-bucket", "video.mp4", range);
```

---

## 4. Error Handling and Exceptions

When a server rejects an S3 operation (e.g. `404 NoSuchBucket`, `403 AccessDenied`), `SharpS3Client` parses the error XML and throws a `SharpS3ClientException`:

```csharp
using SharpS3.Exceptions;

try
{
    await client.DeleteObjectAsync("my-bucket", "missing-file.txt");
}
catch (SharpS3ClientException ex)
{
    Console.WriteLine($"S3 Error Code: {ex.ErrorCode}"); // e.g. "NoSuchKey"
    Console.WriteLine($"HTTP Status: {ex.StatusCode}");   // e.g. 404
    Console.WriteLine($"Message: {ex.Message}");
    Console.WriteLine($"Resource: {ex.Resource}");
    Console.WriteLine($"Request ID: {ex.RequestId}");
}
```
