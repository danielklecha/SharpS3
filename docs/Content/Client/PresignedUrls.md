# Presigned URLs

Presigned URLs grant temporary authenticated access to Amazon S3 objects using AWS Signature Version 4. This enables client applications to upload or download files directly without exposing AWS credentials to end users.

---

## 1. Generating a Presigned Download URL (GET)

Create a temporary download link valid for 1 hour:

```csharp
using System;
using System.Net.Http;
using SharpS3;

var client = new SharpS3Client(new SharpS3ClientOptions
{
    Endpoint = new Uri("http://localhost:8000"),
    AccessKey = "AKIAIOSFODNN7EXAMPLE",
    SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
});

// Generate presigned GET URL valid for 60 minutes
Uri downloadUrl = client.GeneratePresignedUrl(
    bucket: "reports",
    key: "financial/2026-Q1.pdf",
    method: HttpMethod.Get,
    expiresIn: TimeSpan.FromHours(1));

Console.WriteLine($"Download URL: {downloadUrl}");
```

---

## 2. Generating a Presigned Upload URL (PUT)

Generate an authorized upload link that allows an external client to upload an object directly:

```csharp
// Generate presigned PUT URL valid for 15 minutes
Uri uploadUrl = client.GeneratePresignedUrl(
    bucket: "user-uploads",
    key: "avatars/user-9912.png",
    method: HttpMethod.Put,
    expiresIn: TimeSpan.FromMinutes(15));

Console.WriteLine($"Upload URL: {uploadUrl}");
```

The recipient can then upload data directly with a standard HTTP PUT request:

```bash
curl -X PUT --upload-file avatar.png "<presigned-url>"
```
