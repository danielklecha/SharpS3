# Generate-Presigned-Url Example

Demonstrates how to generate an Amazon S3 SigV4 presigned URL for temporary access using `ISharpS3Client.GeneratePresignedUrl`.

```csharp
using System;
using System.Net.Http;
using SharpS3;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

// Generate presigned GET URL valid for 30 minutes
Uri presignedGet = client.GeneratePresignedUrl(
    bucket: "my-bucket",
    key: "invoices/invoice-42.pdf",
    method: HttpMethod.Get,
    expiresIn: TimeSpan.FromMinutes(30));

Console.WriteLine($"Presigned GET URL: {presignedGet}");

// Generate presigned PUT URL valid for 10 minutes
Uri presignedPut = client.GeneratePresignedUrl(
    bucket: "my-bucket",
    key: "uploads/user-asset.png",
    method: HttpMethod.Put,
    expiresIn: TimeSpan.FromMinutes(10));

Console.WriteLine($"Presigned PUT URL: {presignedPut}");
```
