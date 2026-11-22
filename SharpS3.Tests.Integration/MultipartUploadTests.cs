namespace SharpS3.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
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
public class MultipartUploadTests : SharpS3IntegrationTestBase
{
    [TestMethod]
    public async Task InitiateMultipartUploadAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "large-data-bucket";
        const string key = "large-file.bin";

        var expectedResult = new InitiateMultipartUploadResult
        {
            Bucket = bucket,
            Key = key,
            UploadId = "upload-session-xyz"
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
        var result = await client.InitiateMultipartUploadAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectCreateMultipartUpload);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.QueryParameters.Should().ContainKey("uploads");

        result.Should().NotBeNull();
        result.Bucket.Should().Be(bucket);
        result.Key.Should().Be(key);
        result.UploadId.Should().Be("upload-session-xyz");
    }

    [TestMethod]
    public async Task UploadPartAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "large-data-bucket";
        const string key = "large-file.bin";
        const string uploadId = "upload-session-xyz";
        const int partNumber = 1;
        byte[] partData = Encoding.UTF8.GetBytes("PART_1_CONTENT_DATA");

        S3Request? serverRequest = null;
        byte[]? receivedBytes = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            using var ms = new MemoryStream();
            await serverRequest.BodyStream.CopyToAsync(ms, cancellationToken);
            receivedBytes = ms.ToArray();

            var s3Response = CreateEmptyResponse(200, ("ETag", "\"etag-part-1\""));
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var result = await client.UploadPartAsync(bucket, key, uploadId, partNumber, partData);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectUploadPart);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.PartNumber.Should().Be(partNumber);
        serverRequest.UploadId.Should().Be(uploadId);
        receivedBytes.Should().Equal(partData);

        result.Should().NotBeNull();
        result.PartNumber.Should().Be(partNumber);
        result.ETag.Should().Be("\"etag-part-1\"");
    }

    [TestMethod]
    public async Task CompleteMultipartUploadAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "large-data-bucket";
        const string key = "large-file.bin";
        const string uploadId = "upload-session-xyz";

        var completePayload = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = "\"etag-part-1\"" },
                new Part { PartNumber = 2, ETag = "\"etag-part-2\"" }
            }
        };

        var expectedResult = new CompleteMultipartUploadResult
        {
            Bucket = bucket,
            Key = key,
            ETag = "\"final-assembled-etag\"",
            Location = $"http://127.0.0.1:8000/{bucket}/{key}"
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
        var result = await client.CompleteMultipartUploadAsync(bucket, key, uploadId, completePayload);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectCompleteMultipartUpload);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.UploadId.Should().Be(uploadId);

        result.Should().NotBeNull();
        result.Bucket.Should().Be(bucket);
        result.Key.Should().Be(key);
        result.ETag.Should().Be("\"final-assembled-etag\"");
    }

    [TestMethod]
    public async Task AbortMultipartUploadAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "large-data-bucket";
        const string key = "large-file.bin";
        const string uploadId = "upload-session-xyz";

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
        await client.AbortMultipartUploadAsync(bucket, key, uploadId);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectAbortMultipartUpload);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.UploadId.Should().Be(uploadId);
    }
}
