# Download-File Example

Demonstrates how to download an S3 object directly to a destination path on local disk using `ISharpS3Client.DownloadFileAsync`.

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

await client.DownloadFileAsync(
    bucket: "my-bucket",
    key: "documents/report.pdf",
    destinationFilePath: @"C:\Downloads\report.pdf");

Console.WriteLine("File downloaded to disk successfully.");
```
