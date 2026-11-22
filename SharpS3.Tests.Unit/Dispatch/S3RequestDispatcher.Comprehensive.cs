namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;
using SharpS3.Storage;

public partial class S3RequestDispatcherTests
{
    private static S3Request BuildComprehensiveRequest(
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

    private static Stream StringToStream(string str) =>
        new MemoryStream(Encoding.UTF8.GetBytes(str));

    [TestMethod]
    public async Task DispatchAsync_InvalidRequestType_ThrowsArgumentException()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);

        var dummyMock = new DummyGenericRequest();
        var act = async () => await dispatcher.DispatchAsync(dummyMock);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private class DummyGenericRequest : IS3Request
    {
        public string RequestId { get; set; } = "req";
        public string TraceId { get; set; } = "trace";
        public string HttpMethod => "GET";
        public string RawPath => "/";
        public S3RequestType RequestType => S3RequestType.ServiceExists;
        public S3RequestStyle RequestStyle => S3RequestStyle.PathStyle;
        public bool Chunked { get; set; }
        public string? Region { get; set; }
        public string? Hostname { get; set; }
        public string? Host { get; set; }
        public string? BaseDomain { get; set; }
        public string? Bucket => null;
        public string? Key => null;
        public string? AccessKey => null;
        public S3SignatureVersion SignatureVersion => S3SignatureVersion.Unknown;
        public object? Metadata { get; set; }
        public IReadOnlyDictionary<string, string[]> Headers => new Dictionary<string, string[]>();
        public IReadOnlyDictionary<string, string> QueryParameters => new Dictionary<string, string>();
        public Stream BodyStream => Stream.Null;
    }

