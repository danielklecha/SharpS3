# Bucket-Exists Example

Demonstrates how to test whether a bucket exists and is accessible using `ISharpS3Client.BucketExistsAsync`.

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

bool exists = await client.BucketExistsAsync("my-bucket");

if (exists)
{
    Console.WriteLine("Bucket exists and is accessible.");
}
else
{
    Console.WriteLine("Bucket does not exist.");
}
```
