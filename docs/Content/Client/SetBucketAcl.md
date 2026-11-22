# Set-Bucket-Acl Example

Demonstrates how to apply an Access Control Policy to a bucket using `ISharpS3Client.SetBucketAclAsync`.

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

var policy = new AccessControlPolicy
{
    Owner = new Owner { Id = "owner-id", DisplayName = "owner-name" },
    AccessControlList = new AccessControlList
    {
        Grants = new List<Grant>
        {
            new Grant
            {
                Grantee = new Grantee { Id = "owner-id", DisplayName = "owner-name" },
                Permission = PermissionEnum.FULL_CONTROL
            }
        }
    }
};

await client.SetBucketAclAsync("my-bucket", policy);
Console.WriteLine("Bucket ACL updated successfully.");
```
