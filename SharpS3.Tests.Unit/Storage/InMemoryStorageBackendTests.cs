namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Storage;

[TestClass]
public partial class InMemoryStorageBackendTests
{
    [TestMethod]
    public async Task Can_Create_List_And_Delete_Buckets()
    {
        var backend = new InMemoryStorageBackend();

        // Initially no buckets
        var list1 = await backend.ListBucketsAsync(null!);
        list1.Buckets.BucketList.Should().BeEmpty();

        // Create bucket
        await backend.CreateBucketAsync("test-bucket", null);
        (await backend.BucketExistsAsync("test-bucket")).Should().BeTrue();

        // List buckets
        var list2 = await backend.ListBucketsAsync(null!);
        list2.Buckets.BucketList.Should().ContainSingle();
        list2.Buckets.BucketList[0].Name.Should().Be("test-bucket");

        // Delete bucket
        await backend.DeleteBucketAsync("test-bucket");
        (await backend.BucketExistsAsync("test-bucket")).Should().BeFalse();
    }

    [TestMethod]
    public async Task Can_Put_Get_And_Delete_Object()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("bucket-1", null);

        byte[] payload = Encoding.UTF8.GetBytes("Hello World from SharpS3!");
        using var stream = new MemoryStream(payload);

        // PutObject
        string etag = await backend.PutObjectAsync("bucket-1", "hello.txt", stream, payload.Length, "text/plain");
        etag.Should().NotBeNull();

        // HeadObject
        var metadata = await backend.GetObjectMetadataAsync("bucket-1", "hello.txt");
        metadata.Should().NotBeNull();
        metadata!.Size.Should().Be(payload.Length);
        metadata.ContentType.Should().Be("text/plain");

        // GetObject
        using var getObj = await backend.GetObjectAsync("bucket-1", "hello.txt");
        getObj.ContentLength.Should().Be(payload.Length);
        using var ms = new MemoryStream();
        await getObj.Stream.CopyToAsync(ms);
        string retrievedText = Encoding.UTF8.GetString(ms.ToArray());
        retrievedText.Should().Be("Hello World from SharpS3!");

