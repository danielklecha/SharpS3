namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Models;
using SharpS3.Serialization;

[TestClass]
public class ETagHelperCoverageTests
{
    [TestMethod]
    public void ETagHelper_FormatETag_AllPermutations()
    {
        // Null and Empty
        ETagHelper.FormatETag(null).Should().BeNull();
        ETagHelper.FormatETag(string.Empty).Should().Be(string.Empty);
        ETagHelper.FormatETag("   ").Should().Be(string.Empty);

        // Single quote
        ETagHelper.FormatETag("\"").Should().Be("\"\"");

        // Already quoted
        ETagHelper.FormatETag("\"etag-123\"").Should().Be("\"etag-123\"");
        ETagHelper.FormatETag("  \"quoted-with-spaces\"  ").Should().Be("\"quoted-with-spaces\"");

        // Unquoted (missing both quotes)
        ETagHelper.FormatETag("etag-plain").Should().Be("\"etag-plain\"");
        ETagHelper.FormatETag("  etag-plain-spaces  ").Should().Be("\"etag-plain-spaces\"");

        // Missing end quote
        ETagHelper.FormatETag("\"missing-end").Should().Be("\"missing-end\"");

        // Missing start quote
        ETagHelper.FormatETag("missing-start\"").Should().Be("\"missing-start\"");
    }

    [TestMethod]
    public void S3ObjectResponse_Constructor_NullArgumentChecks()
    {
        using var validHttp = new HttpResponseMessage(HttpStatusCode.OK);
        using var validStream = new MemoryStream();

        // httpResponse is null
        Action actNullHttp = () => new S3ObjectResponse(
            null!,
            validStream,
            0,
            "text/plain",
            "\"123\"",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());
        actNullHttp.Should().Throw<ArgumentNullException>().WithParameterName("httpResponse");

        // stream is null
        Action actNullStream = () => new S3ObjectResponse(
            validHttp,
            null!,
            0,
            "text/plain",
            "\"123\"",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>());
        actNullStream.Should().Throw<ArgumentNullException>().WithParameterName("stream");
    }

    [TestMethod]
    public async Task S3ObjectResponse_MultipleDispose_DoesNotThrow()
    {
        var http = new HttpResponseMessage(HttpStatusCode.OK);
        var stream = new MemoryStream();
        var resp = new S3ObjectResponse(http, stream, 0, null, null, null, null!);

        resp.Dispose();
        resp.Dispose(); // second call when already disposed

        var http2 = new HttpResponseMessage(HttpStatusCode.OK);
        var stream2 = new MemoryStream();
        var resp2 = new S3ObjectResponse(http2, stream2, 0, null, null, null, null!);

        await resp2.DisposeAsync();
        await resp2.DisposeAsync(); // second call when already disposed
    }

