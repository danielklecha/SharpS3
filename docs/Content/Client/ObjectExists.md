# Object-Exists Example

Demonstrates how to check whether an object exists in an S3 bucket using `ISharpS3Client.ObjectExistsAsync`.

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

bool exists = await client.ObjectExistsAsync("my-bucket", "documents/report.pdf");

if (exists)
{
    Console.WriteLine("Object exists.");
}
else
{
    Console.WriteLine("Object was not found.");
}
```
