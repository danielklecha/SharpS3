namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Dispatch;
using SharpS3.Extensions;
using SharpS3.Models;
using SharpS3.Storage;

[TestClass]
public partial class SharpS3ClientTests
{
    private SharpS3Client? _Client;
    private SharpS3TestHttpHandler? _Handler;
    private readonly InMemoryStorageBackend _Backend = new();

    [TestInitialize]
    public void Setup()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = false // Allows anonymous / test tokens freely
        };
        var server = new SharpS3Server(settings);
        var dispatcher = new S3RequestDispatcher(_Backend);
        _Handler = new SharpS3TestHttpHandler(server, dispatcher);
        var httpClient = new HttpClient(_Handler, disposeHandler: false);

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            ForcePathStyle = true,
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        };

        _Client = new SharpS3Client(options, httpClient);
    }

    [TestCleanup]
    public void Teardown()
    {
        _Client?.Dispose();
    }

    [TestMethod]
    public async Task Client_Can_Create_List_Check_And_Delete_Bucket()
    {
        const string bucket = "sharps3-client-bucket";

        bool existsInitially = await _Client!.BucketExistsAsync(bucket);
        existsInitially.Should().BeFalse();

        await _Client.CreateBucketAsync(bucket);

        bool existsAfterCreate = await _Client.BucketExistsAsync(bucket);
        existsAfterCreate.Should().BeTrue();

        var listResult = await _Client.ListBucketsAsync();
        listResult.Buckets.BucketList.Should().Contain(b => b.Name == bucket);

        await _Client.DeleteBucketAsync(bucket);

        bool existsAfterDelete = await _Client.BucketExistsAsync(bucket);
        existsAfterDelete.Should().BeFalse();
    }

    [TestMethod]
    public async Task Client_Can_Put_Head_Get_And_Delete_Object()
    {
        const string bucket = "obj-crud-bucket";
        const string key = "documents/test-file.txt";
        const string content = "Hello world from SharpS3Client!";

        await _Client!.CreateBucketAsync(bucket);

        // Put with metadata
        var meta = new Dictionary<string, string> { { "author", "Daniel" }, { "project", "SharpS3" } };
        await _Client.PutObjectAsync(bucket, key, content, "text/plain", meta);

        // Check exists & HEAD
        bool exists = await _Client.ObjectExistsAsync(bucket, key);
        exists.Should().BeTrue();

        var head = await _Client.HeadObjectAsync(bucket, key);
        head.Should().NotBeNull();
        head!.ContentLength.Should().Be(Encoding.UTF8.GetByteCount(content));
        head.Metadata.ContainsKey("author").Should().BeTrue();
        head.Metadata["author"].Should().Be("Daniel");

        // GET object
        using (var getResp = await _Client.GetObjectAsync(bucket, key))
        {
            string downloaded = await getResp.ReadAsStringAsync();
            downloaded.Should().Be(content);
            getResp.Metadata.ContainsKey("author").Should().BeTrue();
        }

        // DELETE object
        await _Client.DeleteObjectAsync(bucket, key);
        bool existsAfterDelete = await _Client.ObjectExistsAsync(bucket, key);
        existsAfterDelete.Should().BeFalse();

        var headAfterDelete = await _Client.HeadObjectAsync(bucket, key);
        headAfterDelete.Should().BeNull();
    }

    [TestMethod]
    public async Task Client_Can_Read_Byte_Range()
    {
        const string bucket = "range-test-bucket";
        const string key = "data.bin";
        byte[] fullData = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");

        await _Client!.CreateBucketAsync(bucket);
        await _Client.PutObjectAsync(bucket, key, fullData, "application/octet-stream");

        // Read range: bytes 10-14 ("ABCDE")
        using var rangeResp = await _Client.GetObjectAsync(bucket, key, ByteRange.FromStartEnd(10, 14));
        byte[] rangeBytes = await rangeResp.ReadAsByteArrayAsync();

        string rangeText = Encoding.UTF8.GetString(rangeBytes);
        rangeText.Should().Be("ABCDE");
        rangeResp.ContentLength.Should().Be(5);
    }

    [TestMethod]
    public async Task Client_Can_ListObjectsV2()
    {
        const string bucket = "list-v2-bucket";
        await _Client!.CreateBucketAsync(bucket);

        await _Client.PutObjectAsync(bucket, "photos/2026/pic1.jpg", "pic1");
        await _Client.PutObjectAsync(bucket, "photos/2026/pic2.jpg", "pic2");
        await _Client.PutObjectAsync(bucket, "docs/readme.txt", "readme");

        var listResp = await _Client.ListObjectsV2Async(bucket, prefix: "photos/");

        listResp.KeyCount.Should().Be(2);
        listResp.Contents.Should().Contain(c => c.Key == "photos/2026/pic1.jpg");
        listResp.Contents.Should().Contain(c => c.Key == "photos/2026/pic2.jpg");
        listResp.Contents.Should().NotContain(c => c.Key == "docs/readme.txt");
    }

    [TestMethod]
    public async Task Client_Can_Execute_Multipart_Upload()
    {
        const string bucket = "multipart-client-bucket";
        const string key = "large-client-file.dat";
        await _Client!.CreateBucketAsync(bucket);

        var initResult = await _Client.InitiateMultipartUploadAsync(bucket, key);
        string uploadId = initResult.UploadId;
        string.IsNullOrEmpty(uploadId).Should().BeFalse();

        byte[] part1Data = Encoding.UTF8.GetBytes("Part 1 Payload Data --- ");
        byte[] part2Data = Encoding.UTF8.GetBytes("Part 2 Payload Data Complete!");

        var part1 = await _Client.UploadPartAsync(bucket, key, uploadId, 1, part1Data);
        var part2 = await _Client.UploadPartAsync(bucket, key, uploadId, 2, part2Data);

        var complete = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = part1.ETag },
                new Part { PartNumber = 2, ETag = part2.ETag }
            }
        };

        var compResult = await _Client.CompleteMultipartUploadAsync(bucket, key, uploadId, complete);
        compResult.Key.Should().Be(key);

        using var getResp = await _Client.GetObjectAsync(bucket, key);
        string assembled = await getResp.ReadAsStringAsync();
        assembled.Should().Be("Part 1 Payload Data --- Part 2 Payload Data Complete!");
    }

    [TestMethod]
    public async Task Client_Can_Delete_Multiple_Objects()
    {
        const string bucket = "delete-multiple-bucket";
        await _Client!.CreateBucketAsync(bucket);

        await _Client.PutObjectAsync(bucket, "file1.txt", "data1");
        await _Client.PutObjectAsync(bucket, "file2.txt", "data2");
        await _Client.PutObjectAsync(bucket, "file3.txt", "data3");

        var deletePayload = new DeleteMultiple
        {
            Objects = new List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object { Key = "file1.txt" },
                new SharpS3.Models.Object { Key = "file2.txt" }
            }
        };

        var deleteResult = await _Client.DeleteObjectsAsync(bucket, deletePayload);
        deleteResult.DeletedObjects.Count.Should().Be(2);

        (await _Client.ObjectExistsAsync(bucket, "file1.txt")).Should().BeFalse();
        (await _Client.ObjectExistsAsync(bucket, "file2.txt")).Should().BeFalse();
        (await _Client.ObjectExistsAsync(bucket, "file3.txt")).Should().BeTrue();
    }

    [TestMethod]
    public async Task Client_Throws_SharpS3ClientException_On_NoSuchBucket()
    {
        var act = async () => await _Client!.GetObjectAsync("non-existent-bucket-xyz", "key.txt");
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.ErrorCode.Should().Be("NoSuchBucket");
    }

    [TestMethod]
    public async Task Client_Can_Copy_Object()
    {
        const string bucket = "client-copy-bucket";
        await _Client!.CreateBucketAsync(bucket);

        await _Client.PutObjectAsync(bucket, "original.txt", "Original text content");

        var copyRes = await _Client.CopyObjectAsync(bucket, "original.txt", bucket, "copied.txt");
        copyRes.ETag.Should().NotBeNull();

        using var getCopy = await _Client.GetObjectAsync(bucket, "copied.txt");
        string body = await getCopy.ReadAsStringAsync();
        body.Should().Be("Original text content");
    }

    [TestMethod]
    public async Task Client_Can_Manage_Object_Tags()
    {
        const string bucket = "client-tags-bucket";
        const string key = "report.csv";
        await _Client!.CreateBucketAsync(bucket);
        await _Client.PutObjectAsync(bucket, key, "col1,col2\r\nval1,val2");

        var tagging = new Tagging(new TagSet(new List<Tag>
        {
            new Tag("tier", "gold"),
            new Tag("department", "finance")
        }));

        await _Client.SetObjectTagsAsync(bucket, key, tagging);

        var retrieved = await _Client.GetObjectTagsAsync(bucket, key);
        retrieved.Tags.Should().NotBeNull();
        retrieved.Tags!.Tags.Count.Should().Be(2);
        retrieved.Tags.Tags.Should().Contain(t => t.Key == "tier" && t.Value == "gold");

        await _Client.DeleteObjectTagsAsync(bucket, key);
        var afterDelete = await _Client.GetObjectTagsAsync(bucket, key);
        (afterDelete.Tags == null || afterDelete.Tags.Tags.Count == 0).Should().BeTrue();
    }

    [TestMethod]
    public async Task Client_Can_Get_Bucket_Location()
    {
        const string bucket = "location-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var loc = await _Client.GetBucketLocationAsync(bucket);
        loc.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Client_Can_Upload_And_Download_Local_File()
    {
        const string bucket = "file-io-bucket";
        await _Client!.CreateBucketAsync(bucket);

        string tempSrcFile = Path.GetTempFileName();
        string tempDestFile = Path.Combine(Path.GetTempPath(), "downloaded_" + Guid.NewGuid().ToString("N") + ".txt");

        try
        {
            string expectedContent = "Testing local file upload and download via SharpS3Client!";
            await File.WriteAllTextAsync(tempSrcFile, expectedContent);

            await _Client.UploadFileAsync(bucket, "uploaded.txt", tempSrcFile, "text/plain");

            await _Client.DownloadFileAsync(bucket, "uploaded.txt", tempDestFile);

            string downloadedContent = await File.ReadAllTextAsync(tempDestFile);
            downloadedContent.Should().Be(expectedContent);
        }
        finally
        {
            try { File.Delete(tempSrcFile); } catch { }
            try { File.Delete(tempDestFile); } catch { }
        }
    }

    [TestMethod]
    public async Task Client_Can_ListAllObjects_AsyncEnumerable()
    {
        const string bucket = "async-enum-bucket";
        await _Client!.CreateBucketAsync(bucket);

        for (int i = 1; i <= 5; i++)
        {
            await _Client.PutObjectAsync(bucket, $"items/item_{i}.json", $"{{\"id\":{i}}}");
        }

        var keys = new List<string>();
        await foreach (var obj in _Client.ListAllObjectsAsync(bucket, prefix: "items/"))
        {
            keys.Add(obj.Key);
        }

        keys.Count.Should().Be(5);
        keys.Should().Contain("items/item_1.json");
        keys.Should().Contain("items/item_5.json");
    }

    [TestMethod]
    public async Task Client_Can_Generate_And_Use_Presigned_Url()
    {
        const string bucket = "presign-bucket";
        const string key = "secret-doc.txt";
        const string content = "Presigned access authorized payload!";

        await _Client!.CreateBucketAsync(bucket);
        await _Client.PutObjectAsync(bucket, key, content, "text/plain");

        Uri presignedUrl = _Client.GeneratePresignedUrl(bucket, key, System.Net.Http.HttpMethod.Get, TimeSpan.FromMinutes(15));
        presignedUrl.Should().NotBeNull();
        presignedUrl.Query.Should().Contain("X-Amz-Signature=");

        // Fetch using a plain unauthenticated HttpClient (no headers, no credentials)
        using var rawClient = new System.Net.Http.HttpClient(_Handler!, disposeHandler: false);
        using var resp = await rawClient.GetAsync(presignedUrl);

        resp.IsSuccessStatusCode.Should().BeTrue();
        string downloaded = await resp.Content.ReadAsStringAsync();
        downloaded.Should().Be(content);
    }

    [TestMethod]
    public async Task Client_Can_Get_And_Set_Bucket_Acl()
    {
        const string bucket = "client-acl-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var acl = new AccessControlPolicy
        {
            Owner = new Owner { ID = "owner-id", DisplayName = "owner-display" },
            Acl = new AccessControlList
            {
                Grants = new List<Grant>
                {
                    new Grant { Permission = PermissionEnum.FullControl, Grantee = new Grantee { ID = "grantee-id" } }
                }
            }
        };

        await _Client.SetBucketAclAsync(bucket, acl);
        var readAcl = await _Client.GetBucketAclAsync(bucket);
        readAcl.Should().NotBeNull();
        readAcl.Owner.ID.Should().Be("owner-id");
    }

    [TestMethod]
    public async Task Client_Can_Get_Set_And_Delete_Bucket_Tags()
    {
        const string bucket = "client-tags-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var tags = new Tagging
        {
            Tags = new TagSet
            {
                Tags = new List<Tag> { new Tag("project", "sharps3") }
            }
        };

        await _Client.SetBucketTagsAsync(bucket, tags);
        var readTags = await _Client.GetBucketTagsAsync(bucket);
        readTags.Tags.Tags.Should().Contain(t => t.Key == "project" && t.Value == "sharps3");

        await _Client.DeleteBucketTagsAsync(bucket);
        var act = async () => await _Client.GetBucketTagsAsync(bucket);
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Client_Can_Get_And_Set_Bucket_Versioning()
    {
        const string bucket = "client-ver-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var ver = new VersioningConfiguration { Status = VersioningStatusEnum.Enabled };
        await _Client.SetBucketVersioningAsync(bucket, ver);
        var readVer = await _Client.GetBucketVersioningAsync(bucket);
        readVer.Status.Should().Be(VersioningStatusEnum.Enabled);
    }

    [TestMethod]
    public async Task Client_Can_Get_Set_And_Delete_Bucket_Website()
    {
        const string bucket = "client-web-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var web = new WebsiteConfiguration
        {
            IndexDocument = new IndexDocument { Suffix = "index.html" }
        };

        await _Client.SetBucketWebsiteAsync(bucket, web);
        var readWeb = await _Client.GetBucketWebsiteAsync(bucket);
        readWeb.IndexDocument.Suffix.Should().Be("index.html");

        await _Client.DeleteBucketWebsiteAsync(bucket);
        var act = async () => await _Client.GetBucketWebsiteAsync(bucket);
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Client_Can_Get_And_Set_Bucket_Logging()
    {
        const string bucket = "client-log-bucket";
        await _Client!.CreateBucketAsync(bucket);

        var log = new BucketLoggingStatus
        {
            Enabled = new LoggingEnabled { TargetBucket = "target-log-bucket", TargetPrefix = "logs/" }
        };

        await _Client.SetBucketLoggingAsync(bucket, log);
        var readLog = await _Client.GetBucketLoggingAsync(bucket);
        readLog.Enabled.TargetBucket.Should().Be("target-log-bucket");
    }
}
