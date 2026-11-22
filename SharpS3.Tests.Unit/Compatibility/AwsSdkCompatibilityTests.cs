namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Storage;
using SharpS3.Extensions;

[TestClass]
public class AwsSdkCompatibilityTests
{
    private IAmazonS3? _Client;
    private readonly InMemoryStorageBackend _Backend = new();

    [TestInitialize]
    public void Setup()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = false // Allows AWS SDK requests freely in this suite
        };
        var server = new SharpS3Server(settings);
        var dispatcher = new S3RequestDispatcher(_Backend);
        var handler = new SharpS3TestHttpHandler(server, dispatcher);
        var httpClient = new HttpClient(handler);

        var config = new AmazonS3Config
        {
            ServiceURL = "http://localhost:8000",
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = "us-east-1",
            HttpClientFactory = new TestHttpClientFactory(httpClient)
        };

        _Client = new AmazonS3Client("mock-access-key", "mock-secret-key", config);
    }

    [TestCleanup]
    public void Teardown()
    {
        _Client?.Dispose();
    }

    [TestMethod]
    public async Task AwsSdk_Can_Put_List_And_Delete_Bucket()
    {
        // 1. PutBucket
        var putBucketResp = await _Client!.PutBucketAsync(new PutBucketRequest
        {
            BucketName = "sdk-test-bucket"
        });
        putBucketResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);

        // 2. ListBuckets
        var listResp = await _Client.ListBucketsAsync();
        listResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        listResp.Buckets.Should().Contain(b => b.BucketName == "sdk-test-bucket");

        // 3. DeleteBucket
        var delResp = await _Client.DeleteBucketAsync(new DeleteBucketRequest
        {
            BucketName = "sdk-test-bucket"
        });
        delResp.HttpStatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task AwsSdk_Can_Put_Get_And_Delete_Object()
    {
        await _Client!.PutBucketAsync(new PutBucketRequest { BucketName = "obj-bucket" });

        string content = "Hello from official Amazon AWSSDK.S3 client!";
        byte[] bytes = Encoding.UTF8.GetBytes(content);

        // 1. PutObject
        using var putStream = new MemoryStream(bytes);
        var putResp = await _Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = "obj-bucket",
            Key = "sample.txt",
            InputStream = putStream,
            ContentType = "text/plain"
        });
        putResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        putResp.ETag.Should().NotBeNull();

        // 2. GetObjectMetadata (HeadObject)
        var headResp = await _Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = "obj-bucket",
            Key = "sample.txt"
        });
        headResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        headResp.ContentLength.Should().Be(bytes.Length);

        // 3. GetObject
        using var getResp = await _Client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = "obj-bucket",
            Key = "sample.txt"
        });
        getResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        using var reader = new StreamReader(getResp.ResponseStream);
        string retrievedText = await reader.ReadToEndAsync();
        retrievedText.Should().Be(content);

        // 4. DeleteObject
        var delResp = await _Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = "obj-bucket",
            Key = "sample.txt"
        });
        delResp.HttpStatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task AwsSdk_Can_List_Objects_V2()
    {
        await _Client!.PutBucketAsync(new PutBucketRequest { BucketName = "v2-bucket" });

        // Upload 3 objects
        for (int i = 1; i <= 3; i++)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"Data {i}"));
            await _Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = "v2-bucket",
                Key = $"file_{i}.txt",
                InputStream = stream
            });
        }

        // ListObjectsV2
        var listResp = await _Client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = "v2-bucket",
            MaxKeys = 10
        });

        listResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        listResp.S3Objects.Count.Should().Be(3);
        listResp.S3Objects.Should().Contain(o => o.Key == "file_1.txt");
        listResp.S3Objects.Should().Contain(o => o.Key == "file_2.txt");
        listResp.S3Objects.Should().Contain(o => o.Key == "file_3.txt");
    }

    [TestMethod]
    public async Task AwsSdk_Can_Perform_Multipart_Upload()
    {
        await _Client!.PutBucketAsync(new PutBucketRequest { BucketName = "mp-sdk-bucket" });

        // 1. Initiate
        var initResp = await _Client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = "mp-sdk-bucket",
            Key = "large-sdk-file.bin"
        });
        initResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        string uploadId = initResp.UploadId;

        // 2. Upload Parts
        byte[] chunk1 = Encoding.UTF8.GetBytes("AWS_SDK_PART_1_CONTENT_");
        using var s1 = new MemoryStream(chunk1);
        var up1 = await _Client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = "mp-sdk-bucket",
            Key = "large-sdk-file.bin",
            UploadId = uploadId,
            PartNumber = 1,
            InputStream = s1
        });

        byte[] chunk2 = Encoding.UTF8.GetBytes("AWS_SDK_PART_2_CONTENT!");
        using var s2 = new MemoryStream(chunk2);
        var up2 = await _Client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = "mp-sdk-bucket",
            Key = "large-sdk-file.bin",
            UploadId = uploadId,
            PartNumber = 2,
            InputStream = s2
        });

        // 3. Complete
        var compResp = await _Client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = "mp-sdk-bucket",
            Key = "large-sdk-file.bin",
            UploadId = uploadId,
            PartETags = new List<PartETag>
            {
                new PartETag(1, up1.ETag),
                new PartETag(2, up2.ETag)
            }
        });
        compResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Verify downloaded data
        using var getResp = await _Client.GetObjectAsync("mp-sdk-bucket", "large-sdk-file.bin");
        using var reader = new StreamReader(getResp.ResponseStream);
        string assembled = await reader.ReadToEndAsync();
        assembled.Should().Be("AWS_SDK_PART_1_CONTENT_AWS_SDK_PART_2_CONTENT!");
    }

    [TestMethod]
    public async Task AwsSdk_Can_Delete_Multiple_Objects()
    {
        await _Client!.PutBucketAsync(new PutBucketRequest { BucketName = "batch-del-bucket" });

        for (int i = 1; i <= 2; i++)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"file {i}"));
            await _Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = "batch-del-bucket",
                Key = $"batch_{i}.txt",
                InputStream = stream
            });
        }

        var delResp = await _Client.DeleteObjectsAsync(new DeleteObjectsRequest
        {
            BucketName = "batch-del-bucket",
            Objects = new List<KeyVersion>
            {
                new KeyVersion { Key = "batch_1.txt" },
                new KeyVersion { Key = "batch_2.txt" }
            }
        });

        delResp.HttpStatusCode.Should().Be(HttpStatusCode.OK);
        delResp.DeletedObjects.Count.Should().Be(2);
    }
}
