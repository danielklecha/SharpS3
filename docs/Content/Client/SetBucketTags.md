# Set-Bucket-Tags Example

Demonstrates how to assign key-value tags to a bucket using `ISharpS3Client.SetBucketTagsAsync`.

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

var tagging = new Tagging();
tagging.TagSet.Tags.Add(new Tag("Environment", "Production"));
tagging.TagSet.Tags.Add(new Tag("Project", "SharpS3"));

await client.SetBucketTagsAsync("my-bucket", tagging);
Console.WriteLine("Bucket tags configured successfully.");
```
