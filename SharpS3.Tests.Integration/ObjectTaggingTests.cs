namespace SharpS3.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class ObjectTaggingTests : SharpS3IntegrationTestBase
{
    [TestMethod]
    public async Task SetObjectTagsAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "tag-bucket";
        const string key = "document.pdf";
        var tagging = new Tagging(new TagSet(new List<Tag>
        {
            new Tag("env", "staging"),
            new Tag("department", "rnd")
        }));

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
        await client.SetObjectTagsAsync(bucket, key, tagging);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectWriteTags);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.QueryParameters.Should().ContainKey("tagging");
    }

    [TestMethod]
    public async Task GetObjectTagsAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "tag-bucket";
        const string key = "document.pdf";

        var expectedTagging = new Tagging(new TagSet(new List<Tag>
        {
            new Tag("project", "SharpS3"),
            new Tag("tier", "premium")
        }));

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);
            var s3Response = CreateXmlResponse(200, expectedTagging);
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var result = await client.GetObjectTagsAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectReadTags);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.QueryParameters.Should().ContainKey("tagging");

        result.Should().NotBeNull();
        result.Tags.Should().NotBeNull();
        result.Tags!.Tags.Should().HaveCount(2);
        result.Tags.Tags.Should().Contain(t => t.Key == "project" && t.Value == "SharpS3");
        result.Tags.Tags.Should().Contain(t => t.Key == "tier" && t.Value == "premium");
    }

    [TestMethod]
    public async Task DeleteObjectTagsAsync_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "tag-bucket";
        const string key = "document.pdf";

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
        await client.DeleteObjectTagsAsync(bucket, key);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.ObjectDeleteTags);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Key.Should().Be(key);
        serverRequest.QueryParameters.Should().ContainKey("tagging");
    }
}
