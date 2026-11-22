# Delete-Bucket-Website Example

Demonstrates how to remove website configuration from an S3 bucket using `ISharpS3Client.DeleteBucketWebsiteAsync`.

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

await client.DeleteBucketWebsiteAsync("my-bucket");
Console.WriteLine("Bucket website hosting disabled.");
```