    [TestMethod]
    public async Task PreRequestHandler_And_AuthenticatedRequestHandler_Hooks()
    {
        var backend = new InMemoryStorageBackend();
        var settings = new SharpS3Settings();

        bool preRun = false;
        settings.PreRequestHandler = req =>
        {
            preRun = true;
            return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 418 });
        };

        var dispatcher = new S3RequestDispatcher(backend, settings);
        var req = BuildComprehensiveRequest("GET", "/");
        var resp = await dispatcher.DispatchAsync(req);

        preRun.Should().BeTrue();
        resp.StatusCode.Should().Be(418);

        // PreRequestHandler returning null falls through
        settings.PreRequestHandler = req => Task.FromResult<S3ServerResponse?>(null);
        var resp2 = await dispatcher.DispatchAsync(req);
        resp2.StatusCode.Should().Be(200);

        // AuthenticatedRequestHandler hook
        bool authRun = false;
        settings.AuthenticatedRequestHandler = req =>
        {
            authRun = true;
            return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 403 });
        };
        var resp3 = await dispatcher.DispatchAsync(req);
        authRun.Should().BeTrue();
        resp3.StatusCode.Should().Be(403);

        // PostRequestHandler hook
        bool postRun = false;
        settings.AuthenticatedRequestHandler = null;
        settings.PostRequestHandler = (r, res) =>
        {
            postRun = true;
            res.Headers["x-custom-post"] = "ok";
            return Task.CompletedTask;
        };
        var resp4 = await dispatcher.DispatchAsync(req);
        postRun.Should().BeTrue();
        resp4.Headers.Should().ContainKey("x-custom-post");
    }

    [TestMethod]
    public async Task PreRequestHandler_And_AuthenticatedRequestHandler_Hooks_With_Telemetry()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instr, l) => l.EnableMeasurementEvents(instr);
        meterListener.Start();

        var backend = new InMemoryStorageBackend();
        var telemetrySettings = new S3ServerTelemetrySettings();
        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(telemetrySettings, () => settings, () => true);

        // 1. AuthenticatedRequestHandler hook with telemetry
        var rtAuth = telemetry.BeginRequest()!;
        rtAuth.Should().NotBeNull();
        RequestTelemetry.Current = rtAuth;
        try
        {
            bool authRun = false;
            settings.AuthenticatedRequestHandler = req =>
            {
                authRun = true;
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 403 });
            };

            var dispatcher = new S3RequestDispatcher(backend, settings);
            var req = BuildComprehensiveRequest("GET", "/");
            var resp = await dispatcher.DispatchAsync(req);

            authRun.Should().BeTrue();
            resp.StatusCode.Should().Be(403);
            rtAuth.Handler.Should().Be(S3ServerTelemetryNames.HandlerAuthenticatedRequest);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }

        // 2. PreRequestHandler hook with telemetry (default: before signature validation)
        var rtPre = telemetry.BeginRequest()!;
        rtPre.Should().NotBeNull();
        RequestTelemetry.Current = rtPre;
        try
        {
            settings.AuthenticatedRequestHandler = null;
            bool preRun = false;
            settings.PreRequestHandler = req =>
            {
                preRun = true;
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 418 });
            };

            var dispatcher = new S3RequestDispatcher(backend, settings);
            var req = BuildComprehensiveRequest("GET", "/");
            var resp = await dispatcher.DispatchAsync(req);

            preRun.Should().BeTrue();
            resp.StatusCode.Should().Be(418);
            rtPre.Handler.Should().Be(S3ServerTelemetryNames.HandlerPreRequest);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }

        // 3. PreRequestHandler hook with telemetry when ValidateSignaturesBeforePreRequestHandler is true
        var rtPrePostSig = telemetry.BeginRequest()!;
        rtPrePostSig.Should().NotBeNull();
        RequestTelemetry.Current = rtPrePostSig;
        try
        {
            settings.ValidateSignaturesBeforePreRequestHandler = true;
            settings.EnableSignatures = false;
            bool preRun = false;
            settings.PreRequestHandler = req =>
            {
                preRun = true;
                return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 418 });
            };

            var dispatcher = new S3RequestDispatcher(backend, settings);
            var req = BuildComprehensiveRequest("GET", "/");
            var resp = await dispatcher.DispatchAsync(req);

            preRun.Should().BeTrue();
            resp.StatusCode.Should().Be(418);
            rtPrePostSig.Handler.Should().Be(S3ServerTelemetryNames.HandlerPreRequest);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }
    }

    [TestMethod]
    public async Task CORS_And_Standard_Headers_Emitted()
    {
        var backend = new InMemoryStorageBackend();
        var settings = new SharpS3Settings { EmitCorsHeaders = true };
        var dispatcher = new S3RequestDispatcher(backend, settings);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["origin"] = new[] { "https://example.com" }
        };
        var req = BuildComprehensiveRequest("HEAD", "/", headers: headers, settings: settings);
        req.RequestId = "test-req-id";
        req.TraceId = "test-trace-id";

        var resp = await dispatcher.DispatchAsync(req);
        resp.Headers["x-amz-request-id"].Should().Be("test-req-id");
        resp.Headers["x-amz-id-2"].Should().Be("test-trace-id");
        resp.Headers["Access-Control-Allow-Origin"].Should().Be("*");
        resp.Headers["Access-Control-Allow-Methods"].Should().Be("GET, PUT, POST, DELETE, HEAD, OPTIONS");
        resp.Headers["Access-Control-Allow-Headers"].Should().Be("*");
        resp.Headers["Access-Control-Expose-Headers"].Should().Contain("ETag");
    }

    [TestMethod]
    public async Task Service_And_Bucket_Listing_Operations()
    {
        var backend = new InMemoryStorageBackend();
        var dispatcher = new S3RequestDispatcher(backend);

        // ServiceExists
        var reqService = BuildComprehensiveRequest("HEAD", "/");
        var respService = await dispatcher.DispatchAsync(reqService);
        respService.StatusCode.Should().Be(200);
        respService.Headers["x-amz-bucket-region"].Should().Be("us-east-1");

        // ListBuckets
        var reqListBuckets = BuildComprehensiveRequest("GET", "/");
        var respListBuckets = await dispatcher.DispatchAsync(reqListBuckets);
        respListBuckets.StatusCode.Should().Be(200);
        respListBuckets.ContentType.Should().Be("application/xml");

        // Create Bucket (empty body)
        var reqCreateBucket = BuildComprehensiveRequest("PUT", "/test-bucket");
        var respCreate = await dispatcher.DispatchAsync(reqCreateBucket);
        respCreate.StatusCode.Should().Be(200);

        // BucketExists (true)
        var reqExists = BuildComprehensiveRequest("HEAD", "/test-bucket");
        var respExists = await dispatcher.DispatchAsync(reqExists);
        respExists.StatusCode.Should().Be(200);

        // BucketExists (false)
        var reqNotExists = BuildComprehensiveRequest("HEAD", "/missing-bucket");
        var respNotExists = await dispatcher.DispatchAsync(reqNotExists);
        respNotExists.StatusCode.Should().Be(404);

        // BucketWrite with CreateBucketConfiguration XML body
        string createXml = "<CreateBucketConfiguration><LocationConstraint>eu-west-1</LocationConstraint></CreateBucketConfiguration>";
        using var createStream = StringToStream(createXml);
        var reqCreateConfig = BuildComprehensiveRequest("PUT", "/eu-bucket", body: createStream);
        (await dispatcher.DispatchAsync(reqCreateConfig)).StatusCode.Should().Be(200);

        // BucketRead (v1)
        var reqReadV1 = BuildComprehensiveRequest("GET", "/test-bucket");
        (await dispatcher.DispatchAsync(reqReadV1)).StatusCode.Should().Be(200);

        // BucketRead (v2)
        var reqReadV2 = BuildComprehensiveRequest("GET", "/test-bucket", new() { ["list-type"] = "2", ["fetch-owner"] = "true" });
        (await dispatcher.DispatchAsync(reqReadV2)).StatusCode.Should().Be(200);

        // BucketReadLocation
        var reqLoc = BuildComprehensiveRequest("GET", "/test-bucket", new() { ["location"] = "" });
        (await dispatcher.DispatchAsync(reqLoc)).StatusCode.Should().Be(200);

        // BucketReadVersions
        var reqVers = BuildComprehensiveRequest("GET", "/test-bucket", new() { ["versions"] = "" });
        (await dispatcher.DispatchAsync(reqVers)).StatusCode.Should().Be(200);

        // BucketReadMultipartUploads
        var reqUps = BuildComprehensiveRequest("GET", "/test-bucket", new() { ["uploads"] = "" });
        (await dispatcher.DispatchAsync(reqUps)).StatusCode.Should().Be(200);

        // BucketDelete
        var reqDelete = BuildComprehensiveRequest("DELETE", "/eu-bucket");
        (await dispatcher.DispatchAsync(reqDelete)).StatusCode.Should().Be(204);
    }

    [TestMethod]
    public async Task Bucket_Configuration_Operations_Acl_Tags_Versioning_Website_Logging()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("cfg-bucket", null);
        var dispatcher = new S3RequestDispatcher(backend);

        // ACL: Read, Write (no body), Write (with XML), Delete
        var reqReadAcl = BuildComprehensiveRequest("GET", "/cfg-bucket", new() { ["acl"] = "" });
        (await dispatcher.DispatchAsync(reqReadAcl)).StatusCode.Should().Be(200);

        var reqWriteAclEmpty = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["acl"] = "" });
        (await dispatcher.DispatchAsync(reqWriteAclEmpty)).StatusCode.Should().Be(200);

        string aclXml = "<AccessControlPolicy><Owner><ID>owner1</ID><DisplayName>Owner</DisplayName></Owner></AccessControlPolicy>";
        using var aclStream = StringToStream(aclXml);
        var reqWriteAcl = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["acl"] = "" }, body: aclStream);
        (await dispatcher.DispatchAsync(reqWriteAcl)).StatusCode.Should().Be(200);

        var reqDelAcl = BuildComprehensiveRequest("DELETE", "/cfg-bucket", new() { ["acl"] = "" });
        (await dispatcher.DispatchAsync(reqDelAcl)).StatusCode.Should().Be(204);

        // Tags: Write, Read, Delete
        string tagsXml = "<Tagging><TagSet><Tag><Key>env</Key><Value>prod</Value></Tag></TagSet></Tagging>";
        using var tagStream = StringToStream(tagsXml);
        var reqWriteTags = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["tagging"] = "" }, body: tagStream);
        (await dispatcher.DispatchAsync(reqWriteTags)).StatusCode.Should().Be(200);

        var reqReadTags = BuildComprehensiveRequest("GET", "/cfg-bucket", new() { ["tagging"] = "" });
        (await dispatcher.DispatchAsync(reqReadTags)).StatusCode.Should().Be(200);

        var reqDelTags = BuildComprehensiveRequest("DELETE", "/cfg-bucket", new() { ["tagging"] = "" });
        (await dispatcher.DispatchAsync(reqDelTags)).StatusCode.Should().Be(204);

        // Versioning: Write, Read
        string verXml = "<VersioningConfiguration><Status>Enabled</Status></VersioningConfiguration>";
        using var verStream = StringToStream(verXml);
        var reqWriteVer = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["versioning"] = "" }, body: verStream);
        (await dispatcher.DispatchAsync(reqWriteVer)).StatusCode.Should().Be(200);

        var reqReadVer = BuildComprehensiveRequest("GET", "/cfg-bucket", new() { ["versioning"] = "" });
        (await dispatcher.DispatchAsync(reqReadVer)).StatusCode.Should().Be(200);

        // Website: Write, Read, Delete
        string webXml = "<WebsiteConfiguration><IndexDocument><Suffix>index.html</Suffix></IndexDocument></WebsiteConfiguration>";
        using var webStream = StringToStream(webXml);
        var reqWriteWeb = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["website"] = "" }, body: webStream);
        (await dispatcher.DispatchAsync(reqWriteWeb)).StatusCode.Should().Be(200);

        var reqReadWeb = BuildComprehensiveRequest("GET", "/cfg-bucket", new() { ["website"] = "" });
        (await dispatcher.DispatchAsync(reqReadWeb)).StatusCode.Should().Be(200);

        var reqDelWeb = BuildComprehensiveRequest("DELETE", "/cfg-bucket", new() { ["website"] = "" });
        (await dispatcher.DispatchAsync(reqDelWeb)).StatusCode.Should().Be(204);

        // Logging: Write, Read
        string logXml = "<BucketLoggingStatus><LoggingEnabled><TargetBucket>logs</TargetBucket><TargetPrefix>access/</TargetPrefix></LoggingEnabled></BucketLoggingStatus>";
        using var logStream = StringToStream(logXml);
        var reqWriteLog = BuildComprehensiveRequest("PUT", "/cfg-bucket", new() { ["logging"] = "" }, body: logStream);
        (await dispatcher.DispatchAsync(reqWriteLog)).StatusCode.Should().Be(200);

        var reqReadLog = BuildComprehensiveRequest("GET", "/cfg-bucket", new() { ["logging"] = "" });
        (await dispatcher.DispatchAsync(reqReadLog)).StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task Object_CRUD_Range_Copy_DeleteMultiple_And_Metadata()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("obj-bucket", null);
        var dispatcher = new S3RequestDispatcher(backend);

        // ObjectExists (not found -> 404)
        var reqHeadNotFound = BuildComprehensiveRequest("HEAD", "/obj-bucket/hello.txt");
        (await dispatcher.DispatchAsync(reqHeadNotFound)).StatusCode.Should().Be(404);

        // ObjectWrite with metadata and content-length header
        byte[] payload = Encoding.UTF8.GetBytes("Hello, Amazon S3!");
        using var putStream = new MemoryStream(payload);
        var putHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = new[] { payload.Length.ToString() },
            ["content-type"] = new[] { "text/plain" },
            ["x-amz-meta-author"] = new[] { "Alice" },
            ["x-amz-meta-project"] = new[] { "SharpS3" }
        };
        var reqPut = BuildComprehensiveRequest("PUT", "/obj-bucket/hello.txt", headers: putHeaders, body: putStream);
        var respPut = await dispatcher.DispatchAsync(reqPut);
        respPut.StatusCode.Should().Be(200);
        respPut.Headers.Should().ContainKey("ETag");

        // ObjectExists (found -> 200, check headers)
        var reqHeadFound = BuildComprehensiveRequest("HEAD", "/obj-bucket/hello.txt");
        var respHead = await dispatcher.DispatchAsync(reqHeadFound);
        respHead.StatusCode.Should().Be(200);
        respHead.Headers["x-amz-meta-author"].Should().Be("Alice");
        respHead.Headers["x-amz-meta-project"].Should().Be("SharpS3");

        // ObjectRead
        var reqGet = BuildComprehensiveRequest("GET", "/obj-bucket/hello.txt");
        var respGet = await dispatcher.DispatchAsync(reqGet);
        respGet.StatusCode.Should().Be(200);
        using var reader = new StreamReader(respGet.BodyStream);
        (await reader.ReadToEndAsync()).Should().Be("Hello, Amazon S3!");

        // ObjectReadRange
        var rangeHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["range"] = new[] { "bytes=0-4" }
        };
        var reqGetRange = BuildComprehensiveRequest("GET", "/obj-bucket/hello.txt", headers: rangeHeaders);
        var respGetRange = await dispatcher.DispatchAsync(reqGetRange);
        respGetRange.StatusCode.Should().Be(206);
        respGetRange.Headers.Should().ContainKey("Content-Range");
        using var rangeReader = new StreamReader(respGetRange.BodyStream);
        (await rangeReader.ReadToEndAsync()).Should().Be("Hello");

        // ObjectCopy
        var copyHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "/obj-bucket/hello.txt" }
        };
        var reqCopy = BuildComprehensiveRequest("PUT", "/obj-bucket/hello-copy.txt", headers: copyHeaders);
        var respCopy = await dispatcher.DispatchAsync(reqCopy);
        respCopy.StatusCode.Should().Be(200);

        // ObjectDelete
        var reqDel = BuildComprehensiveRequest("DELETE", "/obj-bucket/hello-copy.txt");
        (await dispatcher.DispatchAsync(reqDel)).StatusCode.Should().Be(204);

        // ObjectDeleteMultiple
        string deleteMultiXml = "<Delete><Object><Key>hello.txt</Key></Object></Delete>";
        using var delMultiStream = StringToStream(deleteMultiXml);
        var reqDelMulti = BuildComprehensiveRequest("POST", "/obj-bucket", new() { ["delete"] = "" }, body: delMultiStream);
        var respDelMulti = await dispatcher.DispatchAsync(reqDelMulti);
        respDelMulti.StatusCode.Should().Be(200);
        respDelMulti.ContentType.Should().Be("application/xml");
        using var srNormal = new StreamReader(respDelMulti.BodyStream);
        string normalBody = await srNormal.ReadToEndAsync();
        normalBody.Should().Contain("<Deleted>");

        // ObjectDeleteMultiple quiet mode (<Quiet>true</Quiet>)
        string deleteQuietXml = "<Delete><Quiet>true</Quiet><Object><Key>hello.txt</Key></Object></Delete>";
        using var delQuietStream = StringToStream(deleteQuietXml);
        var reqDelQuiet = BuildComprehensiveRequest("POST", "/obj-bucket", new() { ["delete"] = "" }, body: delQuietStream);
        var respDelQuiet = await dispatcher.DispatchAsync(reqDelQuiet);
        respDelQuiet.StatusCode.Should().Be(200);
        using var srQuiet = new StreamReader(respDelQuiet.BodyStream);
        string quietBody = await srQuiet.ReadToEndAsync();
        quietBody.Should().NotContain("<Deleted>");
    }

    [TestMethod]
    public async Task Object_Acl_Tags_LegalHold_Retention_Restore_Select()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("misc-bucket", null);
        using var data = new MemoryStream(new byte[] { 1, 2, 3 });
        await backend.PutObjectAsync("misc-bucket", "item.bin", data, 3, "application/octet-stream");
        var dispatcher = new S3RequestDispatcher(backend);

        // Object ACL: Read, Write, Delete
        var reqGetAcl = BuildComprehensiveRequest("GET", "/misc-bucket/item.bin", new() { ["acl"] = "" });
        (await dispatcher.DispatchAsync(reqGetAcl)).StatusCode.Should().Be(200);

        string aclXml = "<AccessControlPolicy><Owner><ID>o1</ID></Owner></AccessControlPolicy>";
        using var aclStream = StringToStream(aclXml);
        var reqPutAcl = BuildComprehensiveRequest("PUT", "/misc-bucket/item.bin", new() { ["acl"] = "" }, body: aclStream);
        (await dispatcher.DispatchAsync(reqPutAcl)).StatusCode.Should().Be(200);

        var reqDelAcl = BuildComprehensiveRequest("DELETE", "/misc-bucket/item.bin", new() { ["acl"] = "" });
        (await dispatcher.DispatchAsync(reqDelAcl)).StatusCode.Should().Be(204);

        // Object Tags: Write, Read, Delete
        string tagsXml = "<Tagging><TagSet><Tag><Key>tag1</Key><Value>val1</Value></Tag></TagSet></Tagging>";
        using var tagStream = StringToStream(tagsXml);
        var reqPutTags = BuildComprehensiveRequest("PUT", "/misc-bucket/item.bin", new() { ["tagging"] = "" }, body: tagStream);
        (await dispatcher.DispatchAsync(reqPutTags)).StatusCode.Should().Be(200);

        var reqGetTags = BuildComprehensiveRequest("GET", "/misc-bucket/item.bin", new() { ["tagging"] = "" });
        (await dispatcher.DispatchAsync(reqGetTags)).StatusCode.Should().Be(200);

        var reqDelTags = BuildComprehensiveRequest("DELETE", "/misc-bucket/item.bin", new() { ["tagging"] = "" });
        (await dispatcher.DispatchAsync(reqDelTags)).StatusCode.Should().Be(204);

        // LegalHold: Write, Read
        string lhXml = "<LegalHold><Status>ON</Status></LegalHold>";
        using var lhStream = StringToStream(lhXml);
        var reqPutLh = BuildComprehensiveRequest("PUT", "/misc-bucket/item.bin", new() { ["legal-hold"] = "" }, body: lhStream);
        (await dispatcher.DispatchAsync(reqPutLh)).StatusCode.Should().Be(200);

        var reqGetLh = BuildComprehensiveRequest("GET", "/misc-bucket/item.bin", new() { ["legal-hold"] = "" });
        (await dispatcher.DispatchAsync(reqGetLh)).StatusCode.Should().Be(200);

        // Retention: Write, Read
        string retXml = "<Retention><Mode>COMPLIANCE</Mode><RetainUntilDate>2030-01-01T00:00:00Z</RetainUntilDate></Retention>";
        using var retStream = StringToStream(retXml);
        var reqPutRet = BuildComprehensiveRequest("PUT", "/misc-bucket/item.bin", new() { ["retention"] = "" }, body: retStream);
        (await dispatcher.DispatchAsync(reqPutRet)).StatusCode.Should().Be(200);

        var reqGetRet = BuildComprehensiveRequest("GET", "/misc-bucket/item.bin", new() { ["retention"] = "" });
        (await dispatcher.DispatchAsync(reqGetRet)).StatusCode.Should().Be(200);

        // Restore: Post
        string restXml = "<RestoreRequest><Days>5</Days><GlacierJobParameters><Tier>Standard</Tier></GlacierJobParameters></RestoreRequest>";
        using var restStream = StringToStream(restXml);
        var reqRestore = BuildComprehensiveRequest("POST", "/misc-bucket/item.bin", new() { ["restore"] = "" }, body: restStream);
        var respRestore = await dispatcher.DispatchAsync(reqRestore);
        (respRestore.StatusCode == 200 || respRestore.StatusCode == 202).Should().BeTrue();

        // SelectContent: Post
        var reqSelect = BuildComprehensiveRequest("POST", "/misc-bucket/item.bin", new() { ["select"] = "", ["select-type"] = "2" });
        (await dispatcher.DispatchAsync(reqSelect)).StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task Multipart_Uploads_Full_Workflow()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("multi-bucket", null);
        var dispatcher = new S3RequestDispatcher(backend);

        // 1. CreateMultipartUpload
        var reqInit = BuildComprehensiveRequest("POST", "/multi-bucket/large.bin", new() { ["uploads"] = "" });
        var respInit = await dispatcher.DispatchAsync(reqInit);
        respInit.StatusCode.Should().Be(200);
        using var initReader = new StreamReader(respInit.BodyStream);
        string initXml = await initReader.ReadToEndAsync();
        var initResult = SerializationHelper.DeserializeXml<InitiateMultipartUploadResult>(initXml);
        string uploadId = initResult.UploadId;
        uploadId.Should().NotBeNullOrEmpty();

        // 2. UploadPart
        byte[] partBytes = Encoding.UTF8.GetBytes("Part 1 content");
        using var part1Stream = new MemoryStream(partBytes);
        var part1Headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = new[] { partBytes.Length.ToString() }
        };
        var reqPart1 = BuildComprehensiveRequest("PUT", "/multi-bucket/large.bin", new() { ["partNumber"] = "1", ["uploadId"] = uploadId }, headers: part1Headers, body: part1Stream);
        var respPart1 = await dispatcher.DispatchAsync(reqPart1);
        respPart1.StatusCode.Should().Be(200);
        string etag1 = respPart1.Headers["ETag"];

        // 3. UploadPartCopy from another object
        using var copySrcData = new MemoryStream(new byte[] { 10, 20, 30 });
        await backend.PutObjectAsync("multi-bucket", "source.bin", copySrcData, 3, "application/octet-stream");

        var copyPartHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "/multi-bucket/source.bin" }
        };
        var reqCopyPart = BuildComprehensiveRequest("PUT", "/multi-bucket/large.bin", new() { ["partNumber"] = "2", ["uploadId"] = uploadId }, headers: copyPartHeaders);
        var respCopyPart = await dispatcher.DispatchAsync(reqCopyPart);
        respCopyPart.StatusCode.Should().Be(200);

        // 4. ListParts
        var reqListParts = BuildComprehensiveRequest("GET", "/multi-bucket/large.bin", new() { ["uploadId"] = uploadId });
        var respListParts = await dispatcher.DispatchAsync(reqListParts);
        respListParts.StatusCode.Should().Be(200);

        // 5. CompleteMultipartUpload
        string completeXml = $"<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>{etag1}</ETag></Part></CompleteMultipartUpload>";
        using var completeStream = StringToStream(completeXml);
        var reqComplete = BuildComprehensiveRequest("POST", "/multi-bucket/large.bin", new() { ["uploadId"] = uploadId }, body: completeStream);
        var respComplete = await dispatcher.DispatchAsync(reqComplete);
        respComplete.StatusCode.Should().Be(200);

        // 6. AbortMultipartUpload on new upload
        var reqInit2 = BuildComprehensiveRequest("POST", "/multi-bucket/aborted.bin", new() { ["uploads"] = "" });
        var respInit2 = await dispatcher.DispatchAsync(reqInit2);
        using var initReader2 = new StreamReader(respInit2.BodyStream);
        var initResult2 = SerializationHelper.DeserializeXml<InitiateMultipartUploadResult>(await initReader2.ReadToEndAsync());

        var reqAbort = BuildComprehensiveRequest("DELETE", "/multi-bucket/aborted.bin", new() { ["uploadId"] = initResult2.UploadId });
        (await dispatcher.DispatchAsync(reqAbort)).StatusCode.Should().Be(204);
    }

    [TestMethod]
    public async Task Dispatcher_ValidationErrors_And_EdgeCases()
    {
        var backend = new InMemoryStorageBackend();
        var settings = new SharpS3Settings
        {
            OperationLimits = new OperationLimitsSettings { MaxPutObjectSize = 10 }
        };
        var dispatcher = new S3RequestDispatcher(backend, settings);

        // Missing bucket on BucketExists throws NoSuchBucket
        var reqNoBucket = BuildComprehensiveRequest("HEAD", "/");
        reqNoBucket.RequestType = S3RequestType.BucketExists;
        var actNoBucket = async () => await dispatcher.DispatchAsync(reqNoBucket);
        (await actNoBucket.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchBucket);

        // Missing bucket on BucketWrite throws InvalidBucketName
        var reqNoBucketWrite = BuildComprehensiveRequest("PUT", "/");
        reqNoBucketWrite.RequestType = S3RequestType.BucketWrite;
        var actNoBucketWrite = async () => await dispatcher.DispatchAsync(reqNoBucketWrite);
        (await actNoBucketWrite.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

        // MaxPutObjectSize exceeded throws EntityTooLarge
        var headersTooLarge = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-length"] = new[] { "100" }
        };
        var reqTooLarge = BuildComprehensiveRequest("PUT", "/b/huge.bin", headers: headersTooLarge);
        var actTooLarge = async () => await dispatcher.DispatchAsync(reqTooLarge);
        (await actTooLarge.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.EntityTooLarge);

        // ObjectCopy with missing source throws InvalidRequest
        var reqInvalidCopy = BuildComprehensiveRequest("PUT", "/b/dest.bin");
        reqInvalidCopy.RequestType = S3RequestType.ObjectCopy;
        var actInvalidCopy = async () => await dispatcher.DispatchAsync(reqInvalidCopy);
        (await actInvalidCopy.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        // Malformed XML payload throws MalformedXML
        using var badXmlStream = StringToStream("<<<not xml>>>");
        var reqBadXml = BuildComprehensiveRequest("PUT", "/b", new() { ["tagging"] = "" }, body: badXmlStream);
        var actBadXml = async () => await dispatcher.DispatchAsync(reqBadXml);
        (await actBadXml.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.MalformedXML);

        // DefaultRequestHandler handles unknown requests
        var reqUnknown = BuildComprehensiveRequest("OPTIONS", "/b/custom");
        reqUnknown.RequestType = S3RequestType.Unknown;

        // Without DefaultRequestHandler -> throws InvalidRequest
        var actUnknown = async () => await dispatcher.DispatchAsync(reqUnknown);
        (await actUnknown.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        // With DefaultRequestHandler returning response
        settings.DefaultRequestHandler = r => Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 200 });
        (await dispatcher.DispatchAsync(reqUnknown)).StatusCode.Should().Be(200);

        // With DefaultRequestHandler returning null -> throws InvalidRequest
        settings.DefaultRequestHandler = r => Task.FromResult<S3ServerResponse?>(null);
        var actUnknownNull = async () => await dispatcher.DispatchAsync(reqUnknown);
        (await actUnknownNull.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
    }
}
