# List-All-Objects Example

Demonstrates how to automatically paginate through all objects in a bucket using asynchronous streams (`IAsyncEnumerable<ObjectMetadata>`) via `ISharpS3Client.ListAllObjectsAsync`.

```csharp
using System;
using System.Threading.Tasks;
using SharpS3;
using SharpS3.Models;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

int count = 0;

// Automatically fetches subsequent pages as items are enumerated
await foreach (ObjectMetadata obj in client.ListAllObjectsAsync("my-bucket", prefix: "logs/2026/"))
{
    count++;
    Console.WriteLine($"[{count}] {obj.Key} ({obj.Size} bytes)");
}

Console.WriteLine($"Total objects enumerated: {count}");
```
