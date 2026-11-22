namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
public class StorageBackendDeepTests
{
    private string _fsTempDir = null!;
    private FileSystemStorageBackend _fsBackend = null!;
    private InMemoryStorageBackend _memBackend = null!;

    [TestInitialize]
    public void Setup()
    {
        _fsTempDir = Path.Combine(Path.GetTempPath(), "SharpS3_DeepTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fsTempDir);
        _fsBackend = new FileSystemStorageBackend(_fsTempDir);
        _memBackend = new InMemoryStorageBackend();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_fsTempDir))
            {
                Directory.Delete(_fsTempDir, true);
            }
        }
        catch { }
    }

    [TestMethod]
    public async Task InMemory_ListObjects_Delimiter_Prefixes_Pagination_And_Limits()
    {
        await _memBackend.CreateBucketAsync("list-bucket", null);

        var keys = new[]
        {
            "root1.txt",
            "folderA/fileA1.txt",
            "folderA/fileA2.txt",
            "folderB/fileB1.txt",
            "folderB/sub/fileBsub.txt"
        };

        foreach (var k in keys)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes("content-" + k));
            await _memBackend.PutObjectAsync("list-bucket", k, ms, ms.Length, "text/plain");
        }

        // 1. List with delimiter="/" and empty prefix (V2)
        var qParamsV2 = new Dictionary<string, string> { ["list-type"] = "2", ["delimiter"] = "/" };
        var reqDelimV2 = new S3Request("GET", "/list-bucket", qParamsV2, new Dictionary<string, string[]>(), Stream.Null);
        reqDelimV2.RequestType = S3RequestType.BucketRead;
        reqDelimV2.Bucket = "list-bucket";
        reqDelimV2.Delimiter = "/";
        reqDelimV2.MaxKeys = 1000;

        var listV2 = await _memBackend.ListObjectsAsync("list-bucket", reqDelimV2);
        listV2.Contents.Should().ContainSingle(c => c.Key == "root1.txt");
        listV2.CommonPrefixes.Select(cp => cp.Prefix).Should().BeEquivalentTo(new[] { "folderA/", "folderB/" });
        listV2.KeyCount.Should().Be(3);

        // 2. Truncated listing with MaxKeys = 1 (V2 pagination)
        reqDelimV2.MaxKeys = 1;
        var truncListV2 = await _memBackend.ListObjectsAsync("list-bucket", reqDelimV2);
        truncListV2.IsTruncated.Should().BeTrue();
        truncListV2.NextContinuationToken.Should().NotBeNullOrEmpty();

        // 3. Truncated listing with MaxKeys = 1 (V1 pagination)
        var qParamsV1 = new Dictionary<string, string> { ["delimiter"] = "/" };
        var reqDelimV1 = new S3Request("GET", "/list-bucket", qParamsV1, new Dictionary<string, string[]>(), Stream.Null);
        reqDelimV1.RequestType = S3RequestType.BucketRead;
        reqDelimV1.Bucket = "list-bucket";
        reqDelimV1.Delimiter = "/";
        reqDelimV1.MaxKeys = 1;

        var truncListV1 = await _memBackend.ListObjectsAsync("list-bucket", reqDelimV1);
        truncListV1.IsTruncated.Should().BeTrue();
        truncListV1.NextMarker.Should().NotBeNullOrEmpty();

        // 4. ListObjectVersionsAsync
        var versions = await _memBackend.ListObjectVersionsAsync("list-bucket");
        versions.Versions.Count.Should().Be(5);

        // 5. ListMultipartUploadsAsync with active uploads
        var init1 = await _memBackend.CreateMultipartUploadAsync("list-bucket", "upload1.bin");
        var init2 = await _memBackend.CreateMultipartUploadAsync("list-bucket", "upload2.bin");

        var uploads = await _memBackend.ListMultipartUploadsAsync("list-bucket");
        uploads.Uploads.Count.Should().Be(2);

        // 6. PutObjectAsync and UploadPartAsync with contentLength <= 0 (stream to end)
        using var streamUnknownLen = new MemoryStream(new byte[] { 10, 20, 30 });
        string etagZeroLen = await _memBackend.PutObjectAsync("list-bucket", "stream-put.bin", streamUnknownLen, 0, "application/octet-stream");
        etagZeroLen.Should().NotBeNullOrEmpty();

        using var partStreamUnknownLen = new MemoryStream(new byte[] { 40, 50, 60 });
        string partResp = await _memBackend.UploadPartAsync("list-bucket", "upload1.bin", init1.UploadId, 1, partStreamUnknownLen, 0);
        partResp.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task FileSystem_DirectoryKeys_Listing_And_Corrupt_Meta_Handling()
    {
        await _fsBackend.CreateBucketAsync("fs-deep", null);

        // 1. Directory key operations with metadata
        using var dirMs = new MemoryStream();
        var dirCustomMeta = new Dictionary<string, string> { ["custom-tag"] = "test-val" };
        string dirEtag = await _fsBackend.PutObjectAsync("fs-deep", "virtual-dir/", dirMs, 0, "application/x-directory", dirCustomMeta);
        dirEtag.Should().NotBeNullOrEmpty();

        var dirMeta = await _fsBackend.GetObjectMetadataAsync("fs-deep", "virtual-dir/");
        dirMeta.Should().NotBeNull();
        dirMeta!.Key.Should().Be("virtual-dir/");
        dirMeta.Metadata.Should().ContainKey("custom-tag");

        using (var dirObj = await _fsBackend.GetObjectAsync("fs-deep", "virtual-dir/"))
        {
            dirObj.ContentLength.Should().Be(0);
            dirObj.Headers.Should().ContainKey("x-amz-meta-custom-tag");
        }

        // Copy directory key
        await _fsBackend.CreateBucketAsync("fs-deep-dest", null);
        var copyDirRes = await _fsBackend.CopyObjectAsync("fs-deep", "virtual-dir/", "fs-deep-dest", "copied-dir/");
        copyDirRes.ETag.Should().NotBeNullOrEmpty();

        // 2. Listing with delimiter, prefix, and truncation (V1 and V2)
        using var f1 = new MemoryStream(new byte[] { 1 });
        await _fsBackend.PutObjectAsync("fs-deep", "sub/file1.txt", f1, 1, "text/plain");
        using var f2 = new MemoryStream(new byte[] { 2, 3 });
        await _fsBackend.PutObjectAsync("fs-deep", "sub/file2.txt", f2, 2, "text/plain");
        using var f3 = new MemoryStream(new byte[] { 4 });
        await _fsBackend.PutObjectAsync("fs-deep", "sub/other/file3.txt", f3, 1, "text/plain");

        // V2 listing truncated
        var qParamsV2 = new Dictionary<string, string> { ["list-type"] = "2", ["delimiter"] = "/", ["prefix"] = "sub/" };
        var reqDelimV2 = new S3Request("GET", "/fs-deep", qParamsV2, new Dictionary<string, string[]>(), Stream.Null);
        reqDelimV2.RequestType = S3RequestType.BucketRead;
        reqDelimV2.Bucket = "fs-deep";
        reqDelimV2.Delimiter = "/";
        reqDelimV2.Prefix = "sub/";
        reqDelimV2.MaxKeys = 1;

        var listResV2 = await _fsBackend.ListObjectsAsync("fs-deep", reqDelimV2);
        listResV2.IsTruncated.Should().BeTrue();
        listResV2.NextContinuationToken.Should().NotBeNullOrEmpty();

        // V1 listing truncated
        var qParamsV1 = new Dictionary<string, string> { ["delimiter"] = "/", ["prefix"] = "sub/" };
        var reqDelimV1 = new S3Request("GET", "/fs-deep", qParamsV1, new Dictionary<string, string[]>(), Stream.Null);
        reqDelimV1.RequestType = S3RequestType.BucketRead;
        reqDelimV1.Bucket = "fs-deep";
        reqDelimV1.Delimiter = "/";
        reqDelimV1.Prefix = "sub/";
        reqDelimV1.MaxKeys = 1;

        var listResV1 = await _fsBackend.ListObjectsAsync("fs-deep", reqDelimV1);
        listResV1.IsTruncated.Should().BeTrue();
        listResV1.NextMarker.Should().NotBeNullOrEmpty();

        // 3. Invalid range on GetObjectAsync throws InvalidRange
        Func<Task> actBadRange = async () => await _fsBackend.GetObjectAsync("fs-deep", "sub/file1.txt", rangeStart: 100, rangeEnd: 5);
        (await actBadRange.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRange);

        // 4. DeleteObjectAsync with remaining files in directory (hits break in parent pruning)
        await _fsBackend.DeleteObjectAsync("fs-deep", "sub/file1.txt");
        (await _fsBackend.GetObjectMetadataAsync("fs-deep", "sub/file1.txt")).Should().BeNull();

        // 5. DeleteMultipleObjectsAsync with invalid key path (hits catch block)
        var delMult = new DeleteMultiple(false, new List<SharpS3.Models.Object> { new SharpS3.Models.Object("../invalid-traversal", "v1") });
        var delRes = await _fsBackend.DeleteMultipleObjectsAsync("fs-deep", delMult);
        delRes.DeletedObjects.Should().BeEmpty();

        // 6. Delete directory key
        await _fsBackend.DeleteObjectAsync("fs-deep", "virtual-dir/");
        (await _fsBackend.GetObjectMetadataAsync("fs-deep", "virtual-dir/")).Should().BeNull();

        // 7. Non-empty bucket deletion throws BucketNotEmpty
        Func<Task> actDelNonEmpty = async () => await _fsBackend.DeleteBucketAsync("fs-deep");
        (await actDelNonEmpty.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.BucketNotEmpty);

        // 8. Corrupt XML metadata handling (ReadBucketMeta and ReadMeta fallback)
        string bucketDir = Path.Combine(_fsTempDir, "fs-deep");
        string bucketMetaPath = Path.Combine(bucketDir, ".s3bucket.xml");
        File.WriteAllText(bucketMetaPath, "<invalid>XML corrupt <<<");

        var acl = await _fsBackend.GetBucketAclAsync("fs-deep");
        acl.Should().NotBeNull();

        string objMetaPath = Path.Combine(bucketDir, "sub", "file2.txt.s3meta.xml");
        File.WriteAllText(objMetaPath, "<corrupted-xml>>>>");
        var objMeta = await _fsBackend.GetObjectMetadataAsync("fs-deep", "sub/file2.txt");
        objMeta.Should().NotBeNull();
    }
}
