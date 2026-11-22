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

[TestClass]
public partial class FileSystemStorageBackendTests
{
    private string _tempDir = null!;
    private FileSystemStorageBackend _backend = null!;
    private string _TempDir => _tempDir;
    private FileSystemStorageBackend _Backend => _backend;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SharpS3_FSTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _backend = new FileSystemStorageBackend(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup failures on temp dirs
        }
    }

    [TestMethod]
    public void Constructor_NullDirectory_ThrowsArgumentNullException()
    {
        Action act1 = () => new FileSystemStorageBackend(null!);
        act1.Should().Throw<ArgumentNullException>();

        Action act2 = () => new FileSystemStorageBackend("   ");
        act2.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public async Task Bucket_Operations_Full_Lifecycle()
    {
        // 1. BucketExists initially false
        (await _backend.BucketExistsAsync("my-bucket")).Should().BeFalse();

        // 2. CreateBucketAsync
        await _backend.CreateBucketAsync("my-bucket", new CreateBucketConfiguration("us-west-2"));
        (await _backend.BucketExistsAsync("my-bucket")).Should().BeTrue();

        // 3. ListBucketsAsync
        var dummyReq = new S3Request("GET", "/", null!, null!, Stream.Null);
        var buckets = await _backend.ListBucketsAsync(dummyReq);
        buckets.Buckets.BucketList.Should().Contain(b => b.Name == "my-bucket");

        // 4. GetBucketLocationAsync
        var loc = await _backend.GetBucketLocationAsync("my-bucket");
        loc.Should().NotBeNull();

        // 5. Bucket ACL
        var acl = await _backend.GetBucketAclAsync("my-bucket");
        acl.Should().NotBeNull();
        await _backend.SetBucketAclAsync("my-bucket", new AccessControlPolicy());
        await _backend.DeleteBucketAclAsync("my-bucket");

        // 6. Bucket Tags
        var actNoTags = async () => await _backend.GetBucketTagsAsync("my-bucket");
        (await actNoTags.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchTagSet);

        var newTags = new Tagging(new TagSet(new List<Tag> { new Tag("k1", "v1") }));
        await _backend.SetBucketTagsAsync("my-bucket", newTags);
        var retrievedTags = await _backend.GetBucketTagsAsync("my-bucket");
        retrievedTags.Tags.Tags.Should().Contain(t => t.Key == "k1" && t.Value == "v1");
        await _backend.DeleteBucketTagsAsync("my-bucket");

        // 7. Bucket Versioning
        var ver = await _backend.GetBucketVersioningAsync("my-bucket");
        ver.Should().NotBeNull();
        await _backend.SetBucketVersioningAsync("my-bucket", new VersioningConfiguration(VersioningStatusEnum.Enabled, MfaDeleteStatusEnum.Disabled));
        var verEnabled = await _backend.GetBucketVersioningAsync("my-bucket");
        verEnabled.Status.Should().Be(VersioningStatusEnum.Enabled);

        // 8. Bucket Website
        var actNoWeb = async () => await _backend.GetBucketWebsiteAsync("my-bucket");
        (await actNoWeb.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchWebsiteConfiguration);

        var webConfig = new WebsiteConfiguration(new RedirectAllRequestsTo("example.com", ProtocolEnum.Https), new IndexDocument("index.html"), new ErrorDocument("error.html"), new RoutingRules());
        await _backend.SetBucketWebsiteAsync("my-bucket", webConfig);
        var retrievedWeb = await _backend.GetBucketWebsiteAsync("my-bucket");
        retrievedWeb.IndexDocument.Suffix.Should().Be("index.html");
        await _backend.DeleteBucketWebsiteAsync("my-bucket");

        // 9. Bucket Logging
        var logging = await _backend.GetBucketLoggingAsync("my-bucket");
        logging.Should().NotBeNull();
        await _backend.SetBucketLoggingAsync("my-bucket", new BucketLoggingStatus(new LoggingEnabled("logs", "access/", new TargetGrants())));

        // 10. DeleteBucketAsync non-existent bucket throws NoSuchBucket
        var actDelMissing = async () => await _backend.DeleteBucketAsync("ghost-bucket");
        (await actDelMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        // 11. DeleteBucketAsync on empty bucket succeeds
        await _backend.DeleteBucketAsync("my-bucket");
        (await _backend.BucketExistsAsync("my-bucket")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Object_CRUD_Range_And_Copy_Operations()
    {
        await _backend.CreateBucketAsync("files", null);

        // GetObject on non-existent object throws NoSuchKey
        var actMissing = async () => await _backend.GetObjectAsync("files", "ghost.txt");
        (await actMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await _backend.GetObjectMetadataAsync("files", "ghost.txt")).Should().BeNull();

        // PutObjectAsync with metadata
        byte[] data = Encoding.UTF8.GetBytes("0123456789abcdefghij");
        using var putStream = new MemoryStream(data);
        var metadata = new Dictionary<string, string> { ["author"] = "tester" };
        string etag = await _backend.PutObjectAsync("files", "sample.txt", putStream, data.Length, "text/plain", metadata);
        etag.Should().NotBeNullOrEmpty();

        // GetObjectMetadataAsync
        var meta = await _backend.GetObjectMetadataAsync("files", "sample.txt");
        meta.Should().NotBeNull();
        meta!.Size.Should().Be(data.Length);
        meta.ContentType.Should().Be("text/plain");

        // GetObjectAsync full
        using (var obj = await _backend.GetObjectAsync("files", "sample.txt"))
        {
            obj.ContentLength.Should().Be(data.Length);
            using var ms = new MemoryStream();
            await obj.Stream.CopyToAsync(ms);
            ms.ToArray().Should().BeEquivalentTo(data);
        }

        // GetObjectAsync range
        using (var rangeObj = await _backend.GetObjectAsync("files", "sample.txt", rangeStart: 2, rangeEnd: 5))
        {
            rangeObj.ContentLength.Should().Be(4);
            using var rangeMs = new MemoryStream();
            await rangeObj.Stream.CopyToAsync(rangeMs);
            Encoding.UTF8.GetString(rangeMs.ToArray()).Should().Be("2345");
        }

        // CopyObjectAsync
        await _backend.CreateBucketAsync("dest-files", null);
        var copyResult = await _backend.CopyObjectAsync("files", "sample.txt", "dest-files", "copied.txt");
        copyResult.ETag.Should().NotBeNullOrEmpty();

        var copiedMeta = await _backend.GetObjectMetadataAsync("dest-files", "copied.txt");
        copiedMeta.Should().NotBeNull();
        copiedMeta!.Size.Should().Be(data.Length);

        // DeleteMultipleObjectsAsync
        var deleteMultiple = new DeleteMultiple(false, new List<SharpS3.Models.Object> { new SharpS3.Models.Object("sample.txt", "v1") });
        var deleteResult = await _backend.DeleteMultipleObjectsAsync("files", deleteMultiple);
        deleteResult.DeletedObjects.Count.Should().Be(1);

        // DeleteObjectAsync
        await _backend.DeleteObjectAsync("dest-files", "copied.txt");
        (await _backend.GetObjectMetadataAsync("dest-files", "copied.txt")).Should().BeNull();
    }

    [TestMethod]
    public async Task Object_Tags_Acl_LegalHold_Retention_Restore()
    {
        await _backend.CreateBucketAsync("obj-props", null);
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        await _backend.PutObjectAsync("obj-props", "item.bin", ms, 3, "application/octet-stream");

        // Tags
        var tags = await _backend.GetObjectTagsAsync("obj-props", "item.bin");
        tags.Should().NotBeNull();
        var newTags = new Tagging(new TagSet(new List<Tag> { new Tag("env", "staging") }));
        await _backend.SetObjectTagsAsync("obj-props", "item.bin", newTags);
        (await _backend.GetObjectTagsAsync("obj-props", "item.bin")).Tags.Tags.Should().Contain(t => t.Key == "env");
        await _backend.DeleteObjectTagsAsync("obj-props", "item.bin");

        // ACL
        var acl = await _backend.GetObjectAclAsync("obj-props", "item.bin");
        acl.Should().NotBeNull();
        await _backend.SetObjectAclAsync("obj-props", "item.bin", new AccessControlPolicy());
        await _backend.DeleteObjectAclAsync("obj-props", "item.bin");

        // LegalHold
        var lh = await _backend.GetLegalHoldAsync("obj-props", "item.bin");
        lh.Status.Should().Be("OFF");
        await _backend.SetLegalHoldAsync("obj-props", "item.bin", new LegalHold { Status = "ON" });
        (await _backend.GetLegalHoldAsync("obj-props", "item.bin")).Status.Should().Be("ON");

        // Retention
        var ret = await _backend.GetRetentionAsync("obj-props", "item.bin");
        ret.Should().NotBeNull();
        await _backend.SetRetentionAsync("obj-props", "item.bin", new Retention(RetentionModeEnum.Compliance, DateTime.UtcNow.AddDays(30)));
        var updatedRet = await _backend.GetRetentionAsync("obj-props", "item.bin");
        updatedRet.Mode.Should().Be(RetentionModeEnum.Compliance);

        // Restore
        var restoreResult = await _backend.RestoreObjectAsync("obj-props", "item.bin", new RestoreRequest { Days = 5, Tier = RestoreTierEnum.Expedited });
        restoreResult.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Multipart_Upload_Workflow()
    {
        await _backend.CreateBucketAsync("multipart-bucket", null);

        // 1. Create upload
        var init = await _backend.CreateMultipartUploadAsync("multipart-bucket", "large.bin");
        init.UploadId.Should().NotBeNullOrEmpty();

        // 2. UploadPartAsync
        byte[] p1Bytes = Encoding.UTF8.GetBytes("Part 1 content data");
        using var p1Stream = new MemoryStream(p1Bytes);
        string part1Etag = await _backend.UploadPartAsync("multipart-bucket", "large.bin", init.UploadId, 1, p1Stream, p1Bytes.Length);
        part1Etag.Should().NotBeNullOrEmpty();

        // 3. UploadPartCopyAsync from another existing object
        using var srcStream = new MemoryStream(Encoding.UTF8.GetBytes("Part 2 copied from src"));
        await _backend.PutObjectAsync("multipart-bucket", "source.bin", srcStream, 22, "text/plain");

        var copyPartResult = await _backend.UploadPartCopyAsync(
            "multipart-bucket", "source.bin",
            "multipart-bucket", "large.bin",
            init.UploadId, 2, rangeStart: 0, rangeEnd: 10);
        copyPartResult.ETag.Should().NotBeNullOrEmpty();

        // 4. ListPartsAsync
        var parts = await _backend.ListPartsAsync("multipart-bucket", "large.bin", init.UploadId);
        parts.Parts.Count.Should().Be(2);

        // 5. ListMultipartUploadsAsync
        var uploads = await _backend.ListMultipartUploadsAsync("multipart-bucket");
        uploads.Uploads.Should().Contain(u => u.UploadId == init.UploadId);

        // 6. CompleteMultipartUploadAsync
        var complete = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = part1Etag },
                new Part { PartNumber = 2, ETag = copyPartResult.ETag }
            }
        };
        var completeResult = await _backend.CompleteMultipartUploadAsync("multipart-bucket", "large.bin", init.UploadId, complete);
        completeResult.ETag.Should().NotBeNullOrEmpty();

        // Verify the completed object exists
        var meta = await _backend.GetObjectMetadataAsync("multipart-bucket", "large.bin");
        meta.Should().NotBeNull();

        // 7. Abort upload on new upload
        var init2 = await _backend.CreateMultipartUploadAsync("multipart-bucket", "abandon.bin");
        await _backend.AbortMultipartUploadAsync("multipart-bucket", "abandon.bin", init2.UploadId);
    }

    [TestMethod]
    public async Task ListObjects_Pagination_Prefix_And_Versions()
    {
        await _backend.CreateBucketAsync("list-bucket", null);
        for (int i = 0; i < 5; i++)
        {
            using var data = new MemoryStream(new byte[] { (byte)i });
            await _backend.PutObjectAsync("list-bucket", $"prefix/file{i}.txt", data, 1, "text/plain");
        }
        using var rootData = new MemoryStream(new byte[] { 99 });
        await _backend.PutObjectAsync("list-bucket", "rootfile.txt", rootData, 1, "text/plain");

        // ListObjects with prefix and delimiter
        var qParams = new Dictionary<string, string> { ["prefix"] = "prefix/", ["delimiter"] = "/" };
        var req = new S3Request("GET", "/list-bucket", qParams, new Dictionary<string, string[]>(), Stream.Null);
        var listResult = await _backend.ListObjectsAsync("list-bucket", req);
        listResult.Contents.Count.Should().Be(5);

        // ListObjectVersionsAsync
        var versions = await _backend.ListObjectVersionsAsync("list-bucket");
        versions.Versions.Count.Should().BeGreaterThanOrEqualTo(6);
    }

    [TestMethod]
    public void IsEmptyDirectory_ChecksCorrectly_AndHandlesExceptions()
    {
        // 1. Non-existent directory returns false (caught exception)
        string nonExistent = Path.Combine(_tempDir, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        FileSystemStorageBackend.IsEmptyDirectory(nonExistent).Should().BeFalse();

        // 2. Empty directory returns true
        string emptyDir = Path.Combine(_tempDir, "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        FileSystemStorageBackend.IsEmptyDirectory(emptyDir).Should().BeTrue();

        // 3. Directory with only .s3meta.xml returns true
        string metaOnlyDir = Path.Combine(_tempDir, "meta-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metaOnlyDir);
        File.WriteAllText(Path.Combine(metaOnlyDir, "test.s3meta.xml"), "<meta/>");
        FileSystemStorageBackend.IsEmptyDirectory(metaOnlyDir).Should().BeTrue();

        // 4. Directory with only .s3bucket.xml returns true
        string bucketMetaOnlyDir = Path.Combine(_tempDir, "bucket-meta-only-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bucketMetaOnlyDir);
        File.WriteAllText(Path.Combine(bucketMetaOnlyDir, ".s3bucket.xml"), "<meta/>");
        FileSystemStorageBackend.IsEmptyDirectory(bucketMetaOnlyDir).Should().BeTrue();

        // 5. Directory with standard file returns false
        string nonEmptydir = Path.Combine(_tempDir, "nonempty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nonEmptydir);
        File.WriteAllText(Path.Combine(nonEmptydir, "file.txt"), "content");
        FileSystemStorageBackend.IsEmptyDirectory(nonEmptydir).Should().BeFalse();
    }

    [TestMethod]
    public async Task WriteBucketMeta_WritesCorrectly_AndHandlesExceptionsGracefully()
    {
        string bucketDir = Path.Combine(_tempDir, "test-write-bmeta");
        Directory.CreateDirectory(bucketDir);

        // 1. Successful write and read
        var meta = new FileSystemStorageBackend.BucketMetadata
        {
            Tags = new Tagging(new TagSet(new List<Tag> { new Tag("k", "v") }))
        };
        _backend.WriteBucketMeta(bucketDir, meta);
        var read = _backend.ReadBucketMeta(bucketDir);
        read.Tags.Should().NotBeNull();
        read.Tags!.Tags.Tags.Should().Contain(t => t.Key == "k" && t.Value == "v");

        // 2. Exception handling when .s3bucket.xml is a directory
        string conflictBucket = Path.Combine(_tempDir, "conflict-bmeta");
        Directory.CreateDirectory(conflictBucket);
        Directory.CreateDirectory(Path.Combine(conflictBucket, ".s3bucket.xml"));

        Action actDirect = () => _backend.WriteBucketMeta(conflictBucket, meta);
        actDirect.Should().NotThrow();

        // 3. Public API SetBucketAclAsync triggers WriteBucketMeta with exception handling
        await _backend.CreateBucketAsync("acl-err-bucket", null);
        string bPath = Path.Combine(_tempDir, "acl-err-bucket");
        File.Delete(Path.Combine(bPath, ".s3bucket.xml"));
        Directory.CreateDirectory(Path.Combine(bPath, ".s3bucket.xml"));

        var actApi = async () => await _backend.SetBucketAclAsync("acl-err-bucket", new AccessControlPolicy());
        await actApi.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task WriteMeta_WritesCorrectly_AndHandlesExceptionsGracefully()
    {
        string objPath = Path.Combine(_tempDir, "test-write-ometa");

        // 1. Successful write and read
        var meta = new FileSystemStorageBackend.FileMetadata
        {
            ContentType = "text/plain",
            ETag = "\"12345\""
        };
        _backend.WriteMeta(objPath, meta);
        var read = _backend.ReadMeta(objPath);
        read.Should().NotBeNull();
        read!.ContentType.Should().Be("text/plain");
        read.ETag.Should().Be("\"12345\"");

        // 2. Exception handling when metaPath (.s3meta.xml) is a directory
        string conflictObj = Path.Combine(_tempDir, "conflict-ometa");
        Directory.CreateDirectory(conflictObj + ".s3meta.xml");

        Action actDirect = () => _backend.WriteMeta(conflictObj, meta);
        actDirect.Should().NotThrow();

        // 3. Public API SetObjectTagsAsync triggers WriteMeta with exception handling
        await _backend.CreateBucketAsync("obj-err-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        await _backend.PutObjectAsync("obj-err-bucket", "item.txt", stream, 5, "text/plain");

        string metaFilePath = Path.Combine(_tempDir, "obj-err-bucket", "item.txt.s3meta.xml");
        File.Delete(metaFilePath);
        Directory.CreateDirectory(metaFilePath);

        var actApi = async () => await _backend.SetObjectTagsAsync(
            "obj-err-bucket",
            "item.txt",
            new Tagging(new TagSet(new List<Tag> { new Tag("env", "test") })));
        await actApi.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task DeleteObjectAsync_PruneEmptyParentDirectories_WhenDirectoryDeleteThrowsAndDirectoryStillExists_HandlesExceptionAndBreaks()
    {
        await _backend.CreateBucketAsync("prune-err-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _backend.PutObjectAsync("prune-err-bucket", "dir1/dir2/file.txt", stream, 7, "text/plain");

        string dir2Path = Path.Combine(_tempDir, "prune-err-bucket", "dir1", "dir2");
        string filePath = Path.Combine(dir2Path, "file.txt");
        File.Exists(filePath).Should().BeTrue();

        _backend.DirectoryDeleter = dir => throw new IOException("Locked directory or access denied");
        try
        {
            var act = async () => await _backend.DeleteObjectAsync("prune-err-bucket", "dir1/dir2/file.txt");
            await act.Should().NotThrowAsync();

            File.Exists(filePath).Should().BeFalse();
            Directory.Exists(dir2Path).Should().BeTrue();
            Directory.Exists(Path.Combine(_tempDir, "prune-err-bucket", "dir1")).Should().BeTrue();
        }
        finally
        {
            _backend.DirectoryDeleter = null;
        }
    }

    [TestMethod]
    public async Task DeleteObjectAsync_PruneEmptyParentDirectories_WhenDirectoryDeleteThrowsAndDirectoryNoLongerExists_ContinuesPruningParent()
    {
        await _backend.CreateBucketAsync("prune-deleted-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _backend.PutObjectAsync("prune-deleted-bucket", "p1/p2/file.txt", stream, 7, "text/plain");

        string p2Path = Path.Combine(_tempDir, "prune-deleted-bucket", "p1", "p2");
        string p1Path = Path.Combine(_tempDir, "prune-deleted-bucket", "p1");

        // Simulate concurrent deletion where p2 was deleted but delegate throws DirectoryNotFoundException
        bool delegateCalled = false;
        _backend.DirectoryDeleter = dir =>
        {
            if (dir == p2Path && !delegateCalled)
            {
                delegateCalled = true;
                Directory.Delete(dir);
                throw new DirectoryNotFoundException("Simulated race condition");
            }
            Directory.Delete(dir);
        };

        try
        {
            var act = async () => await _backend.DeleteObjectAsync("prune-deleted-bucket", "p1/p2/file.txt");
            await act.Should().NotThrowAsync();

            Directory.Exists(p2Path).Should().BeFalse();
            Directory.Exists(p1Path).Should().BeFalse();
        }
        finally
        {
            _backend.DirectoryDeleter = null;
        }
    }

    [TestMethod]
    public async Task DeleteObjectAsync_PruneEmptyParentDirectories_OnReadOnlyDirectory_HandlesExceptionGracefully()
    {
        if (!OperatingSystem.IsWindows()) return;

        await _backend.CreateBucketAsync("ro-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        await _backend.PutObjectAsync("ro-bucket", "ro-parent/ro-child/file.txt", stream, 7, "text/plain");

        string childDir = Path.Combine(_tempDir, "ro-bucket", "ro-parent", "ro-child");
        File.SetAttributes(childDir, FileAttributes.ReadOnly);
        try
        {
            var act = async () => await _backend.DeleteObjectAsync("ro-bucket", "ro-parent/ro-child/file.txt");
            await act.Should().NotThrowAsync();

            Directory.Exists(childDir).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(childDir, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task PruneEmptyParentDirectories_DirectCall_PrunesEmptyHierarchySuccessfully()
    {
        await _backend.CreateBucketAsync("direct-bucket", null);
        string nestedDir = Path.Combine(_tempDir, "direct-bucket", "a", "b", "c");
        Directory.CreateDirectory(nestedDir);

        string objPath = Path.Combine(nestedDir, "obj.txt");
        _backend.PruneEmptyParentDirectories("direct-bucket", objPath, "a/b/c/obj.txt");

        Directory.Exists(Path.Combine(_tempDir, "direct-bucket", "a")).Should().BeFalse();
        Directory.Exists(Path.Combine(_tempDir, "direct-bucket")).Should().BeTrue();
    }

    [TestMethod]
    public async Task DeleteObjectAsync_DirectoryKey_WhenDirectoryDeleteThrows_HandlesExceptionGracefully()
    {
        await _backend.CreateBucketAsync("dirkey-err-bucket", null);
        string folderPath = Path.Combine(_tempDir, "dirkey-err-bucket", "folder");
        Directory.CreateDirectory(folderPath);

        using var stream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("dirkey-err-bucket", "folder/", stream, 0, "application/x-directory");

        _backend.DirectoryDeleter = dir => throw new IOException("Locked directory or access denied");
        try
        {
            var act = async () => await _backend.DeleteObjectAsync("dirkey-err-bucket", "folder/");
            await act.Should().NotThrowAsync();

            Directory.Exists(folderPath).Should().BeTrue();
        }
        finally
        {
            _backend.DirectoryDeleter = null;
        }
    }

    [TestMethod]
    public async Task DeleteObjectAsync_DirectoryKey_OnReadOnlyDirectory_HandlesExceptionGracefully()
    {
        if (!OperatingSystem.IsWindows()) return;

        await _backend.CreateBucketAsync("dirkey-ro-bucket", null);
        string folderPath = Path.Combine(_tempDir, "dirkey-ro-bucket", "ro-folder");
        Directory.CreateDirectory(folderPath);

        using var stream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("dirkey-ro-bucket", "ro-folder/", stream, 0, "application/x-directory");

        File.SetAttributes(folderPath, FileAttributes.ReadOnly);
        try
        {
            var act = async () => await _backend.DeleteObjectAsync("dirkey-ro-bucket", "ro-folder/");
            await act.Should().NotThrowAsync();

            Directory.Exists(folderPath).Should().BeTrue();
        }
        finally
        {
            File.SetAttributes(folderPath, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public async Task Bucket_NonExistent_ThrowsNoSuchBucket_AllOperations()
    {
        string nonBucket = "non-existent-bucket-" + Guid.NewGuid().ToString("N");
        var req = new S3Request("GET", "/", null!, null!, Stream.Null);

        (await new Func<Task>(() => _backend.DeleteBucketAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketLocationAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.ListObjectsAsync(nonBucket, req)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.ListObjectVersionsAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketAclAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.SetBucketAclAsync(nonBucket, new AccessControlPolicy())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.DeleteBucketAclAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketTagsAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.SetBucketTagsAsync(nonBucket, new Tagging())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.DeleteBucketTagsAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketVersioningAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.SetBucketVersioningAsync(nonBucket, new VersioningConfiguration())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketWebsiteAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.SetBucketWebsiteAsync(nonBucket, new WebsiteConfiguration())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.DeleteBucketWebsiteAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.GetBucketLoggingAsync(nonBucket)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        (await new Func<Task>(() => _backend.SetBucketLoggingAsync(nonBucket, new BucketLoggingStatus())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);
    }

    [TestMethod]
    public async Task Object_NonExistent_ThrowsNoSuchKey_AllOperations()
    {
        await _backend.CreateBucketAsync("err-bucket", null);
        string missingKey = "missing-key.txt";

        (await new Func<Task>(() => _backend.GetObjectAsync("err-bucket", "missing-dir/")).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.CopyObjectAsync("err-bucket", missingKey, "err-bucket", "dest.txt")).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.GetObjectTagsAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.SetObjectTagsAsync("err-bucket", missingKey, new Tagging())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.DeleteObjectTagsAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.GetLegalHoldAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.SetLegalHoldAsync("err-bucket", missingKey, new LegalHold("ON"))).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.GetRetentionAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.SetRetentionAsync("err-bucket", missingKey, new Retention())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.GetObjectAclAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.SetObjectAclAsync("err-bucket", missingKey, new AccessControlPolicy())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.DeleteObjectAclAsync("err-bucket", missingKey)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.RestoreObjectAsync("err-bucket", missingKey, new RestoreRequest())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await new Func<Task>(() => _backend.UploadPartCopyAsync("err-bucket", missingKey, "err-bucket", "dest.txt", "dummyUploadId", 1)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);
    }

    [TestMethod]
    public async Task Range_And_Copy_EdgeCases()
    {
        await _backend.CreateBucketAsync("range-bucket", null);
        byte[] bytes = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        using var ms = new MemoryStream(bytes);
        await _backend.PutObjectAsync("range-bucket", "test.bin", ms, bytes.Length, "application/octet-stream");

        // 1. GetObjectAsync with only rangeStart (rangeEnd = null)
        var p1 = await _backend.GetObjectAsync("range-bucket", "test.bin", rangeStart: 10, rangeEnd: null);
        using (p1.Stream)
        {
            p1.ContentLength.Should().Be(6); // 16 - 10 = 6 bytes
            p1.RangeStart.Should().Be(10);
            p1.RangeEnd.Should().Be(15);
        }

        // 2. GetObjectAsync with only rangeEnd (rangeStart = null)
        var p2 = await _backend.GetObjectAsync("range-bucket", "test.bin", rangeStart: null, rangeEnd: 5);
        using (p2.Stream)
        {
            p2.ContentLength.Should().Be(6); // 0 to 5 = 6 bytes
            p2.RangeStart.Should().Be(0);
            p2.RangeEnd.Should().Be(5);
        }

        // 3. UploadPartCopyAsync range validations
        var up = await _backend.CreateMultipartUploadAsync("range-bucket", "mp-dest.bin");

        // start < 0
        (await new Func<Task>(() => _backend.UploadPartCopyAsync("range-bucket", "test.bin", "range-bucket", "mp-dest.bin", up.UploadId, 1, rangeStart: -1, rangeEnd: 5))
            .Should().ThrowAsync<S3Exception>()).Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        // start > end
        (await new Func<Task>(() => _backend.UploadPartCopyAsync("range-bucket", "test.bin", "range-bucket", "mp-dest.bin", up.UploadId, 1, rangeStart: 8, rangeEnd: 4))
            .Should().ThrowAsync<S3Exception>()).Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        // start >= srcLength
        (await new Func<Task>(() => _backend.UploadPartCopyAsync("range-bucket", "test.bin", "range-bucket", "mp-dest.bin", up.UploadId, 1, rangeStart: 100, rangeEnd: 200))
            .Should().ThrowAsync<S3Exception>()).Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        // end >= srcLength (clamped)
        var copyPartRes = await _backend.UploadPartCopyAsync("range-bucket", "test.bin", "range-bucket", "mp-dest.bin", up.UploadId, 1, rangeStart: 10, rangeEnd: 999);
        copyPartRes.ETag.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task Metadata_Serialization_And_Prefix_Tests()
    {
        await _backend.CreateBucketAsync("meta-bucket", null);

        // Object with user metadata keys that already start with x-amz-meta- and some that do not
        var userMeta = new Dictionary<string, string>
        {
            ["x-amz-meta-custom"] = "val1",
            ["another"] = "val2"
        };
        byte[] bytes = Encoding.UTF8.GetBytes("meta-content");
        using var ms = new MemoryStream(bytes);
        await _backend.PutObjectAsync("meta-bucket", "obj.txt", ms, bytes.Length, "text/plain", userMeta);

        // Verify headers in both full and range GetObject
        var fullPayload = await _backend.GetObjectAsync("meta-bucket", "obj.txt");
        using (fullPayload.Stream)
        {
            fullPayload.Headers["x-amz-meta-custom"].Should().Be("val1");
            fullPayload.Headers["x-amz-meta-another"].Should().Be("val2");
        }

        var rangePayload = await _backend.GetObjectAsync("meta-bucket", "obj.txt", 0, 4);
        using (rangePayload.Stream)
        {
            rangePayload.Headers["x-amz-meta-custom"].Should().Be("val1");
            rangePayload.Headers["x-amz-meta-another"].Should().Be("val2");
        }

        // Directory object with metadata
        using var dirStream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("meta-bucket", "sub/", dirStream, 0, "application/x-directory", userMeta);
        var dirPayload = await _backend.GetObjectAsync("meta-bucket", "sub/");
        dirPayload.Headers["x-amz-meta-custom"].Should().Be("val1");
        dirPayload.Headers["x-amz-meta-another"].Should().Be("val2");

        // Object with metadata file on disk, but with null Tags, LegalHold, Retention, Acl
        var retrievedTags = await _backend.GetObjectTagsAsync("meta-bucket", "obj.txt");
        retrievedTags.Should().NotBeNull();
        retrievedTags.Tags.Should().BeNull();
        (await _backend.GetLegalHoldAsync("meta-bucket", "obj.txt")).Status.Should().Be("OFF");
        (await _backend.GetRetentionAsync("meta-bucket", "obj.txt")).Mode.Should().Be(RetentionModeEnum.None);
        (await _backend.GetObjectAclAsync("meta-bucket", "obj.txt")).Acl.Should().NotBeNull();

        // DeleteObjectTagsAsync and DeleteObjectAclAsync when meta exists
        await _backend.DeleteObjectTagsAsync("meta-bucket", "obj.txt");
        await _backend.DeleteObjectAclAsync("meta-bucket", "obj.txt");

        // FileMetadata class direct unit test for UserMetadata null branches
        var fileMeta = new FileSystemStorageBackend.FileMetadata();
        fileMeta.UserMetadata = null!;
        fileMeta.UserMetadataEntries.Should().BeEmpty();

        fileMeta.UserMetadataEntries = new[]
        {
            new FileSystemStorageBackend.MetadataEntry { Key = "k1", Value = null! },
            new FileSystemStorageBackend.MetadataEntry { Key = null!, Value = "ignored" }
        };
        fileMeta.UserMetadata.Should().ContainKey("k1");
        fileMeta.UserMetadata["k1"].Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task Multipart_And_Directory_EdgeCases()
    {
        await _backend.CreateBucketAsync("edge-bucket", null);

        // UploadPartAsync & ListPartsAsync on missing uploadId
        string badUploadId = "non-existent-upload-id";
        using var dummyStream = new MemoryStream(new byte[] { 1, 2, 3 });
        (await new Func<Task>(() => _backend.UploadPartAsync("edge-bucket", "k", badUploadId, 1, dummyStream, 3)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        (await new Func<Task>(() => _backend.ListPartsAsync("edge-bucket", "k", badUploadId)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // ListPartsAsync where .part exists without .etag file
        var upload = await _backend.CreateMultipartUploadAsync("edge-bucket", "k");
        using var partMs = new MemoryStream(new byte[] { 1, 2, 3 });
        await _backend.UploadPartAsync("edge-bucket", "k", upload.UploadId, 1, partMs, 3);

        // Manually delete the .etag file
        string partPath = Path.Combine(_tempDir, ".uploads", upload.UploadId, "1.part");
        string etagPath = partPath + ".etag";
        if (File.Exists(etagPath)) File.Delete(etagPath);

        var listParts = await _backend.ListPartsAsync("edge-bucket", "k", upload.UploadId);
        listParts.Parts.Should().ContainSingle(p => p.PartNumber == 1 && p.ETag == "\"\"");

        // CopyObjectAsync directory to directory
        using var emptyMs = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("edge-bucket", "srcdir/", emptyMs, 0, "application/x-directory");
        var copyRes = await _backend.CopyObjectAsync("edge-bucket", "srcdir/", "edge-bucket", "destdir/");
        copyRes.ETag.Should().Be("\"d41d8cd98f00b204e9800998ecf8427e\"");

        // GetObjectMetadataAsync for non-existent directory
        var nonExistentDirMeta = await _backend.GetObjectMetadataAsync("edge-bucket", "no-such-dir/");
        nonExistentDirMeta.Should().BeNull();

        // DeleteMultipleObjectsAsync with null request or empty objects
        var delNullRes = await _backend.DeleteMultipleObjectsAsync("edge-bucket", null!);
        delNullRes.DeletedObjects.Should().BeEmpty();

        var delNullObjectsRes = await _backend.DeleteMultipleObjectsAsync("edge-bucket", new DeleteMultiple());
        delNullObjectsRes.DeletedObjects.Should().BeEmpty();
    }

    [TestMethod]
    public async Task More_EdgeCases_For_BranchCoverage()
    {
        await _backend.CreateBucketAsync("edge2-bucket", null);

        // 1. Set & Get Tags, LegalHold, Retention, Acl (exercising meta != null && property != null)
        byte[] b = Encoding.UTF8.GetBytes("edge2");
        using var ms = new MemoryStream(b);
        await _backend.PutObjectAsync("edge2-bucket", "obj2.txt", ms, b.Length, "text/plain");

        // Object with NO meta file on disk: test Set on fresh object (ReadMeta returns null branch)
        string freshKey = "fresh.txt";
        using var msFresh = new MemoryStream(b);
        await _backend.PutObjectAsync("edge2-bucket", freshKey, msFresh, b.Length, "text/plain");
        string lhKey = "fresh-lh.txt";
        using (var msLh = new MemoryStream(b)) await _backend.PutObjectAsync("edge2-bucket", lhKey, msLh, b.Length, "text/plain");
        string lhMeta = Path.Combine(_tempDir, "edge2-bucket", lhKey + ".s3meta.xml");
        if (File.Exists(lhMeta)) File.Delete(lhMeta);
        await _backend.SetLegalHoldAsync("edge2-bucket", lhKey, new LegalHold("ON"));
        (await _backend.GetLegalHoldAsync("edge2-bucket", lhKey)).Status.Should().Be("ON");

        string retKey = "fresh-ret.txt";
        using (var msRet = new MemoryStream(b)) await _backend.PutObjectAsync("edge2-bucket", retKey, msRet, b.Length, "text/plain");
        string retMeta = Path.Combine(_tempDir, "edge2-bucket", retKey + ".s3meta.xml");
        if (File.Exists(retMeta)) File.Delete(retMeta);
        await _backend.SetRetentionAsync("edge2-bucket", retKey, new Retention { Mode = RetentionModeEnum.Governance });
        (await _backend.GetRetentionAsync("edge2-bucket", retKey)).Mode.Should().Be(RetentionModeEnum.Governance);

        string aclKey = "fresh-acl.txt";
        using (var msAcl = new MemoryStream(b)) await _backend.PutObjectAsync("edge2-bucket", aclKey, msAcl, b.Length, "text/plain");
        string aclMeta = Path.Combine(_tempDir, "edge2-bucket", aclKey + ".s3meta.xml");
        if (File.Exists(aclMeta)) File.Delete(aclMeta);
        await _backend.SetObjectAclAsync("edge2-bucket", aclKey, new AccessControlPolicy(new Owner("id", "name"), new AccessControlList()));
        (await _backend.GetObjectAclAsync("edge2-bucket", aclKey)).Owner.ID.Should().Be("id");

        var tags = new Tagging(new TagSet(new List<Tag> { new Tag("k", "v") }));
        await _backend.SetObjectTagsAsync("edge2-bucket", aclKey, tags);
        (await _backend.GetObjectTagsAsync("edge2-bucket", aclKey)).Tags.Tags.Should().ContainSingle();

        // 2. GetObjectAsync and ListObjectVersionsAsync on file without .s3meta.xml
        string noMetaKey = "no-meta.txt";
        using var msNoMeta = new MemoryStream(b);
        await _backend.PutObjectAsync("edge2-bucket", noMetaKey, msNoMeta, b.Length, null);
        string noMetaFilePath = Path.Combine(_tempDir, "edge2-bucket", noMetaKey + ".s3meta.xml");
        if (File.Exists(noMetaFilePath)) File.Delete(noMetaFilePath);

        var pNoMeta = await _backend.GetObjectAsync("edge2-bucket", noMetaKey);
        using (pNoMeta.Stream) { pNoMeta.ContentType.Should().Be("binary/octet-stream"); }

        var pRangeNoMeta = await _backend.GetObjectAsync("edge2-bucket", noMetaKey, 0, 2);
        using (pRangeNoMeta.Stream) { pRangeNoMeta.ContentType.Should().Be("binary/octet-stream"); }

        var versions = await _backend.ListObjectVersionsAsync("edge2-bucket");
        versions.Versions.Should().Contain(v => v.Key == noMetaKey);

        // CopyObjectAsync from source with no meta
        await _backend.CopyObjectAsync("edge2-bucket", noMetaKey, "edge2-bucket", "copied-no-meta.txt");

        // 3. PutObjectAsync directory without metadata
        using var emptyDirStream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("edge2-bucket", "nullmeta-dir/", emptyDirStream, 0, null, null);
        var dirMeta = await _backend.GetObjectMetadataAsync("edge2-bucket", "nullmeta-dir/");
        dirMeta.Should().NotBeNull();

        // Directory without .s3meta.xml: delete meta and call GetObject & ListObjects
        string dirMetaPath = Path.Combine(_tempDir, "edge2-bucket", "nullmeta-dir.s3meta.xml");
        if (File.Exists(dirMetaPath)) File.Delete(dirMetaPath);

        var dirPayload = await _backend.GetObjectAsync("edge2-bucket", "nullmeta-dir/");
        dirPayload.ContentType.Should().Be("application/x-directory");

        // CopyObjectAsync directory to directory when source directory has no metadata
        await _backend.CopyObjectAsync("edge2-bucket", "nullmeta-dir/", "edge2-bucket", "dest-nullmeta-dir/");

        // 4. Corrupted XML files handling
        string bucketMetaFile = Path.Combine(_tempDir, "edge2-bucket", ".s3bucket.xml");
        File.WriteAllText(bucketMetaFile, "<INVALID XML");
        var bMeta = _backend.ReadBucketMeta(Path.Combine(_tempDir, "edge2-bucket"));
        bMeta.Should().NotBeNull();

        string corruptObjMeta = Path.Combine(_tempDir, "edge2-bucket", "corrupt.txt.s3meta.xml");
        File.WriteAllText(corruptObjMeta, "<INVALID XML");
        var oMeta = _backend.ReadMeta(Path.Combine(_tempDir, "edge2-bucket", "corrupt.txt"));
        oMeta.Should().BeNull();
    }

    [TestMethod]
    public async Task Invalid_Bucket_Names_And_Path_Traversal_Defense()
    {
        // 1. Invalid bucket names throw InvalidBucketName
        string[] invalidBucketNames = new[]
        {
            "",
            "   ",
            ".",
            "..",
            "a/b",
            "a\\b",
            "inv?name",
            "inv*name",
            "../escape",
            "..\\escape"
        };

        foreach (var badBucket in invalidBucketNames)
        {
            (await new Func<Task>(() => _backend.BucketExistsAsync(badBucket)).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

            (await new Func<Task>(() => _backend.CreateBucketAsync(badBucket, null)).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

            (await new Func<Task>(() => _backend.ListMultipartUploadsAsync(badBucket)).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

            (await new Func<Task>(() => _backend.CreateMultipartUploadAsync(badBucket, "key")).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);
        }

        // 2. Path traversal defense throws InvalidRequest
        await _backend.CreateBucketAsync("trav-bucket", null);
        string[] traversalKeys = new[]
        {
            "../outside.txt",
            "../../etc/passwd",
            "dir/../../outside.txt",
            "../trav-bucket-sibling/file.txt"
        };

        foreach (var badKey in traversalKeys)
        {
            (await new Func<Task>(() => _backend.GetObjectAsync("trav-bucket", badKey)).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

            using var s = new MemoryStream(new byte[] { 1 });
            (await new Func<Task>(() => _backend.PutObjectAsync("trav-bucket", badKey, s, 1, "text/plain")).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

            (await new Func<Task>(() => _backend.DeleteObjectAsync("trav-bucket", badKey)).Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
        }

        // Key = "" resolves to bucket path and triggers fullPath.Equals(bPath) branch
        var emptyKeyMeta = await _backend.GetObjectMetadataAsync("trav-bucket", "");
        emptyKeyMeta.Should().NotBeNull();
        emptyKeyMeta!.ContentType.Should().Be("application/x-directory");
    }

    [TestMethod]
    public async Task ListObjects_With_Delimiter_CommonPrefixes_And_Pagination()
    {
        await _backend.CreateBucketAsync("delim-bucket", null);

        // Put objects across different virtual folders to test delimiter prefix rollup and deduplication
        byte[] b = new byte[] { 1, 2, 3 };
        using (var s1 = new MemoryStream(b)) await _backend.PutObjectAsync("delim-bucket", "photos/2026/01.jpg", s1, b.Length, "image/jpeg");
        using (var s2 = new MemoryStream(b)) await _backend.PutObjectAsync("delim-bucket", "photos/2026/02.jpg", s2, b.Length, "image/jpeg");
        using (var s3 = new MemoryStream(b)) await _backend.PutObjectAsync("delim-bucket", "photos/2025/12.jpg", s3, b.Length, "image/jpeg");
        using (var s4 = new MemoryStream(b)) await _backend.PutObjectAsync("delim-bucket", "photos/root.jpg", s4, b.Length, "image/jpeg");

        // 1. Delimiter test: common prefixes rollup and seenPrefixes deduplication
        var qParams = new Dictionary<string, string> { ["prefix"] = "photos/", ["delimiter"] = "/" };
        var req = new S3Request("GET", "/delim-bucket", qParams, new Dictionary<string, string[]>(), Stream.Null);
        var result = await _backend.ListObjectsAsync("delim-bucket", req);

        result.CommonPrefixes.Should().HaveCount(2);
        result.CommonPrefixes.Select(cp => cp.Prefix).Should().Contain(new[] { "photos/2025/", "photos/2026/" });
        result.Contents.Should().ContainSingle(c => c.Key == "photos/root.jpg");

        // 2. Marker pagination (V1) where some items <= marker and some > marker
        var markerParams = new Dictionary<string, string>
        {
            ["prefix"] = "photos/",
            ["delimiter"] = "/",
            ["marker"] = "photos/2025/"
        };
        var markerReq = new S3Request("GET", "/delim-bucket", markerParams, new Dictionary<string, string[]>(), Stream.Null);
        var markerResult = await _backend.ListObjectsAsync("delim-bucket", markerReq);

        markerResult.CommonPrefixes.Should().ContainSingle(cp => cp.Prefix == "photos/2026/");
        markerResult.Contents.Should().ContainSingle(c => c.Key == "photos/root.jpg");

        // 3. V2 pagination with start-after
        var v2Params = new Dictionary<string, string>
        {
            ["list-type"] = "2",
            ["prefix"] = "photos/",
            ["delimiter"] = "/",
            ["start-after"] = "photos/2025/"
        };
        var v2Req = new S3Request("GET", "/delim-bucket", v2Params, new Dictionary<string, string[]>(), Stream.Null);
        var v2Result = await _backend.ListObjectsAsync("delim-bucket", v2Req);

        v2Result.CommonPrefixes.Should().ContainSingle(cp => cp.Prefix == "photos/2026/");
        v2Result.KeyCount.Should().Be(2); // 1 common prefix + 1 content
    }

    [TestMethod]
    public async Task ListObjects_NonEmptyDirectory_With_MetaFile_And_Pagination()
    {
        await _backend.CreateBucketAsync("nonempty-dir-bucket", null);

        // Create explicit directory object
        using var emptyDirStream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("nonempty-dir-bucket", "explicit-dir/", emptyDirStream, 0, "application/x-directory");

        // Put a file inside it, so explicit-dir is not empty, but explicit-dir.s3meta.xml exists
        using var fileStream = new MemoryStream(new byte[] { 42 });
        await _backend.PutObjectAsync("nonempty-dir-bucket", "explicit-dir/item.txt", fileStream, 1, "text/plain");

        // Put a file inside implicit-dir directly so implicit-dir is not empty and has NO .s3meta.xml
        using var implicitStream = new MemoryStream(new byte[] { 99 });
        await _backend.PutObjectAsync("nonempty-dir-bucket", "implicit-dir/item.txt", implicitStream, 1, "text/plain");

        var req = new S3Request("GET", "/nonempty-dir-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        var listRes = await _backend.ListObjectsAsync("nonempty-dir-bucket", req);

        listRes.Contents.Should().Contain(c => c.Key == "explicit-dir/");
        listRes.Contents.Should().Contain(c => c.Key == "explicit-dir/item.txt");
        listRes.Contents.Should().Contain(c => c.Key == "implicit-dir/item.txt");
        listRes.Contents.Should().NotContain(c => c.Key == "implicit-dir/");
    }

    [TestMethod]
    public async Task DeleteObjectAsync_DirectoryKey_CleanDelete_And_NonEmptySkip()
    {
        await _backend.CreateBucketAsync("del-dir-bucket", null);

        // 1. Empty directory deleted cleanly
        using var dirStream = new MemoryStream(Array.Empty<byte>());
        await _backend.PutObjectAsync("del-dir-bucket", "clean-folder/", dirStream, 0, "application/x-directory");

        string folderPath = Path.Combine(_tempDir, "del-dir-bucket", "clean-folder");
        Directory.Exists(folderPath).Should().BeTrue();

        await _backend.DeleteObjectAsync("del-dir-bucket", "clean-folder/");
        Directory.Exists(folderPath).Should().BeFalse();

        // 2. Non-empty directory: DeleteObjectAsync deletes directory meta but does not delete directory itself
        await _backend.PutObjectAsync("del-dir-bucket", "has-files/", dirStream, 0, "application/x-directory");
        using var itemStream = new MemoryStream(new byte[] { 1 });
        await _backend.PutObjectAsync("del-dir-bucket", "has-files/doc.txt", itemStream, 1, "text/plain");

        string hasFilesPath = Path.Combine(_tempDir, "del-dir-bucket", "has-files");
        Directory.Exists(hasFilesPath).Should().BeTrue();

        await _backend.DeleteObjectAsync("del-dir-bucket", "has-files/");
        Directory.Exists(hasFilesPath).Should().BeTrue();
        File.Exists(Path.Combine(hasFilesPath, "doc.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_tempDir, "del-dir-bucket", "has-files.s3meta.xml")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Metadata_NullMeta_And_Fallback_Handling()
    {
        await _backend.CreateBucketAsync("null-meta-bucket", null);

        // 1. Create an object and remove its .s3meta.xml to test meta == null branches in getters and deleters
        byte[] data = new byte[] { 10, 20, 30 };
        using var s = new MemoryStream(data);
        await _backend.PutObjectAsync("null-meta-bucket", "plain.bin", s, data.Length, "application/octet-stream");

        string metaPath = Path.Combine(_tempDir, "null-meta-bucket", "plain.bin.s3meta.xml");
        File.Delete(metaPath);
        File.Exists(metaPath).Should().BeFalse();

        var tags = await _backend.GetObjectTagsAsync("null-meta-bucket", "plain.bin");
        tags.Should().NotBeNull();

        var lh = await _backend.GetLegalHoldAsync("null-meta-bucket", "plain.bin");
        lh.Status.Should().Be("OFF");

        var ret = await _backend.GetRetentionAsync("null-meta-bucket", "plain.bin");
        ret.Mode.Should().Be(RetentionModeEnum.None);

        var acl = await _backend.GetObjectAclAsync("null-meta-bucket", "plain.bin");
        acl.Owner.ID.Should().Be("filesystem-owner-id");

        // Delete tags and ACL when meta == null does not throw
        await _backend.DeleteObjectTagsAsync("null-meta-bucket", "plain.bin");
        await _backend.DeleteObjectAclAsync("null-meta-bucket", "plain.bin");

        // 2. Empty / xsi:nil .s3bucket.xml deserialization fallback
        string bPath = Path.Combine(_tempDir, "null-meta-bucket");
        string bMetaPath = Path.Combine(bPath, ".s3bucket.xml");
        File.WriteAllText(bMetaPath, "<BucketMetadata xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\" />");

        var bMeta = _backend.ReadBucketMeta(bPath);
        bMeta.Should().NotBeNull();
        bMeta.Acl.Should().BeNull();

        // 3. Directory without meta file in GetObjectMetadataAsync
        string bareDir = Path.Combine(bPath, "bare-dir");
        Directory.CreateDirectory(bareDir);
        var dirMeta = await _backend.GetObjectMetadataAsync("null-meta-bucket", "bare-dir/");
        dirMeta.Should().NotBeNull();
        dirMeta!.ContentType.Should().Be("application/x-directory");
        dirMeta.ETag.Should().Be("\"d41d8cd98f00b204e9800998ecf8427e\"");

        // Directory with meta but null UserMetadata
        var customMeta = new FileSystemStorageBackend.FileMetadata
        {
            ContentType = "application/x-directory",
            UserMetadata = null!
        };
        _backend.WriteMeta(bareDir, customMeta);
        var dirMetaWithNullUserMeta = await _backend.GetObjectMetadataAsync("null-meta-bucket", "bare-dir/");
        dirMetaWithNullUserMeta.Should().NotBeNull();
        dirMetaWithNullUserMeta!.Metadata.Should().BeEmpty();
    }

    [TestMethod]
    public async Task Multipart_Complete_NoSuchUpload_And_UploadPartCopy_NullRangeEnd()
    {
        await _backend.CreateBucketAsync("mp-edge-bucket", null);

        // 1. CompleteMultipartUpload on non-existent staging dir throws NoSuchUpload
        var actMissing = async () => await _backend.CompleteMultipartUploadAsync("mp-edge-bucket", "file.bin", "ghost-upload-999", new CompleteMultipartUpload());
        (await actMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // 2. UploadPartCopyAsync with rangeStart set and rangeEnd = null
        byte[] srcData = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        using var srcStream = new MemoryStream(srcData);
        await _backend.PutObjectAsync("mp-edge-bucket", "source.bin", srcStream, srcData.Length, "application/octet-stream");

        var upload = await _backend.CreateMultipartUploadAsync("mp-edge-bucket", "dest.bin");
        var copyRes = await _backend.UploadPartCopyAsync("mp-edge-bucket", "source.bin", "mp-edge-bucket", "dest.bin", upload.UploadId, 1, rangeStart: 10, rangeEnd: null);
        copyRes.ETag.Should().NotBeNullOrEmpty();

        var parts = await _backend.ListPartsAsync("mp-edge-bucket", "dest.bin", upload.UploadId);
        parts.Parts.Should().ContainSingle(p => p.PartNumber == 1 && p.Size == 6); // 16 - 10 = 6 bytes
    }

    [TestMethod]
    public async Task DeleteBucketAsync_BucketNotEmpty_ThrowsBucketNotEmpty()
    {
        await _backend.CreateBucketAsync("notempty-bucket", null);
        using var s = new MemoryStream(new byte[] { 1 });
        await _backend.PutObjectAsync("notempty-bucket", "file.txt", s, 1, "text/plain");

        (await new Func<Task>(() => _backend.DeleteBucketAsync("notempty-bucket")).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.BucketNotEmpty);
    }

    [TestMethod]
    public async Task ReadBucketMeta_Valid_Corrupt_And_Missing()
    {
        await _backend.CreateBucketAsync("meta-test-bucket", null);
        string bPath = Path.Combine(_tempDir, "meta-test-bucket");
        string metaPath = Path.Combine(bPath, ".s3bucket.xml");

        // 1. Missing meta file
        File.Delete(metaPath);
        var metaMissing = _backend.ReadBucketMeta(bPath);
        metaMissing.Should().NotBeNull();
        metaMissing.Versioning.Should().BeNull();

        // 2. Valid meta file
        var metaToWrite = new FileSystemStorageBackend.BucketMetadata
        {
            Versioning = new VersioningConfiguration { Status = VersioningStatusEnum.Enabled }
        };
        _backend.WriteBucketMeta(bPath, metaToWrite);
        var metaRead = _backend.ReadBucketMeta(bPath);
        metaRead.Should().NotBeNull();
        metaRead.Versioning.Should().NotBeNull();
        metaRead.Versioning!.Status.Should().Be(VersioningStatusEnum.Enabled);

        // 3. Corrupt XML
        File.WriteAllText(metaPath, "<<<not-valid-xml>>>");
        var metaCorrupt = _backend.ReadBucketMeta(bPath);
        metaCorrupt.Should().NotBeNull();
        metaCorrupt.Versioning.Should().BeNull();
    }

    [TestMethod]
    public async Task IsIncludedDirectory_And_ListObjectsAsync_DirectoryBranches()
    {
        await _backend.CreateBucketAsync("list-dirs-bucket", null);
        string bPath = Path.Combine(_tempDir, "list-dirs-bucket");

        // 1. Directory starting with "."
        string dotDir = Path.Combine(bPath, ".hidden");
        Directory.CreateDirectory(dotDir);
        _backend.IsIncludedDirectory(dotDir).Should().BeFalse();

        // 2. Empty directory
        string emptyDir = Path.Combine(bPath, "empty-dir");
        Directory.CreateDirectory(emptyDir);
        _backend.IsIncludedDirectory(emptyDir).Should().BeTrue();

        // 3. Non-empty directory with metadata file
        string metaDir = Path.Combine(bPath, "meta-dir");
        Directory.CreateDirectory(metaDir);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("sample"));
        await _backend.PutObjectAsync("list-dirs-bucket", "meta-dir/child.txt", stream, 6, "text/plain");
        File.WriteAllText(metaDir + ".s3meta.xml", "<FileMetadata><ContentType>application/x-directory</ContentType></FileMetadata>");
        _backend.IsIncludedDirectory(metaDir).Should().BeTrue();

        // 4. Non-empty directory without metadata file
        string plainDir = Path.Combine(bPath, "plain-dir");
        Directory.CreateDirectory(plainDir);
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes("sample2"));
        await _backend.PutObjectAsync("list-dirs-bucket", "plain-dir/child.txt", stream2, 7, "text/plain");
        _backend.IsIncludedDirectory(plainDir).Should().BeFalse();

        // Run ListObjectsAsync to verify integration
        var req = new S3Request("GET", "/list-dirs-bucket", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        var result = await _backend.ListObjectsAsync("list-dirs-bucket", req);
        result.Contents.Should().Contain(c => c.Key == "empty-dir/");
        result.Contents.Should().Contain(c => c.Key == "meta-dir/");
        result.Contents.Should().NotContain(c => c.Key == ".hidden/");
        result.Contents.Should().NotContain(c => c.Key == "plain-dir/");
    }

    [TestMethod]
    public async Task PutObjectAsync_And_UploadPartAsync_WhenStreamShorterThanContentLength_BreaksLoop()
    {
        await _backend.CreateBucketAsync("short-stream-bucket", null);

        // PutObjectAsync with contentLength (100) > actual stream bytes (10)
        byte[] putData = Encoding.UTF8.GetBytes("0123456789");
        using var putStream = new MemoryStream(putData);
        string putEtag = await _backend.PutObjectAsync("short-stream-bucket", "short-put.bin", putStream, 100, "application/octet-stream");
        putEtag.Should().NotBeNullOrEmpty();

        var getObj = await _backend.GetObjectAsync("short-stream-bucket", "short-put.bin");
        using var ms = new MemoryStream();
        await getObj.Stream.CopyToAsync(ms);
        ms.ToArray().Should().BeEquivalentTo(putData);

        // UploadPartAsync with contentLength (100) > actual stream bytes (10)
        var mpu = await _backend.CreateMultipartUploadAsync("short-stream-bucket", "short-mpu.bin");
        byte[] partData = Encoding.UTF8.GetBytes("9876543210");
        using var partStream = new MemoryStream(partData);
        string partEtag = await _backend.UploadPartAsync("short-stream-bucket", "short-mpu.bin", mpu.UploadId, 1, partStream, 100);
        partEtag.Should().NotBeNullOrEmpty();

        await _backend.AbortMultipartUploadAsync("short-stream-bucket", "short-mpu.bin", mpu.UploadId);
    }

    [TestMethod]
    public async Task DeleteObjectAsync_DirectoryKey_CustomDeleter_Success_And_Exception()
    {
        await _backend.CreateBucketAsync("del-dirkey-bucket", null);

        // 1. Custom DirectoryDeleter called and succeeds
        string calledDir = null!;
        _backend.DirectoryDeleter = dir =>
        {
            calledDir = dir;
            Directory.Delete(dir);
        };

        try
        {
            using var s1 = new MemoryStream(Array.Empty<byte>());
            await _backend.PutObjectAsync("del-dirkey-bucket", "custom-del/", s1, 0, "application/x-directory");
            await _backend.DeleteObjectAsync("del-dirkey-bucket", "custom-del/");

            calledDir.Should().NotBeNull();
            calledDir.Should().EndWith("custom-del");
            Directory.Exists(calledDir).Should().BeFalse();
        }
        finally
        {
            _backend.DirectoryDeleter = null;
        }

        // 2. Custom DirectoryDeleter throws -> handled gracefully
        _backend.DirectoryDeleter = dir => throw new IOException("Access denied");
        try
        {
            using var s2 = new MemoryStream(Array.Empty<byte>());
            await _backend.PutObjectAsync("del-dirkey-bucket", "err-del/", s2, 0, "application/x-directory");

            var act = async () => await _backend.DeleteObjectAsync("del-dirkey-bucket", "err-del/");
            await act.Should().NotThrowAsync();
        }
        finally
        {
            _backend.DirectoryDeleter = null;
        }
    }

    [TestMethod]
    public async Task CompleteMultipartUploadAsync_And_AbortMultipartUploadAsync_RecursiveDeleter_Branches()
    {
        await _backend.CreateBucketAsync("mpu-del-bucket", null);

        // --- CompleteMultipartUploadAsync with custom RecursiveDirectoryDeleter that succeeds ---
        var mpu1 = await _backend.CreateMultipartUploadAsync("mpu-del-bucket", "file1.bin");
        using (var p1 = new MemoryStream(Encoding.UTF8.GetBytes("part1")))
        {
            await _backend.UploadPartAsync("mpu-del-bucket", "file1.bin", mpu1.UploadId, 1, p1, 5);
        }

        string? deletedStagingDir1 = null;
        _backend.RecursiveDirectoryDeleter = (dir, recursive) =>
        {
            deletedStagingDir1 = dir;
            Directory.Delete(dir, recursive);
        };

        try
        {
            var parts1 = new CompleteMultipartUpload
            {
                Parts = new List<Part> { new Part { PartNumber = 1, ETag = "dummy" } }
            };
            var res1 = await _backend.CompleteMultipartUploadAsync("mpu-del-bucket", "file1.bin", mpu1.UploadId, parts1);
            res1.Should().NotBeNull();
            deletedStagingDir1.Should().NotBeNull();
        }
        finally
        {
            _backend.RecursiveDirectoryDeleter = null;
        }

        // --- CompleteMultipartUploadAsync when RecursiveDirectoryDeleter throws ---
        var mpu2 = await _backend.CreateMultipartUploadAsync("mpu-del-bucket", "file2.bin");
        using (var p2 = new MemoryStream(Encoding.UTF8.GetBytes("part2")))
        {
            await _backend.UploadPartAsync("mpu-del-bucket", "file2.bin", mpu2.UploadId, 1, p2, 5);
        }

        _backend.RecursiveDirectoryDeleter = (dir, recursive) => throw new IOException("Cannot delete staging dir");
        try
        {
            var parts2 = new CompleteMultipartUpload
            {
                Parts = new List<Part> { new Part { PartNumber = 1, ETag = "dummy" } }
            };
            var res2 = await _backend.CompleteMultipartUploadAsync("mpu-del-bucket", "file2.bin", mpu2.UploadId, parts2);
            res2.Should().NotBeNull();
        }
        finally
        {
            _backend.RecursiveDirectoryDeleter = null;
        }

        // --- AbortMultipartUploadAsync with custom RecursiveDirectoryDeleter that succeeds ---
        var mpu3 = await _backend.CreateMultipartUploadAsync("mpu-del-bucket", "file3.bin");
        string? deletedStagingDir3 = null;
        _backend.RecursiveDirectoryDeleter = (dir, recursive) =>
        {
            deletedStagingDir3 = dir;
            Directory.Delete(dir, recursive);
        };

        try
        {
            await _backend.AbortMultipartUploadAsync("mpu-del-bucket", "file3.bin", mpu3.UploadId);
            deletedStagingDir3.Should().NotBeNull();
        }
        finally
        {
            _backend.RecursiveDirectoryDeleter = null;
        }

        // --- AbortMultipartUploadAsync when RecursiveDirectoryDeleter throws ---
        var mpu4 = await _backend.CreateMultipartUploadAsync("mpu-del-bucket", "file4.bin");
        _backend.RecursiveDirectoryDeleter = (dir, recursive) => throw new IOException("Cannot delete staging dir");
        try
        {
            var actAbort = async () => await _backend.AbortMultipartUploadAsync("mpu-del-bucket", "file4.bin", mpu4.UploadId);
            await actAbort.Should().NotThrowAsync();
        }
        finally
        {
            _backend.RecursiveDirectoryDeleter = null;
        }
    }

    [TestMethod]
    public async Task GetStagingDirectory_And_ValidateUploadId_Tests()
    {
        await _backend.CreateBucketAsync("staging-test-bucket", null);

        // 1. Direct ValidateUploadId checks
        Action actNull = () => FileSystemStorageBackend.ValidateUploadId(null!);
        actNull.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actEmpty = () => FileSystemStorageBackend.ValidateUploadId("");
        actEmpty.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actWhitespace = () => FileSystemStorageBackend.ValidateUploadId("   ");
        actWhitespace.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actLong = () => FileSystemStorageBackend.ValidateUploadId(new string('a', 129));
        actLong.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actSlash = () => FileSystemStorageBackend.ValidateUploadId("sub/dir");
        actSlash.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actBackslash = () => FileSystemStorageBackend.ValidateUploadId("sub\\dir");
        actBackslash.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actDotDot = () => FileSystemStorageBackend.ValidateUploadId("abc..def");
        actDotDot.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actInvalidChar = () => FileSystemStorageBackend.ValidateUploadId("upload\0id");
        actInvalidChar.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actValid = () => FileSystemStorageBackend.ValidateUploadId("valid-upload-id_123");
        actValid.Should().NotThrow();

        // 2. Direct GetStagingDirectory checks (line 205 coverage)
        Action actDot = () => _backend.GetStagingDirectory(".");
        actDot.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        Action actDotSpace = () => _backend.GetStagingDirectory(". ");
        actDotSpace.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        string validStagingDir = _backend.GetStagingDirectory("valid-upload-id");
        validStagingDir.Should().StartWith(Path.Combine(_tempDir, ".uploads"));

        // 3. Public API checks using dot uploadId (reaches GetStagingDirectory line 205)
        using var dummyStream = new MemoryStream(new byte[] { 1, 2, 3 });
        (await new Func<Task>(() => _backend.UploadPartAsync("staging-test-bucket", "k", ".", 1, dummyStream, 3)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        (await new Func<Task>(() => _backend.ListPartsAsync("staging-test-bucket", "k", ".")).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        (await new Func<Task>(() => _backend.CompleteMultipartUploadAsync("staging-test-bucket", "k", ".", new CompleteMultipartUpload())).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        (await new Func<Task>(() => _backend.AbortMultipartUploadAsync("staging-test-bucket", "k", ".")).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        using var srcStream = new MemoryStream(new byte[] { 1, 2, 3 });
        await _backend.PutObjectAsync("staging-test-bucket", "src", srcStream, 3, "text/plain");

        (await new Func<Task>(() => _backend.UploadPartCopyAsync("staging-test-bucket", "src", "staging-test-bucket", "k", ".", 1)).Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task WriteStreamToFileWithEtagAsync_Cleanup_And_ExceptionHandling()
    {
        await _backend.CreateBucketAsync("stream-fail-bucket", null);

        // 1. PutObjectAsync with bounded contentLength when stream throws during read
        // Verifies the catch block deletes the partially written file and rethrows the original exception.
        string targetPath1 = Path.Combine(_tempDir, "stream-fail-bucket", "partial1.bin");
        using (var failingStream1 = new ThrowingStream(128, 1024))
        {
            var act = async () => await _backend.PutObjectAsync("stream-fail-bucket", "partial1.bin", failingStream1, 1024, "application/octet-stream");
            await act.Should().ThrowAsync<IOException>().WithMessage("*Simulated read failure*");
        }
        File.Exists(targetPath1).Should().BeFalse("partially written file must be cleaned up on failure");

        // 2. PutObjectAsync with unknown / zero contentLength (CopyToAsync branch) when stream throws
        string targetPath2 = Path.Combine(_tempDir, "stream-fail-bucket", "partial2.bin");
        using (var failingStream2 = new ThrowingStream(64, 512))
        {
            var act = async () => await _backend.PutObjectAsync("stream-fail-bucket", "partial2.bin", failingStream2, 0, "application/octet-stream");
            await act.Should().ThrowAsync<IOException>().WithMessage("*Simulated read failure*");
        }
        File.Exists(targetPath2).Should().BeFalse("partially written file must be cleaned up on failure when copying");

        // 3. UploadPartAsync when stream throws
        var mpu = await _backend.CreateMultipartUploadAsync("stream-fail-bucket", "part-fail.bin");
        string partFile = Path.Combine(_tempDir, ".uploads", mpu.UploadId, "1.part");
        using (var failingStream3 = new ThrowingStream(128, 1024))
        {
            var act = async () => await _backend.UploadPartAsync("stream-fail-bucket", "part-fail.bin", mpu.UploadId, 1, failingStream3, 1024);
            await act.Should().ThrowAsync<IOException>().WithMessage("*Simulated read failure*");
        }
        File.Exists(partFile).Should().BeFalse("partially written part file must be cleaned up on failure");

        // 4. Custom FileDeleter invoked on write failure
        string? recordedDeletedFile = null;
        _backend.FileDeleter = path =>
        {
            recordedDeletedFile = path;
            if (File.Exists(path)) File.Delete(path);
        };
        try
        {
            using var failingStream4 = new ThrowingStream(128, 1024);
            var act = async () => await _backend.PutObjectAsync("stream-fail-bucket", "custom-deleter.bin", failingStream4, 1024, "application/octet-stream");
            await act.Should().ThrowAsync<IOException>();
            recordedDeletedFile.Should().NotBeNull();
            recordedDeletedFile.Should().EndWith("custom-deleter.bin");
        }
        finally
        {
            _backend.FileDeleter = null;
        }

        // 5. Custom FileDeleter throws during cleanup -> caught gracefully and original stream exception is still rethrown
        _backend.FileDeleter = path => throw new UnauthorizedAccessException("Simulated file deletion failure");
        try
        {
            using var failingStream5 = new ThrowingStream(128, 1024);
            var act = async () => await _backend.PutObjectAsync("stream-fail-bucket", "failing-deleter.bin", failingStream5, 1024, "application/octet-stream");
            await act.Should().ThrowAsync<IOException>().WithMessage("*Simulated read failure*");
        }
        finally
        {
            _backend.FileDeleter = null;
        }

        // 6. Direct call to WriteStreamToFileWithEtagAsync with non-existent directory -> FileStream constructor throws
        // In catch block, File.Exists is false, rethrows cleanly.
        string nonExistentPath = Path.Combine(_tempDir, "missing-folder", "file.bin");
        using var dummyStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var actDirect = async () => await _backend.WriteStreamToFileWithEtagAsync(nonExistentPath, dummyStream, 3, CancellationToken.None);
        await actDirect.Should().ThrowAsync<DirectoryNotFoundException>();

        // 7. DeleteObjectAsync tests with custom FileDeleter
        byte[] content = Encoding.UTF8.GetBytes("delete-test-data");
        using (var ms = new MemoryStream(content))
        {
            await _backend.PutObjectAsync("stream-fail-bucket", "to-delete.txt", ms, content.Length, "text/plain");
        }

        string? deletedInDeleteObject = null;
        _backend.FileDeleter = path =>
        {
            deletedInDeleteObject = path;
            if (File.Exists(path)) File.Delete(path);
        };
        try
        {
            await _backend.DeleteObjectAsync("stream-fail-bucket", "to-delete.txt");
            deletedInDeleteObject.Should().NotBeNull();
        }
        finally
        {
            _backend.FileDeleter = null;
        }

        // DeleteObjectAsync when FileDeleter throws -> does not throw
        using (var ms = new MemoryStream(content))
        {
            await _backend.PutObjectAsync("stream-fail-bucket", "to-delete-fail.txt", ms, content.Length, "text/plain");
        }
        _backend.FileDeleter = path => throw new IOException("Locked during DeleteObjectAsync");
        try
        {
            var actDel = async () => await _backend.DeleteObjectAsync("stream-fail-bucket", "to-delete-fail.txt");
            await actDel.Should().NotThrowAsync();
        }
        finally
        {
            _backend.FileDeleter = null;
        }
        _backend.ActiveWrites.Should().NotBeNull();
        _backend.ActiveWrites.IsEmpty.Should().BeTrue();

        // DeleteMultipleObjectsAsync with versionId
        var delMultiReq = new DeleteMultiple
        {
            Objects = new List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object("ver-key.txt", "v123")
            }
        };
        var delMultiResult = await _backend.DeleteMultipleObjectsAsync("stream-fail-bucket", delMultiReq);
        delMultiResult.DeletedObjects.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task GetObjectPath_And_GetStagingDirectory_CoverageAndValidationTests()
    {
        await _backend.CreateBucketAsync("test-objpath-bucket", null);

        // 1. GetStagingDirectory valid uploadId and uploadsWithSep check
        string staging = _backend.GetStagingDirectory("valid-upload-123");
        staging.Should().Be(Path.Combine(_tempDir, ".uploads", "valid-upload-123"));

        // 2. GetObjectPath with null key
        string pathNull = _backend.GetObjectPath("test-objpath-bucket", null);
        pathNull.Should().Be(Path.Combine(_tempDir, "test-objpath-bucket"));

        // Public API with null key
        var metaNull = await _backend.GetObjectMetadataAsync("test-objpath-bucket", null!);
        metaNull.Should().NotBeNull();
        metaNull!.ContentType.Should().Be("application/x-directory");

        // 3. GetObjectPath with empty key
        string pathEmpty = _backend.GetObjectPath("test-objpath-bucket", "");
        pathEmpty.Should().Be(Path.Combine(_tempDir, "test-objpath-bucket"));

        // 4. GetObjectPath with "." and ".."
        Action actDot = () => _backend.GetObjectPath("test-objpath-bucket", ".");
        actDot.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidRequest);

        Action actDotDot = () => _backend.GetObjectPath("test-objpath-bucket", "..");
        actDotDot.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidRequest);

        // 5. Direct access to internal metadata files
        Action actBucketMeta = () => _backend.GetObjectPath("test-objpath-bucket", ".s3bucket.xml");
        actBucketMeta.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.AccessDenied);

        Action actBucketMetaSlash = () => _backend.GetObjectPath("test-objpath-bucket", "/.s3bucket.xml");
        actBucketMetaSlash.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.AccessDenied);

        Action actBucketMetaBackslash = () => _backend.GetObjectPath("test-objpath-bucket", "\\.s3bucket.xml");
        actBucketMetaBackslash.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.AccessDenied);

        Action actObjectMeta = () => _backend.GetObjectPath("test-objpath-bucket", "item.txt.s3meta.xml");
        actObjectMeta.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.AccessDenied);

        Action actNestedMeta = () => _backend.GetObjectPath("test-objpath-bucket", "nested/folder/file.bin.s3meta.xml");
        actNestedMeta.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.AccessDenied);

        // 6. Colons in keys (NTFS Alternate Data Streams)
        Action actColon = () => _backend.GetObjectPath("test-objpath-bucket", "stream:ads");
        var colonEx = actColon.Should().Throw<S3Exception>().Which;
        colonEx.Error!.Code.Should().Be(ErrorCode.InvalidRequest);
        colonEx.Error!.Key.Should().Be("Colons are not permitted in object keys.");

        Action actColonData = () => _backend.GetObjectPath("test-objpath-bucket", "dir/file.txt:$DATA");
        actColonData.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidRequest);

        // 7. Normal key with and without leading slash
        string normalPath = _backend.GetObjectPath("test-objpath-bucket", "dir/file.txt");
        normalPath.Should().Be(Path.GetFullPath(Path.Combine(_tempDir, "test-objpath-bucket", "dir", "file.txt")));

        string leadingSlashPath = _backend.GetObjectPath("test-objpath-bucket", "/dir/file.txt");
        leadingSlashPath.Should().Be(Path.GetFullPath(Path.Combine(_tempDir, "test-objpath-bucket", "dir", "file.txt")));

        // 8. Non-existent bucket and invalid bucket name
        Action actNoBucket = () => _backend.GetObjectPath("nonexistent-bucket-xyz", "key.txt");
        actNoBucket.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.NoSuchBucket);

        Action actBadBucket = () => _backend.GetObjectPath("bad/bucket", "key.txt");
        actBadBucket.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidBucketName);

        // Windows reserved names with and without extensions (line 207 coverage)
        Action actReservedWithExt = () => _backend.GetBucketPath("nul.txt");
        actReservedWithExt.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidBucketName);

        Action actReservedWithExt2 = () => _backend.GetBucketPath("com1.data");
        actReservedWithExt2.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidBucketName);

        Action actReservedWithoutExt = () => _backend.GetBucketPath("aux");
        actReservedWithoutExt.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidBucketName);

        // Valid bucket name with dot in the middle (line 207 non-reserved branch coverage)
        string validBucketWithDot = _backend.GetBucketPath("my.sample.bucket");
        validBucketWithDot.Should().Be(Path.Combine(_tempDir, "my.sample.bucket"));

        // 9. Path traversal outside bucket
        Action actTrav = () => _backend.GetObjectPath("test-objpath-bucket", "../escape.txt");
        actTrav.Should().Throw<S3Exception>().Which.Error!.Code.Should().Be(ErrorCode.InvalidRequest);
    }

    private class ThrowingStream : Stream
    {
        private readonly int _throwAfterBytes;
        private int _bytesRead;
        private readonly byte[] _content;

        public ThrowingStream(int throwAfterBytes, int totalBytes = 1024)
        {
            _throwAfterBytes = throwAfterBytes;
            _content = new byte[totalBytes];
            Array.Fill<byte>(_content, 0x42);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _content.Length;
        public override long Position { get => _bytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bytesRead >= _throwAfterBytes)
                throw new IOException("Simulated read failure in ThrowingStream");

            int toCopy = Math.Min(count, _throwAfterBytes - _bytesRead);
            if (toCopy <= 0)
                throw new IOException("Simulated read failure at boundary in ThrowingStream");

            Array.Copy(_content, _bytesRead, buffer, offset, toCopy);
            _bytesRead += toCopy;
            return toCopy;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] arr = new byte[buffer.Length];
            int n = Read(arr, 0, arr.Length);
            arr.AsMemory(0, n).CopyTo(buffer);
            return ValueTask.FromResult(n);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
