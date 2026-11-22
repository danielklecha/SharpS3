# Complete-Multipart-Upload Example

Demonstrates how to assemble uploaded parts to complete a multipart upload using `ISharpS3Client.CompleteMultipartUploadAsync`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpS3;
using SharpS3.Models;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

string uploadId = "<active-upload-id>";

// List the parts that were uploaded
var completion = new CompleteMultipartUpload
{
    Parts = new List<Part>
    {
        new Part(1, "\"etag-of-part-1\""),
        new Part(2, "\"etag-of-part-2\"")
    }
};

CompleteMultipartUploadResult result = await client.CompleteMultipartUploadAsync(
    bucket: "my-bucket",
    key: "media/large-video.mp4",
    uploadId: uploadId,
    complete: completion);

Console.WriteLine($"Multipart upload completed successfully!");
Console.WriteLine($"Location: {result.Location}");
Console.WriteLine($"ETag: {result.ETag}");
```
