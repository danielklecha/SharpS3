# Set-Bucket-Logging Example

Demonstrates how to configure server access logging on an S3 bucket using `ISharpS3Client.SetBucketLoggingAsync`.

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

var status = new BucketLoggingStatus
{
    LoggingEnabled = new LoggingEnabled
    {
        TargetBucket = "logs-bucket",
        TargetPrefix = "s3-access-logs/"
    }
};

await client.SetBucketLoggingAsync("my-bucket", status);
Console.WriteLine("Bucket logging configuration updated.");
```
