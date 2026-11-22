# Create-Bucket Example

Demonstrates how to create a new Amazon S3 bucket using `ISharpS3Client.CreateBucketAsync`.

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

// Create a new bucket named "my-new-bucket"
await client.CreateBucketAsync("my-new-bucket");

Console.WriteLine("Bucket 'my-new-bucket' created successfully.");
```
