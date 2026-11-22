# Set-Bucket-Versioning Example

Demonstrates how to enable or suspend versioning on an S3 bucket using `ISharpS3Client.SetBucketVersioningAsync`.

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

var config = new VersioningConfiguration
{
    Status = VersioningStatusEnum.Enabled
};

await client.SetBucketVersioningAsync("my-bucket", config);
Console.WriteLine("Bucket versioning enabled.");
```
