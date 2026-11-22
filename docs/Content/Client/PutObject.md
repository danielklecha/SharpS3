# Put-Object Example

Demonstrates how to upload an object to an Amazon S3 bucket from a stream, byte array, or string using `ISharpS3Client.PutObjectAsync`.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SharpS3;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

// Custom metadata tags
var metadata = new Dictionary<string, string>
{
    { "Author", "Daniel" },
    { "Department", "Engineering" }
};

// 1. Upload from String
await client.PutObjectAsync(
    bucket: "my-bucket",
    key: "notes/hello.txt",
    content: "Hello, world from SharpS3!",
    contentType: "text/plain; charset=utf-8",
    metadata: metadata);

// 2. Upload from Stream
using var stream = File.OpenRead(@"C:\Data\sample.pdf");
await client.PutObjectAsync(
    bucket: "my-bucket",
    key: "documents/sample.pdf",
    content: stream,
    contentLength: stream.Length,
    contentType: "application/pdf");

Console.WriteLine("Objects uploaded successfully.");
```
