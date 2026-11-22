# Head-Object Example

Demonstrates how to retrieve metadata, size, ETag, and HTTP headers of an object without downloading its payload (HTTP HEAD) using `ISharpS3Client.HeadObjectAsync`.

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

ObjectMetadataResponse? metadata = await client.HeadObjectAsync("my-bucket", "documents/report.pdf");

if (metadata != null)
{
    Console.WriteLine($"ETag: {metadata.ETag}");
    Console.WriteLine($"Content-Length: {metadata.ContentLength} bytes");
    Console.WriteLine($"Content-Type: {metadata.ContentType}");
    Console.WriteLine($"Last-Modified: {metadata.LastModified}");

    foreach (var tag in metadata.Metadata)
    {
        Console.WriteLine($"Metadata: {tag.Key} = {tag.Value}");
    }
}
else
{
    Console.WriteLine("Object does not exist.");
}
```
