# List-Buckets Example

Demonstrates how to retrieve a list of all S3 buckets owned by the authenticated sender using `ISharpS3Client.ListBucketsAsync`.

```csharp
using System;
using System.Threading.Tasks;
using SharpS3;
using SharpS3.Models;

var options = new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
};

using ISharpS3Client client = new SharpS3Client(options);

// Call ListBucketsAsync
ListAllMyBucketsResult result = await client.ListBucketsAsync();

Console.WriteLine($"Owner: {result.Owner?.DisplayName} ({result.Owner?.Id})");
Console.WriteLine($"Total Buckets: {result.Buckets.BucketList.Count}");

foreach (var bucket in result.Buckets.BucketList)
{
    Console.WriteLine($" - {bucket.Name} (Created: {bucket.CreationDate})");
}
```
