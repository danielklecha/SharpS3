# Delete-Object Example

Demonstrates how to delete a single object from an S3 bucket using `ISharpS3Client.DeleteObjectAsync`.

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

await client.DeleteObjectAsync("my-bucket", "documents/old-report.pdf");
Console.WriteLine("Object deleted successfully.");
```
