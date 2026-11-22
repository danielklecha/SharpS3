namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

public partial class S3RequestDispatcherTests
{
    private static S3Request BuildRequest(
        string method,
        string path,
        Dictionary<string, string>? query = null,
        Dictionary<string, string[]>? headers = null,
        Stream? body = null,
        SharpS3Settings? settings = null)
    {
        return new S3Request(
            method,
            path,
            query ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            headers ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            body ?? Stream.Null,
            settings);
    }

    private static Stream StringToStreamCoverage(string str)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(str));
    }

    private class NonS3GenericRequest : IS3Request
    {
        public string RequestId => "req-id";
        public string TraceId => "trace-id";
        public string HttpMethod => "GET";
        public string RawPath => "/";
        public S3RequestType RequestType => S3RequestType.ServiceExists;
        public S3RequestStyle RequestStyle => S3RequestStyle.PathStyle;
        public string? Bucket => null;
        public string? Key => null;
        public string? AccessKey => null;
        public S3SignatureVersion SignatureVersion => S3SignatureVersion.Unknown;
        public IReadOnlyDictionary<string, string[]> Headers => new Dictionary<string, string[]>();
        public IReadOnlyDictionary<string, string> QueryParameters => new Dictionary<string, string>();
        public Stream BodyStream => Stream.Null;
        public object? Metadata { get; set; }
    }

    private class CustomMetadataBackend : InMemoryStorageBackend
    {
        private readonly ObjectMetadata? _metadataToReturn;

        public CustomMetadataBackend(ObjectMetadata? metadataToReturn)
        {
            _metadataToReturn = metadataToReturn;
        }

        public override Task<ObjectMetadata?> GetObjectMetadataAsync(string bucket, string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_metadataToReturn);
        }
    }

    #region Constructor & Basic Dispatch Tests (Lines 40, 50)

    [TestMethod]
    public void Constructor_NullBackend_ThrowsArgumentNullException()
    {
        Action act = () => new S3RequestDispatcher(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("backend");
    }

    [TestMethod]
    public void Constructor_NullSettings_UsesDefaultSettings_And_ExposesProperties()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend, null);

        dispatcher.Backend.Should().BeSameAs(backend);
        dispatcher.Settings.Should().NotBeNull();
        dispatcher.Settings.ValidateSignaturesBeforePreRequestHandler.Should().BeFalse();
    }

    [TestMethod]
    public async Task DispatchAsync_NonS3Request_ThrowsArgumentException()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);

        var act = async () => await dispatcher.DispatchAsync(new NonS3GenericRequest());
        (await act.Should().ThrowAsync<ArgumentException>())
            .WithMessage("*Request must be of type S3Request*");
    }

    #endregion

    #region Standard Response Headers Branches (Lines 145, 149)

    [TestMethod]
    public async Task ApplyStandardResponseHeaders_RequestId_And_TraceId_Branches()
    {
        var backend = new InMemoryStorageBackend();
        var settings = new SharpS3Settings();

        // 1. When preResponse already has x-amz-request-id and x-amz-id-2, dispatcher should NOT overwrite them
        settings.PreRequestHandler = req =>
        {
            var r = new S3ServerResponse { StatusCode = 200 };
            r.Headers["x-amz-request-id"] = "pre-existing-request-id";
            r.Headers["x-amz-id-2"] = "pre-existing-trace-id";
            return Task.FromResult<S3ServerResponse?>(r);
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var req1 = BuildRequest("GET", "/");
        req1.RequestId = "new-request-id";
        req1.TraceId = "new-trace-id";

        var resp1 = await dispatcher.DispatchAsync(req1);
        resp1.Headers["x-amz-request-id"].Should().Be("pre-existing-request-id");
        resp1.Headers["x-amz-id-2"].Should().Be("pre-existing-trace-id");

        // 2. Normal dispatch where response does NOT have headers -> headers are added from req
        settings.PreRequestHandler = null;
        var req2 = BuildRequest("GET", "/");
        req2.RequestType = S3RequestType.ServiceExists;
        req2.RequestId = "fresh-request-id";
        req2.TraceId = "fresh-trace-id";

        var resp2 = await dispatcher.DispatchAsync(req2);
        resp2.Headers["x-amz-request-id"].Should().Be("fresh-request-id");
        resp2.Headers["x-amz-id-2"].Should().Be("fresh-trace-id");

        // 3. Request without RequestId and TraceId -> headers are not added
        var req3 = BuildRequest("GET", "/");
        req3.RequestType = S3RequestType.ServiceExists;
        req3.RequestId = null!;
        req3.TraceId = null!;

        var resp3 = await dispatcher.DispatchAsync(req3);
        resp3.Headers.ContainsKey("x-amz-request-id").Should().BeFalse();
        resp3.Headers.ContainsKey("x-amz-id-2").Should().BeFalse();
    }

    #endregion

    #region Missing / Empty Bucket Validation Tests (Lines 204, 210, 226, 232, 238, 244, 250, 262, 268, 274, 282, 293, 301, 307, 315, 321, 327, 447)

    [TestMethod]
    public async Task BucketOperations_EmptyOrNullBucket_ThrowsNoSuchBucket()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);

        var bucketOperations = new[]
        {
            S3RequestType.BucketExists,
            S3RequestType.BucketDelete,
            S3RequestType.BucketRead,
            S3RequestType.BucketReadLocation,
            S3RequestType.BucketReadVersions,
            S3RequestType.BucketReadMultipartUploads,
            S3RequestType.BucketReadAcl,
            S3RequestType.BucketWriteAcl,
            S3RequestType.BucketDeleteAcl,
            S3RequestType.BucketReadTags,
            S3RequestType.BucketWriteTags,
            S3RequestType.BucketDeleteTags,
            S3RequestType.BucketReadVersioning,
            S3RequestType.BucketWriteVersioning,
            S3RequestType.BucketReadWebsite,
            S3RequestType.BucketWriteWebsite,
            S3RequestType.BucketDeleteWebsite,
            S3RequestType.BucketReadLogging,
            S3RequestType.BucketWriteLogging,
            S3RequestType.ObjectDeleteMultiple
        };

        foreach (var op in bucketOperations)
        {
            // Empty bucket string
            var reqEmpty = BuildRequest("GET", "/");
            reqEmpty.RequestType = op;
            reqEmpty.Bucket = string.Empty;

            var actEmpty = async () => await dispatcher.DispatchAsync(reqEmpty);
            (await actEmpty.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket, $"operation {op} should throw NoSuchBucket on empty bucket");

            // Null bucket string
            var reqNull = BuildRequest("GET", "/");
            reqNull.RequestType = op;
            reqNull.Bucket = null;

            var actNull = async () => await dispatcher.DispatchAsync(reqNull);
            (await actNull.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket, $"operation {op} should throw NoSuchBucket on null bucket");
        }
    }

    #endregion

    #region ObjectExists Metadata & ContentType Branches (Lines 344, 351)

    [TestMethod]
    public async Task ObjectExists_Metadata_ContentType_And_HeaderPrefix_Branches()
    {
        // 1. Metadata with ContentType == null, and mixed metadata keys (one with x-amz-meta- and one without)
        var owner = new Owner("owner-id", "owner-name");
        var mdWithPrefixes = new ObjectMetadata("file.bin", DateTime.UtcNow, "\"etag123\"", 100, owner)
        {
            ContentType = null
        };
        mdWithPrefixes.Metadata["x-amz-meta-already-prefixed"] = "value1";
        mdWithPrefixes.Metadata["custom-key"] = "value2";

        var backend1 = new CustomMetadataBackend(mdWithPrefixes);
        var dispatcher1 = new S3RequestDispatcher(backend1);

        var reqHead1 = BuildRequest("HEAD", "/mybucket/file.bin");
        reqHead1.RequestType = S3RequestType.ObjectExists;
        reqHead1.Bucket = "mybucket";
        reqHead1.Key = "file.bin";

        var respHead1 = await dispatcher1.DispatchAsync(reqHead1);
        respHead1.StatusCode.Should().Be(200);
        // Line 344: ContentType is null -> defaults to binary/octet-stream
        respHead1.Headers["Content-Type"].Should().Be("binary/octet-stream");
        // Line 351: StartsWith x-amz-meta- true branch
        respHead1.Headers["x-amz-meta-already-prefixed"].Should().Be("value1");
        // Line 351: StartsWith x-amz-meta- false branch (prepends x-amz-meta-)
        respHead1.Headers["x-amz-meta-custom-key"].Should().Be("value2");

        // 2. Metadata with non-null ContentType and null Metadata dictionary
        var mdWithoutMeta = new ObjectMetadata("file2.bin", DateTime.UtcNow, "\"etag456\"", 200, owner)
        {
            ContentType = "application/json"
        };
        // Reflection to set _Metadata to null
        var metaField = typeof(ObjectMetadata).GetField("_Metadata", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        metaField?.SetValue(mdWithoutMeta, null);

        var backend2 = new CustomMetadataBackend(mdWithoutMeta);
        var dispatcher2 = new S3RequestDispatcher(backend2);

        var reqHead2 = BuildRequest("HEAD", "/mybucket/file2.bin");
        reqHead2.RequestType = S3RequestType.ObjectExists;
        reqHead2.Bucket = "mybucket";
        reqHead2.Key = "file2.bin";

        var respHead2 = await dispatcher2.DispatchAsync(reqHead2);
        respHead2.StatusCode.Should().Be(200);
        // Line 344: Non-null ContentType preserved
        respHead2.Headers["Content-Type"].Should().Be("application/json");
    }

    #endregion

    #region OperationLimits on ObjectWrite & UploadPart (Lines 416, 422, 522, 527)

    [TestMethod]
    public async Task ObjectWrite_And_UploadPart_OperationLimits_Branches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b", null);

        var settings = new SharpS3Settings
        {
            OperationLimits = new OperationLimitsSettings
            {
                MaxPutObjectSize = 100,
                MaxUploadPartSize = 100
            }
        };
        var dispatcher = new S3RequestDispatcher(backend, settings);

        // --- ObjectWrite (PUT Object) ---

        // 1. Line 416: contentLength > MaxPutObjectSize -> throws EntityTooLarge
        var reqPutTooLarge = BuildRequest("PUT", "/b/huge.bin", body: new MemoryStream(new byte[150]));
        reqPutTooLarge.RequestType = S3RequestType.ObjectWrite;
        reqPutTooLarge.Bucket = "b";
        reqPutTooLarge.Key = "huge.bin";
        reqPutTooLarge.DecodedContentLength = 150;

        var actPutTooLarge = async () => await dispatcher.DispatchAsync(reqPutTooLarge);
        (await actPutTooLarge.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);

        // 2. Line 422: MaxPutObjectSize > 0 && req.BodyStream != Stream.Null -> wraps in BoundedReadStream and succeeds
        using var putStreamValid = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var reqPutValid = BuildRequest("PUT", "/b/valid.bin", body: putStreamValid);
        reqPutValid.RequestType = S3RequestType.ObjectWrite;
        reqPutValid.Bucket = "b";
        reqPutValid.Key = "valid.bin";
        reqPutValid.DecodedContentLength = 4;

        var respPutValid = await dispatcher.DispatchAsync(reqPutValid);
        respPutValid.StatusCode.Should().Be(200);

        // 3. Line 422: MaxPutObjectSize > 0 && req.BodyStream == Stream.Null
        var reqPutNullStream = BuildRequest("PUT", "/b/empty.bin", body: Stream.Null);
        reqPutNullStream.RequestType = S3RequestType.ObjectWrite;
        reqPutNullStream.Bucket = "b";
        reqPutNullStream.Key = "empty.bin";
        reqPutNullStream.DecodedContentLength = 0;

        var respPutNullStream = await dispatcher.DispatchAsync(reqPutNullStream);
        respPutNullStream.StatusCode.Should().Be(200);

        // --- ObjectUploadPart ---
        var initMp = await backend.CreateMultipartUploadAsync("b", "upload.bin");

        // 4. Line 522: partLength > MaxUploadPartSize -> throws EntityTooLarge
        var reqPartTooLarge = BuildRequest("PUT", $"/b/upload.bin?partNumber=1&uploadId={initMp.UploadId}", body: new MemoryStream(new byte[150]));
        reqPartTooLarge.RequestType = S3RequestType.ObjectUploadPart;
        reqPartTooLarge.Bucket = "b";
        reqPartTooLarge.Key = "upload.bin";
        reqPartTooLarge.UploadId = initMp.UploadId;
        reqPartTooLarge.PartNumber = 1;
        reqPartTooLarge.DecodedContentLength = 150;

        var actPartTooLarge = async () => await dispatcher.DispatchAsync(reqPartTooLarge);
        (await actPartTooLarge.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);

        // 5. Line 527: MaxUploadPartSize > 0 && req.BodyStream != Stream.Null -> wraps in BoundedReadStream and succeeds
        using var partStreamValid = new MemoryStream(new byte[] { 10, 20, 30 });
        var reqPartValid = BuildRequest("PUT", $"/b/upload.bin?partNumber=1&uploadId={initMp.UploadId}", body: partStreamValid);
        reqPartValid.RequestType = S3RequestType.ObjectUploadPart;
        reqPartValid.Bucket = "b";
        reqPartValid.Key = "upload.bin";
        reqPartValid.UploadId = initMp.UploadId;
        reqPartValid.PartNumber = 1;
        reqPartValid.DecodedContentLength = 3;

        var respPartValid = await dispatcher.DispatchAsync(reqPartValid);
        respPartValid.StatusCode.Should().Be(200);

        // 6. Line 527: MaxUploadPartSize > 0 && req.BodyStream == Stream.Null
        var reqPartNullStream = BuildRequest("PUT", $"/b/upload.bin?partNumber=2&uploadId={initMp.UploadId}", body: Stream.Null);
        reqPartNullStream.RequestType = S3RequestType.ObjectUploadPart;
        reqPartNullStream.Bucket = "b";
        reqPartNullStream.Key = "upload.bin";
        reqPartNullStream.UploadId = initMp.UploadId;
        reqPartNullStream.PartNumber = 2;
        reqPartNullStream.DecodedContentLength = 0;

        var respPartNullStream = await dispatcher.DispatchAsync(reqPartNullStream);
        respPartNullStream.StatusCode.Should().Be(200);

        // 7. OperationLimits is null -> limits checking skipped for ObjectWrite and ObjectUploadPart
        var settingsNullLimits = new SharpS3Settings { OperationLimits = null! };
        var dispatcherNullLimits = new S3RequestDispatcher(backend, settingsNullLimits);

        using var putStreamNullLimits = new MemoryStream(new byte[] { 1, 2, 3 });
        var reqPutNullLimits = BuildRequest("PUT", "/b/nolimits.bin", body: putStreamNullLimits);
        reqPutNullLimits.RequestType = S3RequestType.ObjectWrite;
        reqPutNullLimits.Bucket = "b";
        reqPutNullLimits.Key = "nolimits.bin";
        reqPutNullLimits.DecodedContentLength = 3;

        var respPutNullLimits = await dispatcherNullLimits.DispatchAsync(reqPutNullLimits);
        respPutNullLimits.StatusCode.Should().Be(200);

        using var partStreamNullLimits = new MemoryStream(new byte[] { 4, 5, 6 });
        var reqPartNullLimits = BuildRequest("PUT", $"/b/upload.bin?partNumber=3&uploadId={initMp.UploadId}", body: partStreamNullLimits);
        reqPartNullLimits.RequestType = S3RequestType.ObjectUploadPart;
        reqPartNullLimits.Bucket = "b";
        reqPartNullLimits.Key = "upload.bin";
        reqPartNullLimits.UploadId = initMp.UploadId;
        reqPartNullLimits.PartNumber = 3;
        reqPartNullLimits.DecodedContentLength = 3;

        var respPartNullLimits = await dispatcherNullLimits.DispatchAsync(reqPartNullLimits);
        respPartNullLimits.StatusCode.Should().Be(200);
    }

    #endregion

    #region Telemetry-Active Branches (Lines 73, 127, 139, 169, 363, 387, 427, 532)

    [TestMethod]
    public async Task Telemetry_Active_Execution_And_Exception_Paths()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b", null);
        await backend.PutObjectAsync("b", "sample.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello world")), 11, "text/plain");

        var telemetrySettings = new S3ServerTelemetrySettings { EnableTraces = true, EnableMetrics = true };
        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(telemetrySettings, () => settings, () => true);
        var dispatcher = new S3RequestDispatcher(backend, settings);

        // 1. Normal requests with active RequestTelemetry
        var rt = telemetry.BeginRequest()!;
        RequestTelemetry.Current = rt;
        try
        {
            // Lines 169 (StartCallback), 139 (Complete)
            var reqService = BuildRequest("GET", "/");
            reqService.RequestType = S3RequestType.ServiceExists;
            var respService = await dispatcher.DispatchAsync(reqService);
            respService.StatusCode.Should().Be(200);

            // Line 363: ObjectRead -> rt.RecordObjectSize(objPayload.ContentLength)
            var reqGet = BuildRequest("GET", "/b/sample.txt");
            reqGet.RequestType = S3RequestType.ObjectRead;
            reqGet.Bucket = "b";
            reqGet.Key = "sample.txt";
            var respGet = await dispatcher.DispatchAsync(reqGet);
            respGet.StatusCode.Should().Be(200);

            // Line 387: ObjectReadRange -> rt.RecordObjectSize(rangePayload.ContentLength)
            var reqRange = BuildRequest("GET", "/b/sample.txt");
            reqRange.RequestType = S3RequestType.ObjectReadRange;
            reqRange.Bucket = "b";
            reqRange.Key = "sample.txt";
            reqRange.RangeStart = 0;
            reqRange.RangeEnd = 4;
            var respRange = await dispatcher.DispatchAsync(reqRange);
            respRange.StatusCode.Should().Be(206);

            // Line 427: ObjectWrite -> rt.RecordObjectSize(contentLength)
            var reqPut = BuildRequest("PUT", "/b/new.bin", body: new MemoryStream(new byte[] { 1, 2, 3 }));
            reqPut.RequestType = S3RequestType.ObjectWrite;
            reqPut.Bucket = "b";
            reqPut.Key = "new.bin";
            reqPut.DecodedContentLength = 3;
            var respPut = await dispatcher.DispatchAsync(reqPut);
            respPut.StatusCode.Should().Be(200);

            // Line 532: ObjectUploadPart -> rt.RecordObjectSize(partLength)
            var initMp = await backend.CreateMultipartUploadAsync("b", "upload.bin");
            var reqPart = BuildRequest("PUT", $"/b/upload.bin?partNumber=1&uploadId={initMp.UploadId}", body: new MemoryStream(new byte[] { 1, 2 }));
            reqPart.RequestType = S3RequestType.ObjectUploadPart;
            reqPart.Bucket = "b";
            reqPart.Key = "upload.bin";
            reqPart.UploadId = initMp.UploadId;
            reqPart.PartNumber = 1;
            reqPart.DecodedContentLength = 2;
            var respPart = await dispatcher.DispatchAsync(reqPart);
            respPart.StatusCode.Should().Be(200);

            // Line 127: Exception inside InternalDispatchAsync -> rt.MarkException
            var reqMissingKey = BuildRequest("GET", "/b/missing.txt");
            reqMissingKey.RequestType = S3RequestType.ObjectRead;
            reqMissingKey.Bucket = "b";
            reqMissingKey.Key = "missing.txt";
            var actMissingKey = async () => await dispatcher.DispatchAsync(reqMissingKey);
            await actMissingKey.Should().ThrowAsync<Exception>();
        }
        finally
        {
            RequestTelemetry.Current = null;
        }

        // 2. Line 73: Signature validation with active RequestTelemetry (Success path)
        settings.EnableSignatures = true;
        settings.IsAnonymousRequestAllowedAsync = _ => Task.FromResult(true);
        var rtSigSuccess = telemetry.BeginRequest()!;
        RequestTelemetry.Current = rtSigSuccess;
        try
        {
            var reqSig = BuildRequest("GET", "/b/sample.txt");
            reqSig.RequestType = S3RequestType.ObjectRead;
            reqSig.Bucket = "b";
            reqSig.Key = "sample.txt";
            reqSig.IsSignatureValidated = false;

            var respSig = await dispatcher.DispatchAsync(reqSig);
            respSig.StatusCode.Should().Be(200);
            reqSig.IsSignatureValidated.Should().BeTrue();
        }
        finally
        {
            RequestTelemetry.Current = null;
        }

        // 3. Line 73: Signature validation with active RequestTelemetry (Failure path -> TelemetryStage.Fail)
        settings.EnableSignatures = true;
        settings.IsAnonymousRequestAllowedAsync = null; // No anonymous allowed -> ValidateSignatureAsync throws AccessDenied
        var rtSigFail = telemetry.BeginRequest()!;
        RequestTelemetry.Current = rtSigFail;
        try
        {
            var reqSigFail = BuildRequest("GET", "/b/sample.txt");
            reqSigFail.RequestType = S3RequestType.ObjectRead;
            reqSigFail.Bucket = "b";
            reqSigFail.Key = "sample.txt";
            reqSigFail.IsSignatureValidated = false;

            var actSigFail = async () => await dispatcher.DispatchAsync(reqSigFail);
            (await actSigFail.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }
    }

    #endregion

    #region ReadBodyStringAsync & DeserializeXmlPayload Branches (Lines 640, 646, 696)

    [TestMethod]
    public async Task ReadBodyStringAsync_OperationLimits_MaxXmlPayloadSize_Branches()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b", null);

        // 1. Line 640: MaxXmlPayloadSize configured in OperationLimits
        var settings = new SharpS3Settings
        {
            OperationLimits = new OperationLimitsSettings
            {
                MaxXmlPayloadSize = 50
            }
        };
        var dispatcher = new S3RequestDispatcher(backend, settings);

        // Payload > 50 bytes -> ReadBodyStringAsync throws EntityTooLarge
        string bigXml = "<Tagging><TagSet>" + new string('x', 60) + "</TagSet></Tagging>";
        using var bigStream = StringToStreamCoverage(bigXml);
        var reqBig = BuildRequest("PUT", "/b", new() { ["tagging"] = "" }, body: bigStream);
        reqBig.RequestType = S3RequestType.BucketWriteTags;
        reqBig.Bucket = "b";

        var actBig = async () => await dispatcher.DispatchAsync(reqBig);
        (await actBig.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);

        // 2. Line 643: OperationLimits is null -> falls back to default 10MB
        var settingsNullLimits = new SharpS3Settings { OperationLimits = null! };
        var dispatcherNullLimits = new S3RequestDispatcher(backend, settingsNullLimits);
        using var validStream = StringToStreamCoverage("<Tagging><TagSet><Tag><Key>k</Key><Value>v</Value></Tag></TagSet></Tagging>");
        var reqNullLimits = BuildRequest("PUT", "/b", new() { ["tagging"] = "" }, body: validStream);
        reqNullLimits.RequestType = S3RequestType.BucketWriteTags;
        reqNullLimits.Bucket = "b";

        var respNullLimits = await dispatcherNullLimits.DispatchAsync(reqNullLimits);
        respNullLimits.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task ReadBodyStringAsync_NullOrStreamNull_ReturnsEmptyString()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b", null);

        var dispatcher = new S3RequestDispatcher(backend);

        // Line 649: stream == Stream.Null in BucketWriteAcl
        // When body is empty, BucketWriteAcl defaults to new AccessControlPolicy(owner, acl)
        var reqStreamNull = BuildRequest("PUT", "/b", new() { ["acl"] = "" }, body: Stream.Null);
        reqStreamNull.RequestType = S3RequestType.BucketWriteAcl;
        reqStreamNull.Bucket = "b";

        var respStreamNull = await dispatcher.DispatchAsync(reqStreamNull);
        respStreamNull.StatusCode.Should().Be(200);

        // Line 649: stream == null in BucketWriteAcl
        var reqNullStream = BuildRequest("PUT", "/b", new() { ["acl"] = "" });
        typeof(S3Request).GetProperty(nameof(S3Request.BodyStream))!.SetValue(reqNullStream, null);
        reqNullStream.RequestType = S3RequestType.BucketWriteAcl;
        reqNullStream.Bucket = "b";

        var respNullStream = await dispatcher.DispatchAsync(reqNullStream);
        respNullStream.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task DeserializeXmlPayload_MalformedXml_ThrowsMalformedXML()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("b", null);
        var dispatcher = new S3RequestDispatcher(backend);

        // Line 696: Malformed XML throws S3Exception with ErrorCode.MalformedXML
        using var malformedStream = StringToStreamCoverage("<Tagging><BrokenXml>");
        var reqMalformed = BuildRequest("PUT", "/b", new() { ["tagging"] = "" }, body: malformedStream);
        reqMalformed.RequestType = S3RequestType.BucketWriteTags;
        reqMalformed.Bucket = "b";

        var actMalformed = async () => await dispatcher.DispatchAsync(reqMalformed);
        (await actMalformed.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.MalformedXML);
    }

    #endregion
}
