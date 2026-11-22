namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Storage;

[TestClass]
public class StorageBackendEdgeCasesTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SharpS3_Edge_" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task FileSystemBackend_SecurityAndValidation_Rejections()
    {
        var backend = new FileSystemStorageBackend(_tempDir);

        // 1. Invalid bucket name (whitespace or invalid chars)
        var actInvalidBucket1 = async () => await backend.CreateBucketAsync("   ", null);
        (await actInvalidBucket1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

        var actInvalidBucket2 = async () => await backend.CreateBucketAsync("bucket?name*", null);
        (await actInvalidBucket2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

        // 2. NoSuchBucket when accessing non-existent bucket
        var actNoSuchBucket = async () => await backend.GetObjectMetadataAsync("ghost-bucket", "file.txt");
        (await actNoSuchBucket.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        // Create valid bucket
        await backend.CreateBucketAsync("safe-bucket", null);

        // 3. Path traversal attack in key
        var actPathTraversal = async () => await backend.GetObjectAsync("safe-bucket", "../../traversal.txt");
        (await actPathTraversal.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        // 4. NoSuchKey on non-existent file
        var actNoSuchKey = async () => await backend.GetObjectAsync("safe-bucket", "missing.txt");
        (await actNoSuchKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        // 5. Corrupted bucket metadata file (.s3bucket.xml)
        string bucketPath = Path.Combine(_tempDir, "safe-bucket");
        string metaPath = Path.Combine(bucketPath, ".s3bucket.xml");
        await File.WriteAllTextAsync(metaPath, "CORRUPT_NOT_XML<<<>>>");

        // Should fall back cleanly to default metadata rather than throwing unhandled exception
        var location = await backend.GetBucketLocationAsync("safe-bucket");
        location.Should().NotBeNull();

        // 6. Corrupted object metadata file (.s3meta.xml)
        byte[] payload = Encoding.UTF8.GetBytes("Object data");
        using var mem = new MemoryStream(payload);
        await backend.PutObjectAsync("safe-bucket", "doc.txt", mem, payload.Length, "text/plain");

        string objMetaPath = Path.Combine(bucketPath, "doc.txt.s3meta.xml");
        await File.WriteAllTextAsync(objMetaPath, "INVALID_XML_METADATA");

        // Metadata getter handles corrupt file gracefully
        var meta = await backend.GetObjectMetadataAsync("safe-bucket", "doc.txt");
        meta.Should().NotBeNull();
        meta!.Key.Should().Be("doc.txt");

        // 7. Multipart operations on non-existent upload
        var actNoSuchUpload = async () => await backend.UploadPartAsync("safe-bucket", "doc.txt", "non-existent-upload", 1, Stream.Null, 0);
        (await actNoSuchUpload.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        var actCompleteMissing = async () => await backend.CompleteMultipartUploadAsync(
            "safe-bucket", "doc.txt", "non-existent-upload", new CompleteMultipartUpload());
        (await actCompleteMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // Aborting non-existent upload shouldn't throw or should throw NoSuchUpload
        await backend.AbortMultipartUploadAsync("safe-bucket", "doc.txt", "non-existent-upload");
    }

    [TestMethod]
    public async Task InMemoryBackend_EdgeCases_And_Rejections()
    {
        var backend = new InMemoryStorageBackend();

        // 1. Bucket operations on non-existent bucket
        var actNoSuchBucket = async () => await backend.GetObjectAsync("ghost-bucket", "file.txt");
        (await actNoSuchBucket.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        await backend.CreateBucketAsync("test-bucket", null);
        (await backend.BucketExistsAsync("test-bucket")).Should().BeTrue();

        // 2. Deleting non-empty bucket throws BucketNotEmpty
        using var putData = new MemoryStream(new byte[] { 1, 2, 3 });
        await backend.PutObjectAsync("test-bucket", "obj1", putData, 3, "text/plain");
        var actDeleteNonEmpty = async () => await backend.DeleteBucketAsync("test-bucket");
        (await actDeleteNonEmpty.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.BucketNotEmpty);

        // Delete object so bucket becomes empty, then delete bucket
        await backend.DeleteObjectAsync("test-bucket", "obj1");
        await backend.DeleteBucketAsync("test-bucket");
        (await backend.BucketExistsAsync("test-bucket")).Should().BeFalse();

        // Re-create bucket
        await backend.CreateBucketAsync("test-bucket", null);

        // 3. NoSuchKey
        var actNoSuchKey = async () => await backend.GetObjectAsync("test-bucket", "ghost.txt");
        (await actNoSuchKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        // 4. Multipart operations on non-existent upload
        var actNoSuchUpload = async () => await backend.UploadPartAsync("test-bucket", "ghost.txt", "fake-up", 1, Stream.Null, 0);
        (await actNoSuchUpload.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        var actCompleteNoSuchUpload = async () => await backend.CompleteMultipartUploadAsync(
            "test-bucket", "ghost.txt", "fake-up", new CompleteMultipartUpload());
        (await actCompleteNoSuchUpload.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        var actAbortNoSuchUpload = async () => await backend.AbortMultipartUploadAsync("test-bucket", "ghost.txt", "fake-up");
        (await actAbortNoSuchUpload.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // 5. Invalid range on GetObjectAsync
        using var putObj = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        await backend.PutObjectAsync("test-bucket", "data.txt", putObj, 5, "text/plain");

        var actInvalidRange1 = async () => await backend.GetObjectAsync("test-bucket", "data.txt", rangeStart: 10, rangeEnd: 5);
        (await actInvalidRange1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        var actInvalidRange2 = async () => await backend.GetObjectAsync("test-bucket", "data.txt", rangeStart: 20, rangeEnd: 30);
        (await actInvalidRange2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        // 6. Multipart completion and invalid range on UploadPartCopy
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "multi.txt");

        var actCopyInvalidRange = async () => await backend.UploadPartCopyAsync("test-bucket", "data.txt", "test-bucket", "multi.txt", init.UploadId, 1, rangeStart: 10, rangeEnd: 2);
        (await actCopyInvalidRange.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        var completeRes = await backend.CompleteMultipartUploadAsync("test-bucket", "multi.txt", init.UploadId, new CompleteMultipartUpload());
        completeRes.Should().NotBeNull();
        completeRes.Key.Should().Be("multi.txt");
    }
}