    [TestMethod]
    public void CopyPartResult_Constructors_Properties_And_SerializationHelpers()
    {
        // Default constructor
        var res = new CopyPartResult();
        res.ETag.Should().BeNull();
        res.LastModified.Kind.Should().Be(DateTimeKind.Utc);
        res.ChecksumCRC32.Should().BeNull();
        res.ChecksumCRC32C.Should().BeNull();
        res.ChecksumCRC64NVME.Should().BeNull();
        res.ChecksumSHA1.Should().BeNull();
        res.ChecksumSHA256.Should().BeNull();
        res.ChecksumType.Should().BeNull();

        res.ShouldSerializeETag().Should().BeFalse();
        res.ShouldSerializeChecksumCRC32().Should().BeFalse();
        res.ShouldSerializeChecksumCRC32C().Should().BeFalse();
        res.ShouldSerializeChecksumCRC64NVME().Should().BeFalse();
        res.ShouldSerializeChecksumSHA1().Should().BeFalse();
        res.ShouldSerializeChecksumSHA256().Should().BeFalse();
        res.ShouldSerializeChecksumType().Should().BeFalse();

        // Parameterized constructor
        var dt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var resParam = new CopyPartResult("unquoted-etag", dt);
        resParam.ETag.Should().Be("\"unquoted-etag\"");
        resParam.LastModified.Kind.Should().Be(DateTimeKind.Utc);
        resParam.ShouldSerializeETag().Should().BeTrue();

        // ETag formatting branches on model
        res.ETag = null!;
        res.ETag.Should().BeNull();
        res.ShouldSerializeETag().Should().BeFalse();

        res.ETag = "";
        res.ETag.Should().Be("");
        res.ShouldSerializeETag().Should().BeFalse();

        res.ETag = "raw-part";
        res.ETag.Should().Be("\"raw-part\"");
        res.ShouldSerializeETag().Should().BeTrue();

        res.ETag = "\"missing-end";
        res.ETag.Should().Be("\"missing-end\"");

        res.ETag = "missing-start\"";
        res.ETag.Should().Be("\"missing-start\"");

        res.ETag = "\"already-quoted\"";
        res.ETag.Should().Be("\"already-quoted\"");

        // LastModified specify kind
        res.LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        res.LastModified.Kind.Should().Be(DateTimeKind.Utc);

        // Checksums and ShouldSerialize
        res.ChecksumCRC32 = "crc32";
        res.ChecksumCRC32.Should().Be("crc32");
        res.ShouldSerializeChecksumCRC32().Should().BeTrue();

        res.ChecksumCRC32C = "crc32c";
        res.ChecksumCRC32C.Should().Be("crc32c");
        res.ShouldSerializeChecksumCRC32C().Should().BeTrue();

        res.ChecksumCRC64NVME = "crc64";
        res.ChecksumCRC64NVME.Should().Be("crc64");
        res.ShouldSerializeChecksumCRC64NVME().Should().BeTrue();

        res.ChecksumSHA1 = "sha1";
        res.ChecksumSHA1.Should().Be("sha1");
        res.ShouldSerializeChecksumSHA1().Should().BeTrue();

        res.ChecksumSHA256 = "sha256";
        res.ChecksumSHA256.Should().Be("sha256");
        res.ShouldSerializeChecksumSHA256().Should().BeTrue();

        res.ChecksumType = "FULL_OBJECT";
        res.ChecksumType.Should().Be("FULL_OBJECT");
        res.ShouldSerializeChecksumType().Should().BeTrue();
    }

