# Get-Bucket-Acl Example

Demonstrates how to retrieve the Access Control Policy of a bucket using `ISharpS3Client.GetBucketAclAsync`.

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

AccessControlPolicy acl = await client.GetBucketAclAsync("my-bucket");

Console.WriteLine($"Owner: {acl.Owner?.DisplayName} ({acl.Owner?.Id})");
if (acl.AccessControlList?.Grants != null)
{
    foreach (var grant in acl.AccessControlList.Grants)
    {
        Console.WriteLine($"Grantee: {grant.Grantee?.DisplayName}, Permission: {grant.Permission}");
    }
}
```
