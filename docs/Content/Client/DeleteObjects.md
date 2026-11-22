# Delete-Objects Example

Demonstrates how to delete multiple objects in a single batch request using `ISharpS3Client.DeleteObjectsAsync`.

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpS3;
using SharpS3.Models;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

var batch = new DeleteMultiple
{
    Quiet = false,
    Objects = new List<ObjectIdentifier>
    {
        new ObjectIdentifier("documents/file1.txt"),
        new ObjectIdentifier("documents/file2.txt"),
        new ObjectIdentifier("documents/file3.txt")
    }
};

DeleteResult result = await client.DeleteObjectsAsync("my-bucket", batch);

foreach (var deleted in result.Deleted)
{
    Console.WriteLine($"Deleted: {deleted.Key}");
}

foreach (var error in result.Errors)
{
    Console.WriteLine($"Failed to delete: {error.Key} - {error.Message}");
}
```
