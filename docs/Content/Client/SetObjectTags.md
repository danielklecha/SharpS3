# Set-Object-Tags Example

Demonstrates how to assign key-value tags to an existing object using `ISharpS3Client.SetObjectTagsAsync`.

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
tagging.TagSet.Tags.Add(new Tag("Status", "Approved"));
tagging.TagSet.Tags.Add(new Tag("RetentionPolicy", "3-Years"));

await client.SetObjectTagsAsync("my-bucket", "documents/report.pdf", tagging);
Console.WriteLine("Object tags set successfully.");
```