        // DeleteObject
        await backend.DeleteObjectAsync("bucket-1", "hello.txt");
        var metadataAfter = await backend.GetObjectMetadataAsync("bucket-1", "hello.txt");
        metadataAfter.Should().BeNull();
    }

    [TestMethod]
    public async Task Can_Perform_Range_Read()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("bucket-range", null);

        byte[] payload = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        using var stream = new MemoryStream(payload);
        await backend.PutObjectAsync("bucket-range", "digits.txt", stream, payload.Length, "text/plain");

        // Read range 5 to 9 -> "56789"
        using var rangeObj = await backend.GetObjectAsync("bucket-range", "digits.txt", 5, 9);
        rangeObj.ContentLength.Should().Be(5);
        rangeObj.TotalSize.Should().Be(16);
        using var ms = new MemoryStream();
        await rangeObj.Stream.CopyToAsync(ms);
        string rangeText = Encoding.UTF8.GetString(ms.ToArray());
        rangeText.Should().Be("56789");
    }

    [TestMethod]
    public async Task Can_Perform_Multipart_Upload()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("mp-bucket", null);

        // 1. Initiate
        var init = await backend.CreateMultipartUploadAsync("mp-bucket", "large.dat");
        init.UploadId.Should().NotBeNull();

        // 2. Upload Part 1
        byte[] part1 = Encoding.UTF8.GetBytes("Part1Data-");
        using var s1 = new MemoryStream(part1);
        string etag1 = await backend.UploadPartAsync("mp-bucket", "large.dat", init.UploadId, 1, s1, part1.Length);

        // 3. Upload Part 2
        byte[] part2 = Encoding.UTF8.GetBytes("Part2Data!");
        using var s2 = new MemoryStream(part2);
        string etag2 = await backend.UploadPartAsync("mp-bucket", "large.dat", init.UploadId, 2, s2, part2.Length);

        // 4. List Parts
        var partsList = await backend.ListPartsAsync("mp-bucket", "large.dat", init.UploadId);
        partsList.Parts.Count.Should().Be(2);

        // 5. Complete
        var completeReq = new CompleteMultipartUpload();
        var completeResult = await backend.CompleteMultipartUploadAsync("mp-bucket", "large.dat", init.UploadId, completeReq);
        completeResult.ETag.Should().NotBeNull();

        // 6. Verify assembled object
        using var finalObj = await backend.GetObjectAsync("mp-bucket", "large.dat");
        finalObj.ContentLength.Should().Be(part1.Length + part2.Length);
        using var ms = new MemoryStream();
        await finalObj.Stream.CopyToAsync(ms);
        string fullText = Encoding.UTF8.GetString(ms.ToArray());
        fullText.Should().Be("Part1Data-Part2Data!");
    }

    [TestMethod]
    public async Task UploadPartAsync_WhenContentLengthExceedsMaxObjectSize_ThrowsEntityTooLarge()
    {
        var backend = new InMemoryStorageBackend { MaxObjectSize = 50 };
        await backend.CreateBucketAsync("test-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "large.dat");

        byte[] payload = new byte[100];
        using var stream = new MemoryStream(payload);

        var act = async () => await backend.UploadPartAsync("test-bucket", "large.dat", init.UploadId, 1, stream, 100);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task UploadPartAsync_WhenStreamEndsBeforeContentLength_BreaksAndResizesBuffer()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("test-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "early-eof.dat");

        byte[] actualData = Encoding.UTF8.GetBytes("ExactFive");
        using var stream = new MemoryStream(actualData);

        // Claim contentLength is 50, but stream only has 9 bytes
        string etag = await backend.UploadPartAsync("test-bucket", "early-eof.dat", init.UploadId, 1, stream, 50);
        etag.Should().NotBeNull();

        var parts = await backend.ListPartsAsync("test-bucket", "early-eof.dat", init.UploadId);
        parts.Parts.Should().ContainSingle();
        parts.Parts[0].Size.Should().Be(9);

        var completeRes = await backend.CompleteMultipartUploadAsync("test-bucket", "early-eof.dat", init.UploadId, new CompleteMultipartUpload());
        completeRes.Should().NotBeNull();

        using var obj = await backend.GetObjectAsync("test-bucket", "early-eof.dat");
        obj.ContentLength.Should().Be(9);
        using var ms = new MemoryStream();
        await obj.Stream.CopyToAsync(ms);
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be("ExactFive");
    }

    [TestMethod]
    public async Task UploadPartAsync_WhenStreamExceedsMaxObjectSize_ThrowsEntityTooLarge()
    {
        var backend = new InMemoryStorageBackend { MaxObjectSize = 50 };
        await backend.CreateBucketAsync("test-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "stream-large.dat");

        byte[] largeData = new byte[100];
        using var stream = new MemoryStream(largeData);

        // contentLength <= 0 triggers the streaming loop
        var act = async () => await backend.UploadPartAsync("test-bucket", "stream-large.dat", init.UploadId, 1, stream, 0);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task UploadPartAsync_Validation_NullData_And_IntMaxExceeded()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("test-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "val.dat");

        var actNull = async () => await backend.UploadPartAsync("test-bucket", "val.dat", init.UploadId, 1, null!, 10);
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        var actTooLarge = async () => await backend.UploadPartAsync("test-bucket", "val.dat", init.UploadId, 1, Stream.Null, (long)int.MaxValue + 1);
        var ex = await actTooLarge.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task PutObjectAsync_WhenStreamEndsBeforeContentLength_BreaksAndResizesBuffer()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("test-bucket", null);

        byte[] actualData = Encoding.UTF8.GetBytes("ShortBody");
        using var stream = new MemoryStream(actualData);

        // Claim contentLength is 100, but only 9 bytes provided
        string etag = await backend.PutObjectAsync("test-bucket", "short.txt", stream, 100, "text/plain");
        etag.Should().NotBeNull();

        using var obj = await backend.GetObjectAsync("test-bucket", "short.txt");
        obj.ContentLength.Should().Be(9);
        using var ms = new MemoryStream();
        await obj.Stream.CopyToAsync(ms);
        Encoding.UTF8.GetString(ms.ToArray()).Should().Be("ShortBody");
    }

    [TestMethod]
    public async Task PutObjectAsync_Validation_NullData_And_IntMaxExceeded()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("test-bucket", null);

        var actNull = async () => await backend.PutObjectAsync("test-bucket", "null.txt", null!, 10, "text/plain");
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        var actTooLarge = async () => await backend.PutObjectAsync("test-bucket", "huge.txt", Stream.Null, (long)int.MaxValue + 1, "text/plain");
        var ex = await actTooLarge.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task GetBucket_WhenBucketNullOrEmpty_ThrowsNoSuchBucket()
    {
        var backend = new InMemoryStorageBackend();

        var actNull = () => backend.GetBucketLocationAsync(null!);
        await actNull.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);

        var actEmpty = () => backend.GetBucketLocationAsync("");
        await actEmpty.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);
    }

    [TestMethod]
    public async Task BucketMetadata_NullPropertyBranches_ThrowAppropriateExceptions()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("meta-b", null);

        // 1. GetBucketTagsAsync and GetBucketWebsiteAsync on completely fresh bucket (_BucketMeta has NO entry, TryGetValue is false)
        var actTagsFresh = () => backend.GetBucketTagsAsync("meta-b");
        await actTagsFresh.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchTagSet);

        var actWebFresh = () => backend.GetBucketWebsiteAsync("meta-b");
        await actWebFresh.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchWebsiteConfiguration);

        // 2. Populate _BucketMeta without setting tags or website (TryGetValue is true, but Tags/Website is null)
        await backend.SetBucketAclAsync("meta-b", new AccessControlPolicy(backend.DefaultOwner, new AccessControlList()));

        // GetBucketTagsAsync when _BucketMeta exists but Tags == null
        var actTags = () => backend.GetBucketTagsAsync("meta-b");
        await actTags.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchTagSet);

        // GetBucketWebsiteAsync when _BucketMeta exists but Website == null
        var actWeb = () => backend.GetBucketWebsiteAsync("meta-b");
        await actWeb.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchWebsiteConfiguration);

        // Also test DeleteBucketTagsAsync then GetBucketTagsAsync
        await backend.SetBucketTagsAsync("meta-b", new Tagging(new TagSet(new System.Collections.Generic.List<Tag> { new("k", "v") })));
        await backend.DeleteBucketTagsAsync("meta-b");
        await actTags.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchTagSet);

        // DeleteBucketWebsiteAsync then GetBucketWebsiteAsync
        await backend.SetBucketWebsiteAsync("meta-b", new WebsiteConfiguration());
        await backend.DeleteBucketWebsiteAsync("meta-b");
        await actWeb.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchWebsiteConfiguration);
    }

    [TestMethod]
    public async Task ListObjectsAsync_PaginationAndMarker_Branches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("list-b", null);

        foreach (var key in new[] { "apple", "banana", "cherry", "date" })
        {
            await backend.PutObjectAsync("list-b", key, new MemoryStream(new byte[] { 1 }), 1, "text/plain");
        }

        // V1 Marker: marker is "banana". "apple" and "banana" <= marker (continue), "cherry" and "date" > marker (yielded)
        var reqV1 = new S3Request("GET", "/list-b?marker=banana", new System.Collections.Generic.Dictionary<string, string> { { "marker", "banana" } }, new System.Collections.Generic.Dictionary<string, string[]>(), Stream.Null);
        reqV1.Marker = "banana";
        var resV1 = await backend.ListObjectsAsync("list-b", reqV1);
        resV1.Contents.Select(c => c.Key).Should().Equal("cherry", "date");

        // V2 ContinuationToken: continuation token is "banana"
        var reqV2 = new S3Request("GET", "/list-b?list-type=2&continuation-token=banana", new System.Collections.Generic.Dictionary<string, string> { { "list-type", "2" }, { "continuation-token", "banana" } }, new System.Collections.Generic.Dictionary<string, string[]>(), Stream.Null);
        reqV2.ContinuationToken = "banana";
        var resV2 = await backend.ListObjectsAsync("list-b", reqV2);
        resV2.Contents.Select(c => c.Key).Should().Equal("cherry", "date");
        resV2.KeyCount.Should().Be(2);

        // V2 StartAfter: start-after is "apple"
        var reqV2Sa = new S3Request("GET", "/list-b?list-type=2&start-after=apple", new System.Collections.Generic.Dictionary<string, string> { { "list-type", "2" }, { "start-after", "apple" } }, new System.Collections.Generic.Dictionary<string, string[]>(), Stream.Null);
        reqV2Sa.StartAfter = "apple";
        var resV2Sa = await backend.ListObjectsAsync("list-b", reqV2Sa);
        resV2Sa.Contents.Select(c => c.Key).Should().Equal("banana", "cherry", "date");
    }

    [TestMethod]
    public async Task GetObjectAsync_RangeAndMetadataPrefixing_Branches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("get-b", null);

        var metadata = new System.Collections.Generic.Dictionary<string, string>
        {
            ["x-amz-meta-custom"] = "val1",
            ["plain-key"] = "val2"
        };
        byte[] data = Encoding.UTF8.GetBytes("0123456789");
        await backend.PutObjectAsync("get-b", "obj.txt", new MemoryStream(data), data.Length, "text/plain", metadata);

        // 1. Full read: verify metadata prefixing
        using var fullObj = await backend.GetObjectAsync("get-b", "obj.txt");
        fullObj.Headers["x-amz-meta-custom"].Should().Be("val1");
        fullObj.Headers["x-amz-meta-plain-key"].Should().Be("val2");

        // 2. Range read with rangeStart specified and rangeEnd == null (rangeEnd ?? (total - 1))
        using var rangeNoEnd = await backend.GetObjectAsync("get-b", "obj.txt", rangeStart: 4, rangeEnd: null);
        rangeNoEnd.ContentLength.Should().Be(6); // 4..9 -> 6 bytes
        rangeNoEnd.Headers["x-amz-meta-custom"].Should().Be("val1");
        rangeNoEnd.Headers["x-amz-meta-plain-key"].Should().Be("val2");
        using var ms1 = new MemoryStream();
        await rangeNoEnd.Stream.CopyToAsync(ms1);
        Encoding.UTF8.GetString(ms1.ToArray()).Should().Be("456789");

        // 3. Range read with rangeStart == null and rangeEnd specified (rangeStart ?? 0)
        using var rangeNoStart = await backend.GetObjectAsync("get-b", "obj.txt", rangeStart: null, rangeEnd: 3);
        rangeNoStart.ContentLength.Should().Be(4); // 0..3 -> 4 bytes
        using var ms2 = new MemoryStream();
        await rangeNoStart.Stream.CopyToAsync(ms2);
        Encoding.UTF8.GetString(ms2.ToArray()).Should().Be("0123");
    }

    [TestMethod]
    public async Task DeleteMultipleObjectsAsync_NullGuards_AndEmptyKeys()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("del-b", null);

        // 1. request == null
        var resNullReq = await backend.DeleteMultipleObjectsAsync("del-b", null!);
        resNullReq.DeletedObjects.Should().BeEmpty();

        // 2. request.Objects == null
        var resNullObjs = await backend.DeleteMultipleObjectsAsync("del-b", new DeleteMultiple { Objects = null! });
        resNullObjs.DeletedObjects.Should().BeEmpty();

        // 3. request.Objects with empty or null keys
        var delWithEmptyKey = new DeleteMultiple
        {
            Objects = new System.Collections.Generic.List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object { Key = "" },
                new SharpS3.Models.Object { Key = null! }
            }
        };
        var resEmptyKeys = await backend.DeleteMultipleObjectsAsync("del-b", delWithEmptyKey);
        resEmptyKeys.DeletedObjects.Should().BeEmpty();
    }

    [TestMethod]
    public async Task ObjectOperations_NoSuchKey_ThrowsException()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("key-b", null);
        string nonExistentKey = "missing-key";

        var actGetTags = () => backend.GetObjectTagsAsync("key-b", nonExistentKey);
        await actGetTags.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actSetTags = () => backend.SetObjectTagsAsync("key-b", nonExistentKey, new Tagging());
        await actSetTags.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actDelTags = () => backend.DeleteObjectTagsAsync("key-b", nonExistentKey);
        await actDelTags.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actGetHold = () => backend.GetLegalHoldAsync("key-b", nonExistentKey);
        await actGetHold.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actSetHold = () => backend.SetLegalHoldAsync("key-b", nonExistentKey, new LegalHold("ON"));
        await actSetHold.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actGetRet = () => backend.GetRetentionAsync("key-b", nonExistentKey);
        await actGetRet.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actSetRet = () => backend.SetRetentionAsync("key-b", nonExistentKey, new Retention());
        await actSetRet.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actGetAcl = () => backend.GetObjectAclAsync("key-b", nonExistentKey);
        await actGetAcl.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actSetAcl = () => backend.SetObjectAclAsync("key-b", nonExistentKey, new AccessControlPolicy(backend.DefaultOwner, new AccessControlList()));
        await actSetAcl.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actDelAcl = () => backend.DeleteObjectAclAsync("key-b", nonExistentKey);
        await actDelAcl.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);

        var actRestore = () => backend.RestoreObjectAsync("key-b", nonExistentKey, new RestoreRequest());
        await actRestore.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchKey);
    }

    [TestMethod]
    public async Task ObjectProperties_Defaults_And_SetValues()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("prop-b", null);
        await backend.PutObjectAsync("prop-b", "file.dat", new MemoryStream(new byte[] { 1 }), 1, "text/plain");

        // LegalHold default when null is "OFF"
        var holdDefault = await backend.GetLegalHoldAsync("prop-b", "file.dat");
        holdDefault.Status.Should().Be("OFF");

        // LegalHold after set
        await backend.SetLegalHoldAsync("prop-b", "file.dat", new LegalHold("ON"));
        var holdSet = await backend.GetLegalHoldAsync("prop-b", "file.dat");
        holdSet.Status.Should().Be("ON");

        // Retention default when null
        var retDefault = await backend.GetRetentionAsync("prop-b", "file.dat");
        retDefault.Mode.Should().Be(RetentionModeEnum.None);

        // Retention after set
        var retention = new Retention(RetentionModeEnum.Governance, DateTime.UtcNow.AddDays(1));
        await backend.SetRetentionAsync("prop-b", "file.dat", retention);
        var retSet = await backend.GetRetentionAsync("prop-b", "file.dat");
        retSet.Mode.Should().Be(RetentionModeEnum.Governance);

        // Object Tags default when null is empty Tagging
        var tagsDefault = await backend.GetObjectTagsAsync("prop-b", "file.dat");
        tagsDefault.Tags.Should().BeNull();

        await backend.SetObjectTagsAsync("prop-b", "file.dat", new Tagging(new TagSet(new System.Collections.Generic.List<Tag> { new("env", "test") })));
        var tagsSet = await backend.GetObjectTagsAsync("prop-b", "file.dat");
        tagsSet.Tags.Tags.Should().ContainSingle();

        await backend.DeleteObjectTagsAsync("prop-b", "file.dat");
        var tagsAfterDelete = await backend.GetObjectTagsAsync("prop-b", "file.dat");
        tagsAfterDelete.Tags.Should().BeNull();

        // Object ACL default when null
        var aclDefault = await backend.GetObjectAclAsync("prop-b", "file.dat");
        aclDefault.Owner.Should().Be(backend.DefaultOwner);

        var customAcl = new AccessControlPolicy(new Owner("custom", "Custom"), new AccessControlList());
        await backend.SetObjectAclAsync("prop-b", "file.dat", customAcl);
        var aclSet = await backend.GetObjectAclAsync("prop-b", "file.dat");
        aclSet.Owner.ID.Should().Be("custom");

        await backend.DeleteObjectAclAsync("prop-b", "file.dat");
        var aclAfterDelete = await backend.GetObjectAclAsync("prop-b", "file.dat");
        aclAfterDelete.Owner.Should().Be(backend.DefaultOwner);

        // RestoreObjectAsync success
        var restoreRes = await backend.RestoreObjectAsync("prop-b", "file.dat", new RestoreRequest());
        restoreRes.AlreadyRestored.Should().BeFalse();
    }

    [TestMethod]
    public async Task UploadPartCopyAsync_RangeClamping_And_FullCopy()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("src-b", null);
        await backend.CreateBucketAsync("dest-b", null);

        byte[] srcData = Encoding.UTF8.GetBytes("0123456789");
        await backend.PutObjectAsync("src-b", "source.bin", new MemoryStream(srcData), srcData.Length, "application/octet-stream");

        var init = await backend.CreateMultipartUploadAsync("dest-b", "dest.bin");

        // Clamping branch: rangeEnd (50) >= srcData.Length (10) -> clamped to 9 (length becomes 10 - 2 = 8 bytes)
        var copyPartRes = await backend.UploadPartCopyAsync("src-b", "source.bin", "dest-b", "dest.bin", init.UploadId, 1, rangeStart: 2, rangeEnd: 50);
        copyPartRes.ETag.Should().NotBeNull();

        var parts = await backend.ListPartsAsync("dest-b", "dest.bin", init.UploadId);
        parts.Parts.Should().ContainSingle();
        parts.Parts[0].Size.Should().Be(8);
    }

    [TestMethod]
    public async Task ListObjectsAsync_Prefixes_Delimiters_And_Truncation_Branches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("list-deep-b", null);

        var keys = new[]
        {
            "photos/2026/jan.jpg",
            "photos/2026/feb.jpg",
            "photos/summer.png",
            "root.txt",
            "zebra.txt"
        };
        foreach (var key in keys)
        {
            await backend.PutObjectAsync("list-deep-b", key, new MemoryStream(new byte[] { 1 }), 1, "text/plain");
        }

        // 1. Delimiter "/" with prefix "photos/"
        // Expect common prefix "photos/2026/" (duplicate suppressed) and content "photos/summer.png"
        var reqDelim = new S3Request("GET", "/list-deep-b?prefix=photos/&delimiter=/", new Dictionary<string, string> { { "prefix", "photos/" }, { "delimiter", "/" } }, new Dictionary<string, string[]>(), Stream.Null);
        reqDelim.Prefix = "photos/";
        reqDelim.Delimiter = "/";
        var resDelim = await backend.ListObjectsAsync("list-deep-b", reqDelim);
        resDelim.CommonPrefixes.Select(cp => cp.Prefix).Should().Equal("photos/2026/");
        resDelim.Contents.Select(c => c.Key).Should().Equal("photos/summer.png");

        // 2. V1 Truncation: max-keys = 2, prefix = null
        var reqV1Trunc = new S3Request("GET", "/list-deep-b?max-keys=2", new Dictionary<string, string> { { "max-keys", "2" } }, new Dictionary<string, string[]>(), Stream.Null);
        reqV1Trunc.MaxKeys = 2;
        reqV1Trunc.Prefix = null;
        var resV1Trunc = await backend.ListObjectsAsync("list-deep-b", reqV1Trunc);
        resV1Trunc.IsTruncated.Should().BeTrue();
        resV1Trunc.NextMarker.Should().Be("photos/2026/jan.jpg");
        resV1Trunc.NextContinuationToken.Should().BeNull();
        resV1Trunc.Contents.Count.Should().Be(2);

        // 3. V2 Truncation with StartAfter: max-keys = 1, start-after = "photos/2026/jan.jpg"
        var reqV2Trunc = new S3Request("GET", "/list-deep-b?list-type=2&max-keys=1&start-after=photos/2026/jan.jpg", new Dictionary<string, string> { { "list-type", "2" }, { "max-keys", "1" }, { "start-after", "photos/2026/jan.jpg" } }, new Dictionary<string, string[]>(), Stream.Null);
        reqV2Trunc.MaxKeys = 1;
        reqV2Trunc.StartAfter = "photos/2026/jan.jpg";
        var resV2Trunc = await backend.ListObjectsAsync("list-deep-b", reqV2Trunc);
        resV2Trunc.IsTruncated.Should().BeTrue();
        resV2Trunc.NextContinuationToken.Should().Be("photos/summer.png");
        resV2Trunc.KeyCount.Should().Be(1);

        // 4. MaxKeys clamping boundaries: <= 0 clamps to 1, > 1000 clamps to 1000
        var reqMin = new S3Request("GET", "/list-deep-b", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        reqMin.MaxKeys = -5;
        var resMin = await backend.ListObjectsAsync("list-deep-b", reqMin);
        resMin.MaxKeys.Should().Be(1);

        var reqMax = new S3Request("GET", "/list-deep-b", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        reqMax.MaxKeys = 5000;
        var resMax = await backend.ListObjectsAsync("list-deep-b", reqMax);
        resMax.MaxKeys.Should().Be(1000);
    }

    [TestMethod]
    public async Task RangeRequests_EdgeCases_And_Clamping_Full()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("range-full-b", null);

        byte[] payload = Encoding.UTF8.GetBytes("0123456789"); // 10 bytes: 0..9
        await backend.PutObjectAsync("range-full-b", "numbers.txt", new MemoryStream(payload), payload.Length, "text/plain");

        // 1. GetObjectAsync: end >= total clamps to total - 1
        using var clampedObj = await backend.GetObjectAsync("range-full-b", "numbers.txt", rangeStart: 5, rangeEnd: 100);
        clampedObj.ContentLength.Should().Be(5); // 5..9 -> 5 bytes
        using var msClamped = new MemoryStream();
        await clampedObj.Stream.CopyToAsync(msClamped);
        Encoding.UTF8.GetString(msClamped.ToArray()).Should().Be("56789");

        // 2. GetObjectAsync: start > end throws InvalidRange
        var actStartGtEnd = () => backend.GetObjectAsync("range-full-b", "numbers.txt", rangeStart: 6, rangeEnd: 4);
        await actStartGtEnd.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.InvalidRange);

        // 3. GetObjectAsync: start >= total throws InvalidRange
        var actStartGteTotal = () => backend.GetObjectAsync("range-full-b", "numbers.txt", rangeStart: 10, rangeEnd: 15);
        await actStartGteTotal.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.InvalidRange);

        // 4. UploadPartCopyAsync range validations
        await backend.CreateBucketAsync("dest-range-b", null);
        var init = await backend.CreateMultipartUploadAsync("dest-range-b", "dest.dat");

        // start < 0
        var actNegStart = () => backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 1, rangeStart: -1, rangeEnd: 5);
        await actNegStart.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.InvalidRange);

        // start > end
        var actCopyStartGtEnd = () => backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 1, rangeStart: 5, rangeEnd: 2);
        await actCopyStartGtEnd.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.InvalidRange);

        // start >= srcData.Length
        var actCopyStartGteTotal = () => backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 1, rangeStart: 10, rangeEnd: 20);
        await actCopyStartGteTotal.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.InvalidRange);

        // rangeStart == null && rangeEnd == null -> full copy (0..9)
        var copyFullRes = await backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 1, rangeStart: null, rangeEnd: null);
        copyFullRes.ETag.Should().NotBeNull();

        // rangeStart == null && rangeEnd != null -> suffix copy (0..3)
        var copyPrefixRes = await backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 2, rangeStart: null, rangeEnd: 3);
        copyPrefixRes.ETag.Should().NotBeNull();

        // rangeStart != null && rangeEnd == null -> open-ended copy (7..9)
        var copySuffixRes = await backend.UploadPartCopyAsync("range-full-b", "numbers.txt", "dest-range-b", "dest.dat", init.UploadId, 3, rangeStart: 7, rangeEnd: null);
        copySuffixRes.ETag.Should().NotBeNull();

        var parts = await backend.ListPartsAsync("dest-range-b", "dest.dat", init.UploadId);
        parts.Parts.Should().HaveCount(3);
        parts.Parts[0].Size.Should().Be(10);
        parts.Parts[1].Size.Should().Be(4);
        parts.Parts[2].Size.Should().Be(3);
    }

    [TestMethod]
    public async Task BucketMetadata_AllProperties_SetGetDelete_AllBranches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("meta-all-b", null);

        // 1. GetBucketLocationAsync on valid bucket
        var loc = await backend.GetBucketLocationAsync("meta-all-b");
        loc.Text.Should().Be("us-east-1");

        // 2. Bucket ACL: default -> set -> get -> delete -> get
        var defaultAcl = await backend.GetBucketAclAsync("meta-all-b");
        defaultAcl.Owner.Should().Be(backend.DefaultOwner);

        var customAcl = new AccessControlPolicy(new Owner("custom-owner", "Custom"), new AccessControlList());
        await backend.SetBucketAclAsync("meta-all-b", customAcl);
        var getCustomAcl = await backend.GetBucketAclAsync("meta-all-b");
        getCustomAcl.Owner.ID.Should().Be("custom-owner");

        await backend.DeleteBucketAclAsync("meta-all-b");
        var afterDelAcl = await backend.GetBucketAclAsync("meta-all-b");
        afterDelAcl.Owner.Should().Be(backend.DefaultOwner);

        // DeleteBucketAclAsync on bucket with no _BucketMeta entry
        await backend.CreateBucketAsync("fresh-meta-b", null);
        await backend.DeleteBucketAclAsync("fresh-meta-b");

        // 3. Bucket Tags: set -> get -> delete
        var tags = new Tagging(new TagSet(new System.Collections.Generic.List<Tag> { new("team", "infra") }));
        await backend.SetBucketTagsAsync("meta-all-b", tags);
        var getTags = await backend.GetBucketTagsAsync("meta-all-b");
        getTags.Tags.Tags.Should().ContainSingle();

        await backend.DeleteBucketTagsAsync("meta-all-b");
        var actTagsAfterDel = () => backend.GetBucketTagsAsync("meta-all-b");
        await actTagsAfterDel.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchTagSet);

        // DeleteBucketTagsAsync on bucket with no _BucketMeta entry
        await backend.DeleteBucketTagsAsync("fresh-meta-b");

        // 4. Bucket Versioning: default (Suspended) -> set -> get
        var defaultVer = await backend.GetBucketVersioningAsync("fresh-meta-b");
        defaultVer.Status.Should().Be(VersioningStatusEnum.Suspended);

        var verConfig = new VersioningConfiguration { Status = VersioningStatusEnum.Enabled };
        await backend.SetBucketVersioningAsync("meta-all-b", verConfig);
        var getVer = await backend.GetBucketVersioningAsync("meta-all-b");
        getVer.Status.Should().Be(VersioningStatusEnum.Enabled);

        // 5. Bucket Website: set -> get -> delete -> delete on fresh
        var webConfig = new WebsiteConfiguration { IndexDocument = new IndexDocument { Suffix = "index.html" } };
        await backend.SetBucketWebsiteAsync("meta-all-b", webConfig);
        var getWeb = await backend.GetBucketWebsiteAsync("meta-all-b");
        getWeb.IndexDocument!.Suffix.Should().Be("index.html");

        await backend.DeleteBucketWebsiteAsync("meta-all-b");
        var actWebAfterDel = () => backend.GetBucketWebsiteAsync("meta-all-b");
        await actWebAfterDel.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchWebsiteConfiguration);

        await backend.DeleteBucketWebsiteAsync("fresh-meta-b");

        // 6. Bucket Logging: default -> set -> get
        var defaultLog = await backend.GetBucketLoggingAsync("fresh-meta-b");
        defaultLog.Enabled.Should().NotBeNull();

        var logStatus = new BucketLoggingStatus();
        await backend.SetBucketLoggingAsync("meta-all-b", logStatus);
        var getLog = await backend.GetBucketLoggingAsync("meta-all-b");
        getLog.Should().NotBeNull();

        // 7. DeleteBucketAsync on non-empty bucket throws BucketNotEmpty
        await backend.PutObjectAsync("meta-all-b", "dummy.txt", new MemoryStream(new byte[] { 1 }), 1, "text/plain");
        var actDelNonEmpty = () => backend.DeleteBucketAsync("meta-all-b");
        await actDelNonEmpty.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.BucketNotEmpty);
    }

    [TestMethod]
    public async Task StreamReading_ZeroByte_And_EarlyEof_And_ContentTypeTernary()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("stream-opt-b", null);

        // 1. Zero-byte stream PutObjectAsync
        string emptyEtag = await backend.PutObjectAsync("stream-opt-b", "empty.dat", Stream.Null, 0, null);
        emptyEtag.Should().Be("\"d41d8cd98f00b204e9800998ecf8427e\"");

        using var emptyObj = await backend.GetObjectAsync("stream-opt-b", "empty.dat");
        emptyObj.ContentLength.Should().Be(0);
        emptyObj.ContentType.Should().Be("binary/octet-stream");

        // 2. ContentType branches: "" -> "binary/octet-stream", "image/png" -> "image/png"
        await backend.PutObjectAsync("stream-opt-b", "empty-str.dat", Stream.Null, 0, "");
        using var emptyStrObj = await backend.GetObjectAsync("stream-opt-b", "empty-str.dat");
        emptyStrObj.ContentType.Should().Be("binary/octet-stream");

        await backend.PutObjectAsync("stream-opt-b", "image.png", new MemoryStream(new byte[] { 1 }), 1, "image/png");
        using var imgObj = await backend.GetObjectAsync("stream-opt-b", "image.png");
        imgObj.ContentType.Should().Be("image/png");

        // 3. CopyObjectAsync with custom metadata & MaxObjectSize enforcement
        var meta = new Dictionary<string, string> { { "project", "sharps3" } };
        await backend.PutObjectAsync("stream-opt-b", "original.txt", new MemoryStream(new byte[] { 1, 2, 3 }), 3, "text/plain", meta);

        await backend.CreateBucketAsync("dest-opt-b", null);
        var copyRes = await backend.CopyObjectAsync("stream-opt-b", "original.txt", "dest-opt-b", "copied.txt");
        copyRes.ETag.Should().NotBeNull();

        var copiedMeta = await backend.GetObjectMetadataAsync("dest-opt-b", "copied.txt");
        copiedMeta!.Metadata.Should().ContainKey("project");
        copiedMeta.Metadata["project"].Should().Be("sharps3");

        // CopyObjectAsync exceeding MaxObjectSize
        var limitedBackend = new InMemoryStorageBackend { MaxObjectSize = 2 };
        await limitedBackend.CreateBucketAsync("lim-b", null);
        await limitedBackend.PutObjectAsync("lim-b", "orig.bin", new MemoryStream(new byte[] { 1, 2 }), 2, "application/octet-stream");
        // Pretend object length was enlarged or MaxObjectSize decreased to 1
        limitedBackend.MaxObjectSize = 1;
        var actCopyTooLarge = () => limitedBackend.CopyObjectAsync("lim-b", "orig.bin", "lim-b", "copy.bin");
        await actCopyTooLarge.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.EntityTooLarge);

        // 4. DeleteMultipleObjectsAsync with valid keys
        var delReq = new DeleteMultiple
        {
            Objects = new System.Collections.Generic.List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object { Key = "original.txt" }
            }
        };
        var delRes = await backend.DeleteMultipleObjectsAsync("stream-opt-b", delReq);
        delRes.DeletedObjects.Should().ContainSingle();
        delRes.DeletedObjects[0].Key.Should().Be("original.txt");
        (await backend.GetObjectMetadataAsync("stream-opt-b", "original.txt")).Should().BeNull();
    }

    [TestMethod]
    public async Task Multipart_Lifecycle_ErrorHandling_And_Limits()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("mp-life-b", null);

        // 1. ListObjectVersionsAsync
        await backend.PutObjectAsync("mp-life-b", "doc1.txt", new MemoryStream(new byte[] { 1 }), 1, "text/plain");
        await backend.PutObjectAsync("mp-life-b", "doc2.txt", new MemoryStream(new byte[] { 2 }), 1, "text/plain");
        var versions = await backend.ListObjectVersionsAsync("mp-life-b");
        versions.Versions.Should().HaveCount(2);
        versions.Versions[0].VersionId.Should().Be("null");

        // 2. ListMultipartUploadsAsync
        var init1 = await backend.CreateMultipartUploadAsync("mp-life-b", "file1.bin");
        var init2 = await backend.CreateMultipartUploadAsync("mp-life-b", "file2.bin");
        var mpList = await backend.ListMultipartUploadsAsync("mp-life-b");
        mpList.Uploads.Should().HaveCount(2);
        mpList.Uploads[0].Key.Should().Be("file1.bin");
        mpList.Uploads[0].Owner.Should().Be(backend.DefaultOwner);

        // 3. AbortMultipartUploadAsync validations
        var actAbortNoBucket = () => backend.AbortMultipartUploadAsync("nonexistent-b", "file1.bin", init1.UploadId);
        await actAbortNoBucket.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);

        var actAbortNoUpload = () => backend.AbortMultipartUploadAsync("mp-life-b", "file1.bin", "nonexistent-upload");
        await actAbortNoUpload.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchUpload);

        await backend.AbortMultipartUploadAsync("mp-life-b", "file1.bin", init1.UploadId);

        // 4. ListPartsAsync validations
        var actListPartsNoBucket = () => backend.ListPartsAsync("nonexistent-b", "file2.bin", init2.UploadId);
        await actListPartsNoBucket.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);

        var actListPartsNoUpload = () => backend.ListPartsAsync("mp-life-b", "file2.bin", "nonexistent-upload");
        await actListPartsNoUpload.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchUpload);

        // 5. CompleteMultipartUploadAsync validations
        var actCompleteNoBucket = () => backend.CompleteMultipartUploadAsync("nonexistent-b", "file2.bin", init2.UploadId, new CompleteMultipartUpload());
        await actCompleteNoBucket.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);

        var actCompleteNoUpload = () => backend.CompleteMultipartUploadAsync("mp-life-b", "file2.bin", "nonexistent-upload", new CompleteMultipartUpload());
        await actCompleteNoUpload.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchUpload);

        // 6. CompleteMultipartUploadAsync exceeding MaxObjectSize
        var limitedBackend = new InMemoryStorageBackend { MaxObjectSize = 5 };
        await limitedBackend.CreateBucketAsync("mp-lim-b", null);
        var limInit = await limitedBackend.CreateMultipartUploadAsync("mp-lim-b", "lim.bin");
        await limitedBackend.UploadPartAsync("mp-lim-b", "lim.bin", limInit.UploadId, 1, new MemoryStream(new byte[4]), 4);
        await limitedBackend.UploadPartAsync("mp-lim-b", "lim.bin", limInit.UploadId, 2, new MemoryStream(new byte[4]), 4);

        var actCompleteTooLarge = () => limitedBackend.CompleteMultipartUploadAsync("mp-lim-b", "lim.bin", limInit.UploadId, new CompleteMultipartUpload());
        await actCompleteTooLarge.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.EntityTooLarge);

        // 7. UploadPartCopyAsync destination bucket validation and MaxObjectSize enforcement
        await backend.CreateBucketAsync("src-mp-b", null);
        await backend.PutObjectAsync("src-mp-b", "src.bin", new MemoryStream(new byte[10]), 10, "application/octet-stream");

        var actCopyPartNoDestBucket = () => backend.UploadPartCopyAsync("src-mp-b", "src.bin", "missing-dest-b", "dest.bin", init2.UploadId, 1);
        await actCopyPartNoDestBucket.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.NoSuchBucket);

        backend.MaxObjectSize = 5;
        var actCopyPartTooLarge = () => backend.UploadPartCopyAsync("src-mp-b", "src.bin", "mp-life-b", "file2.bin", init2.UploadId, 1);
        await actCopyPartTooLarge.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.EntityTooLarge);
    }
}