    [TestMethod]
    public void CopyObjectResult_Constructors_Properties_And_SerializationHelpers()
    {
        // Default constructor
        var res = new CopyObjectResult();
        res.ETag.Should().BeNull();
        res.LastModified.Kind.Should().Be(DateTimeKind.Utc);
        res.ChecksumCRC32.Should().BeNull();
        res.ChecksumCRC32C.Should().BeNull();
        res.ChecksumCRC64NVME.Should().BeNull();
        res.ChecksumSHA1.Should().BeNull();
        res.ChecksumSHA256.Should().BeNull();
        res.ChecksumType.Should().BeNull();

        res.ShouldSerializeETag().Should().BeFalse();
        res.ShouldSerializeChecksumCRC32().Should().BeFalse();
        res.ShouldSerializeChecksumCRC32C().Should().BeFalse();
        res.ShouldSerializeChecksumCRC64NVME().Should().BeFalse();
        res.ShouldSerializeChecksumSHA1().Should().BeFalse();
        res.ShouldSerializeChecksumSHA256().Should().BeFalse();
        res.ShouldSerializeChecksumType().Should().BeFalse();

        // Parameterized constructor
        var dt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var resParam = new CopyObjectResult("unquoted-etag", dt);
        resParam.ETag.Should().Be("\"unquoted-etag\"");
        resParam.LastModified.Kind.Should().Be(DateTimeKind.Utc);
        resParam.ShouldSerializeETag().Should().BeTrue();

        // ETag formatting branches on model
        res.ETag = null!;
        res.ETag.Should().BeNull();
        res.ShouldSerializeETag().Should().BeFalse();

        res.ETag = "";
        res.ETag.Should().Be("");
        res.ShouldSerializeETag().Should().BeFalse();

        res.ETag = "raw-obj";
        res.ETag.Should().Be("\"raw-obj\"");
        res.ShouldSerializeETag().Should().BeTrue();

        res.ETag = "\"missing-end";
        res.ETag.Should().Be("\"missing-end\"");

        res.ETag = "missing-start\"";
        res.ETag.Should().Be("\"missing-start\"");

        res.ETag = "\"already-quoted\"";
        res.ETag.Should().Be("\"already-quoted\"");

        // LastModified specify kind
        res.LastModified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
        res.LastModified.Kind.Should().Be(DateTimeKind.Utc);

        // Checksums and ShouldSerialize
        res.ChecksumCRC32 = "crc32";
        res.ChecksumCRC32.Should().Be("crc32");
        res.ShouldSerializeChecksumCRC32().Should().BeTrue();

        res.ChecksumCRC32C = "crc32c";
        res.ChecksumCRC32C.Should().Be("crc32c");
        res.ShouldSerializeChecksumCRC32C().Should().BeTrue();

        res.ChecksumCRC64NVME = "crc64";
        res.ChecksumCRC64NVME.Should().Be("crc64");
        res.ShouldSerializeChecksumCRC64NVME().Should().BeTrue();

        res.ChecksumSHA1 = "sha1";
        res.ChecksumSHA1.Should().Be("sha1");
        res.ShouldSerializeChecksumSHA1().Should().BeTrue();

        res.ChecksumSHA256 = "sha256";
        res.ChecksumSHA256.Should().Be("sha256");
        res.ShouldSerializeChecksumSHA256().Should().BeTrue();

        res.ChecksumType = "FULL_OBJECT";
        res.ChecksumType.Should().Be("FULL_OBJECT");
        res.ShouldSerializeChecksumType().Should().BeTrue();
    }

    [TestMethod]
    public void OtherModels_ETag_FormatVerification()
    {
        var part = new Part { ETag = "part-unquoted" };
        part.ETag.Should().Be("\"part-unquoted\"");

        var meta = new ObjectMetadata { ETag = "meta-unquoted" };
        meta.ETag.Should().Be("\"meta-unquoted\"");

        var comp = new CompleteMultipartUploadResult { ETag = "comp-unquoted" };
        comp.ETag.Should().Be("\"comp-unquoted\"");

        var entity = new VersionedEntity { ETag = "entity-unquoted" };
        entity.ETag.Should().Be("\"entity-unquoted\"");
    }

    [TestMethod]
    public void VersionedEntity_NegativeSize_ThrowsArgumentOutOfRangeException()
    {
        var entity = new VersionedEntity();
        Action act = () => entity.Size = -1;
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Size");

        entity.Size = null;
        entity.Size.Should().BeNull();

        entity.Size = 100;
        entity.Size.Should().Be(100);
    }

    [TestMethod]
    public void Part_NegativePartNumber_Or_Size_ThrowsArgumentOutOfRangeException()
    {
        var part = new Part();
        Action actPartNumber = () => part.PartNumber = -1;
        actPartNumber.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("PartNumber");

        Action actSize = () => part.Size = -1;
        actSize.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Size");

        part.PartNumber = 1;
        part.PartNumber.Should().Be(1);

        part.Size = 100;
        part.Size.Should().Be(100);
    }

    [TestMethod]
    public void ObjectMetadata_NegativeSize_ThrowsArgumentOutOfRangeException()
    {
        var meta = new ObjectMetadata();
        Action act = () => meta.Size = -1;
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("Size");

        meta.Size = 100;
        meta.Size.Should().Be(100);
    }

