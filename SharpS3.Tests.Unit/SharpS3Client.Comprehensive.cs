namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;

public partial class SharpS3ClientTests
{

    [TestMethod]
    public async Task Service_And_Bucket_Operations()
    {
        var (client, mock) = CreateMockClient();

        // 1. ListBucketsAsync
        string listBucketsXml = "<ListAllMyBucketsResult><Buckets><Bucket><Name>b1</Name><CreationDate>2026-01-01T00:00:00Z</CreationDate></Bucket></Buckets></ListAllMyBucketsResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listBucketsXml, Encoding.UTF8, "application/xml") };
        var listBuckets = await client.ListBucketsAsync();
        listBuckets.Buckets.BucketList.Should().ContainSingle(b => b.Name == "b1");

        // 2. CreateBucketAsync
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.CreateBucketAsync("b1");

        // 3. DeleteBucketAsync
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NoContent);
        await client.DeleteBucketAsync("b1");

        // 4. BucketExistsAsync (200 -> true, 404 -> false, 500 -> throws)
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        (await client.BucketExistsAsync("b1")).Should().BeTrue();

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NotFound);
        (await client.BucketExistsAsync("b1")).Should().BeFalse();

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<Error><Code>InternalError</Code></Error>", Encoding.UTF8, "application/xml")
        };
        var actExistsEx = async () => await client.BucketExistsAsync("b1");
        await actExistsEx.Should().ThrowAsync<SharpS3ClientException>();

        // 5. GetBucketLocationAsync
        string locXml = "<LocationConstraint>eu-central-1</LocationConstraint>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(locXml, Encoding.UTF8, "application/xml") };
        var loc = await client.GetBucketLocationAsync("b1");
        loc.Text.Should().Be("eu-central-1");

        // 6. Bucket ACL
        string aclXml = "<AccessControlPolicy><Owner><ID>o1</ID></Owner></AccessControlPolicy>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(aclXml, Encoding.UTF8, "application/xml") };
        var acl = await client.GetBucketAclAsync("b1");
        acl.Owner.ID.Should().Be("o1");

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetBucketAclAsync("b1", acl);

        // 7. Bucket Tags
        string tagsXml = "<Tagging><TagSet><Tag><Key>env</Key><Value>prod</Value></Tag></TagSet></Tagging>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagsXml, Encoding.UTF8, "application/xml") };
        var tags = await client.GetBucketTagsAsync("b1");
        tags.Tags.Tags.Should().ContainSingle(t => t.Key == "env");

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetBucketTagsAsync("b1", tags);
        await client.DeleteBucketTagsAsync("b1");

        // 8. Bucket Versioning
        string verXml = "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(verXml, Encoding.UTF8, "application/xml") };
        var ver = await client.GetBucketVersioningAsync("b1");
        ver.Status.Should().Be(VersioningStatusEnum.Enabled);

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetBucketVersioningAsync("b1", ver);

        // 9. Bucket Website
        string webXml = "<WebsiteConfiguration><IndexDocument><Suffix>index.html</Suffix></IndexDocument></WebsiteConfiguration>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(webXml, Encoding.UTF8, "application/xml") };
        var web = await client.GetBucketWebsiteAsync("b1");
        web.IndexDocument.Suffix.Should().Be("index.html");

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetBucketWebsiteAsync("b1", web);
        await client.DeleteBucketWebsiteAsync("b1");

        // 10. Bucket Logging
        string logXml = "<BucketLoggingStatus><LoggingEnabled><TargetBucket>target</TargetBucket></LoggingEnabled></BucketLoggingStatus>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(logXml, Encoding.UTF8, "application/xml") };
        var logging = await client.GetBucketLoggingAsync("b1");
        logging.Enabled.TargetBucket.Should().Be("target");

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetBucketLoggingAsync("b1", logging);
    }

    [TestMethod]
    public async Task Object_Operations_And_Listings()
    {
        var (client, mock) = CreateMockClient();

        // 1. GetObjectAsync with Range and Metadata
        mock.Handler = req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"))
            };
            resp.Content.Headers.ContentLength = 5;
            resp.Content.Headers.Add("x-amz-meta-custom", "meta-val");
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag123\"");
            return resp;
        };

        var byteRange = new ByteRange(0, 4);
        using var s3Obj = await client.GetObjectAsync("b1", "file.txt", byteRange);
        s3Obj.ContentLength.Should().Be(5);
        s3Obj.ETag.Should().Be("\"etag123\"");
        s3Obj.Metadata.Should().ContainKey("custom").WhoseValue.Should().Be("meta-val");

        // 2. HeadObjectAsync
        mock.Handler = req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new ByteArrayContent(Array.Empty<byte>());
            resp.Content.Headers.ContentLength = 100;
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"head-etag\"");
            resp.Headers.Add("x-amz-meta-type", "doc");
            return resp;
        };
        var headMeta = await client.HeadObjectAsync("b1", "file.txt");
        headMeta.Should().NotBeNull();
        headMeta!.ContentLength.Should().Be(100);
        headMeta.ETag.Should().Be("\"head-etag\"");

        // HeadObject on 404 returns null
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NotFound);
        (await client.HeadObjectAsync("b1", "missing.txt")).Should().BeNull();

        // 3. PutObjectAsync variations
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.PutObjectAsync("b1", "f1.txt", new byte[] { 1, 2, 3 });
        await client.PutObjectAsync("b1", "f2.txt", "string content");
        using var stream = new MemoryStream(new byte[] { 4, 5 });
        await client.PutObjectAsync("b1", "f3.txt", stream, 2);

        // 4. DeleteObjectAsync
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NoContent);
        await client.DeleteObjectAsync("b1", "f1.txt");

        // 5. DeleteObjectsAsync
        string delRespXml = "<DeleteResult><Deleted><Key>f1.txt</Key></Deleted></DeleteResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(delRespXml, Encoding.UTF8, "application/xml") };
        var delRes = await client.DeleteObjectsAsync("b1", new DeleteMultiple(false, new List<SharpS3.Models.Object> { new SharpS3.Models.Object("f1.txt", "v1") }));
        delRes.DeletedObjects.Count.Should().Be(1);

        // 6. CopyObjectAsync
        string copyXml = "<CopyObjectResult><ETag>\"copy-etag\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyObjectResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(copyXml, Encoding.UTF8, "application/xml") };
        var copyRes = await client.CopyObjectAsync("b1", "src.txt", "b1", "dst.txt");
        copyRes.ETag.Should().Be("\"copy-etag\"");

        // 7. ListObjectsV2Async
        string listV2Xml = "<ListBucketResult><Name>b1</Name><Contents><Key>item1</Key><Size>10</Size></Contents><IsTruncated>false</IsTruncated></ListBucketResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listV2Xml, Encoding.UTF8, "application/xml") };
        var v2Res = await client.ListObjectsV2Async("b1", prefix: "it", maxKeys: 100);
        v2Res.Contents.Count.Should().Be(1);

        // 8. ListAllObjectsAsync (pagination)
        int callCount = 0;
        mock.Handler = req =>
        {
            callCount++;
            if (callCount == 1)
            {
                string p1 = "<ListBucketResult><Name>b1</Name><Contents><Key>k1</Key></Contents><IsTruncated>true</IsTruncated><NextContinuationToken>token2</NextContinuationToken></ListBucketResult>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(p1, Encoding.UTF8, "application/xml") };
            }
            else
            {
                string p2 = "<ListBucketResult><Name>b1</Name><Contents><Key>k2</Key></Contents><IsTruncated>false</IsTruncated></ListBucketResult>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(p2, Encoding.UTF8, "application/xml") };
            }
        };
        var allObjs = new List<ObjectMetadata>();
        await foreach (var item in client.ListAllObjectsAsync("b1"))
        {
            allObjs.Add(item);
        }
        allObjs.Count.Should().Be(2);

        // 9. Object Tags
        string objTagsXml = "<Tagging><TagSet><Tag><Key>tag1</Key><Value>val1</Value></Tag></TagSet></Tagging>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(objTagsXml, Encoding.UTF8, "application/xml") };
        var oTags = await client.GetObjectTagsAsync("b1", "k1");
        oTags.Tags.Tags.Should().ContainSingle(t => t.Key == "tag1");

        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.SetObjectTagsAsync("b1", "k1", oTags);
        await client.DeleteObjectTagsAsync("b1", "k1");
    }

    [TestMethod]
    public async Task Multipart_Upload_And_File_Helpers()
    {
        var (client, mock) = CreateMockClient();

        // 1. InitiateMultipartUploadAsync
        string initXml = "<InitiateMultipartUploadResult><Bucket>b1</Bucket><Key>large.bin</Key><UploadId>up123</UploadId></InitiateMultipartUploadResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(initXml, Encoding.UTF8, "application/xml") };
        var init = await client.InitiateMultipartUploadAsync("b1", "large.bin");
        init.UploadId.Should().Be("up123");

        // 2. UploadPartAsync (bytes and stream)
        mock.Handler = req =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"part-etag\"");
            return resp;
        };
        var pRes1 = await client.UploadPartAsync("b1", "large.bin", "up123", 1, new byte[] { 1, 2, 3 });
        pRes1.ETag.Should().Be("\"part-etag\"");

        using var partStream = new MemoryStream(new byte[] { 4, 5 });
        var pRes2 = await client.UploadPartAsync("b1", "large.bin", "up123", 2, partStream, 2);
        pRes2.ETag.Should().Be("\"part-etag\"");

        // 3. CompleteMultipartUploadAsync
        string compXml = "<CompleteMultipartUploadResult><Bucket>b1</Bucket><Key>large.bin</Key><ETag>\"final-etag\"</ETag></CompleteMultipartUploadResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(compXml, Encoding.UTF8, "application/xml") };
        var complete = new CompleteMultipartUpload { Parts = new List<Part> { new Part { PartNumber = 1, ETag = "\"part-etag\"" } } };
        var compRes = await client.CompleteMultipartUploadAsync("b1", "large.bin", "up123", complete);
        compRes.ETag.Should().Be("\"final-etag\"");

        // 4. AbortMultipartUploadAsync
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NoContent);
        await client.AbortMultipartUploadAsync("b1", "large.bin", "up123");

        // 5. UploadFileAsync and DownloadFileAsync
        string tempUploadFile = Path.Combine(Path.GetTempPath(), "upload_" + Guid.NewGuid().ToString("N") + ".txt");
        string tempDownloadFile = Path.Combine(Path.GetTempPath(), "download_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(tempUploadFile, "file-content");

            mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
            await client.UploadFileAsync("b1", "file.txt", tempUploadFile);

            mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("downloaded-content"))
            };
            await client.DownloadFileAsync("b1", "file.txt", tempDownloadFile);
            string downloaded = await File.ReadAllTextAsync(tempDownloadFile);
            downloaded.Should().Be("downloaded-content");
        }
        finally
        {
            if (File.Exists(tempUploadFile)) File.Delete(tempUploadFile);
            if (File.Exists(tempDownloadFile)) File.Delete(tempDownloadFile);
        }
    }

    [TestMethod]
    public async Task EnsureSuccessAsync_Error_Handling()
    {
        var (client, mock) = CreateMockClient();

        // 1. Error with XML body
        string errXml = "<Error><Code>NoSuchKey</Code><Message>The key does not exist.</Message><Resource>/b1/ghost</Resource><RequestId>req-1</RequestId></Error>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(errXml, Encoding.UTF8, "application/xml")
        };
        var actNotFound = async () => await client.DeleteObjectAsync("b1", "ghost");
        var ex1 = await actNotFound.Should().ThrowAsync<SharpS3ClientException>();
        ex1.Which.ErrorCode.Should().Be("NoSuchKey");
        ex1.Which.Resource.Should().Be("/b1/ghost");

        // 2. Error with plain text (non-XML) body
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("Bad Gateway Error", Encoding.UTF8, "text/plain")
        };
        var actBadGateway = async () => await client.DeleteObjectAsync("b1", "ghost");
        var ex2 = await actBadGateway.Should().ThrowAsync<SharpS3ClientException>();
        ex2.Which.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        ex2.Which.Message.Should().Contain("Bad Gateway Error");

        // 3. VirtualHostedStyle URL building
        var (vhClient, vhMock) = CreateMockClient(forcePathStyle: false);
        vhMock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await vhClient.CreateBucketAsync("mybucket");
        vhMock.LastRequest!.RequestUri!.Host.Should().Be("mybucket.localhost");
    }
}
