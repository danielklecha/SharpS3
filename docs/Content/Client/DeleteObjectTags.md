# Delete-Object-Tags Example

Demonstrates how to remove all tags from an S3 object using `ISharpS3Client.DeleteObjectTagsAsync`.

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

await client.DeleteObjectTagsAsync("my-bucket", "documents/report.pdf");
Console.WriteLine("Object tags deleted.");
```