    [TestMethod]
    public void ObjectMetadata_ShouldSerializeOwner_AllBranchesCovered()
    {
        var meta = new ObjectMetadata();

        // Branch 1: Owner == null -> returns false
        meta.Owner = null!;
        meta.ShouldSerializeOwner().Should().BeFalse();

        // Branch 2: Owner != null and SuppressListOwner == true -> returns false
        meta.Owner = new Owner("owner-id", "owner-name");
        ResponseSerializationContext.Run(1, suppressListOwner: true, false, () =>
        {
            meta.ShouldSerializeOwner().Should().BeFalse();
            return "ok";
        });

        // Branch 3: Owner != null and SuppressListOwner == false -> returns true
        meta.ShouldSerializeOwner().Should().BeTrue();
    }

    [TestMethod]
    public void SelectObjectContentRequest_AutoProperties_AllowNull_And_DefaultValues()
    {
        var req = new SelectObjectContentRequest();
        req.RequestProgress.Should().NotBeNull();
        req.InputSerialization.Should().NotBeNull();
        req.OutputSerialization.Should().NotBeNull();
        req.ScanRange.Should().NotBeNull();

        req.RequestProgress = null;
        req.RequestProgress.Should().BeNull();

        req.InputSerialization = null!;
        req.InputSerialization.Should().BeNull();

        req.OutputSerialization = null!;
        req.OutputSerialization.Should().BeNull();

        req.ScanRange = null;
        req.ScanRange.Should().BeNull();
    }

    [TestMethod]
    public void ListVersionsResult_Constructors_Validation_And_ShouldSerialize()
    {
        // Parameterized constructor validations
        Action actNullName = () => new ListVersionsResult(null!, new List<ObjectVersion>(), new List<DeleteMarker>(), 100);
        actNullName.Should().Throw<ArgumentNullException>().WithParameterName("name");

        Action actEmptyName = () => new ListVersionsResult("", new List<ObjectVersion>(), new List<DeleteMarker>(), 100);
        actEmptyName.Should().Throw<ArgumentNullException>().WithParameterName("name");

        var versions = new List<ObjectVersion> { new ObjectVersion { Key = "v-key", VersionId = "v1" } };
        var deleteMarkers = new List<DeleteMarker> { new DeleteMarker { Key = "dm-key", VersionId = "dm1" } };
        var commonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("sub/") };

        var full = new ListVersionsResult(
            name: "test-bucket",
            versions: versions,
            deleteMarkers: deleteMarkers,
            maxKeys: 50,
            prefix: "folder/",
            keyMarker: "km",
            versionIdMarker: "vm",
            isTruncated: true,
            nextKeyMarker: "nkm",
            nextVersionIdMarker: "nvm",
            delimiter: "/",
            commonPrefixes: commonPrefixes,
            encodingType: "url",
            bucketRegion: "us-east-1");

        full.Name.Should().Be("test-bucket");
        full.Versions.Should().HaveCount(1);
        full.DeleteMarkers.Should().HaveCount(1);
        full.MaxKeys.Should().Be(50);
        full.Prefix.Should().Be("folder/");
        full.KeyMarker.Should().Be("km");
        full.VersionIdMarker.Should().Be("vm");
        full.IsTruncated.Should().BeTrue();
        full.NextKeyMarker.Should().Be("nkm");
        full.NextVersionIdMarker.Should().Be("nvm");
        full.Delimiter.Should().Be("/");
        full.CommonPrefixes.Should().HaveCount(1);
        full.EncodingType.Should().Be("url");
        full.BucketRegion.Should().Be("us-east-1");

        // Negative MaxKeys
        var lvr = new ListVersionsResult();
        Action actNegMaxKeys = () => lvr.MaxKeys = -1;
        actNegMaxKeys.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxKeys");
        lvr.MaxKeys = 10;
        lvr.MaxKeys.Should().Be(10);

        // Prefix null vs non-empty
        lvr.Prefix = null!;
        lvr.Prefix.Should().Be("");
        lvr.Prefix = "pfx";
        lvr.Prefix.Should().Be("pfx");

        // Versions & ShouldSerializeVersions
        lvr.Versions = null!;
        lvr.Versions.Should().NotBeNull().And.BeEmpty();
        lvr.ShouldSerializeVersions().Should().BeFalse();
        lvr.Versions = new List<ObjectVersion> { new ObjectVersion { Key = "k1" } };
        lvr.ShouldSerializeVersions().Should().BeTrue();

