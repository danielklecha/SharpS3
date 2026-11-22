namespace SharpS3.Tests.Unit;

#pragma warning disable CS8625
#pragma warning disable CS8602
#pragma warning disable CS8604

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

public partial class SharpS3ClientTests
{
    

    private static (SharpS3Client client, MockHttpMessageHandler mockHandler) CreateMockClient(
        string endpoint = "http://localhost:8000",
        bool forcePathStyle = true,
        string? accessKey = "test-access-key",
        string? secretKey = "test-secret-key")
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri(endpoint),
            ForcePathStyle = forcePathStyle,
            AccessKey = accessKey,
            SecretKey = secretKey
        };
        var client = new SharpS3Client(options, httpClient);
        return (client, mockHandler);
    }

    private static async Task ShouldThrowAsync<TException>(Func<Task> action) where TException : Exception
    {
        await action.Should().ThrowAsync<TException>();
    }

    [TestMethod]
    public async Task SharpS3Client_ArgumentValidation_GuardClauses()
    {
        var (client, _) = CreateMockClient();

        // GetBucketTagsAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketTagsAsync(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketTagsAsync(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketTagsAsync("   "));

        // SetBucketTagsAsync
        var dummyTags = new Tagging { Tags = new TagSet { Tags = new List<Tag>() } };
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketTagsAsync(null!, dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketTagsAsync("", dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketTagsAsync("   ", dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketTagsAsync("b", null!));

        // DeleteBucketTagsAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketTagsAsync(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketTagsAsync(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketTagsAsync("   "));

        // GetBucketVersioningAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketVersioningAsync(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketVersioningAsync(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketVersioningAsync("   "));

        // SetBucketVersioningAsync
        var dummyVer = new VersioningConfiguration { Status = VersioningStatusEnum.Enabled };
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketVersioningAsync(null!, dummyVer));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketVersioningAsync("", dummyVer));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketVersioningAsync("   ", dummyVer));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketVersioningAsync("b", null!));

        // GetBucketWebsiteAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketWebsiteAsync(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketWebsiteAsync(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetBucketWebsiteAsync("   "));

        // SetBucketWebsiteAsync
        var dummyWeb = new WebsiteConfiguration();
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketWebsiteAsync(null!, dummyWeb));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketWebsiteAsync("", dummyWeb));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketWebsiteAsync("   ", dummyWeb));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetBucketWebsiteAsync("b", null!));

        // DeleteBucketWebsiteAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketWebsiteAsync(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketWebsiteAsync(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteBucketWebsiteAsync("   "));

        // HeadObjectAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync(null!, "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync("", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync("   ", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync("b", null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync("b", ""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.HeadObjectAsync("b", "   "));

        // DeleteObjectsAsync
        var dummyDel = new DeleteMultiple();
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectsAsync(null!, dummyDel));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectsAsync("", dummyDel));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectsAsync("   ", dummyDel));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectsAsync("b", null!));

        // CopyObjectAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync(null!, "sk", "db", "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("", "sk", "db", "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", null!, "db", "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", "", "db", "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", "sk", null!, "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", "sk", "", "dk"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", "sk", "db", null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.CopyObjectAsync("sb", "sk", "db", ""));

        // ListObjectsV2Async
        await ShouldThrowAsync<ArgumentNullException>(() => client.ListObjectsV2Async(null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.ListObjectsV2Async(""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.ListObjectsV2Async("   "));

        // GetObjectTagsAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync(null!, "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync("", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync("   ", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync("b", null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync("b", ""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.GetObjectTagsAsync("b", "   "));

        // SetObjectTagsAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetObjectTagsAsync(null!, "key", dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetObjectTagsAsync("", "key", dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetObjectTagsAsync("b", null!, dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetObjectTagsAsync("b", "", dummyTags));
        await ShouldThrowAsync<ArgumentNullException>(() => client.SetObjectTagsAsync("b", "key", null!));

        // DeleteObjectTagsAsync
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync(null!, "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync("", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync("   ", "key"));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync("b", null!));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync("b", ""));
        await ShouldThrowAsync<ArgumentNullException>(() => client.DeleteObjectTagsAsync("b", "   "));

        // UploadPartAsync (Stream overload null content)
        await ShouldThrowAsync<ArgumentNullException>(() => client.UploadPartAsync("b", "k", "upload-1", 1, (Stream)null!, 0));
    }

    [TestMethod]
    public async Task SharpS3Client_Metadata_WithPrefix_And_InitiateMultipart()
    {
        var (client, mock) = CreateMockClient();

        var meta = new Dictionary<string, string>
        {
            { "x-amz-meta-existing", "val1" },
            { "X-AMZ-META-UPPER", "val2" },
            { "plain-key", "val3" }
        };

        // PutObject with prefixed and unprefixed metadata
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await client.PutObjectAsync("test-bucket", "test-key", "hello world", metadata: meta);

        mock.LastRequest.Should().NotBeNull();
        mock.LastRequest!.Headers.Contains("x-amz-meta-existing").Should().BeTrue();
        mock.LastRequest!.Headers.Contains("X-AMZ-META-UPPER").Should().BeTrue();
        mock.LastRequest!.Headers.Contains("x-amz-meta-plain-key").Should().BeTrue();

        // InitiateMultipartUpload with prefixed metadata
        string initXml = "<InitiateMultipartUploadResult><Bucket>test-bucket</Bucket><Key>test-key</Key><UploadId>up123</UploadId></InitiateMultipartUploadResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(initXml, Encoding.UTF8, "application/xml") };
        var initResult = await client.InitiateMultipartUploadAsync("test-bucket", "test-key", metadata: meta);

        initResult.UploadId.Should().Be("up123");
        mock.LastRequest!.Headers.Contains("x-amz-meta-existing").Should().BeTrue();
        mock.LastRequest!.Headers.Contains("X-AMZ-META-UPPER").Should().BeTrue();
        mock.LastRequest!.Headers.Contains("x-amz-meta-plain-key").Should().BeTrue();
    }

    [TestMethod]
    public async Task SharpS3Client_ListObjectsV2_WithDelimiter()
    {
        var (client, mock) = CreateMockClient();

        string listXml = "<ListBucketResult><Name>test-bucket</Name><Delimiter>/</Delimiter></ListBucketResult>";
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(listXml, Encoding.UTF8, "application/xml") };

        var result = await client.ListObjectsV2Async("test-bucket", delimiter: "/");
        result.Should().NotBeNull();
        mock.LastRequest.Should().NotBeNull();
        mock.LastRequest!.RequestUri!.Query.Should().Contain("delimiter=%2F");
    }

    [TestMethod]
    public async Task SharpS3Client_DefaultPort_HostHeader()
    {
        // 1. Standard HTTP (port 80 default)
        var (clientHttp, mockHttp) = CreateMockClient(endpoint: "http://s3.example.com");
        mockHttp.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await clientHttp.CreateBucketAsync("my-bucket");

        mockHttp.LastRequest.Should().NotBeNull();
        mockHttp.LastRequest!.Headers.Host.Should().Be("s3.example.com");

        // Presigned URL on default port
        var presignedUriHttp = clientHttp.GeneratePresignedUrl("my-bucket", "my-key", HttpMethod.Get, TimeSpan.FromMinutes(10));
        presignedUriHttp.ToString().Should().NotContain(":80");
        presignedUriHttp.Host.Should().Be("s3.example.com");

        // 2. Standard HTTPS (port 443 default)
        var (clientHttps, mockHttps) = CreateMockClient(endpoint: "https://s3.amazonaws.com");
        mockHttps.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);
        await clientHttps.CreateBucketAsync("my-bucket");

        mockHttps.LastRequest.Should().NotBeNull();
        mockHttps.LastRequest!.Headers.Host.Should().Be("s3.amazonaws.com");

        var presignedUriHttps = clientHttps.GeneratePresignedUrl("my-bucket", "my-key", HttpMethod.Get, TimeSpan.FromMinutes(10));
        presignedUriHttps.ToString().Should().NotContain(":443");
        presignedUriHttps.Host.Should().Be("s3.amazonaws.com");
    }

    [TestMethod]
    public async Task SharpS3Client_Endpoint_WithBasePath()
    {
        // Endpoint with subpath /s3-api
        var (client, mock) = CreateMockClient(endpoint: "http://localhost:8000/s3-api");
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.OK);

        // Path-style request
        await client.CreateBucketAsync("my-bucket");
        mock.LastRequest.Should().NotBeNull();
        mock.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/s3-api/my-bucket");

        // Presigned URL with basePath
        var presignedUri = client.GeneratePresignedUrl("my-bucket", "folder/file.txt", HttpMethod.Get, TimeSpan.FromMinutes(5));
        presignedUri.AbsolutePath.Should().Be("/s3-api/my-bucket/folder/file.txt");
    }
}
