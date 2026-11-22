namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

public partial class FileSystemStorageBackendTests
{
    

    [TestMethod]
    public async Task Can_Manage_Buckets_On_Disk()
    {
        (await _Backend.BucketExistsAsync("disk-bucket")).Should().BeFalse();

        await _Backend.CreateBucketAsync("disk-bucket", null);
        (await _Backend.BucketExistsAsync("disk-bucket")).Should().BeTrue();
        Directory.Exists(Path.Combine(_TempDir, "disk-bucket")).Should().BeTrue();

        var list = await _Backend.ListBucketsAsync(null!);
        list.Buckets.BucketList.Should().Contain(b => b.Name == "disk-bucket");

        await _Backend.DeleteBucketAsync("disk-bucket");
        (await _Backend.BucketExistsAsync("disk-bucket")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Can_Put_Get_And_Range_Read_File_On_Disk()
    {
        await _Backend.CreateBucketAsync("files", null);

        byte[] original = Encoding.UTF8.GetBytes("ABCDEFGHIJ0123456789KLMNOPQRSTUVWXYZ");
        using var stream = new MemoryStream(original);

        string etag = await _Backend.PutObjectAsync("files", "data/test.bin", stream, original.Length, "application/octet-stream");
        etag.Should().NotBeNull();

        string expectedFilePath = Path.Combine(_TempDir, "files", "data", "test.bin");
        File.Exists(expectedFilePath).Should().BeTrue();
        new FileInfo(expectedFilePath).Length.Should().Be(original.Length);

        // HeadObject
        var md = await _Backend.GetObjectMetadataAsync("files", "data/test.bin");
        md.Should().NotBeNull();
        md!.Size.Should().Be(original.Length);
        md.ContentType.Should().Be("application/octet-stream");

        // Full GetObject
        using (var fullObj = await _Backend.GetObjectAsync("files", "data/test.bin"))
        {
            fullObj.ContentLength.Should().Be(original.Length);
            using var ms = new MemoryStream();
            await fullObj.Stream.CopyToAsync(ms);
            ms.ToArray().Should().Equal(original);
        }

        // Range Read (bytes 10 to 19: "0123456789")
        using (var rangeObj = await _Backend.GetObjectAsync("files", "data/test.bin", 10, 19))
        {
            rangeObj.ContentLength.Should().Be(10);
            rangeObj.TotalSize.Should().Be(original.Length);
            using var ms = new MemoryStream();
            await rangeObj.Stream.CopyToAsync(ms);
            string rangeText = Encoding.UTF8.GetString(ms.ToArray());
            rangeText.Should().Be("0123456789");
        }

        // DeleteObject
        await _Backend.DeleteObjectAsync("files", "data/test.bin");
        File.Exists(expectedFilePath).Should().BeFalse();
    }

    [TestMethod]
    public async Task Can_Perform_Multipart_Upload_On_Disk()
    {
        await _Backend.CreateBucketAsync("mp-disk", null);

        var init = await _Backend.CreateMultipartUploadAsync("mp-disk", "archive.iso");
        init.UploadId.Should().NotBeNull();

        byte[] p1 = Encoding.UTF8.GetBytes("CHUNK_ONE_");
        using var s1 = new MemoryStream(p1);
        string etag1 = await _Backend.UploadPartAsync("mp-disk", "archive.iso", init.UploadId, 1, s1, p1.Length);

        byte[] p2 = Encoding.UTF8.GetBytes("CHUNK_TWO!");
        using var s2 = new MemoryStream(p2);
        string etag2 = await _Backend.UploadPartAsync("mp-disk", "archive.iso", init.UploadId, 2, s2, p2.Length);

        var complete = await _Backend.CompleteMultipartUploadAsync("mp-disk", "archive.iso", init.UploadId, new CompleteMultipartUpload());
        complete.ETag.Should().NotBeNull();

        using var finalObj = await _Backend.GetObjectAsync("mp-disk", "archive.iso");
        finalObj.ContentLength.Should().Be(p1.Length + p2.Length);
        using var ms = new MemoryStream();
        await finalObj.Stream.CopyToAsync(ms);
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be("CHUNK_ONE_CHUNK_TWO!");
    }

    [TestMethod]
    public async Task Can_Store_And_Retrieve_User_Metadata_And_Tags()
    {
        const string bucket = "meta-bucket";
        const string key = "docs/sample.txt";
        await _Backend.CreateBucketAsync(bucket, null);

        var userMeta = new Dictionary<string, string>
        {
            { "Department", "Engineering" },
            { "Sensitivity", "Confidential" }
        };

        byte[] data = Encoding.UTF8.GetBytes("Confidential document body");
        using var stream = new MemoryStream(data);
        await _Backend.PutObjectAsync(bucket, key, stream, data.Length, "text/plain", userMeta);

        // Verify sidecar exists
        string metaPath = Path.Combine(_TempDir, bucket, "docs", "sample.txt.s3meta.xml");
        File.Exists(metaPath).Should().BeTrue();

        // HeadObject returns user metadata
        var md = await _Backend.GetObjectMetadataAsync(bucket, key);
        md.Should().NotBeNull();
        md!.Size.Should().Be(data.Length);
        md.ContentType.Should().Be("text/plain");
        md.Metadata.ContainsKey("Department").Should().BeTrue();
        md.Metadata["Department"].Should().Be("Engineering");

        // Object Tags
        var tags = new Tagging(new TagSet(new List<Tag>
        {
            new Tag("env", "production"),
            new Tag("cost-center", "1234")
        }));
        await _Backend.SetObjectTagsAsync(bucket, key, tags);

        var retrievedTags = await _Backend.GetObjectTagsAsync(bucket, key);
        retrievedTags.Tags.Should().NotBeNull();
        retrievedTags.Tags!.Tags.Count.Should().Be(2);
        retrievedTags.Tags.Tags.Should().Contain(t => t.Key == "env" && t.Value == "production");

        // Legal Hold
        await _Backend.SetLegalHoldAsync(bucket, key, new LegalHold("ON"));
        var hold = await _Backend.GetLegalHoldAsync(bucket, key);
        hold.Status.Should().Be("ON");
    }

    [TestMethod]
    public async Task Can_List_Objects_With_Prefix_And_Delimiter()
    {
        const string bucket = "hierarchy-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        byte[] dummy = Encoding.UTF8.GetBytes("x");
        await _Backend.PutObjectAsync(bucket, "photos/2026/summer/pic1.jpg", new MemoryStream(dummy), dummy.Length, "image/jpeg");
        await _Backend.PutObjectAsync(bucket, "photos/2026/winter/pic2.jpg", new MemoryStream(dummy), dummy.Length, "image/jpeg");
        await _Backend.PutObjectAsync(bucket, "photos/2025/vacation.jpg", new MemoryStream(dummy), dummy.Length, "image/jpeg");
        await _Backend.PutObjectAsync(bucket, "documents/report.pdf", new MemoryStream(dummy), dummy.Length, "application/pdf");

        // List with delimiter "/" and prefix "photos/2026/"
        var query = new Dictionary<string, string>
        {
            { "list-type", "2" },
            { "prefix", "photos/2026/" },
            { "delimiter", "/" }
        };
        var req = new S3Request("GET", "/" + bucket, query, new Dictionary<string, string[]>(), Stream.Null);

        var listResult = await _Backend.ListObjectsAsync(bucket, req);

        listResult.CommonPrefixes.Count.Should().Be(2);
        listResult.CommonPrefixes.Should().Contain(cp => cp.Prefix == "photos/2026/summer/");
        listResult.CommonPrefixes.Should().Contain(cp => cp.Prefix == "photos/2026/winter/");
        listResult.Contents.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Can_Copy_Object_On_Disk()
    {
        const string bucket = "copy-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        byte[] srcData = Encoding.UTF8.GetBytes("Source file content for copy test");
        await _Backend.PutObjectAsync(bucket, "source.txt", new MemoryStream(srcData), srcData.Length, "text/plain");

        var copyRes = await _Backend.CopyObjectAsync(bucket, "source.txt", bucket, "backup/source_copy.txt");
        copyRes.ETag.Should().NotBeNull();

        // Verify copy content
        using var copyObj = await _Backend.GetObjectAsync(bucket, "backup/source_copy.txt");
        copyObj.ContentLength.Should().Be(srcData.Length);
        using var ms = new MemoryStream();
        await copyObj.Stream.CopyToAsync(ms);
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be("Source file content for copy test");
    }

    [TestMethod]
    public async Task Path_Traversal_Protection_Rejects_Escape()
    {
        const string bucket = "traversal-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        // Attempting to read outside the bucket using "../../"
        var act = async () => await _Backend.GetObjectAsync(bucket, "../../windows/system32/cmd.exe");
        await act.Should().ThrowAsync<S3Exception>();
    }

    [TestMethod]
    public async Task Can_Create_And_Manage_Subfolder_On_Disk()
    {
        const string bucket = "subfolder-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        // Put folder marker "subfol/"
        string etag = await _Backend.PutObjectAsync(bucket, "subfol/", Stream.Null, 0, "application/x-directory");
        etag.Should().NotBeNull();
        Directory.Exists(Path.Combine(_TempDir, bucket, "subfol")).Should().BeTrue();

        // Metadata check
        var meta = await _Backend.GetObjectMetadataAsync(bucket, "subfol/");
        meta.Should().NotBeNull();
        meta!.Size.Should().Be(0);
        meta.ContentType.Should().Be("application/x-directory");

        // Get object
        using var obj = await _Backend.GetObjectAsync(bucket, "subfol/");
        obj.ContentLength.Should().Be(0);

        // List objects with delimiter (listing root shows folder in CommonPrefixes)
        var listReq = new S3Request("GET", "/subfolder-bucket", new Dictionary<string, string> { { "delimiter", "/" } }, new Dictionary<string, string[]>(), Stream.Null, new SharpS3Settings());
        var listRes = await _Backend.ListObjectsAsync(bucket, listReq);
        listRes.CommonPrefixes.Should().Contain(cp => cp.Prefix == "subfol/");

        // WinSCP existence check on empty folder: GET /?prefix=subfol/&delimiter=/&max-keys=1
        var existCheckReq = new S3Request("GET", "/subfolder-bucket", new Dictionary<string, string> { { "prefix", "subfol/" }, { "delimiter", "/" }, { "max-keys", "1" } }, new Dictionary<string, string[]>(), Stream.Null, new SharpS3Settings());
        var existCheckRes = await _Backend.ListObjectsAsync(bucket, existCheckReq);
        existCheckRes.Contents.Should().ContainSingle();
        existCheckRes.Contents[0].Key.Should().Be("subfol/");

        // WinSCP browse empty folder: GET /?prefix=subfol/&delimiter=/
        var browseReq = new S3Request("GET", "/subfolder-bucket", new Dictionary<string, string> { { "prefix", "subfol/" }, { "delimiter", "/" } }, new Dictionary<string, string[]>(), Stream.Null, new SharpS3Settings());
        var browseRes = await _Backend.ListObjectsAsync(bucket, browseReq);
        browseRes.Contents.Should().ContainSingle();
        browseRes.Contents[0].Key.Should().Be("subfol/");

        // Put a file inside the subfolder
        byte[] fileData = Encoding.UTF8.GetBytes("File inside subfol");
        await _Backend.PutObjectAsync(bucket, "subfol/test.txt", new MemoryStream(fileData), fileData.Length, "text/plain");

        File.Exists(Path.Combine(_TempDir, bucket, "subfol", "test.txt")).Should().BeTrue();

        // Delete file and folder
        await _Backend.DeleteObjectAsync(bucket, "subfol/test.txt");
        await _Backend.DeleteObjectAsync(bucket, "subfol/");
    }

    [TestMethod]
    public async Task Can_Store_And_Retrieve_Bucket_Metadata_Sidecar_On_Disk()
    {
        const string bucket = "meta-disk-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        // Put and get ACL
        var acl = new AccessControlPolicy
        {
            Owner = new Owner { ID = "test-owner", DisplayName = "Test Owner" },
            Acl = new AccessControlList
            {
                Grants = new List<Grant>
                {
                    new Grant { Permission = PermissionEnum.Read, Grantee = new Grantee { ID = "test-user" } }
                }
            }
        };
        await _Backend.SetBucketAclAsync(bucket, acl);
        var readAcl = await _Backend.GetBucketAclAsync(bucket);
        readAcl.Owner.ID.Should().Be("test-owner");

        // Put and get Tags
        var tags = new Tagging
        {
            Tags = new TagSet
            {
                Tags = new List<Tag> { new Tag("env", "prod") }
            }
        };
        await _Backend.SetBucketTagsAsync(bucket, tags);
        var readTags = await _Backend.GetBucketTagsAsync(bucket);
        readTags.Tags.Tags.Should().Contain(t => t.Key == "env" && t.Value == "prod");

        // Put and get Versioning
        var ver = new VersioningConfiguration { Status = VersioningStatusEnum.Suspended };
        await _Backend.SetBucketVersioningAsync(bucket, ver);
        var readVer = await _Backend.GetBucketVersioningAsync(bucket);
        readVer.Status.Should().Be(VersioningStatusEnum.Suspended);

        // Put and get Website
        var web = new WebsiteConfiguration
        {
            IndexDocument = new IndexDocument { Suffix = "index.htm" }
        };
        await _Backend.SetBucketWebsiteAsync(bucket, web);
        var readWeb = await _Backend.GetBucketWebsiteAsync(bucket);
        readWeb.IndexDocument.Suffix.Should().Be("index.htm");

        // Put and get Logging
        var log = new BucketLoggingStatus
        {
            Enabled = new LoggingEnabled { TargetBucket = "target", TargetPrefix = "pfx/" }
        };
        await _Backend.SetBucketLoggingAsync(bucket, log);
        var readLog = await _Backend.GetBucketLoggingAsync(bucket);
        readLog.Enabled.TargetBucket.Should().Be("target");

        // Verify sidecar file does NOT appear in object listing
        var listReq = new S3Request("GET", "/meta-disk-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, new SharpS3Settings());
        var listRes = await _Backend.ListObjectsAsync(bucket, listReq);
        listRes.Contents.Should().BeEmpty();

        // Delete Website & Tags
        await _Backend.DeleteBucketWebsiteAsync(bucket);
        var actWeb = async () => await _Backend.GetBucketWebsiteAsync(bucket);
        await actWeb.Should().ThrowAsync<S3Exception>();

        await _Backend.DeleteBucketTagsAsync(bucket);
        var actTag = async () => await _Backend.GetBucketTagsAsync(bucket);
        await actTag.Should().ThrowAsync<S3Exception>();
    }

    [TestMethod]
    public async Task Can_Perform_UploadPartCopy_On_Disk()
    {
        const string bucket = "partcopy-disk-bucket";
        await _Backend.CreateBucketAsync(bucket, null);

        // Put source object
        byte[] srcData = Encoding.UTF8.GetBytes("Part Copy On Disk Testing Payload");
        await _Backend.PutObjectAsync(bucket, "source.bin", new MemoryStream(srcData), srcData.Length, "application/octet-stream");

        // Initiate multipart upload
        var initResult = await _Backend.CreateMultipartUploadAsync(bucket, "target.bin");
        string uploadId = initResult.UploadId;

        // UploadPartCopy
        var copyRes = await _Backend.UploadPartCopyAsync(bucket, "source.bin", bucket, "target.bin", uploadId, 1);
        copyRes.ETag.Should().NotBeNullOrWhiteSpace();

        // Complete multipart upload
        var comp = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = copyRes.ETag }
            }
        };
        await _Backend.CompleteMultipartUploadAsync(bucket, "target.bin", uploadId, comp);

        // Read destination object
        using var destObj = await _Backend.GetObjectAsync(bucket, "target.bin");
        destObj.ContentLength.Should().Be(srcData.Length);
        using var ms = new MemoryStream();
        await destObj.Stream.CopyToAsync(ms);
        ms.ToArray().Should().Equal(srcData);
    }
}
