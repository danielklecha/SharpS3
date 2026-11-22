namespace SharpS3.Tests.Unit;

#pragma warning disable CS8625
#pragma warning disable CS8602
#pragma warning disable CS8604

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Auth;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;
using SharpS3.Storage;
using SharpS3.Streams;

[TestClass]
public class StorageBackendCoverageCompletionTests
{
    [TestMethod]
    public void RestoreRequest_EffectiveTier_And_UnsupportedSelectFields()
    {
        var req = new RestoreRequest();
        req.EffectiveTier.Should().BeNull();

        req.Tier = RestoreTierEnum.Expedited;
        req.EffectiveTier.Should().Be(RestoreTierEnum.Expedited);

        req.Tier = null;
        req.GlacierJobParameters = new GlacierJobParameters { Tier = RestoreTierEnum.Bulk };
        req.EffectiveTier.Should().Be(RestoreTierEnum.Bulk);

        // Unsupported restore select fields
        req.AdditionalElements = null;
        req.HasUnsupportedRestoreSelectFields.Should().BeFalse();

        req.AdditionalElements = Array.Empty<XmlElement>();
        req.HasUnsupportedRestoreSelectFields.Should().BeFalse();

        var doc = new XmlDocument();
        req.AdditionalElements = new[] { doc.CreateElement("OutputLocation") };
        req.HasUnsupportedRestoreSelectFields.Should().BeTrue();

        req.AdditionalElements = new[] { doc.CreateElement("SelectParameters") };
        req.HasUnsupportedRestoreSelectFields.Should().BeTrue();

        req.AdditionalElements = new[] { doc.CreateElement("Type") };
        req.HasUnsupportedRestoreSelectFields.Should().BeTrue();

        req.AdditionalElements = new XmlElement[] { null!, doc.CreateElement("OtherElement") };
        req.HasUnsupportedRestoreSelectFields.Should().BeFalse();
    }

    [TestMethod]
    public void DeleteError_Properties_And_SerializationHelpers()
    {
        var err = new DeleteError();
        err.VersionId = "v1";
        err.VersionId.Should().Be("v1");
        err.Code = ErrorCode.NoSuchKey;
        err.Code.Should().Be(ErrorCode.NoSuchKey);
        err.Message = "Key not found";
        err.Message.Should().Be("Key not found");
        err.Error.Should().NotBeNull();

        err.Key = "";
        err.ShouldSerializeKey().Should().BeFalse();
        err.Key = "my-key";
        err.ShouldSerializeKey().Should().BeTrue();

        err.VersionId = "";
        err.ShouldSerializeVersionId().Should().BeFalse();
        err.VersionId = "v2";
        err.ShouldSerializeVersionId().Should().BeTrue();
    }

    [TestMethod]
    public void VersionedEntity_SerializationHelpers()
    {
        var entity = new VersionedEntity();
        entity.ETag = null;
        entity.ShouldSerializeETag().Should().BeFalse();
        entity.ETag = "";
        entity.ShouldSerializeETag().Should().BeFalse();
        entity.ETag = "\"12345\"";
        entity.ShouldSerializeETag().Should().BeTrue();

        entity.Size = null;
        entity.ShouldSerializeSize().Should().BeFalse();
        entity.Size = 42;
        entity.ShouldSerializeSize().Should().BeTrue();

        entity.ShouldSerializeStorageClass().Should().BeTrue();
    }

    [TestMethod]
    public void ListVersionsResult_And_EntryCollection()
    {
        var result = new ListVersionsResult();
        result.Entries = null!;
        result.Entries.Should().NotBeNull();

        var list = new List<VersionedEntity> { new ObjectVersion { Key = "k1", VersionId = "v1" } };
        result.Entries = list;
        result.Entries.Should().HaveCount(1);

        // XmlEntries getter when Entries.Count > 0
        var xmlEntries = result.XmlEntries;
        xmlEntries.Should().HaveCount(1);

        // XmlEntries getter when Entries.Count == 0
        var emptyResult = new ListVersionsResult();
        emptyResult.Versions.Add(new ObjectVersion { Key = "k2", VersionId = "v2" });
        emptyResult.DeleteMarkers.Add(new DeleteMarker { Key = "k3", VersionId = "v3" });
        var xmlEntries2 = emptyResult.XmlEntries;
        xmlEntries2.Should().HaveCount(2);

        // ListVersionsEntryCollection.InsertItem ObjectVersion branch
        var coll = new ListVersionsEntryCollection(emptyResult, new List<VersionedEntity>());
        coll.Add(new ObjectVersion { Key = "k4", VersionId = "v4" });
        emptyResult.Versions.Should().Contain(v => v.Key == "k4");
    }

    [TestMethod]
    public void ListAllMyBucketsResult_Constructor()
    {
        var owner = new Owner("owner-1", "Owner One");
        var buckets = new Buckets();
        var res = new ListAllMyBucketsResult(owner, buckets);
        res.Owner.Should().Be(owner);
        res.Buckets.Should().Be(buckets);
    }

    [TestMethod]
    public void SharpS3Settings_GetMatchedBaseDomain_And_Logger()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains = new List<string> { "s3.amazonaws.com" };

        // Hostname without dot
        settings.GetMatchingBaseDomain("localhost").Should().BeNull();

