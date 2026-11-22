namespace SharpS3.Tests.Unit;

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public partial class SharpS3ServerTests
{
    private static S3Request CreateRequest(
        string method,
        string path,
        Dictionary<string, string>? query = null,
        Dictionary<string, string[]>? headers = null,
        SharpS3Settings? settings = null)
    {
        return new S3Request(
            method,
            path,
            query ?? new Dictionary<string, string>(),
            headers ?? new Dictionary<string, string[]>(),
            Stream.Null,
            settings);
    }

    [TestMethod]
    public void Root_Get_Routes_To_ListBuckets()
    {
        var req = CreateRequest("GET", "/");
        req.RequestType.Should().Be(S3RequestType.ListBuckets);
        req.Bucket.Should().BeNull();
        req.Key.Should().BeNull();
    }

    [TestMethod]
    public void Root_Head_Routes_To_ServiceExists()
    {
        var req = CreateRequest("HEAD", "/");
        req.RequestType.Should().Be(S3RequestType.ServiceExists);
    }

    [TestMethod]
    public void Bucket_Head_Routes_To_BucketExists()
    {
        var req = CreateRequest("HEAD", "/my-bucket");
        req.RequestType.Should().Be(S3RequestType.BucketExists);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().BeNull();
    }

    [TestMethod]
    public void Bucket_Put_Routes_To_BucketWrite()
    {
        var req = CreateRequest("PUT", "/my-bucket");
        req.RequestType.Should().Be(S3RequestType.BucketWrite);
        req.Bucket.Should().Be("my-bucket");
    }

    [TestMethod]
    public void Bucket_Delete_Routes_To_BucketDelete()
    {
        var req = CreateRequest("DELETE", "/my-bucket");
        req.RequestType.Should().Be(S3RequestType.BucketDelete);
        req.Bucket.Should().Be("my-bucket");
    }

    [TestMethod]
    public void Bucket_Get_Routes_To_BucketRead()
    {
        var req = CreateRequest("GET", "/my-bucket");
        req.RequestType.Should().Be(S3RequestType.BucketRead);
        req.Bucket.Should().Be("my-bucket");
    }

    [TestMethod]
    public void Bucket_Get_Location_Routes_To_BucketReadLocation()
    {
        var query = new Dictionary<string, string> { { "location", "" } };
        var req = CreateRequest("GET", "/my-bucket", query);
        req.RequestType.Should().Be(S3RequestType.BucketReadLocation);
    }

    [TestMethod]
    public void Object_Put_Routes_To_ObjectWrite()
    {
        var req = CreateRequest("PUT", "/my-bucket/folder/test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectWrite);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().Be("folder/test.txt");
    }

    [TestMethod]
    public void Object_Get_Routes_To_ObjectRead()
    {
        var req = CreateRequest("GET", "/my-bucket/test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectRead);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().Be("test.txt");
    }

    [TestMethod]
    public void Object_Get_With_Range_Routes_To_ObjectReadRange()
    {
        var headers = new Dictionary<string, string[]>
        {
            { "range", new[] { "bytes=0-499" } }
        };
        var req = CreateRequest("GET", "/my-bucket/test.txt", headers: headers);
        req.RequestType.Should().Be(S3RequestType.ObjectReadRange);
        req.RangeStart.Should().Be(0);
        req.RangeEnd.Should().Be(499);
    }

    [TestMethod]
    public void Object_Head_Routes_To_ObjectExists()
    {
        var req = CreateRequest("HEAD", "/my-bucket/test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectExists);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().Be("test.txt");
    }

    [TestMethod]
    public void Object_Delete_Routes_To_ObjectDelete()
    {
        var req = CreateRequest("DELETE", "/my-bucket/test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectDelete);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().Be("test.txt");
    }

    [TestMethod]
    public void Object_Delete_Multiple_Routes_To_ObjectDeleteMultiple()
    {
        var query = new Dictionary<string, string> { { "delete", "" } };
        var req = CreateRequest("POST", "/my-bucket", query);
        req.RequestType.Should().Be(S3RequestType.ObjectDeleteMultiple);
        req.Bucket.Should().Be("my-bucket");
    }

    [TestMethod]
    public void Multipart_Initiate_Routes_To_ObjectCreateMultipartUpload()
    {
        var query = new Dictionary<string, string> { { "uploads", "" } };
        var req = CreateRequest("POST", "/my-bucket/large.bin", query);
        req.RequestType.Should().Be(S3RequestType.ObjectCreateMultipartUpload);
    }

    [TestMethod]
    public void Multipart_UploadPart_Routes_To_ObjectUploadPart()
    {
        var query = new Dictionary<string, string>
        {
            { "partNumber", "1" },
            { "uploadId", "abc123" }
        };
        var req = CreateRequest("PUT", "/my-bucket/large.bin", query);
        req.RequestType.Should().Be(S3RequestType.ObjectUploadPart);
        req.PartNumber.Should().Be(1);
        req.UploadId.Should().Be("abc123");
    }

    [TestMethod]
    public void Multipart_Complete_Routes_To_ObjectCompleteMultipartUpload()
    {
        var query = new Dictionary<string, string> { { "uploadId", "abc123" } };
        var req = CreateRequest("POST", "/my-bucket/large.bin", query);
        req.RequestType.Should().Be(S3RequestType.ObjectCompleteMultipartUpload);
        req.UploadId.Should().Be("abc123");
    }

    [TestMethod]
    public void Multipart_Abort_Routes_To_ObjectAbortMultipartUpload()
    {
        var query = new Dictionary<string, string> { { "uploadId", "abc123" } };
        var req = CreateRequest("DELETE", "/my-bucket/large.bin", query);
        req.RequestType.Should().Be(S3RequestType.ObjectAbortMultipartUpload);
    }

    [TestMethod]
    public void VirtualHostedStyle_Resolves_Bucket_From_Host()
    {
        var settings = new SharpS3Settings
        {
            FindMatchingBaseDomain = host => host.EndsWith("s3.local") ? "s3.local" : null
        };
        var headers = new Dictionary<string, string[]>
        {
            { "host", new[] { "my-bucket.s3.local" } }
        };

        var req = CreateRequest("GET", "/images/pic.png", headers: headers, settings: settings);
        req.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        req.Bucket.Should().Be("my-bucket");
        req.Key.Should().Be("images/pic.png");
        req.RequestType.Should().Be(S3RequestType.ObjectRead);
    }

    [TestMethod]
    public void VirtualHostedStyle_Localhost_Default_Resolves_Bucket_And_Key()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains.Should().BeEmpty();

        var headers = new Dictionary<string, string[]>
        {
            { "host", new[] { "fol.localhost:8000" } }
        };

        var req = CreateRequest("PUT", "/test.txt", headers: headers, settings: settings);
        req.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        req.Bucket.Should().Be("fol");
        req.Key.Should().Be("test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectWrite);
    }

    [TestMethod]
    public void VirtualHostedStyle_Hostname_In_BaseDomains_Routes_PathStyle()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains.Add("s3.mydomain.com");

        var headers = new Dictionary<string, string[]>
        {
            { "host", new[] { "s3.mydomain.com" } }
        };

        var req = CreateRequest("GET", "/fol/test.txt", headers: headers, settings: settings);
        req.RequestStyle.Should().Be(S3RequestStyle.PathStyle);
        req.Bucket.Should().Be("fol");
        req.Key.Should().Be("test.txt");
        req.RequestType.Should().Be(S3RequestType.ObjectRead);
    }

    [TestMethod]
    public void VirtualHostedStyle_Hostname_Not_In_BaseDomains_Assumes_Subdomain_Is_Folder()
    {
        var settings = new SharpS3Settings();

        var headers = new Dictionary<string, string[]>
        {
            { "host", new[] { "myfolder.customdomain.org" } }
        };

        var req = CreateRequest("PUT", "/data.csv", headers: headers, settings: settings);
        req.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        req.Bucket.Should().Be("myfolder");
        req.Key.Should().Be("data.csv");
        req.RequestType.Should().Be(S3RequestType.ObjectWrite);
    }

    [TestMethod]
    public void PathStyle_And_VirtualHosted_Preserve_Trailing_Slash_For_Subfolders()
    {
        var settings = new SharpS3Settings();

        // 1. Virtual hosted style creating subfolder: PUT /subfol/ on fol.localhost
        var vhHeaders = new Dictionary<string, string[]>
        {
            { "host", new[] { "fol.localhost:8000" } }
        };
        var vhReq = CreateRequest("PUT", "/subfol/", headers: vhHeaders, settings: settings);
        vhReq.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        vhReq.Bucket.Should().Be("fol");
        vhReq.Key.Should().Be("subfol/");
        vhReq.RequestType.Should().Be(S3RequestType.ObjectWrite);

        // 2. Path style creating subfolder: PUT /fol/subfol/
        var psReq = CreateRequest("PUT", "/fol/subfol/", settings: settings);
        psReq.RequestStyle.Should().Be(S3RequestStyle.PathStyle);
        psReq.Bucket.Should().Be("fol");
        psReq.Key.Should().Be("subfol/");
        psReq.RequestType.Should().Be(S3RequestType.ObjectWrite);
    }

    [TestMethod]
    public void VirtualHostedStyle_BaseDomains_Configured_Resolves_Correctly()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains.Add("s3.mydomain.com");
        var headers = new Dictionary<string, string[]>
        {
            { "host", new[] { "mybucket.s3.mydomain.com" } }
        };

        var req = CreateRequest("GET", "/file.pdf", headers: headers, settings: settings);
        req.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        req.Bucket.Should().Be("mybucket");
        req.Key.Should().Be("file.pdf");
        req.RequestType.Should().Be(S3RequestType.ObjectRead);
    }

    [TestMethod]
    public async Task Server_VerbSpecificMethods_Route_Correctly()
    {
        ISharpS3Server server = new SharpS3Server();

        var getReq = await server.ReceiveGetRequestAsync("/my-bucket/key.txt");
        getReq.RequestType.Should().Be(S3RequestType.ObjectRead);
        getReq.Bucket.Should().Be("my-bucket");
        getReq.Key.Should().Be("key.txt");

        var headReq = await server.ReceiveHeadRequestAsync("/my-bucket/key.txt");
        headReq.RequestType.Should().Be(S3RequestType.ObjectExists);
        headReq.Bucket.Should().Be("my-bucket");
        headReq.Key.Should().Be("key.txt");

        using var putStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var putReq = await server.ReceivePutRequestAsync("/my-bucket/key.txt", putStream);
        putReq.RequestType.Should().Be(S3RequestType.ObjectWrite);
        putReq.Bucket.Should().Be("my-bucket");

        using var postStream = new MemoryStream(new byte[] { 4, 5, 6 });
        var postReq = await server.ReceivePostRequestAsync("/my-bucket/key.txt", postStream, new Dictionary<string, string> { { "uploads", "" } });
        postReq.RequestType.Should().Be(S3RequestType.ObjectCreateMultipartUpload);

        var deleteReq = await server.ReceiveDeleteRequestAsync("/my-bucket/key.txt");
        deleteReq.RequestType.Should().Be(S3RequestType.ObjectDelete);
        deleteReq.Bucket.Should().Be("my-bucket");
    }
}
