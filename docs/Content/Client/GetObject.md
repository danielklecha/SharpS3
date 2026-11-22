# Get-Object Example

Demonstrates how to download an object or stream a specific byte-range from an Amazon S3 bucket using `ISharpS3Client.GetObjectAsync`.

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using SharpS3;
using SharpS3.Models;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

// 1. Download full object
using (S3ObjectResponse response = await client.GetObjectAsync("my-bucket", "documents/sample.txt"))
{
    Console.WriteLine($"ETag: {response.ETag}");
    Console.WriteLine($"Content-Type: {response.ContentType}");
    Console.WriteLine($"Size: {response.ContentLength} bytes");

    string text = await response.ReadAsStringAsync();
    Console.WriteLine($"Content: {text}");
}

// 2. Download partial byte range (e.g. bytes 0-511)
var range = new ByteRange(0, 511);
using (S3ObjectResponse partialResponse = await client.GetObjectAsync("my-bucket", "documents/sample.txt", range))
{
    Console.WriteLine($"Range size: {partialResponse.ContentLength} bytes");
}
```