        // Hostname ending with dot
        settings.GetMatchingBaseDomain("localhost.").Should().BeNull();

        // Hostname with segments not matching base domains
        settings.GetMatchingBaseDomain("mybucket.customdomain.org").Should().Be("customdomain.org");

        // Logger getter/setter
        Action<string> logAction = msg => { };
        settings.Logger = logAction;
        settings.Logger.Should().BeSameAs(logAction);

        // Null or whitespace hostname (line 64)
        settings.GetMatchingBaseDomain(null!).Should().BeNull();
        settings.GetMatchingBaseDomain("").Should().BeNull();
        settings.GetMatchingBaseDomain("   ").Should().BeNull();

        // Null, empty or whitespace entries in BaseDomains (lines 70 and 82)
        settings.BaseDomains = new List<string> { null!, "", "  ", "s3.amazonaws.com" };
        settings.GetMatchingBaseDomain("mybucket.s3.amazonaws.com").Should().Be("s3.amazonaws.com");
        settings.GetMatchingBaseDomain("localhost").Should().BeNull();
    }

    [TestMethod]
    public void ModelEdgeProperties_Retention_S3ObjectResponse_S3Exception_AwsChunkSigner()
    {
        // Retention.RetainUntilDate null branch
        var ret = new Retention();
        ret.RetainUntilDate = DateTime.UtcNow;
        ret.RetainUntilDate.Should().NotBeNull();
        ret.RetainUntilDate = null;
        ret.RetainUntilDate.Should().BeNull();

        // S3ObjectResponse LastModified getter
        using var httpMsg = new HttpResponseMessage(HttpStatusCode.OK);
        var s3Resp = new S3ObjectResponse(httpMsg, Stream.Null, 0, "text/plain", "\"123\"", DateTimeOffset.UtcNow, new Dictionary<string, string>());
        s3Resp.LastModified.Should().NotBeNull();

        // S3Exception with Error = null
        var s3Ex = new S3Exception();
        s3Ex.Error = null;
        s3Ex.HttpStatusCode.Should().Be(500);

        // AwsChunkSigner CurrentSignature getter
        var signer = new AwsChunkSigner("20261117T000000Z", "us-east-1", "s3", new byte[32], "seed123");
        signer.CurrentSignature.Should().Be("seed123");
    }

    [TestMethod]
    public void SerializationContext_ObjectMetadata_ListBucketResult()
    {
        // ResponseSerializationContext null serialize delegate throws ArgumentNullException (line 63)
        Action actNull = () => ResponseSerializationContext.Run(0, false, false, null!);
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("serialize");

        // ResponseSerializationContext getters inside and outside Run
        _ = ResponseSerializationContext.SuppressListOwner;
        _ = ResponseSerializationContext.ListType;
        _ = ResponseSerializationContext.DeleteQuiet;

        ResponseSerializationContext.Run(1, true, true, () =>
        {
            ResponseSerializationContext.ListType.Should().Be(1);
            ResponseSerializationContext.SuppressListOwner.Should().BeTrue();
            ResponseSerializationContext.DeleteQuiet.Should().BeTrue();
            return "ok";
        }).Should().Be("ok");

        // ObjectMetadata.ShouldSerializeOwner
        var meta = new ObjectMetadata("obj1", DateTime.UtcNow, "\"etag\"", 100, new Owner("id", "name"));
        meta.ShouldSerializeOwner().Should().BeTrue();

        // ListBucketResult.Marker non-null
        var listRes = new ListBucketResult();
        listRes.Marker = "marker-val";
        listRes.Marker.Should().Be("marker-val");

        // ShouldSerializeMarker when ListType is 0 (default)
        listRes.Marker = null;
        listRes.ShouldSerializeMarker().Should().BeFalse();
        listRes.Marker = "marker-val";
        listRes.ShouldSerializeMarker().Should().BeTrue();
    }

    [TestMethod]
    public void AwsSignatureV2_And_V4_EdgeValidation()
    {
        // SigV2: Signature is null or empty
        var reqEmptySig = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        var act1 = () => AwsSignatureV2.Validate(reqEmptySig, "secret");
        act1.Should().Throw<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.SignatureDoesNotMatch);

        // SigV2: Authorization present, but no date or x-amz-date
        var headersNoDate = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Authorization", new[] { "AWS AKIAIOSFODNN7EXAMPLE:signature" } }
        };
        var reqNoDate = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), headersNoDate, Stream.Null);
        var act2 = () => AwsSignatureV2.Validate(reqNoDate, "secret");
        act2.Should().Throw<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.SignatureDoesNotMatch);

        // SigV4: Header with multiple consecutive spaces
        var headersMultiSpace = new Dictionary<string, string>
        {
            { "host", "s3.amazonaws.com" },
            { "x-amz-date", "20261117T000000Z" },
            { "x-amz-custom", "foo   bar    baz" }
        };
        var sigV4 = new AwsSignatureV4(
            "20261117T000000Z",
            "GET",
            "http://s3.amazonaws.com/bucket/key",
            "AKIAIOSFODNN7EXAMPLE",
            "secret",
            "us-east-1",
            "s3",
            headersMultiSpace,
            new[] { "host", "x-amz-date", "x-amz-custom" });
        sigV4.CanonicalHeaders.Should().Contain("foo bar baz");
    }

    [TestMethod]
    public void SerializationHelper_CopyObject_CatchBlock_And_TelemetryStages()
    {
        // CopyObject catch block
        var copyRes = SerializationHelper.CopyObject<ThrowingXmlObject>(new ThrowingXmlObject());
        copyRes.Should().BeNull();

        // DeserializeXml / SerializeXml with active RequestTelemetry to cover TelemetryStage.Fail catch filters
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (instr, l) => l.EnableMeasurementEvents(instr);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rt = telemetry.BeginRequest();
        RequestTelemetry.Current = rt;

        // DeserializeXml with invalid XML to trigger TelemetryStage.Fail
        var actDeserialize = () => SerializationHelper.DeserializeXml<Error>("<InvalidXml>>");
        actDeserialize.Should().Throw<Exception>();

        // SerializeXml with type that throws on serialization
        var actSerialize = () => SerializationHelper.SerializeXml(new ThrowingXmlObject());
        actSerialize.Should().Throw<Exception>();

        RequestTelemetry.Current = null;
    }

    public class ThrowingXmlObject
    {
        public string Value => throw new InvalidOperationException("Serialization failure test");
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_EdgeBranches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b1", null);

        // GetBucketVersioningAsync on bucket with no versioning
        var vConf = await backend.GetBucketVersioningAsync("b1");
        vConf.Status.Should().Be(VersioningStatusEnum.Suspended);

        // GetBucketLoggingAsync on bucket with no logging
        var logConf = await backend.GetBucketLoggingAsync("b1");
        logConf.Enabled.Should().NotBeNull();

        // CopyObjectAsync NoSuchKey
        var actCopy = () => backend.CopyObjectAsync("b1", "nonexistent", "b1", "dest");
        await actCopy.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.NoSuchKey);

        // ListPartsAsync NoSuchUpload
        var actListParts = () => backend.ListPartsAsync("b1", "key", "nonexistent-upload");
        await actListParts.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.NoSuchUpload);

        // UploadPartCopyAsync NoSuchKey
        var actUploadCopy1 = () => backend.UploadPartCopyAsync("b1", "nonexistent", "b1", "key", "upload-id", 1);
        await actUploadCopy1.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.NoSuchKey);

        // UploadPartCopyAsync NoSuchUpload
        await backend.PutObjectAsync("b1", "sourceKey", new MemoryStream(new byte[] { 1, 2, 3 }), 3, "text/plain");
        var actUploadCopy2 = () => backend.UploadPartCopyAsync("b1", "sourceKey", "b1", "key", "nonexistent-upload", 1);
        await actUploadCopy2.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_EdgeBranches()
    {
        string rootDir = Path.Combine(Path.GetTempPath(), "SharpS3_FinalTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDir);

        try
        {
            var backend = new FileSystemStorageBackend(rootDir);
            await backend.CreateBucketAsync("b1", null);

            // Put an object and verify DeleteBucketAsync throws BucketNotEmpty
            await backend.PutObjectAsync("b1", "obj1.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), 5, "text/plain");
            var actDel = () => backend.DeleteBucketAsync("b1");
            await actDel.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.BucketNotEmpty);

            // Add a second object for truncation in ListObjectsAsync
            await backend.PutObjectAsync("b1", "obj2.txt", new MemoryStream(Encoding.UTF8.GetBytes("world")), 5, "text/plain");

            // ListObjectsAsync V1 truncation
            var reqV1 = new S3Request("GET", "/b1?max-keys=1", new Dictionary<string, string> { { "max-keys", "1" } }, new Dictionary<string, string[]>(), Stream.Null);
            reqV1.MaxKeys = 1;
            var listV1 = await backend.ListObjectsAsync("b1", reqV1);
            listV1.IsTruncated.Should().BeTrue();
            listV1.NextMarker.Should().NotBeNullOrEmpty();

            // ListObjectsAsync V2 truncation
            var reqV2 = new S3Request("GET", "/b1?list-type=2&max-keys=1", new Dictionary<string, string> { { "list-type", "2" }, { "max-keys", "1" } }, new Dictionary<string, string[]>(), Stream.Null);
            reqV2.MaxKeys = 1;
            var listV2 = await backend.ListObjectsAsync("b1", reqV2);
            listV2.IsTruncated.Should().BeTrue();
            listV2.NextContinuationToken.Should().NotBeNullOrEmpty();

            // Directory key with metadata
            var dirMeta = new Dictionary<string, string>
            {
                { "custom", "val1" },
                { "x-amz-meta-custom2", "val2" }
            };
            await backend.PutObjectAsync("b1", "folder/", new MemoryStream(new byte[] { 1 }), 1, "application/x-directory", dirMeta);

            // GetObjectMetadataAsync on directory key
            var dirObjMeta = await backend.GetObjectMetadataAsync("b1", "folder/");
            dirObjMeta.Should().NotBeNull();
            dirObjMeta!.Metadata.Should().ContainKey("custom");

            // GetObjectAsync on directory key
            using var dirPayload = await backend.GetObjectAsync("b1", "folder/");
            dirPayload.Should().NotBeNull();
            dirPayload.Headers.Should().ContainKey("x-amz-meta-custom");
            dirPayload.Headers.Should().ContainKey("x-amz-meta-custom2");

            // GetObjectAsync InvalidRange
            var actRange = () => backend.GetObjectAsync("b1", "obj1.txt", rangeStart: 100, rangeEnd: 200);
            await actRange.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.InvalidRange);

            // PutObjectAsync with contentLength <= 0 for non-directory
            await backend.PutObjectAsync("b1", "empty.bin", new MemoryStream(Encoding.UTF8.GetBytes("chunked-content")), 0, "binary/octet-stream");

            // UploadPartCopyAsync InvalidRange
            var mpu = await backend.CreateMultipartUploadAsync("b1", "copied-mpu.bin");
            var actCopyPartRange = () => backend.UploadPartCopyAsync("b1", "obj1.txt", "b1", "copied-mpu.bin", mpu.UploadId, 1, rangeStart: 100, rangeEnd: 200);
            await actCopyPartRange.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.InvalidRange);

            // UploadPartAsync with contentLength <= 0
            await backend.UploadPartAsync("b1", "copied-mpu.bin", mpu.UploadId, 1, new MemoryStream(Encoding.UTF8.GetBytes("part1")), 0);
            await backend.AbortMultipartUploadAsync("b1", "copied-mpu.bin", mpu.UploadId);

            // DeleteMultipleObjectsAsync on non-existent bucket (hits catch block)
            var delMulti = new DeleteMultiple
            {
                Objects = new List<SharpS3.Models.Object> { new SharpS3.Models.Object("k1", "v1") }
            };
            var delRes = await backend.DeleteMultipleObjectsAsync("nonexistent-bucket", delMulti);
            delRes.DeletedObjects.Should().BeEmpty();

            // DeleteObjectAsync parent directory pruning break branch (sibling file exists)
            await backend.PutObjectAsync("b1", "sub/nested/fileA.txt", new MemoryStream(new byte[] { 1 }), 1, "text/plain");
            await backend.PutObjectAsync("b1", "sub/nested/fileB.txt", new MemoryStream(new byte[] { 2 }), 1, "text/plain");
            await backend.DeleteObjectAsync("b1", "sub/nested/fileA.txt");
            Directory.Exists(Path.Combine(rootDir, "b1", "sub", "nested")).Should().BeTrue();

            // Corrupt metadata for ReadMeta returning null
            string bPath = Path.Combine(rootDir, "b1");
            string metaFile = Path.Combine(bPath, "corrupt.txt.s3meta.xml");
            File.WriteAllText(metaFile, "<broken xml>>>");
            File.WriteAllText(Path.Combine(bPath, "corrupt.txt"), "data");
            var corruptMeta = await backend.GetObjectMetadataAsync("b1", "corrupt.txt");
            corruptMeta.Should().NotBeNull();

            // Directory with only .s3meta.xml for IsEmptyDirectory returning true
            string emptySubDir = Path.Combine(bPath, "onlymeta");
            Directory.CreateDirectory(emptySubDir);
            File.WriteAllText(Path.Combine(emptySubDir, "test.s3meta.xml"), "<meta/>");
            var listWithEmptyDir = await backend.ListObjectsAsync("b1", new S3Request("GET", "/b1", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null));
            listWithEmptyDir.Contents.Should().Contain(c => c.Key == "onlymeta/");
        }
        finally
        {
            try { Directory.Delete(rootDir, true); } catch { }
        }
    }

    [TestMethod]
    public async Task SharpS3Server_And_Dispatcher_EdgeCases()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            Credentials = { { "TESTKEY", "TESTSECRET" } }
        };
        var server = new SharpS3Server(settings);
        server.Settings.Should().BeSameAs(settings);

        // ReceiveRequestAsync with null method -> throws ArgumentNullException, caught by telemetry parse stage
        var actNullMethod = () => server.ReceiveRequestAsync(null!, "/bucket/key");
        await actNullMethod.Should().ThrowAsync<ArgumentNullException>();

        // ReceiveRequestAsync with EnableSignatures and invalid signature -> fails in signature validation stage
        var invalidSigHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Authorization", new[] { "AWS4-HMAC-SHA256 Credential=TESTKEY/20261117/us-east-1/s3/aws4_request, SignedHeaders=host, Signature=bad" } },
            { "Host", new[] { "localhost" } },
            { "x-amz-date", new[] { DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact) } }
        };
        var actInvalidSig = () => server.ReceiveRequestAsync("GET", "/bucket/key", headers: invalidSigHeaders);
        await actInvalidSig.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.SignatureDoesNotMatch);

        // SharpS3Server.ValidateSignatureAsync with S3SignatureVersion.None when EnableSignatures is true -> AccessDenied
        var noSigReq = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settings);
        var actNoSig = () => SharpS3Server.ValidateSignatureAsync(noSigReq, settings, null, CancellationToken.None);
        await actNoSig.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.AccessDenied);

        // S3RequestDispatcher properties
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("bucket", null);
        await backend.PutObjectAsync("bucket", "key", new MemoryStream(new byte[] { 1 }), 1, "text/plain");
        var dispatcher = new S3RequestDispatcher(backend, settings);
        dispatcher.Backend.Should().BeSameAs(backend);
        dispatcher.Settings.Should().BeSameAs(settings);

        // DispatchAsync with EnableSignatures = true and signature not yet validated (failure path)
        var reqForDispatch = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), invalidSigHeaders, Stream.Null, settings);
        reqForDispatch.IsSignatureValidated = false;
        using var serverTelemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rtSigFail = serverTelemetry.BeginRequest();
        RequestTelemetry.Current = rtSigFail;
        try
        {
            var actDispatchSigFail = () => dispatcher.DispatchAsync(reqForDispatch);
            await actDispatchSigFail.Should().ThrowAsync<S3Exception>().Where(e => e.Error != null && e.Error.Code == ErrorCode.SignatureDoesNotMatch);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }

        // DispatchAsync with EnableSignatures = true and valid signature (success path -> IsSignatureValidated = true)
        string validDate = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);
        var validHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Host", new[] { "localhost" } },
            { "x-amz-date", new[] { validDate } },
            { "x-amz-content-sha256", new[] { "UNSIGNED-PAYLOAD" } }
        };
        var validSigV4 = new AwsSignatureV4(
            validDate,
            "GET",
            "http://localhost/bucket/key",
            "TESTKEY",
            "TESTSECRET",
            "us-east-1",
            "s3",
            validHeaders,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.Unsigned);
        validHeaders["Authorization"] = new[] { $"AWS4-HMAC-SHA256 Credential=TESTKEY/{validSigV4.DateStamp}/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature={validSigV4.Signature}" };

        var validReq = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), validHeaders, Stream.Null, settings);
        validReq.IsSignatureValidated = false;
        var validResp = await dispatcher.DispatchAsync(validReq);
        validResp.StatusCode.Should().Be(200);
        validReq.IsSignatureValidated.Should().BeTrue();

        // DispatchAsync with ValidateSignaturesBeforePreRequestHandler = true and PreRequestHandler returning response
        var settingsPreHook = new SharpS3Settings
        {
            ValidateSignaturesBeforePreRequestHandler = true,
            EnableSignatures = false,
            PreRequestHandler = req => Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 418 })
        };
        var dispPreHook = new S3RequestDispatcher(backend, settingsPreHook);
        var reqPre = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settingsPreHook);
        var preResp = await dispPreHook.DispatchAsync(reqPre);
        preResp.StatusCode.Should().Be(418);

        // PreRequestHandler returning null (falls through)
        settingsPreHook.PreRequestHandler = req => Task.FromResult<S3ServerResponse?>(null);
        var preNullResp = await dispPreHook.DispatchAsync(reqPre);
        preNullResp.Should().NotBeNull();

        // DispatchAsync with AuthenticatedRequestHandler returning response
        var settingsAuthHook = new SharpS3Settings
        {
            EnableSignatures = false,
            AuthenticatedRequestHandler = req => Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 402 })
        };
        var dispAuthHook = new S3RequestDispatcher(backend, settingsAuthHook);
        var reqAuth = new S3Request("GET", "/bucket/key", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settingsAuthHook);
        var authResp = await dispAuthHook.DispatchAsync(reqAuth);
        authResp.StatusCode.Should().Be(402);

        // AuthenticatedRequestHandler returning null (falls through)
        settingsAuthHook.AuthenticatedRequestHandler = req => Task.FromResult<S3ServerResponse?>(null);
        var authNullResp = await dispAuthHook.DispatchAsync(reqAuth);
        authNullResp.Should().NotBeNull();

        // BuildSignatureUrl with query parameters via ReceiveRequestAsync
        var qHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Authorization", new[] { "AWS4-HMAC-SHA256 Credential=TESTKEY/20261117/us-east-1/s3/aws4_request, SignedHeaders=host, Signature=bad" } },
            { "Host", new[] { "localhost" } },
            { "x-amz-date", new[] { DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact) } }
        };
        var qParams = new Dictionary<string, string> { { "a", "1" }, { "b", "" }, { "c", "foo bar" } };
        var actQSig = () => server.ReceiveRequestAsync("GET", "/bucket/key", queryParameters: qParams, headers: qHeaders);
        await actQSig.Should().ThrowAsync<S3Exception>();

        // RestoreObject with RestoreOutputPath returned
        var mockBackend = new CallbackStorageBackend
        {
            RestoreObject = (b, k, r, ct) => Task.FromResult(new RestoreObjectResult { AlreadyRestored = true, RestoreOutputPath = "restored/path" })
        };
        var dispRestore = new S3RequestDispatcher(mockBackend, new SharpS3Settings());
        var restoreXml = SerializationHelper.SerializeXml(new RestoreRequest { Days = 1 });
        var reqRestore = new S3Request("POST", "/b/k?restore", new Dictionary<string, string> { { "restore", "" } }, new Dictionary<string, string[]>(), new MemoryStream(Encoding.UTF8.GetBytes(restoreXml)));
        reqRestore.Bucket = "b";
        reqRestore.Key = "k";
        reqRestore.RequestType = S3RequestType.ObjectRestore;
        var respRestore = await dispRestore.DispatchAsync(reqRestore);
        respRestore.Headers.Should().ContainKey(Constants.HeaderRestoreOutputPath);
    }

    [TestMethod]
    public async Task SharpS3Client_EdgeCases()
    {
        // 1. Virtual-hosted style endpoint
        var handler = new MockHttpMessageHandler();
        handler.RegisterHandler((req, ct) =>
        {
            // Verify virtual host
            req.RequestUri!.Host.Should().StartWith("mybucket.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8080"),
            ForcePathStyle = false
        };
        using var client = new SharpS3Client(options, handler.CreateClient());
        var exists = await client.BucketExistsAsync("mybucket");
        exists.Should().BeTrue();

        // Presigned URL virtual host
        var presigned = client.GeneratePresignedUrl("mybucket", "mykey", HttpMethod.Get, TimeSpan.FromHours(1));
        presigned.Host.Should().StartWith("mybucket.");

        // HeadObjectAsync with unquoted ETag (covers GetHeaderValue)
        handler.RegisterHandler((req, ct) =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK);
            msg.Headers.TryAddWithoutValidation("ETag", "unquoted-etag-val");
            return Task.FromResult(msg);
        });
        var metaUnquoted = await client.HeadObjectAsync("mybucket", "mykey");
        metaUnquoted.Should().NotBeNull();
        metaUnquoted!.ETag.Should().Be("unquoted-etag-val");

        // 2. UploadPartAsync without ETag header in response
        var handlerNoEtag = new MockHttpMessageHandler();
        handlerNoEtag.RegisterHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var clientNoEtag = new SharpS3Client(options, handlerNoEtag.CreateClient());
        var partRes = await clientNoEtag.UploadPartAsync("mybucket", "obj.bin", "upload1", 1, new MemoryStream(new byte[] { 1 }), 1);
        partRes.ETag.Should().Be(string.Empty);
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_EdgeCases()
    {
        // 1. Signer property getter
        var signer = new AwsChunkSigner("20261117T000000Z", "us-east-1", "s3", new byte[32], "seed");
        using var stream1 = new AwsChunkedDecodingStream(new MemoryStream(), signer);
        stream1.Signer.Should().BeSameAs(signer);

        // 2. Unbuffered mode where ReadLineAsync returns empty (empty stream)
        using var streamEmpty = new AwsChunkedDecodingStream(new MemoryStream(Array.Empty<byte>()));
        byte[] buf = new byte[10];
        int read0 = await streamEmpty.ReadAsync(buf, 0, buf.Length);
        read0.Should().Be(0);

        // 3. Unbuffered mode where inner stream has chunk size > 0 but inner stream ends before completing chunk
        var badChunkInner = new MemoryStream(Encoding.ASCII.GetBytes("5\r\nab")); // declared 5, gave 2 then EOF
        using var streamBadChunk = new AwsChunkedDecodingStream(badChunkInner);
        int readBad = await streamBadChunk.ReadAsync(buf, 0, buf.Length);
        readBad.Should().Be(2);
        int readBadEof = await streamBadChunk.ReadAsync(buf, 0, buf.Length);
        readBadEof.Should().Be(0);

        // 4. Buffered mode where ReadLineAsync returns empty
        using var streamBufferedEmpty = new AwsChunkedDecodingStream(new MemoryStream(Array.Empty<byte>()), signer);
        int readBufEmpty = await streamBufferedEmpty.ReadAsync(buf, 0, buf.Length);
        readBufEmpty.Should().Be(0);

        // 5. Chunk size exceeding in-memory buffer limit
        var hugeChunkHeader = Encoding.ASCII.GetBytes("80000000;chunk-signature=abc\r\n");
        using var streamHuge = new AwsChunkedDecodingStream(new MemoryStream(hugeChunkHeader), signer)
        {
            MaxChunkSize = long.MaxValue
        };
        var actHuge = () => streamHuge.ReadAsync(buf, 0, buf.Length);
        var exHuge = await actHuge.Should().ThrowAsync<InvalidDataException>();
        exHuge.WithMessage("*exceeds supported in-memory buffer limit*");

        // 6. ReadLineAsync with \r followed by non-\n
        var rWithoutN = new MemoryStream(Encoding.ASCII.GetBytes("5;foo\rX\n12345\r\n0\r\n\r\n"));
        using var streamRWithoutN = new AwsChunkedDecodingStream(rWithoutN);
        int readR = await streamRWithoutN.ReadAsync(buf, 0, 5);
        readR.Should().Be(5);

        // 6b. ReadLineAsync with \r followed by non-\n exceeding MaxLineLength
        var rWithoutNExceeded = new MemoryStream(Encoding.ASCII.GetBytes("1234\rX"));
        using var streamRExceeded = new AwsChunkedDecodingStream(rWithoutNExceeded)
        {
            MaxLineLength = 4
        };
        var actRExceeded = () => streamRExceeded.ReadAsync(buf, 0, 5);
        var exRExceeded = await actRExceeded.Should().ThrowAsync<InvalidDataException>();
        exRExceeded.WithMessage("*exceeds maximum length of 4 characters.*");
    }

    [TestMethod]
    public async Task Dispatcher_ReadBodyStringAsync_NonSeekableStream_Exceeded_Throws_EntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("nonseekable-bucket", null);

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxXmlPayloadSize = 30;

        var dispatcher = new S3RequestDispatcher(backend, settings);

        string xmlPayload = "<Tagging><TagSet><Tag><Key>env</Key><Value>production</Value></Tag></TagSet></Tagging>";
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xmlPayload);
        xmlBytes.Length.Should().BeGreaterThan(30);

        var req = new S3Request(
            "PUT",
            "/nonseekable-bucket",
            new Dictionary<string, string> { { "tagging", "" } },
            new Dictionary<string, string[]>(),
            new NonSeekableStream(new MemoryStream(xmlBytes)),
            settings);

        var act = () => dispatcher.DispatchAsync(req);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task Dispatcher_ReadBodyStringAsync_NonSeekableStream_WithinLimit_Succeeds()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("nonseekable-bucket-ok", null);

        var settings = new SharpS3Settings();
        settings.OperationLimits.MaxXmlPayloadSize = 1000;

        var dispatcher = new S3RequestDispatcher(backend, settings);

        string xmlPayload = "<Tagging><TagSet><Tag><Key>env</Key><Value>prod</Value></Tag></TagSet></Tagging>";
        byte[] xmlBytes = Encoding.UTF8.GetBytes(xmlPayload);

        var req = new S3Request(
            "PUT",
            "/nonseekable-bucket-ok",
            new Dictionary<string, string> { { "tagging", "" } },
            new Dictionary<string, string[]>(),
            new NonSeekableStream(new MemoryStream(xmlBytes)),
            settings);

        using var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task SharpS3Client_EnsureSuccessAsync_And_GetHeaderValue_Coverage()
    {
        // 1. EnsureSuccessAsync with success status code (should return without throwing)
        using (var okResponse = new HttpResponseMessage(HttpStatusCode.OK))
        {
            await SharpS3Client.EnsureSuccessAsync(okResponse);
        }

        // 2. EnsureSuccessAsync with null Content
        using (var nullContentResponse = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = null })
        {
            var actNullContent = () => SharpS3Client.EnsureSuccessAsync(nullContentResponse);
            var ex = await actNullContent.Should().ThrowAsync<SharpS3ClientException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
            ex.Which.ErrorCode.Should().BeNull();
        }

        // 3. EnsureSuccessAsync with whitespace Content
        using (var whitespaceResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("   \t\r\n   ", Encoding.UTF8, "text/plain")
        })
        {
            var actWhitespace = () => SharpS3Client.EnsureSuccessAsync(whitespaceResponse);
            var ex = await actWhitespace.Should().ThrowAsync<SharpS3ClientException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            ex.Which.ErrorCode.Should().BeNull();
        }

        // 4. EnsureSuccessAsync with valid XML Error
        string errorXml = "<Error><Code>InvalidBucketName</Code><Message>The specified bucket is not valid.</Message><Resource>/test</Resource><RequestId>123</RequestId></Error>";
        using (var xmlResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(errorXml, Encoding.UTF8, "application/xml")
        })
        {
            var actXml = () => SharpS3Client.EnsureSuccessAsync(xmlResponse);
            var ex = await actXml.Should().ThrowAsync<SharpS3ClientException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            ex.Which.ErrorCode.Should().Be("InvalidBucketName");
            ex.Which.S3ErrorMessage.Should().Be("The specified bucket is not valid.");
            ex.Which.Resource.Should().Be("/test");
            ex.Which.RequestId.Should().Be("123");
        }

        // 5. EnsureSuccessAsync with invalid XML / non-error text
        using (var textResponse = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Gateway Timeout from upstream proxy", Encoding.UTF8, "text/plain")
        })
        {
            var actText = () => SharpS3Client.EnsureSuccessAsync(textResponse);
            var ex = await actText.Should().ThrowAsync<SharpS3ClientException>();
            ex.Which.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            ex.Which.ErrorCode.Should().BeNull();
            ex.Which.Message.Should().Contain("Gateway Timeout");
        }

        // 6. GetHeaderValue: header not found
        using var dummyMsg = new HttpResponseMessage();
        SharpS3Client.GetHeaderValue(dummyMsg.Headers, "NonExistentHeader").Should().BeNull();

        // 7. GetHeaderValue: single value
        dummyMsg.Headers.TryAddWithoutValidation("X-Custom-Test", "value1");
        SharpS3Client.GetHeaderValue(dummyMsg.Headers, "X-Custom-Test").Should().Be("value1");

        // 8. GetHeaderValue: multiple values
        dummyMsg.Headers.TryAddWithoutValidation("X-Multi-Test", new[] { "first-val", "second-val" });
        SharpS3Client.GetHeaderValue(dummyMsg.Headers, "X-Multi-Test").Should().Be("first-val");

        // 9. GetHeaderValue: empty values collection
        using var emptyMsg = new HttpRequestMessage();
        emptyMsg.Headers.TryAddWithoutValidation("X-Empty-Test", Array.Empty<string>());
        SharpS3Client.GetHeaderValue(emptyMsg.Headers, "X-Empty-Test").Should().BeNull();
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_CompleteMultipartUploadAsync_ExceedsIntMaxValue_ThrowsEntityTooLarge()
    {
        var backend = new InMemoryStorageBackend();
        backend.DefaultOwner = new Owner("custom-owner", "custom");
        backend.DefaultOwner.ID.Should().Be("custom-owner");

        await backend.CreateBucketAsync("mp-huge-bucket", null);
        var init = await backend.CreateMultipartUploadAsync("mp-huge-bucket", "huge.bin");

        // Reusing the same 1MB byte array across 2100 parts so totalSizeLong exceeds int.MaxValue without allocating 2GB
        byte[] sharedBuffer = new byte[1024 * 1024]; // 1MB
        var upload = backend.Uploads[init.UploadId];
        for (int i = 1; i <= 2100; i++)
        {
            upload.Parts[i] = new InMemoryStorageBackend.StoredPart
            {
                PartNumber = i,
                Data = sharedBuffer,
                ETag = $"\"etag-{i}\"",
                LastModified = DateTime.UtcNow
            };
        }

        var act = () => backend.CompleteMultipartUploadAsync("mp-huge-bucket", "huge.bin", init.UploadId, new CompleteMultipartUpload());
        await act.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.EntityTooLarge);
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_UploadPartCopyAsync_MaxObjectSize_Enforcement()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("copy-src", null);
        await backend.CreateBucketAsync("copy-dest", null);

        byte[] srcData = new byte[20];
        await backend.PutObjectAsync("copy-src", "data.bin", new MemoryStream(srcData), srcData.Length, "application/octet-stream");

        var init = await backend.CreateMultipartUploadAsync("copy-dest", "part.bin");

        backend.MaxObjectSize = 10;

        // Part copy within MaxObjectSize (5 bytes <= 10)
        var copyPartOk = await backend.UploadPartCopyAsync("copy-src", "data.bin", "copy-dest", "part.bin", init.UploadId, 1, rangeStart: 0, rangeEnd: 4);
        copyPartOk.ETag.Should().NotBeNullOrEmpty();

        // Part copy exceeding MaxObjectSize (15 bytes > 10)
        var actTooLarge = () => backend.UploadPartCopyAsync("copy-src", "data.bin", "copy-dest", "part.bin", init.UploadId, 2, rangeStart: 0, rangeEnd: 14);
        await actTooLarge.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.EntityTooLarge);

        // Without MaxObjectSize, larger copy succeeds
        backend.MaxObjectSize = null;
        var copyPartNoLimit = await backend.UploadPartCopyAsync("copy-src", "data.bin", "copy-dest", "part.bin", init.UploadId, 2, rangeStart: 0, rangeEnd: 14);
        copyPartNoLimit.ETag.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_PopulateMetadataHeaders_And_NullMetadata_Handling()
    {
        // 1. Direct unit test of PopulateMetadataHeaders with null metadata
        var payload1 = new S3ObjectPayload();
        InMemoryStorageBackend.PopulateMetadataHeaders(payload1, null);
        payload1.Headers.Should().BeEmpty();

        // 2. Direct unit test of PopulateMetadataHeaders with prefixed and non-prefixed keys
        var payload2 = new S3ObjectPayload();
        var metadata = new Dictionary<string, string>
        {
            ["x-amz-meta-custom"] = "value1",
            ["other"] = "value2"
        };
        InMemoryStorageBackend.PopulateMetadataHeaders(payload2, metadata);
        payload2.Headers["x-amz-meta-custom"].Should().Be("value1");
        payload2.Headers["x-amz-meta-other"].Should().Be("value2");

        // 3. End-to-end integration via backend: object without metadata
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("meta-bucket", null);
        await backend.PutObjectAsync("meta-bucket", "no-meta.txt", new MemoryStream(new byte[] { 1, 2, 3 }), 3, "text/plain");

        using (var getNoMeta = await backend.GetObjectAsync("meta-bucket", "no-meta.txt"))
        {
            getNoMeta.Headers.Should().BeEmpty();
        }

        var metaNoMeta = await backend.GetObjectMetadataAsync("meta-bucket", "no-meta.txt");
        metaNoMeta.Should().NotBeNull();
        metaNoMeta!.Metadata.Should().BeEmpty();

        // 4. End-to-end integration: object with metadata
        await backend.PutObjectAsync("meta-bucket", "with-meta.txt", new MemoryStream(new byte[] { 4, 5, 6 }), 3, "text/plain", metadata);
        using (var getWithMeta = await backend.GetObjectAsync("meta-bucket", "with-meta.txt"))
        {
            getWithMeta.Headers["x-amz-meta-custom"].Should().Be("value1");
            getWithMeta.Headers["x-amz-meta-other"].Should().Be("value2");
        }

        var metaWithMeta = await backend.GetObjectMetadataAsync("meta-bucket", "with-meta.txt");
        metaWithMeta.Should().NotBeNull();
        metaWithMeta!.Metadata["x-amz-meta-custom"].Should().Be("value1");
        metaWithMeta!.Metadata["other"].Should().Be("value2");

        // 5. CopyObjectAsync for both null and non-null metadata
        await backend.CopyObjectAsync("meta-bucket", "no-meta.txt", "meta-bucket", "no-meta-copy.txt");
        var copyNoMeta = await backend.GetObjectMetadataAsync("meta-bucket", "no-meta-copy.txt");
        copyNoMeta!.Metadata.Should().BeEmpty();

        await backend.CopyObjectAsync("meta-bucket", "with-meta.txt", "meta-bucket", "with-meta-copy.txt");
        var copyWithMeta = await backend.GetObjectMetadataAsync("meta-bucket", "with-meta-copy.txt");
        copyWithMeta!.Metadata["x-amz-meta-custom"].Should().Be("value1");
    }

    private class NonSeekableStream : Stream
    {
        private readonly Stream _Inner;
        public NonSeekableStream(Stream inner) => _Inner = inner;
        public override bool CanRead => _Inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _Inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _Inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _Inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    
}
