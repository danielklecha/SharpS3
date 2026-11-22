namespace SharpS3.Tests.Unit;

#pragma warning disable CS8602

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
using SharpS3.Serialization;

public partial class SharpS3ClientTests
{
    

    [TestMethod]
    public async Task Client_ListAllObjects_Pagination_And_Error_Responses()
    {
        int listCallCount = 0;
        var mockHandler = new MockHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.ToString();

            // BucketExistsAsync 404 test
            if (url.Contains("/missing-bucket"))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            // HeadObjectAsync with user metadata headers
            if (req.Method == HttpMethod.Head && url.Contains("/meta-file.txt"))
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Headers.Add("ETag", "\"custom-etag\"");
                resp.Headers.Add("x-amz-meta-custom-key", "custom-value");
                resp.Content = new ByteArrayContent(Array.Empty<byte>());
                resp.Content.Headers.ContentLength = 123;
                resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return resp;
            }

            // InitiateMultipartUpload with metadata
            if (req.Method == HttpMethod.Post && url.Contains("uploads"))
            {
                string xml = "<InitiateMultipartUploadResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Bucket>b</Bucket><Key>k</Key><UploadId>upload-123</UploadId></InitiateMultipartUploadResult>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }

            // ListAllObjectsAsync pagination
            if (url.Contains("list-type=2"))
            {
                listCallCount++;
                if (listCallCount == 1)
                {
                    string p1 = "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>b</Name><IsTruncated>true</IsTruncated><NextContinuationToken>token2</NextContinuationToken><Contents><Key>file1.txt</Key><Size>10</Size><ETag>\"e1\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></Contents></ListBucketResult>";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(p1, Encoding.UTF8, "application/xml") };
                }
                else
                {
                    string p2 = "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>b</Name><IsTruncated>false</IsTruncated><Contents><Key>file2.txt</Key><Size>20</Size><ETag>\"e2\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></Contents></ListBucketResult>";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(p2, Encoding.UTF8, "application/xml") };
                }
            }

            // Malformed XML error response
            if (url.Contains("/malformed-error"))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("not an xml error", Encoding.UTF8, "text/plain") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        using var httpClient = new HttpClient(mockHandler);
        var options = new SharpS3ClientOptions { Endpoint = new Uri("http://localhost:8080"), AccessKey = "access", SecretKey = "secret" };
        using var client = new SharpS3Client(options, httpClient);

        // 1. BucketExistsAsync returning false on 404
        (await client.BucketExistsAsync("missing-bucket")).Should().BeFalse();

        // 2. HeadObjectAsync user metadata extraction
        var head = await client.HeadObjectAsync("b", "meta-file.txt");
        head.Metadata.Should().ContainKey("custom-key");
        head.Metadata["custom-key"].Should().Be("custom-value");

        // 3. InitiateMultipartUploadAsync with metadata
        var init = await client.InitiateMultipartUploadAsync("b", "k", "binary/octet-stream", new Dictionary<string, string> { ["author"] = "tester" });
        init.UploadId.Should().Be("upload-123");

        // 4. ListAllObjectsAsync pagination
        var allObjects = new List<ObjectMetadata>();
        await foreach (var obj in client.ListAllObjectsAsync("b"))
        {
            allObjects.Add(obj);
        }
        allObjects.Count.Should().Be(2);
        allObjects[0].Key.Should().Be("file1.txt");
        allObjects[1].Key.Should().Be("file2.txt");

        // 5. EnsureSuccessAsync with malformed XML error
        Func<Task> actMalformed = async () => await client.DeleteBucketAsync("malformed-error");
        await actMalformed.Should().ThrowAsync<SharpS3ClientException>();
    }

    [TestMethod]
    public void ListBucketResult_Constructors_And_Serialization_Rules()
    {
        var owner = new Owner("owner-id", "owner-name");
        var contents = new List<ObjectMetadata>
        {
            new ObjectMetadata("file1.txt", DateTime.UtcNow, "\"etag\"", 10, owner)
        };
        var prefixes = new List<CommonPrefixes>
        {
            new CommonPrefixes("folder/")
        };

        // Full constructor
        var lbr = new ListBucketResult(
            name: "test-bucket",
            contents: contents,
            keyCount: 2,
            maxKeys: 100,
            prefix: "folder/",
            marker: "mark",
            delimiter: "/",
            isTruncated: false,
            nextToken: "token",
            prefixes: prefixes,
            bucketRegion: "eu-central-1"
        );

        lbr.Name.Should().Be("test-bucket");
        lbr.Contents.Count.Should().Be(1);
        lbr.CommonPrefixes.Count.Should().Be(1);
        lbr.BucketRegion.Should().Be("eu-central-1");
        lbr.ShouldSerializeCommonPrefixes().Should().BeTrue();

        // Null name throws ArgumentNullException
        Action actNull = () => new ListBucketResult(null!, contents, 1, 100);
        actNull.Should().Throw<ArgumentNullException>();

        // ResponseSerializationContext.ListType == 1 rules
        var emptyLbr = new ListBucketResult();
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            emptyLbr.Marker.Should().Be("");
            emptyLbr.ShouldSerializeMarker().Should().BeTrue();
            emptyLbr.ShouldSerializeContinuationToken().Should().BeFalse();
            return "";
        });

        ResponseSerializationContext.Run(2, false, false, () =>
        {
            emptyLbr.ShouldSerializeMarker().Should().BeFalse();
            return "";
        });
    }

    [TestMethod]
    public void Models_Remaining_Constructors_And_EdgeCases()
    {
        // RestoreRequest
        var rest = new RestoreRequest
        {
            Days = 10,
            Tier = RestoreTierEnum.Expedited,
            Description = "job description",
            GlacierJobParameters = new GlacierJobParameters { Tier = RestoreTierEnum.Expedited }
        };
        rest.Days.Should().Be(10);
        rest.GlacierJobParameters.Tier.Should().Be(RestoreTierEnum.Expedited);

        Action actInvalidDays = () => rest.Days = 0;
        actInvalidDays.Should().Throw<ArgumentOutOfRangeException>();

        // DeleteError
        var err = new Error(ErrorCode.NoSuchKey, "key.txt", "v1", "req-1", "/b/key.txt");
        var delErr = new DeleteError(err);
        delErr.Key.Should().Be("key.txt");
        delErr.VersionId.Should().Be("v1");
        delErr.Code.Should().Be(ErrorCode.NoSuchKey);
        delErr.Message.Should().NotBeNull();

        var delErrDefault = new DeleteError();
        delErrDefault.Key = "k2";
        delErrDefault.Key.Should().Be("k2");

        Action actNullErr = () => new DeleteError(null!);
        actNullErr.Should().Throw<ArgumentNullException>();

        // ListMultipartUploadsResult
        var lmu = new ListMultipartUploadsResult("my-bucket", "key-m", "upload-id");
        lmu.Bucket.Should().Be("my-bucket");
        lmu.KeyMarker.Should().Be("key-m");
        lmu.UploadIdMarker.Should().Be("upload-id");
        lmu.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lmu.CommonPrefixes.Add(new CommonPrefixes("sub/"));
        lmu.ShouldSerializeCommonPrefixes().Should().BeTrue();

        Action actNullBucket = () => new ListMultipartUploadsResult("", "k", "u");
        actNullBucket.Should().Throw<ArgumentNullException>();

        // ListVersionsResult ShouldSerializeCommonPrefixes
        var lvr = new ListVersionsResult();
        lvr.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lvr.CommonPrefixes.Add(new CommonPrefixes("folder/"));
        lvr.ShouldSerializeCommonPrefixes().Should().BeTrue();
    }
}
