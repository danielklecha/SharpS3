# Delete-Bucket Example

Demonstrates how to delete an empty S3 bucket using `ISharpS3Client.DeleteBucketAsync`.

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

// Delete an empty bucket
await client.DeleteBucketAsync("my-empty-bucket");

Console.WriteLine("Bucket deleted successfully.");
```

> [!NOTE]
> Per Amazon S3 specification, all objects and in-progress multipart uploads inside a bucket must be deleted before the bucket can be deleted. Otherwise, a `BucketNotEmpty` error is returned.