        // DeleteMarkers & ShouldSerializeDeleteMarkers
        lvr.DeleteMarkers = null!;
        lvr.DeleteMarkers.Should().NotBeNull().And.BeEmpty();
        lvr.ShouldSerializeDeleteMarkers().Should().BeFalse();
        lvr.DeleteMarkers = new List<DeleteMarker> { new DeleteMarker { Key = "dm1" } };
        lvr.ShouldSerializeDeleteMarkers().Should().BeTrue();

        // NextKeyMarker & ShouldSerializeNextKeyMarker
        lvr.NextKeyMarker = null!;
        lvr.ShouldSerializeNextKeyMarker().Should().BeFalse();
        lvr.NextKeyMarker = "";
        lvr.ShouldSerializeNextKeyMarker().Should().BeFalse();
        lvr.NextKeyMarker = "key-marker-val";
        lvr.ShouldSerializeNextKeyMarker().Should().BeTrue();

        // NextVersionIdMarker & ShouldSerializeNextVersionIdMarker
        lvr.NextVersionIdMarker = null!;
        lvr.ShouldSerializeNextVersionIdMarker().Should().BeFalse();
        lvr.NextVersionIdMarker = "";
        lvr.ShouldSerializeNextVersionIdMarker().Should().BeFalse();
        lvr.NextVersionIdMarker = "vid-marker-val";
        lvr.ShouldSerializeNextVersionIdMarker().Should().BeTrue();

        // Delimiter & ShouldSerializeDelimiter
        lvr.Delimiter = null!;
        lvr.ShouldSerializeDelimiter().Should().BeFalse();
        lvr.Delimiter = "";
        lvr.ShouldSerializeDelimiter().Should().BeFalse();
        lvr.Delimiter = "/";
        lvr.ShouldSerializeDelimiter().Should().BeTrue();

        // EncodingType & ShouldSerializeEncodingType
        lvr.EncodingType = null!;
        lvr.ShouldSerializeEncodingType().Should().BeFalse();
        lvr.EncodingType = "";
        lvr.ShouldSerializeEncodingType().Should().BeFalse();
        lvr.EncodingType = "url";
        lvr.ShouldSerializeEncodingType().Should().BeTrue();

