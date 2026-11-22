namespace SharpS3.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class BucketOperationsTests : SharpS3IntegrationTestBase
{
    [TestMethod]
    public async Task CreateBucketAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucketName = "new-test-bucket";
        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateEmptyResponse(200);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        await client.CreateBucketAsync(bucketName);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketWrite);
        serverRequest.Bucket.Should().Be(bucketName);
    }

    [TestMethod]
    public async Task ListBucketsAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        S3Request? serverRequest = null;
        var expectedResult = new ListAllMyBucketsResult
        {
            Owner = new Owner { ID = "test-owner-id", DisplayName = "test-owner" },
            Buckets = new Buckets
            {
                BucketList = new List<Bucket>
                {
                    new Bucket { Name = "bucket-1", CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
                    new Bucket { Name = "bucket-2", CreationDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) }
                }
            }
        };

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateXmlResponse(200, expectedResult);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var clientResponse = await client.ListBucketsAsync();

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ListBuckets);
        serverRequest.Bucket.Should().BeNull();
        serverRequest.Key.Should().BeNull();

        clientResponse.Should().NotBeNull();
        clientResponse.Owner.ID.Should().Be(expectedResult.Owner.ID);
        clientResponse.Owner.DisplayName.Should().Be(expectedResult.Owner.DisplayName);
        clientResponse.Buckets.BucketList.Should().HaveCount(2);
        clientResponse.Buckets.BucketList[0].Name.Should().Be("bucket-1");
        clientResponse.Buckets.BucketList[1].Name.Should().Be("bucket-2");
    }

    [TestMethod]
    public async Task DeleteBucketAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucketName = "bucket-to-delete";
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
        await client.DeleteBucketAsync(bucketName);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketDelete);
        serverRequest.Bucket.Should().Be(bucketName);
    }

    [TestMethod]
    public async Task BucketExistsAsync_WhenBucketExists_ReturnsTrue()
    {
        const string bucketName = "existing-bucket";
        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateEmptyResponse(200);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        bool exists = await client.BucketExistsAsync(bucketName);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketExists);
        serverRequest.Bucket.Should().Be(bucketName);
        exists.Should().BeTrue();
    }

    [TestMethod]
    public async Task BucketExistsAsync_WhenBucketDoesNotExist_ReturnsFalse()
    {
        const string bucketName = "missing-bucket";
        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateEmptyResponse(404);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        bool exists = await client.BucketExistsAsync(bucketName);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketExists);
        serverRequest.Bucket.Should().Be(bucketName);
        exists.Should().BeFalse();
    }

    [TestMethod]
    public async Task GetBucketLocationAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucketName = "location-bucket";
        var expectedLocation = new LocationConstraint { Text = "eu-west-1" };
        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateXmlResponse(200, expectedLocation);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var location = await client.GetBucketLocationAsync(bucketName);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketReadLocation);
        serverRequest.Bucket.Should().Be(bucketName);

        location.Should().NotBeNull();
        location.Text.Should().Be("eu-west-1");
    }
}
