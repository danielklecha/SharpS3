# Copy-Object Example

Demonstrates how to copy an object from a source bucket and key to a destination bucket and key without downloading the payload locally using `ISharpS3Client.CopyObjectAsync`.

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

CopyObjectResult result = await client.CopyObjectAsync(
    sourceBucket: "source-bucket",
    sourceKey: "templates/contract.pdf",
    destinationBucket: "client-bucket",
    destinationKey: "contracts/2026/signed-contract.pdf");

Console.WriteLine($"Object copied successfully!");
Console.WriteLine($"Destination ETag: {result.ETag}");
Console.WriteLine($"Last Modified: {result.LastModified}");
```
