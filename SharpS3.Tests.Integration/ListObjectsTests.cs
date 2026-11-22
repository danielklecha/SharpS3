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
using SharpS3.Serialization;

[TestClass]
public class ListObjectsTests : SharpS3IntegrationTestBase
{
    [TestMethod]
    public async Task ListObjectsV2Async_WhenSendingRequestAndReceivingResponse_ServerReceivesSameRequestAndReturnsExpectedResponse()
    {
        const string bucket = "catalog-bucket";
        const string prefix = "items/";
        const string delimiter = "/";

        var expectedResult = new ListBucketResult
        {
            Name = bucket,
            Prefix = prefix,
            Delimiter = delimiter,
            KeyCount = 2,
            MaxKeys = 1000,
            IsTruncated = false,
            Contents = new List<ObjectMetadata>
            {
                new ObjectMetadata { Key = "items/item1.json", Size = 512, ETag = "\"etag-item-1\"" },
                new ObjectMetadata { Key = "items/item2.json", Size = 1024, ETag = "\"etag-item-2\"" }
            },
            CommonPrefixes = new List<CommonPrefixes>
            {
                new CommonPrefixes("items/subfolder/")
            }
        };

        S3Request? serverRequest = null;

        async Task<HttpResponseMessage> Handler(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var server = new SharpS3Server();
            serverRequest = await ReceiveServerRequestAsync(server, request, cancellationToken);

            string xml = ResponseSerializationContext.Run(2, false, false, () => SerializationHelper.SerializeXml(expectedResult, true));
            var s3Response = new S3ServerResponse
            {
                StatusCode = 200,
                ContentType = "application/xml",
                BodyStream = new MemoryStream(Encoding.UTF8.GetBytes(xml))
            };
            var response = await server.CreateResponseAsync(s3Response, cancellationToken);
            return ToHttpResponseMessage(response);
        }

        var client = CreateClient(Handler);
        var result = await client.ListObjectsV2Async(bucket, prefix: prefix, delimiter: delimiter);

        serverRequest.Should().NotBeNull();
        serverRequest!.RequestType.Should().Be(S3RequestType.BucketRead);
        serverRequest.Bucket.Should().Be(bucket);
        serverRequest.Prefix.Should().Be(prefix);
        serverRequest.Delimiter.Should().Be(delimiter);
        serverRequest.QueryParameters["list-type"].Should().Be("2");

        result.Should().NotBeNull();
        result.Name.Should().Be(bucket);
        result.Prefix.Should().Be(prefix);
        result.Delimiter.Should().Be(delimiter);
        result.KeyCount.Should().Be(2);
        result.Contents.Should().HaveCount(2);
        result.Contents[0].Key.Should().Be("items/item1.json");
        result.Contents[1].Key.Should().Be("items/item2.json");
        result.CommonPrefixes.Should().HaveCount(1);
        result.CommonPrefixes[0].Prefix.Should().Be("items/subfolder/");
    }
}
