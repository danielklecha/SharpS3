# Set-Bucket-Website Example

Demonstrates how to configure static website hosting on an S3 bucket using `ISharpS3Client.SetBucketWebsiteAsync`.

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

var website = new WebsiteConfiguration
{
    IndexDocument = new IndexDocument { Suffix = "index.html" },
    ErrorDocument = new ErrorDocument { Key = "error.html" }
};

await client.SetBucketWebsiteAsync("my-bucket", website);
Console.WriteLine("Bucket website configuration set.");
```
