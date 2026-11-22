# Get-Bucket-Location Example

Demonstrates how to retrieve the region constraint of an Amazon S3 bucket using `ISharpS3Client.GetBucketLocationAsync`.

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

LocationConstraint location = await client.GetBucketLocationAsync("my-bucket");

Console.WriteLine($"Bucket Location / Region: {location.Value}");
```
