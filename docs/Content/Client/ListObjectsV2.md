# List-Objects-V2 Example

Demonstrates how to list objects and prefixes in a bucket using `ISharpS3Client.ListObjectsV2Async`.

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

// List first page of objects under prefix "photos/"
ListBucketResult result = await client.ListObjectsV2Async(
    bucket: "my-bucket",
    prefix: "photos/",
    maxKeys: 100);

Console.WriteLine($"Objects returned: {result.Contents.Count}");

foreach (var item in result.Contents)
{
    Console.WriteLine($"Key: {item.Key}, Size: {item.Size} bytes, Modified: {item.LastModified}");
}

if (result.IsTruncated == true)
{
    Console.WriteLine($"Next continuation token: {result.NextContinuationToken}");
}
```