        // CommonPrefixes & ShouldSerializeCommonPrefixes (null, empty, populated)
        lvr.CommonPrefixes = null!;
        lvr.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lvr.CommonPrefixes = new List<CommonPrefixes>();
        lvr.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lvr.CommonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("prefix/") };
        lvr.ShouldSerializeCommonPrefixes().Should().BeTrue();
    }

    [TestMethod]
    public void ListBucketResult_NegativeProperties_And_ShouldSerializeCoverage()
    {
        var lbr = new ListBucketResult();

        // Negative KeyCount
        Action actNegKeyCount = () => lbr.KeyCount = -1;
        actNegKeyCount.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("KeyCount");
        lbr.KeyCount = 5;
        lbr.KeyCount.Should().Be(5);

        // Negative MaxKeys
        Action actNegMaxKeys = () => lbr.MaxKeys = -1;
        actNegMaxKeys.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxKeys");
        lbr.MaxKeys = 200;
        lbr.MaxKeys.Should().Be(200);

        // Prefix null vs non-empty
        lbr.Prefix = null!;
        lbr.Prefix.Should().Be("");
        lbr.Prefix = "pfx";
        lbr.Prefix.Should().Be("pfx");

        // CommonPrefixes & ShouldSerializeCommonPrefixes (null, empty, populated)
        lbr.CommonPrefixes = null!;
        lbr.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lbr.CommonPrefixes = new List<CommonPrefixes>();
        lbr.ShouldSerializeCommonPrefixes().Should().BeFalse();
        lbr.CommonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("folder/") };
        lbr.ShouldSerializeCommonPrefixes().Should().BeTrue();

        // Delimiter & ShouldSerializeDelimiter
        lbr.Delimiter = null!;
        lbr.ShouldSerializeDelimiter().Should().BeFalse();
        lbr.Delimiter = "";
        lbr.ShouldSerializeDelimiter().Should().BeFalse();
        lbr.Delimiter = "/";
        lbr.ShouldSerializeDelimiter().Should().BeTrue();

        // EncodingType & ShouldSerializeEncodingType
        lbr.EncodingType = null!;
        lbr.ShouldSerializeEncodingType().Should().BeFalse();
        lbr.EncodingType = "";
        lbr.ShouldSerializeEncodingType().Should().BeFalse();
        lbr.EncodingType = "url";
        lbr.ShouldSerializeEncodingType().Should().BeTrue();

        // KeyCount ShouldSerializeKeyCount
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            lbr.ShouldSerializeKeyCount().Should().BeFalse();
            return "";
        });
        ResponseSerializationContext.Run(2, false, false, () =>
        {
            lbr.ShouldSerializeKeyCount().Should().BeTrue();
            return "";
        });

        // NextContinuationToken ShouldSerializeNextContinuationToken
        lbr.NextContinuationToken = null!;
        lbr.ShouldSerializeNextContinuationToken().Should().BeFalse();
        lbr.NextContinuationToken = "next-token";
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            lbr.ShouldSerializeNextContinuationToken().Should().BeFalse();
            return "";
        });
        ResponseSerializationContext.Run(2, false, false, () =>
        {
            lbr.ShouldSerializeNextContinuationToken().Should().BeTrue();
            return "";
        });

        // NextMarker ShouldSerializeNextMarker
        lbr.NextMarker = null!;
        lbr.ShouldSerializeNextMarker().Should().BeFalse();
        lbr.NextMarker = "nm";
        ResponseSerializationContext.Run(2, false, false, () =>
        {
            lbr.ShouldSerializeNextMarker().Should().BeFalse();
            return "";
        });
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            lbr.ShouldSerializeNextMarker().Should().BeTrue();
            return "";
        });

        // ContinuationToken ShouldSerializeContinuationToken
        lbr.ContinuationToken = null!;
        lbr.ShouldSerializeContinuationToken().Should().BeFalse();
        lbr.ContinuationToken = "ct";
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            lbr.ShouldSerializeContinuationToken().Should().BeFalse();
            return "";
        });
        ResponseSerializationContext.Run(2, false, false, () =>
        {
            lbr.ShouldSerializeContinuationToken().Should().BeTrue();
            return "";
        });

        // StartAfter ShouldSerializeStartAfter
        lbr.StartAfter = null!;
        lbr.ShouldSerializeStartAfter().Should().BeFalse();
        lbr.StartAfter = "start";
        ResponseSerializationContext.Run(1, false, false, () =>
        {
            lbr.ShouldSerializeStartAfter().Should().BeFalse();
            return "";
        });
        ResponseSerializationContext.Run(2, false, false, () =>
        {
            lbr.ShouldSerializeStartAfter().Should().BeTrue();
            return "";
        });
    }

    [TestMethod]
    public void InitiateMultipartUploadResult_Constructors_And_ArgumentValidation()
    {
        // Default constructor
        var res = new InitiateMultipartUploadResult();
        res.Bucket = "b";
        res.Key = "k";
        res.UploadId = "u";
        res.Bucket.Should().Be("b");
        res.Key.Should().Be("k");
        res.UploadId.Should().Be("u");

        // Parameterized constructor valid
        var paramRes = new InitiateMultipartUploadResult("my-bucket", "my-key", "my-upload-id");
        paramRes.Bucket.Should().Be("my-bucket");
        paramRes.Key.Should().Be("my-key");
        paramRes.UploadId.Should().Be("my-upload-id");

        // Null / empty bucket
        Action actNullBucket = () => new InitiateMultipartUploadResult(null!, "k", "u");
        actNullBucket.Should().Throw<ArgumentNullException>().WithParameterName("bucket");
        Action actEmptyBucket = () => new InitiateMultipartUploadResult("", "k", "u");
        actEmptyBucket.Should().Throw<ArgumentNullException>().WithParameterName("bucket");

        // Null / empty key
        Action actNullKey = () => new InitiateMultipartUploadResult("b", null!, "u");
        actNullKey.Should().Throw<ArgumentNullException>().WithParameterName("key");
        Action actEmptyKey = () => new InitiateMultipartUploadResult("b", "", "u");
        actEmptyKey.Should().Throw<ArgumentNullException>().WithParameterName("key");

        // Null / empty uploadId
        Action actNullUploadId = () => new InitiateMultipartUploadResult("b", "k", null!);
        actNullUploadId.Should().Throw<ArgumentNullException>().WithParameterName("uploadId");
        Action actEmptyUploadId = () => new InitiateMultipartUploadResult("b", "k", "");
        actEmptyUploadId.Should().Throw<ArgumentNullException>().WithParameterName("uploadId");
    }

    [TestMethod]
    public void ListPartsResult_Properties_Validation_And_SerializationHelper()
    {
        var res = new ListPartsResult();

        // Default property values
        res.PartNumberMarker.Should().Be(0);
        res.NextPartNumberMarker.Should().Be(0);
        res.MaxParts.Should().Be(1000);
        res.IsTruncated.Should().BeFalse();
        res.StorageClass.Should().Be(StorageClassEnum.STANDARD);
        res.ChecksumAlgorithm.Should().Be(ChecksumAlgorithmEnum.CRC32);
        res.IncludeChecksumAlgorithm.Should().BeFalse();
        res.Parts.Should().NotBeNull().And.BeEmpty();

        // Negative PartNumberMarker
        Action actNegPartNumber = () => res.PartNumberMarker = -1;
        actNegPartNumber.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("PartNumberMarker");
        res.PartNumberMarker = 5;
        res.PartNumberMarker.Should().Be(5);

        // Negative NextPartNumberMarker
        Action actNegNextPartNumber = () => res.NextPartNumberMarker = -1;
        actNegNextPartNumber.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("NextPartNumberMarker");
        res.NextPartNumberMarker = 10;
        res.NextPartNumberMarker.Should().Be(10);

        // Negative MaxParts
        Action actNegMaxParts = () => res.MaxParts = -1;
        actNegMaxParts.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxParts");
        res.MaxParts = 50;
        res.MaxParts.Should().Be(50);

        // Other properties
        res.Bucket = "b";
        res.Key = "k";
        res.UploadId = "u";
        res.IsTruncated = true;
        res.Initiator = new Owner("init-id", "init-name");
        res.Owner = new Owner("owner-id", "owner-name");
        res.StorageClass = StorageClassEnum.GLACIER;
        res.ChecksumAlgorithm = ChecksumAlgorithmEnum.SHA256;

        res.Bucket.Should().Be("b");
        res.Key.Should().Be("k");
        res.UploadId.Should().Be("u");
        res.IsTruncated.Should().BeTrue();
        res.Initiator.ID.Should().Be("init-id");
        res.Owner.ID.Should().Be("owner-id");
        res.StorageClass.Should().Be(StorageClassEnum.GLACIER);
        res.ChecksumAlgorithm.Should().Be(ChecksumAlgorithmEnum.SHA256);

        // ChecksumAlgorithm ShouldSerialize
        res.IncludeChecksumAlgorithm = false;
        res.ShouldSerializeChecksumAlgorithm().Should().BeFalse();
        res.IncludeChecksumAlgorithm = true;
        res.ShouldSerializeChecksumAlgorithm().Should().BeTrue();

        // Parts property
        var partsList = new List<Part> { new Part { PartNumber = 1, ETag = "\"etag\"", Size = 100 } };
        res.Parts = partsList;
        res.Parts.Should().HaveCount(1);
    }

    [TestMethod]
    public void ListMultipartUploadsResult_Constructors_Properties_And_SerializationHelpers()
    {
        // Default constructor
        var res = new ListMultipartUploadsResult();
        res.MaxUploads.Should().Be(1000);
        res.IsTruncated.Should().BeFalse();
        res.Uploads.Should().NotBeNull().And.BeEmpty();
        res.CommonPrefixes.Should().NotBeNull().And.BeEmpty();

        // Parameterized constructor valid
        var paramRes = new ListMultipartUploadsResult("my-bucket", "my-key", "my-upload-id");
        paramRes.Bucket.Should().Be("my-bucket");
        paramRes.KeyMarker.Should().Be("my-key");
        paramRes.UploadIdMarker.Should().Be("my-upload-id");

        // Null / empty bucket
        Action actNullBucket = () => new ListMultipartUploadsResult(null!, "k", "u");
        actNullBucket.Should().Throw<ArgumentNullException>().WithParameterName("bucket");
        Action actEmptyBucket = () => new ListMultipartUploadsResult("", "k", "u");
        actEmptyBucket.Should().Throw<ArgumentNullException>().WithParameterName("bucket");

        // Null / empty key
        Action actNullKey = () => new ListMultipartUploadsResult("b", null!, "u");
        actNullKey.Should().Throw<ArgumentNullException>().WithParameterName("key");
        Action actEmptyKey = () => new ListMultipartUploadsResult("b", "", "u");
        actEmptyKey.Should().Throw<ArgumentNullException>().WithParameterName("key");

        // Null / empty uploadId
        Action actNullUploadId = () => new ListMultipartUploadsResult("b", "k", null!);
        actNullUploadId.Should().Throw<ArgumentNullException>().WithParameterName("uploadId");
        Action actEmptyUploadId = () => new ListMultipartUploadsResult("b", "k", "");
        actEmptyUploadId.Should().Throw<ArgumentNullException>().WithParameterName("uploadId");

        // Negative MaxUploads
        Action actNegMaxUploads = () => res.MaxUploads = -1;
        actNegMaxUploads.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("MaxUploads");
        res.MaxUploads = 25;
        res.MaxUploads.Should().Be(25);

        // Uploads & ShouldSerializeUploads (null, empty, populated)
        res.Uploads = null!;
        res.ShouldSerializeUploads().Should().BeFalse();
        res.Uploads = new List<Upload>();
        res.ShouldSerializeUploads().Should().BeFalse();
        res.Uploads = new List<Upload> { new Upload { Key = "k1", UploadId = "u1" } };
        res.ShouldSerializeUploads().Should().BeTrue();

        // CommonPrefixes & ShouldSerializeCommonPrefixes (null, empty, populated)
        res.CommonPrefixes = null!;
        res.ShouldSerializeCommonPrefixes().Should().BeFalse();
        res.CommonPrefixes = new List<CommonPrefixes>();
        res.ShouldSerializeCommonPrefixes().Should().BeFalse();
        res.CommonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("prefix/") };
        res.ShouldSerializeCommonPrefixes().Should().BeTrue();

        // EncodingType & ShouldSerializeEncodingType (null, empty, populated)
        res.EncodingType = null!;
        res.ShouldSerializeEncodingType().Should().BeFalse();
        res.EncodingType = "";
        res.ShouldSerializeEncodingType().Should().BeFalse();
        res.EncodingType = "url";
        res.ShouldSerializeEncodingType().Should().BeTrue();

        // Other properties
        res.Bucket = "b";
        res.Prefix = "pfx";
        res.Delimiter = "/";
        res.NextKeyMarker = "nkm";
        res.NextUploadIdMarker = "nuid";
        res.IsTruncated = true;

        res.Bucket.Should().Be("b");
        res.Prefix.Should().Be("pfx");
        res.Delimiter.Should().Be("/");
        res.NextKeyMarker.Should().Be("nkm");
        res.NextUploadIdMarker.Should().Be("nuid");
        res.IsTruncated.Should().BeTrue();
    }
}
