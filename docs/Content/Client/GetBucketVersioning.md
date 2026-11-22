# Get-Bucket-Versioning Example

Demonstrates how to retrieve the versioning configuration of a bucket using `ISharpS3Client.GetBucketVersioningAsync`.

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

VersioningConfiguration versioning = await client.GetBucketVersioningAsync("my-bucket");

Console.WriteLine($"Versioning Status: {versioning.Status}");
```
