# Get-Bucket-Website Example

Demonstrates how to retrieve static website configuration for an S3 bucket using `ISharpS3Client.GetBucketWebsiteAsync`.

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

WebsiteConfiguration website = await client.GetBucketWebsiteAsync("my-bucket");

Console.WriteLine($"Index Document: {website.IndexDocument?.Suffix}");
Console.WriteLine($"Error Document: {website.ErrorDocument?.Key}");
```
