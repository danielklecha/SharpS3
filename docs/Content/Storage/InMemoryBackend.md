# InMemory Storage Backend

`InMemoryStorageBackend` is a fast, thread-safe, in-memory implementation of `IS3StorageBackend`. It is designed specifically for automated testing, integration tests, mock services, and ephemeral caching where persistence to physical disk is not required.

---

## 1. Key Benefits

* **Zero Disk I/O:** Operates entirely within memory, ensuring rapid test execution.
* **Thread-Safe:** Uses thread-safe collections (`ConcurrentDictionary`) to support concurrent client requests and parallel test runners.
* **Complete S3 API Support:** Implements the entire `IS3StorageBackend` contract, including:
  * Bucket creation, deletion, existence checks, tagging, versioning, website, and ACLs.
  * Object reading, streaming, writing, byte ranges, copying, and batch deletions.
  * Full multipart upload workflow (initiation, part uploading, completion, and abort).
  * Object tagging, legal holds, and retention policies.
* **No Cleanup Required:** When the test process ends or the backend is disposed, all allocated data is instantly garbage-collected with no leftover temporary files on disk.

---

## 2. Using in Unit & Integration Tests

Here is an example showing how to initialize `InMemoryStorageBackend` alongside `SharpS3Server` and `SharpS3Client` in an xUnit test:

```csharp
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Storage;

public class S3IntegrationTests
{
    [Fact]
    public async Task PutAndGetObject_InMemoryBackend_Success()
    {
        // 1. Initialize in-memory storage backend
        var backend = new InMemoryStorageBackend();
        var server = new SharpS3Server();
        var dispatcher = new S3RequestDispatcher(backend);

        // 2. Create bucket in backend
        await backend.CreateBucketAsync("test-bucket", null);

        // 3. Put an object directly or via dispatcher
        byte[] payload = Encoding.UTF8.GetBytes("Hello In-Memory S3!");
        using var stream = new MemoryStream(payload);
        
        string etag = await backend.PutObjectAsync(
            "test-bucket",
            "notes/sample.txt",
            stream,
            payload.Length,
            "text/plain");

        Assert.NotNull(etag);

        // 4. Read object back
        var result = await backend.GetObjectAsync("test-bucket", "notes/sample.txt");
        using var reader = new StreamReader(result.DataStream);
        string content = await reader.ReadToEndAsync();

        Assert.Equal("Hello In-Memory S3!", content);
        Assert.Equal("text/plain", result.ContentType);
    }
}
```

---

## 3. Dependency Injection Setup

To use `InMemoryStorageBackend` in ASP.NET Core for local development or mock environments:

```csharp
builder.Services.AddSingleton<IS3StorageBackend, InMemoryStorageBackend>();
```
