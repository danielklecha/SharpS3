namespace SharpS3.Tests.Integration;

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
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class ObjectOperationsTests : SharpS3IntegrationTestBase
{
    [TestMethod]
    public async Task PutObjectAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "test-bucket";
        const string key = "folder/file.txt";
        const string content = "Integration test file body!";
        var userMetadata = new Dictionary<string, string>
        {
            { "Environment", "IntegrationTesting" },
            { "Author", "Daniel" }
        };

        S3Request? serverRequest = null;
        byte[]? receivedBytes = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            using var ms = new MemoryStream();
            await serverRequest.BodyStream.CopyToAsync(ms, cancellationToken);
            receivedBytes = ms.ToArray();

            var s3Response = CreateEmptyResponse(200, ("ETag", "\"etag-put-12345\""));
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        await client.PutObjectAsync(bucket, key, content, "text/plain", userMetadata);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectWrite);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        Encoding.UTF8.GetString(receivedBytes!).Should().Be(content);
        serverRequest.Headers.Should().ContainKey("x-amz-meta-environment");
        serverRequest.Headers.Should().ContainKey("x-amz-meta-author");
    }

    [TestMethod]
    public async Task GetObjectAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "test-bucket";
        const string key = "data/sample.json";
        const string expectedJson = "{\"id\":42,\"name\":\"test\"}";

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateStreamResponse(
                200,
                new MemoryStream(Encoding.UTF8.GetBytes(expectedJson)),
                "application/json",
                ("ETag", "\"etag-json-42\""),
                ("x-amz-meta-custom", "meta-value"));
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        using var clientResponse = await client.GetObjectAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectRead);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);

        var body = await clientResponse.ReadAsStringAsync();
        body.Should().Be(expectedJson);
        clientResponse.ETag.Should().Be("\"etag-json-42\"");
        clientResponse.ContentType.Should().Be("application/json");
        clientResponse.Metadata.Should().ContainKey("custom");
        clientResponse.Metadata["custom"].Should().Be("meta-value");
    }

    [TestMethod]
    public async Task GetObjectAsync_WithByteRange_ServerReceivesRangeHeader()
    {
        const string bucket = "test-bucket";
        const string key = "video.mp4";
        byte[] rangeBytes = Encoding.UTF8.GetBytes("PARTIAL_DATA");

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateStreamResponse(
                206,
                new MemoryStream(rangeBytes),
                "video/mp4",
                ("Content-Range", "bytes 100-111/10000"),
                ("Content-Length", rangeBytes.Length.ToString()));
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        using var clientResponse = await client.GetObjectAsync(bucket, key, ByteRange.FromStartEnd(100, 111));

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectReadRange);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.RangeStart.Should().Be(100);
        serverRequest.RangeEnd.Should().Be(111);

        var downloadedBytes = await clientResponse.ReadAsByteArrayAsync();
        downloadedBytes.Should().Equal(rangeBytes);
    }

    [TestMethod]
    public async Task HeadObjectAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "test-bucket";
        const string key = "images/pic.png";

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateEmptyResponse(
                200,
                ("Content-Length", "2048"),
                ("Content-Type", "image/png"),
                ("ETag", "\"etag-png-pic\""),
                ("x-amz-meta-photographer", "Alice"));
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var head = await client.HeadObjectAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectExists);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);

        head.Should().NotBeNull();
        head!.ContentLength.Should().Be(2048);
        head.ContentType.Should().Be("image/png");
        head.ETag.Should().Be("\"etag-png-pic\"");
        head.Metadata.Should().ContainKey("photographer");
        head.Metadata["photographer"].Should().Be("Alice");
    }

    [TestMethod]
    public async Task DeleteObjectAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "test-bucket";
        const string key = "temp/junk.dat";

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateEmptyResponse(204);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        await client.DeleteObjectAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectDelete);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
    }

    [TestMethod]
    public async Task DeleteObjectsAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "test-bucket";
        var deletePayload = new DeleteMultiple
        {
            Objects = new List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object { Key = "obj1.txt" },
                new SharpS3.Models.Object { Key = "obj2.txt" }
            }
        };

        var expectedResult = new DeleteResult
        {
            DeletedObjects = new List<Deleted>
            {
                new Deleted { Key = "obj1.txt" },
                new Deleted { Key = "obj2.txt" }
            }
        };

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateXmlResponse(200, expectedResult);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var result = await client.DeleteObjectsAsync(bucket, deletePayload);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectDeleteMultiple);
        serverRequest.Bucket.Should().Be(bucket);

        result.Should().NotBeNull();
        result.DeletedObjects.Should().HaveCount(2);
        result.DeletedObjects[0].Key.Should().Be("obj1.txt");
        result.DeletedObjects[1].Key.Should().Be("obj2.txt");
    }

    [TestMethod]
    public async Task CopyObjectAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string srcBucket = "src-bucket";
        const string srcKey = "original.jpg";
        const string destBucket = "dest-bucket";
        const string destKey = "archive/original_copy.jpg";

        var expectedResult = new CopyObjectResult
        {
            ETag = "\"copied-etag-789\"",
            LastModified = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc)
        };

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            var s3Response = CreateXmlResponse(200, expectedResult);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var result = await client.CopyObjectAsync(srcBucket, srcKey, destBucket, destKey);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectCopy);
        serverRequest.Bucket.Should().Be(destBucket);
        serverRequest.Key.Should().Be(destKey);
        serverRequest.CopySourceBucket.Should().Be(srcBucket);
        serverRequest.CopySourceKey.Should().Be(srcKey);

        result.Should().NotBeNull();
        result.ETag.Should().Be("\"copied-etag-789\"");
        result.LastModified.Should().Be(expectedResult.LastModified);
    }
}
