# Get-Bucket-Logging Example

Demonstrates how to retrieve server access logging settings for an S3 bucket using `ISharpS3Client.GetBucketLoggingAsync`.

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

BucketLoggingStatus logging = await client.GetBucketLoggingAsync("my-bucket");

if (logging.LoggingEnabled != null)
{
    Console.WriteLine($"Target Bucket: {logging.LoggingEnabled.TargetBucket}");
    Console.WriteLine($"Target Prefix: {logging.LoggingEnabled.TargetPrefix}");
}
else
{
    Console.WriteLine("Bucket logging is disabled.");
}
```
