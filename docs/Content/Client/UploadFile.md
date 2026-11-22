# Upload-File Example

Demonstrates how to upload a local disk file directly into an Amazon S3 bucket using `ISharpS3Client.UploadFileAsync`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpS3;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

var metadata = new Dictionary<string, string>
{
    { "UploadedBy", "AdminApp" }
};

await client.UploadFileAsync(
    bucket: "my-bucket",
    key: "backups/database-backup.bak",
    filePath: @"C:\Backups\database-backup.bak",
    contentType: "application/octet-stream",
    metadata: metadata);

Console.WriteLine("File uploaded successfully.");
```
