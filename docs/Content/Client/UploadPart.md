# Upload-Part Example

Demonstrates how to upload individual numbered parts of a multipart upload using `ISharpS3Client.UploadPartAsync`.

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

string uploadId = "<active-upload-id>";
int partNumber = 1;

// Upload from stream
using var partStream = File.OpenRead(@"C:\Data\part1.bin");
UploadPartResult partResult = await client.UploadPartAsync(
    bucket: "my-bucket",
    key: "media/large-video.mp4",
    uploadId: uploadId,
    partNumber: partNumber,
    content: partStream,
    contentLength: partStream.Length);

Console.WriteLine($"Part {partResult.PartNumber} uploaded successfully. ETag: {partResult.ETag}");
```
