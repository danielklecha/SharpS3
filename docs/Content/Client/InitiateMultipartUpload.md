# Initiate-Multipart-Upload Example

Demonstrates how to initiate a multipart upload session for large objects using `ISharpS3Client.InitiateMultipartUploadAsync`.

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

InitiateMultipartUploadResult initResult = await client.InitiateMultipartUploadAsync(
    bucket: "my-bucket",
    key: "media/large-video.mp4",
    contentType: "video/mp4");

Console.WriteLine($"Multipart upload initiated!");
Console.WriteLine($"Bucket: {initResult.Bucket}");
Console.WriteLine($"Key: {initResult.Key}");
Console.WriteLine($"Upload ID: {initResult.UploadId}");
```
