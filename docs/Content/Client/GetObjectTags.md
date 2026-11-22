# Get-Object-Tags Example

Demonstrates how to retrieve key-value tags associated with an S3 object using `ISharpS3Client.GetObjectTagsAsync`.

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

Tagging tagging = await client.GetObjectTagsAsync("my-bucket", "documents/report.pdf");

foreach (var tag in tagging.TagSet.Tags)
{
    Console.WriteLine($"Tag: {tag.Key} = {tag.Value}");
}
```
