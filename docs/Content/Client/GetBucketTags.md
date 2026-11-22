# Get-Bucket-Tags Example

Demonstrates how to retrieve tags assigned to a bucket using `ISharpS3Client.GetBucketTagsAsync`.

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

Tagging tagging = await client.GetBucketTagsAsync("my-bucket");

foreach (var tag in tagging.TagSet.Tags)
{
    Console.WriteLine($"Key: {tag.Key}, Value: {tag.Value}");
}
```
