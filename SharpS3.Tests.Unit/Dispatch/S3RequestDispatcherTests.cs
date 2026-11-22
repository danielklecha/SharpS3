namespace SharpS3.Tests.Unit;

using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Serialization;
using SharpS3.Storage;

[TestClass]
public partial class S3RequestDispatcherTests
{
    [TestMethod]
    public async Task Full_Lifecycle_Via_Dispatcher()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // 1. Create bucket via PUT /test-bucket
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/test-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using (var putBucketResp = await dispatcher.DispatchAsync(putBucketReq))
        {
            putBucketResp.StatusCode.Should().Be(200);
        }

        // 2. PutObject via PUT /test-bucket/doc.txt
        byte[] docBytes = Encoding.UTF8.GetBytes("Testing Dispatcher Pipeline");
        var putHeaders = new Dictionary<string, string[]>
        {
            { "content-length", new[] { docBytes.Length.ToString() } },
            { "content-type", new[] { "text/plain" } }
        };

        var putObjReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/test-bucket/doc.txt",
            new Dictionary<string, string>(),
            putHeaders,
            new MemoryStream(docBytes));

        using (var putObjResp = await dispatcher.DispatchAsync(putObjReq))
        {
            putObjResp.StatusCode.Should().Be(200);
            putObjResp.Headers.ContainsKey("ETag").Should().BeTrue();
        }

        // 3. GetObject via GET /test-bucket/doc.txt
        var getObjReq = await s3Server.ReceiveRequestAsync(
            "GET",
            "/test-bucket/doc.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using (var getObjResp = await dispatcher.DispatchAsync(getObjReq))
        {
            getObjResp.StatusCode.Should().Be(200);
            getObjResp.ContentType.Should().Be("text/plain");
            using var ms = new MemoryStream();
            await getObjResp.BodyStream.CopyToAsync(ms);
            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("Testing Dispatcher Pipeline");
        }

        // 4. ListObjects via GET /test-bucket
        var listObjReq = await s3Server.ReceiveRequestAsync(
            "GET",
            "/test-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using (var listObjResp = await dispatcher.DispatchAsync(listObjReq))
        {
            listObjResp.StatusCode.Should().Be(200);
            listObjResp.ContentType.Should().Be("application/xml");
            using var reader = new StreamReader(listObjResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("<Key>doc.txt</Key>");
        }
    }

    [TestMethod]
    public async Task BucketWrite_MalformedXml_Throws_S3Exception_MalformedXml()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        byte[] invalidXmlBytes = Encoding.UTF8.GetBytes("Not valid XML content at all");
        var req = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/some-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            new MemoryStream(invalidXmlBytes));

        var act = async () => await dispatcher.DispatchAsync(req);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.MalformedXML);
        ex.Which.HttpStatusCode.Should().Be(400);
    }

    [TestMethod]
    public async Task PreRequestHandler_Can_ShortCircuit()
    {
        var backend = new InMemoryStorageBackend();
        var settings = new SharpS3Settings
        {
            PreRequestHandler = req =>
            {
                var customResp = new S3ServerResponse
                {
                    StatusCode = 418,
                    ContentType = "text/plain",
                    BodyStream = new MemoryStream(Encoding.UTF8.GetBytes("I'm a teapot"))
                };
                return Task.FromResult<S3ServerResponse?>(customResp);
            }
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        var req = await s3Server.ReceiveRequestAsync(
            "GET",
            "/some-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(418);
        using var reader = new StreamReader(resp.BodyStream);
        string body = await reader.ReadToEndAsync();
        body.Should().Be("I'm a teapot");
    }

    [TestMethod]
    public async Task PostRequestHandler_Is_Called()
    {
        var backend = new InMemoryStorageBackend();
        bool postCalled = false;
        var settings = new SharpS3Settings
        {
            PostRequestHandler = (req, resp) =>
            {
                postCalled = true;
                return Task.CompletedTask;
            }
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        var createReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/post-hook-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(createReq);
        resp.StatusCode.Should().Be(200);
        postCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task Bucket_Acl_Lifecycle()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // 1. Create bucket
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/acl-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        // 2. Put Bucket ACL
        string aclXml = @"<AccessControlPolicy xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Owner><ID>owner1</ID><DisplayName>Owner One</DisplayName></Owner>
  <AccessControlList>
    <Grant>
      <Grantee xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:type=""CanonicalUser"">
        <ID>grantee1</ID>
        <DisplayName>Grantee One</DisplayName>
      </Grantee>
      <Permission>FULL_CONTROL</Permission>
    </Grant>
  </AccessControlList>
</AccessControlPolicy>";
        var putAclReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/acl-bucket", new Dictionary<string, string> { { "acl", "" } }, new Dictionary<string, string[]>(),
            new MemoryStream(Encoding.UTF8.GetBytes(aclXml)));
        using (var putResp = await dispatcher.DispatchAsync(putAclReq))
        {
            putResp.StatusCode.Should().Be(200);
        }

        // 3. Get Bucket ACL
        var getAclReq = await s3Server.ReceiveRequestAsync(
            "GET", "/acl-bucket", new Dictionary<string, string> { { "acl", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var getResp = await dispatcher.DispatchAsync(getAclReq))
        {
            getResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("grantee1");
        }

        // 4. Delete Bucket ACL
        var delAclReq = await s3Server.ReceiveRequestAsync(
            "DELETE", "/acl-bucket", new Dictionary<string, string> { { "acl", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var delResp = await dispatcher.DispatchAsync(delAclReq))
        {
            delResp.StatusCode.Should().Be(204);
        }
    }

    [TestMethod]
    public async Task Bucket_Tagging_Lifecycle()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // Create bucket
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/tag-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        // Put tags
        string tagXml = @"<Tagging xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <TagSet>
    <Tag><Key>Environment</Key><Value>Production</Value></Tag>
  </TagSet>
</Tagging>";
        var putTagReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/tag-bucket", new Dictionary<string, string> { { "tagging", "" } }, new Dictionary<string, string[]>(),
            new MemoryStream(Encoding.UTF8.GetBytes(tagXml)));
        using (var putResp = await dispatcher.DispatchAsync(putTagReq))
        {
            putResp.StatusCode.Should().Be(200);
        }

        // Get tags
        var getTagReq = await s3Server.ReceiveRequestAsync(
            "GET", "/tag-bucket", new Dictionary<string, string> { { "tagging", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var getResp = await dispatcher.DispatchAsync(getTagReq))
        {
            getResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("<Key>Environment</Key>");
            xml.Should().Contain("<Value>Production</Value>");
        }

        // Delete tags
        var delTagReq = await s3Server.ReceiveRequestAsync(
            "DELETE", "/tag-bucket", new Dictionary<string, string> { { "tagging", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var delResp = await dispatcher.DispatchAsync(delTagReq))
        {
            delResp.StatusCode.Should().Be(204);
        }

        // Subsequent GET tags throws NoSuchTagSet (404)
        var getTagReq2 = await s3Server.ReceiveRequestAsync(
            "GET", "/tag-bucket", new Dictionary<string, string> { { "tagging", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        var act = async () => await dispatcher.DispatchAsync(getTagReq2);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.HttpStatusCode.Should().Be(404);
    }

    [TestMethod]
    public async Task Bucket_Versioning_Lifecycle()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // Create bucket
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/ver-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        // Put versioning
        string verXml = @"<VersioningConfiguration xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Status>Enabled</Status>
</VersioningConfiguration>";
        var putVerReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/ver-bucket", new Dictionary<string, string> { { "versioning", "" } }, new Dictionary<string, string[]>(),
            new MemoryStream(Encoding.UTF8.GetBytes(verXml)));
        using (var putResp = await dispatcher.DispatchAsync(putVerReq))
        {
            putResp.StatusCode.Should().Be(200);
        }

        // Get versioning
        var getVerReq = await s3Server.ReceiveRequestAsync(
            "GET", "/ver-bucket", new Dictionary<string, string> { { "versioning", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var getResp = await dispatcher.DispatchAsync(getVerReq))
        {
            getResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("Enabled");
        }
    }

    [TestMethod]
    public async Task Bucket_Website_Lifecycle()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // Create bucket
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/web-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        // Put website
        string webXml = @"<WebsiteConfiguration xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <IndexDocument><Suffix>index.html</Suffix></IndexDocument>
</WebsiteConfiguration>";
        var putWebReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/web-bucket", new Dictionary<string, string> { { "website", "" } }, new Dictionary<string, string[]>(),
            new MemoryStream(Encoding.UTF8.GetBytes(webXml)));
        using (var putResp = await dispatcher.DispatchAsync(putWebReq))
        {
            putResp.StatusCode.Should().Be(200);
        }

        // Get website
        var getWebReq = await s3Server.ReceiveRequestAsync(
            "GET", "/web-bucket", new Dictionary<string, string> { { "website", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var getResp = await dispatcher.DispatchAsync(getWebReq))
        {
            getResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("index.html");
        }

        // Delete website
        var delWebReq = await s3Server.ReceiveRequestAsync(
            "DELETE", "/web-bucket", new Dictionary<string, string> { { "website", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        using (var delResp = await dispatcher.DispatchAsync(delWebReq))
        {
            delResp.StatusCode.Should().Be(204);
        }

        // Subsequent GET website throws NoSuchWebsiteConfiguration (404)
        var getWebReq2 = await s3Server.ReceiveRequestAsync(
            "GET", "/web-bucket", new Dictionary<string, string> { { "website", "" } }, new Dictionary<string, string[]>(), Stream.Null);
        var act = async () => await dispatcher.DispatchAsync(getWebReq2);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.HttpStatusCode.Should().Be(404);
    }

    [TestMethod]
    public async Task Object_LegalHold_And_Retention_Lifecycle()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // Create bucket & object
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/gov-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        var putObjReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/gov-bucket/item.bin", new Dictionary<string, string>(),
            new Dictionary<string, string[]> { { "content-length", new[] { "4" } } },
            new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        using (await dispatcher.DispatchAsync(putObjReq)) { }

        // Put LegalHold
        string legalHoldXml = @"<LegalHold xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Status>ON</Status>
</LegalHold>";
        var putLhReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/gov-bucket/item.bin", new Dictionary<string, string> { { "legal-hold", "" } },
            new Dictionary<string, string[]>(), new MemoryStream(Encoding.UTF8.GetBytes(legalHoldXml)));
        using (var putLhResp = await dispatcher.DispatchAsync(putLhReq))
        {
            putLhResp.StatusCode.Should().Be(200);
        }

        // Get LegalHold
        var getLhReq = await s3Server.ReceiveRequestAsync(
            "GET", "/gov-bucket/item.bin", new Dictionary<string, string> { { "legal-hold", "" } },
            new Dictionary<string, string[]>(), Stream.Null);
        using (var getLhResp = await dispatcher.DispatchAsync(getLhReq))
        {
            getLhResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getLhResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("<Status>ON</Status>");
        }

        // Put Retention
        string retentionXml = @"<Retention xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Mode>GOVERNANCE</Mode>
  <RetainUntilDate>2030-01-01T00:00:00Z</RetainUntilDate>
</Retention>";
        var putRetReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/gov-bucket/item.bin", new Dictionary<string, string> { { "retention", "" } },
            new Dictionary<string, string[]>(), new MemoryStream(Encoding.UTF8.GetBytes(retentionXml)));
        using (var putRetResp = await dispatcher.DispatchAsync(putRetReq))
        {
            putRetResp.StatusCode.Should().Be(200);
        }

        // Get Retention
        var getRetReq = await s3Server.ReceiveRequestAsync(
            "GET", "/gov-bucket/item.bin", new Dictionary<string, string> { { "retention", "" } },
            new Dictionary<string, string[]>(), Stream.Null);
        using (var getRetResp = await dispatcher.DispatchAsync(getRetReq))
        {
            getRetResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(getRetResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            xml.Should().Contain("<Mode>GOVERNANCE</Mode>");
        }
    }

    [TestMethod]
    public async Task Object_DeleteAcl_And_Restore()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // Create bucket & object
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/res-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        var putObjReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/res-bucket/archived.bin", new Dictionary<string, string>(),
            new Dictionary<string, string[]> { { "content-length", new[] { "4" } } },
            new MemoryStream(new byte[] { 1, 2, 3, 4 }));
        using (await dispatcher.DispatchAsync(putObjReq)) { }

        // Delete Object ACL
        var delAclReq = await s3Server.ReceiveRequestAsync(
            "DELETE", "/res-bucket/archived.bin", new Dictionary<string, string> { { "acl", "" } },
            new Dictionary<string, string[]>(), Stream.Null);
        using (var delResp = await dispatcher.DispatchAsync(delAclReq))
        {
            delResp.StatusCode.Should().Be(204);
        }

        // Object Restore
        string restoreXml = @"<RestoreRequest xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Days>7</Days>
</RestoreRequest>";
        var restoreReq = await s3Server.ReceiveRequestAsync(
            "POST", "/res-bucket/archived.bin", new Dictionary<string, string> { { "restore", "" } },
            new Dictionary<string, string[]>(), new MemoryStream(Encoding.UTF8.GetBytes(restoreXml)));
        using (var restoreResp = await dispatcher.DispatchAsync(restoreReq))
        {
            restoreResp.StatusCode.Should().Be(202);
        }
    }

    [TestMethod]
    public async Task Object_UploadPartCopy_Works()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        // 1. Create bucket & source object
        var putBucketReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/mp-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (await dispatcher.DispatchAsync(putBucketReq)) { }

        byte[] srcData = Encoding.UTF8.GetBytes("Data to be copied via UploadPartCopy");
        var putSrcReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/mp-bucket/source.txt", new Dictionary<string, string>(),
            new Dictionary<string, string[]> { { "content-length", new[] { srcData.Length.ToString() } } },
            new MemoryStream(srcData));
        using (await dispatcher.DispatchAsync(putSrcReq)) { }

        // 2. Initiate multipart upload for dest.txt
        var initReq = await s3Server.ReceiveRequestAsync(
            "POST", "/mp-bucket/dest.txt", new Dictionary<string, string> { { "uploads", "" } },
            new Dictionary<string, string[]>(), Stream.Null);
        string uploadId;
        using (var initResp = await dispatcher.DispatchAsync(initReq))
        {
            initResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(initResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            var initResult = SerializationHelper.DeserializeXml<InitiateMultipartUploadResult>(xml);
            uploadId = initResult.UploadId;
        }

        // 3. UploadPartCopy
        var copyPartReq = await s3Server.ReceiveRequestAsync(
            "PUT", "/mp-bucket/dest.txt",
            new Dictionary<string, string> { { "uploadId", uploadId }, { "partNumber", "1" } },
            new Dictionary<string, string[]> { { "x-amz-copy-source", new[] { "/mp-bucket/source.txt" } } },
            Stream.Null);

        string partEtag;
        using (var copyPartResp = await dispatcher.DispatchAsync(copyPartReq))
        {
            copyPartResp.StatusCode.Should().Be(200);
            using var reader = new StreamReader(copyPartResp.BodyStream);
            string xml = await reader.ReadToEndAsync();
            var copyPartResult = SerializationHelper.DeserializeXml<CopyPartResult>(xml);
            partEtag = copyPartResult.ETag;
            partEtag.Should().NotBeNullOrWhiteSpace();
        }

        // 4. Complete multipart upload
        string completeXml = $@"<CompleteMultipartUpload xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Part>
    <PartNumber>1</PartNumber>
    <ETag>{partEtag}</ETag>
  </Part>
</CompleteMultipartUpload>";
        var compReq = await s3Server.ReceiveRequestAsync(
            "POST", "/mp-bucket/dest.txt",
            new Dictionary<string, string> { { "uploadId", uploadId } },
            new Dictionary<string, string[]>(),
            new MemoryStream(Encoding.UTF8.GetBytes(completeXml)));
        using (var compResp = await dispatcher.DispatchAsync(compReq))
        {
            compResp.StatusCode.Should().Be(200);
        }

        // 5. Verify dest.txt content matches source.txt
        var getDestReq = await s3Server.ReceiveRequestAsync(
            "GET", "/mp-bucket/dest.txt", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        using (var getDestResp = await dispatcher.DispatchAsync(getDestReq))
        {
            getDestResp.StatusCode.Should().Be(200);
            using var ms = new MemoryStream();
            await getDestResp.BodyStream.CopyToAsync(ms);
            Encoding.UTF8.GetString(ms.ToArray()).Should().Be("Data to be copied via UploadPartCopy");
        }
    }

    [TestMethod]
    public async Task OperationLimits_MaxPutObjectSize_Exceeded_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("limit-bucket", null);

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxPutObjectSize = 100; // 100 bytes max

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        byte[] largeData = new byte[200];
        var putReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/limit-bucket/large.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { { "content-length", new[] { "200" } } },
            new MemoryStream(largeData));

        var act = async () => await dispatcher.DispatchAsync(putReq);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
        ex.Which.HttpStatusCode.Should().Be(400);
    }

    [TestMethod]
    public async Task OperationLimits_MaxPutObjectSize_StreamExceeded_Chunked_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("limit-bucket", null);

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxPutObjectSize = 50;

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        byte[] largeData = new byte[100];
        var putReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/limit-bucket/chunked.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(), // No content-length header simulates chunked transfer
            new MemoryStream(largeData));

        var act = async () => await dispatcher.DispatchAsync(putReq);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task OperationLimits_MaxUploadPartSize_Exceeded_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("limit-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("limit-bucket", "key.bin");

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxUploadPartSize = 100;

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        byte[] partData = new byte[200];
        var partReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            $"/limit-bucket/key.bin?partNumber=1&uploadId={init.UploadId}",
            new Dictionary<string, string> { { "partNumber", "1" }, { "uploadId", init.UploadId } },
            new Dictionary<string, string[]> { { "content-length", new[] { "200" } } },
            new MemoryStream(partData));

        var act = async () => await dispatcher.DispatchAsync(partReq);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task OperationLimits_MaxUploadPartSize_StreamExceeded_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("limit-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("limit-bucket", "key.bin");

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxUploadPartSize = 50;

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        byte[] partData = new byte[100];
        var partReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            $"/limit-bucket/key.bin?partNumber=1&uploadId={init.UploadId}",
            new Dictionary<string, string> { { "partNumber", "1" }, { "uploadId", init.UploadId } },
            new Dictionary<string, string[]>(), // No content-length simulates chunked
            new MemoryStream(partData));

        var act = async () => await dispatcher.DispatchAsync(partReq);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task OperationLimits_MaxXmlPayloadSize_Exceeded_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("limit-bucket", null);

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxXmlPayloadSize = 30; // Very small XML limit

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        string xmlPayload = "<Tagging><TagSet><Tag><Key>env</Key><Value>production</Value></Tag></TagSet></Tagging>";
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xmlPayload);
        xmlBytes.Length.Should().BeGreaterThan(30);

        var tagReq = await s3Server.ReceiveRequestAsync(
            "PUT",
            "/limit-bucket?tagging",
            new Dictionary<string, string> { { "tagging", "" } },
            new Dictionary<string, string[]> { { "content-length", new[] { xmlBytes.Length.ToString() } } },
            new MemoryStream(xmlBytes));

        var act = async () => await dispatcher.DispatchAsync(tagReq);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_MaxObjectSize_EnforcesLimit()
    {
        var backend = new InMemoryStorageBackend { MaxObjectSize = 50 };
        await backend.CreateBucketAsync("limit-bucket", null);

        byte[] largeData = new byte[100];
        var actPut = async () => await backend.PutObjectAsync("limit-bucket", "test.bin", new MemoryStream(largeData), 100, "application/octet-stream");
        var ex = await actPut.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);

        var actPutStream = async () => await backend.PutObjectAsync("limit-bucket", "test.bin", new MemoryStream(largeData), 0, "application/octet-stream");
        var ex2 = await actPutStream.Should().ThrowAsync<S3Exception>();
        ex2.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public void OperationLimitsSettings_Validation_Throws_When_Less_Than_One()
    {
        var limits = new OperationLimitsSettings();
        Action actPut = () => limits.MaxPutObjectSize = 0;
        actPut.Should().Throw<ArgumentOutOfRangeException>();

        Action actPart = () => limits.MaxUploadPartSize = 0;
        actPart.Should().Throw<ArgumentOutOfRangeException>();

        Action actXml = () => limits.MaxXmlPayloadSize = 0;
        actXml.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public async Task AuthenticatedRequestHandler_Is_Called_And_ShortCircuits()
    {
        var backend = new InMemoryStorageBackend();
        bool authHookCalled = false;
        var settings = new SharpS3Settings
        {
            AuthenticatedRequestHandler = req =>
            {
                authHookCalled = true;
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 203 });
            }
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        var req = await s3Server.ReceiveRequestAsync(
            "GET",
            "/some-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(203);
        authHookCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task ValidateSignaturesBeforePreRequestHandler_Ordering_Respected()
    {
        // When false (default), PreRequestHandler runs BEFORE signature check, allowing it to inspect or short-circuit unsigned requests
        bool preHookCalled = false;
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            ValidateSignaturesBeforePreRequestHandler = false,
            PreRequestHandler = req =>
            {
                preHookCalled = true;
                // short-circuit with 200 so signature validation is skipped
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 200 });
            }
        };
        settings.Credentials["key"] = "secret";

        var dispatcher = new S3RequestDispatcher(new InMemoryStorageBackend(), settings);
        var s3Server = new SharpS3Server(settings);

        var unsignedReq = await s3Server.ReceiveRequestAsync(
            "GET",
            "/unsigned-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(unsignedReq);
        resp.StatusCode.Should().Be(200);
        preHookCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task DefaultRequestHandler_Invoked_On_Unknown_Request()
    {
        var backend = new InMemoryStorageBackend();
        bool defaultCalled = false;
        var settings = new SharpS3Settings
        {
            DefaultRequestHandler = req =>
            {
                defaultCalled = true;
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 200 });
            }
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        var req = (S3Request)await s3Server.ReceiveRequestAsync(
            "GET",
            "/some-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        req.RequestType = S3RequestType.Unknown;

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(200);
        defaultCalled.Should().BeTrue();
    }

    [TestMethod]
    public async Task StandardResponseHeaders_And_Cors_Headers_Applied()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("cors-bucket", null);

        var settings = new SharpS3Settings
        {
            EmitCorsHeaders = true
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var s3Server = new SharpS3Server(settings);

        var req = await s3Server.ReceiveRequestAsync(
            "GET",
            "/cors-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { { "origin", new[] { "https://example.com" } } },
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(200);

        // x-amz-request-id & x-amz-id-2
        resp.Headers.ContainsKey("x-amz-request-id").Should().BeTrue();
        resp.Headers.ContainsKey("x-amz-id-2").Should().BeTrue();

        // CORS headers
        resp.Headers.ContainsKey("Access-Control-Allow-Origin").Should().BeTrue();
        resp.Headers["Access-Control-Allow-Origin"].Should().Be("*");
    }

    [TestMethod]
    public async Task CallbackStorageBackend_Dispatches_To_Callbacks()
    {
        bool bucketExistsCalled = false;
        var backend = new CallbackStorageBackend
        {
            BucketExists = (bucket, ct) =>
            {
                bucketExistsCalled = true;
                return Task.FromResult(bucket == "callback-bucket");
            }
        };

        var dispatcher = new S3RequestDispatcher(backend);
        var s3Server = new SharpS3Server();

        var req = await s3Server.ReceiveRequestAsync(
            "HEAD",
            "/callback-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>(),
            Stream.Null);

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(200);
        bucketExistsCalled.Should().BeTrue();
    }
}
