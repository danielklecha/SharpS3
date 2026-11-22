# Abort-Multipart-Upload Example

Demonstrates how to cancel an active multipart upload and clean up all stored staging parts using `ISharpS3Client.AbortMultipartUploadAsync`.

```csharp
using System;
using System.Threading.Tasks;
using SharpS3;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

string uploadId = "<active-upload-id>";

// Abort the upload session
await client.AbortMultipartUploadAsync("my-bucket", "media/large-video.mp4", uploadId);

Console.WriteLine("Multipart upload aborted and temporary parts purged.");
```
