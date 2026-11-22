namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Auth;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

[TestClass]
public class SecurityRemediationTests
{
    private string _tempDir = null!;
    private FileSystemStorageBackend _fileBackend = null!;
    private InMemoryStorageBackend _memBackend = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SharpS3_SecRemedTests_" + Guid.NewGuid().ToString("N"));
        _fileBackend = new FileSystemStorageBackend(_tempDir);
        _memBackend = new InMemoryStorageBackend();
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    #region ConstantTimeHelper-Tests

    [TestMethod]
    public void ConstantTimeHelper_FixedTimeEquals_ValidatesCorrectly()
    {
        byte[] a = Encoding.UTF8.GetBytes("abcdef123456");
        byte[] b = Encoding.UTF8.GetBytes("abcdef123456");
        byte[] c = Encoding.UTF8.GetBytes("abcdef123457");
        byte[] d = Encoding.UTF8.GetBytes("short");

        ConstantTimeHelper.FixedTimeEquals(a, b).Should().BeTrue();
        ConstantTimeHelper.FixedTimeEquals(a, c).Should().BeFalse();
        ConstantTimeHelper.FixedTimeEquals(a, d).Should().BeFalse();
        ConstantTimeHelper.FixedTimeEquals(null, null).Should().BeTrue();
        ConstantTimeHelper.FixedTimeEquals(a, null).Should().BeFalse();
        ConstantTimeHelper.FixedTimeEquals(null, b).Should().BeFalse();
    }

    [TestMethod]
    public void ConstantTimeHelper_FixedTimeEqualsHex_HandlesCaseAndMismatch()
    {
        string hex1 = "4A7B8C9D0E";
        string hex2 = "4a7b8c9d0e";
        string hex3 = "4a7b8c9d0f";
        string hex4 = "4a7b";

        ConstantTimeHelper.FixedTimeEqualsHex(hex1, hex2).Should().BeTrue();
        ConstantTimeHelper.FixedTimeEqualsHex(hex1, hex3).Should().BeFalse();
        ConstantTimeHelper.FixedTimeEqualsHex(hex1, hex4).Should().BeFalse();
        ConstantTimeHelper.FixedTimeEqualsHex(null, null).Should().BeTrue();
        ConstantTimeHelper.FixedTimeEqualsHex(hex1, null).Should().BeFalse();
    }

    #endregion

    #region SigV4-PresignedUrl-And-Expiration-Tests

    [TestMethod]
    public async Task ValidateV4Signature_ValidPresignedUrl_Succeeds()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        var clientOptions = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost"),
            AccessKey = "TESTKEY",
            SecretKey = "TESTSECRET"
        };
        var client = new SharpS3Client(clientOptions);

        var presignedUri = client.GeneratePresignedUrl("mybucket", "report.pdf", HttpMethod.Get, TimeSpan.FromMinutes(10));

        var req = new S3Request(
            "GET",
            presignedUri.AbsolutePath,
            ParseQuery(presignedUri.Query),
            new Dictionary<string, string[]> { ["host"] = new[] { presignedUri.Host } },
            Stream.Null,
            settings);

        req.SignatureVersion.Should().Be(S3SignatureVersion.Version4);
        req.AccessKey.Should().Be("TESTKEY");

        Func<Task> act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ValidateV4Signature_ExpiredPresignedUrl_ThrowsAccessDenied()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        // Query parameters with an expired timestamp (1 hour ago, 60-second expiration)
        string pastDate = DateTime.UtcNow.AddHours(-1).ToString(Constants.AmazonTimestampFormatCompact);
        var qParams = new Dictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = "TESTKEY/20261121/us-east-1/s3/aws4_request",
            ["X-Amz-Date"] = pastDate,
            ["X-Amz-Expires"] = "60",
            ["X-Amz-SignedHeaders"] = "host",
            ["X-Amz-Signature"] = "dummy"
        };

        var req = new S3Request("GET", "/mybucket/report.pdf", qParams, new Dictionary<string, string[]> { ["host"] = new[] { "localhost" } }, Stream.Null, settings);

        Func<Task> act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
        ex.Which.Error.Message.Should().Contain("expired");
    }

    [TestMethod]
    public async Task ValidateV4Signature_ClockSkew_WhenEnabled_RejectsOldHeaderRequests()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            ValidateTimestampSkew = true,
            MaxClockSkew = TimeSpan.FromMinutes(15)
        };
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        // Request with timestamp 30 minutes in the past
        string oldTimestamp = DateTime.UtcNow.AddMinutes(-30).ToString(Constants.AmazonTimestampFormatCompact);
        var headers = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { oldTimestamp },
            ["authorization"] = new[] { $"AWS4-HMAC-SHA256 Credential=TESTKEY/20261121/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-date, Signature=dummy" }
        };

        var req = new S3Request("GET", "/mybucket/test.txt", new Dictionary<string, string>(), headers, Stream.Null, settings);

        Func<Task> act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.RequestTimeTooSkewed);
    }

    #endregion

    #region Multipart-Upload-Isolation-And-Validation-Tests

    [TestMethod]
    public async Task MultipartUpload_CrossTenant_BucketMismatch_ThrowsNoSuchUpload()
    {
        await _fileBackend.CreateBucketAsync("tenant-a", null);
        await _fileBackend.CreateBucketAsync("tenant-b", null);

        var init = await _fileBackend.CreateMultipartUploadAsync("tenant-a", "secret.doc");

        using var partData = new MemoryStream(new byte[] { 1, 2, 3 });

        // Attempting to upload a part targeting tenant-b with tenant-a's uploadId
        Func<Task> actPart = async () => await _fileBackend.UploadPartAsync("tenant-b", "secret.doc", init.UploadId, 1, partData, 3);
        (await actPart.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // Attempting to complete upload in tenant-b
        Func<Task> actComplete = async () => await _fileBackend.CompleteMultipartUploadAsync("tenant-b", "secret.doc", init.UploadId, new CompleteMultipartUpload());
        (await actComplete.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // Attempting to abort upload in tenant-b
        Func<Task> actAbort = async () => await _fileBackend.AbortMultipartUploadAsync("tenant-b", "secret.doc", init.UploadId);
        (await actAbort.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task MultipartUpload_CrossTenant_InMemory_BucketMismatch_ThrowsNoSuchUpload()
    {
        await _memBackend.CreateBucketAsync("tenant-a", null);
        await _memBackend.CreateBucketAsync("tenant-b", null);

        var init = await _memBackend.CreateMultipartUploadAsync("tenant-a", "doc.txt");

        using var partData = new MemoryStream(new byte[] { 1, 2 });
        Func<Task> actPart = async () => await _memBackend.UploadPartAsync("tenant-b", "doc.txt", init.UploadId, 1, partData, 2);
        (await actPart.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task MultipartUpload_InvalidPartNumber_ThrowsInvalidArgument()
    {
        await _fileBackend.CreateBucketAsync("mybucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("mybucket", "data.bin");

        using var data = new MemoryStream(new byte[] { 1 });

        // Part number 0
        Func<Task> actZero = async () => await _fileBackend.UploadPartAsync("mybucket", "data.bin", init.UploadId, 0, data, 1);
        (await actZero.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        // Negative part number
        Func<Task> actNeg = async () => await _fileBackend.UploadPartAsync("mybucket", "data.bin", init.UploadId, -1, data, 1);
        (await actNeg.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        // Part number > 10000
        Func<Task> actHigh = async () => await _fileBackend.UploadPartAsync("mybucket", "data.bin", init.UploadId, 10001, data, 1);
        (await actHigh.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);
    }

    [TestMethod]
    public async Task MultipartUpload_Complete_PartValidation_OrderingAndMissingPart()
    {
        await _fileBackend.CreateBucketAsync("mybucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("mybucket", "ordered.bin");

        using var p1 = new MemoryStream(new byte[] { 1, 2 });
        string etag1 = await _fileBackend.UploadPartAsync("mybucket", "ordered.bin", init.UploadId, 1, p1, 2);
        using var p2 = new MemoryStream(new byte[] { 3, 4 });
        string etag2 = await _fileBackend.UploadPartAsync("mybucket", "ordered.bin", init.UploadId, 2, p2, 2);

        // Non-ascending order in CompleteMultipartUpload
        var badOrder = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 2, ETag = etag2 },
                new Part { PartNumber = 1, ETag = etag1 }
            }
        };

        Func<Task> actOrder = async () => await _fileBackend.CompleteMultipartUploadAsync("mybucket", "ordered.bin", init.UploadId, badOrder);
        (await actOrder.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPartOrder);

        // Missing part
        var missingPart = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = etag1 },
                new Part { PartNumber = 3, ETag = etag2 }
            }
        };

        Func<Task> actMissing = async () => await _fileBackend.CompleteMultipartUploadAsync("mybucket", "ordered.bin", init.UploadId, missingPart);
        (await actMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPart);
    }

    [TestMethod]
    public async Task MultipartUpload_Complete_ETagMismatch_WhenValidationEnabled_ThrowsInvalidPart()
    {
        _fileBackend.ValidatePartETags = true;
        await _fileBackend.CreateBucketAsync("mybucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("mybucket", "etagcheck.bin");

        using var p1 = new MemoryStream(new byte[] { 1, 2, 3 });
        await _fileBackend.UploadPartAsync("mybucket", "etagcheck.bin", init.UploadId, 1, p1, 3);

        var badEtag = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = "\"mismatched-etag\"" }
            }
        };

        Func<Task> act = async () => await _fileBackend.CompleteMultipartUploadAsync("mybucket", "etagcheck.bin", init.UploadId, badEtag);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPart);
    }

    #endregion

    #region Self-Copy-And-BatchDelete-Tests

    [TestMethod]
    public async Task CopyObjectAsync_SelfCopy_DoesNotThrowFileLockException()
    {
        await _fileBackend.CreateBucketAsync("copy-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("hello self copy"));
        await _fileBackend.PutObjectAsync("copy-bucket", "item.txt", stream, 15, "text/plain");

        // Self-copy: copying an object to the exact same path
        Func<Task> act = async () => await _fileBackend.CopyObjectAsync("copy-bucket", "item.txt", "copy-bucket", "item.txt");
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task DeleteMultipleObjectsAsync_ErrorRecordedInResult()
    {
        await _fileBackend.CreateBucketAsync("del-bucket", null);

        // Provide an invalid key traversal that fails DeleteObjectAsync
        var deleteMultiple = new DeleteMultiple
        {
            Objects = new List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object("."),
                new SharpS3.Models.Object("..")
            }
        };

        var result = await _fileBackend.DeleteMultipleObjectsAsync("del-bucket", deleteMultiple);
        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Code.Should().Be(ErrorCode.InvalidRequest);
    }

    #endregion

    #region IPv6-And-VirtualHosted-Tests

    [TestMethod]
    public void S3Request_IPv6Host_ParsedCorrectlyWithoutCrashing()
    {
        var settings = new SharpS3Settings();

        // IPv6 bracketed host
        var req = new S3Request(
            "GET",
            "/mybucket/mykey",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { ["host"] = new[] { "[::1]:8080" } },
            Stream.Null,
            settings);

        req.Hostname.Should().Be("[::1]");
        req.RequestStyle.Should().Be(S3RequestStyle.PathStyle);
        req.Bucket.Should().Be("mybucket");
        req.Key.Should().Be("mykey");
    }

    [TestMethod]
    public void SharpS3Settings_RestrictVirtualHostingToBaseDomains_Enforced()
    {
        var settings = new SharpS3Settings
        {
            RestrictVirtualHostingToBaseDomains = true,
            BaseDomains = new List<string> { "s3.mycompany.com" }
        };

        // Allowed configured domain
        settings.GetMatchingBaseDomain("mybucket.s3.mycompany.com").Should().Be("s3.mycompany.com");

        // Unconfigured domain should not match when restricted
        settings.GetMatchingBaseDomain("mybucket.attacker.com").Should().BeNull();
    }

    #endregion

    #region Conditional-Headers-RFC9110-Tests

    [TestMethod]
    public async Task S3RequestDispatcher_IfMatch_ConditionEvaluation()
    {
        var dispatcher = new S3RequestDispatcher(_fileBackend);
        await _fileBackend.CreateBucketAsync("cond-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test conditional"));
        string etag = await _fileBackend.PutObjectAsync("cond-bucket", "file.txt", stream, 16, "text/plain");

        // 1. If-Match with matching ETag succeeds (200 OK)
        var matchReq = new S3Request(
            "GET",
            "/cond-bucket/file.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-match"] = new[] { etag }
            },
            Stream.Null);

        var respMatch = await dispatcher.DispatchAsync(matchReq);
        respMatch.StatusCode.Should().Be(200);

        // 2. If-Match with mismatching ETag throws 412 PreconditionFailed
        var mismatchReq = new S3Request(
            "GET",
            "/cond-bucket/file.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-match"] = new[] { "\"wrong-etag\"" }
            },
            Stream.Null);

        Func<Task> actMismatch = async () => await dispatcher.DispatchAsync(mismatchReq);
        var ex = await actMismatch.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);
    }

    [TestMethod]
    public async Task S3RequestDispatcher_IfNoneMatch_Returns304NotModified()
    {
        var dispatcher = new S3RequestDispatcher(_fileBackend);
        await _fileBackend.CreateBucketAsync("cond-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test conditional"));
        string etag = await _fileBackend.PutObjectAsync("cond-bucket", "cached.txt", stream, 16, "text/plain");

        var ifNoneMatchReq = new S3Request(
            "GET",
            "/cond-bucket/cached.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { etag }
            },
            Stream.Null);

        var resp = await dispatcher.DispatchAsync(ifNoneMatchReq);
        resp.StatusCode.Should().Be(304);
    }

    [TestMethod]
    public async Task S3RequestDispatcher_IfModifiedSince_Returns304NotModified()
    {
        var dispatcher = new S3RequestDispatcher(_fileBackend);
        await _fileBackend.CreateBucketAsync("cond-bucket", null);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test conditional"));
        await _fileBackend.PutObjectAsync("cond-bucket", "date.txt", stream, 16, "text/plain");

        // If-Modified-Since with a future date -> 304 Not Modified
        string futureDate = DateTime.UtcNow.AddDays(1).ToString("r");
        var ifModReq = new S3Request(
            "GET",
            "/cond-bucket/date.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-modified-since"] = new[] { futureDate }
            },
            Stream.Null);

        var resp = await dispatcher.DispatchAsync(ifModReq);
        resp.StatusCode.Should().Be(304);
    }

    #endregion

    #region CORS-Allowed-Origins-Tests

    [TestMethod]
    public async Task S3RequestDispatcher_CORS_AllowedOrigins_Restricted()
    {
        var settings = new SharpS3Settings
        {
            EmitCorsHeaders = true,
            AllowedCorsOrigins = new List<string> { "https://trusted.app.com" }
        };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);
        await _fileBackend.CreateBucketAsync("cors-bucket", null);

        // 1. Trusted origin -> reflected in header
        var reqTrusted = new S3Request(
            "HEAD",
            "/cors-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["origin"] = new[] { "https://trusted.app.com" }
            },
            Stream.Null,
            settings);

        var respTrusted = await dispatcher.DispatchAsync(reqTrusted);
        respTrusted.Headers["Access-Control-Allow-Origin"].Should().Be("https://trusted.app.com");

        // 2. Untrusted origin -> Access-Control-Allow-Origin omitted
        var reqUntrusted = new S3Request(
            "HEAD",
            "/cors-bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["origin"] = new[] { "https://evil.attacker.com" }
            },
            Stream.Null,
            settings);

        var respUntrusted = await dispatcher.DispatchAsync(reqUntrusted);
        respUntrusted.Headers.ContainsKey("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [TestMethod]
    public async Task ValidateV4Signature_Expires_InvalidFormatOrNegative_ThrowsAccessDenied()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        // 1. Non-integer expires value
        var qParamsNonInt = new Dictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = "TESTKEY/20261121/us-east-1/s3/aws4_request",
            ["X-Amz-Date"] = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact),
            ["X-Amz-Expires"] = "not_a_number",
            ["X-Amz-SignedHeaders"] = "host",
            ["X-Amz-Signature"] = "dummy"
        };
        var reqNonInt = new S3Request("GET", "/bucket/file.txt", qParamsNonInt, new Dictionary<string, string[]> { ["host"] = new[] { "localhost" } }, Stream.Null, settings);
        Func<Task> act1 = async () => await SharpS3Server.ValidateSignatureAsync(reqNonInt, settings, null, default);
        (await act1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // 2. Negative expires value
        var qParamsNegative = new Dictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = "TESTKEY/20261121/us-east-1/s3/aws4_request",
            ["X-Amz-Date"] = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact),
            ["X-Amz-Expires"] = "-60",
            ["X-Amz-SignedHeaders"] = "host",
            ["X-Amz-Signature"] = "dummy"
        };
        var reqNegative = new S3Request("GET", "/bucket/file.txt", qParamsNegative, new Dictionary<string, string[]> { ["host"] = new[] { "localhost" } }, Stream.Null, settings);
        Func<Task> act2 = async () => await SharpS3Server.ValidateSignatureAsync(reqNegative, settings, null, default);
        (await act2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
    }

    [TestMethod]
    public async Task ValidateV4Signature_TimestampParsingAndSkew_Branches()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            ValidateTimestampSkew = true,
            MaxClockSkew = TimeSpan.FromMinutes(10)
        };
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        // Invalid timestamp format (cannot parse)
        var headersInvalidDate = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=TESTKEY/20261121/us-east-1/s3/aws4_request,SignedHeaders=host,Signature=0123456789abcdef" },
            ["x-amz-date"] = new[] { "invalid-timestamp" }
        };
        var reqInvalidDate = new S3Request("GET", "/bucket/file.txt", new Dictionary<string, string>(), headersInvalidDate, Stream.Null, settings);
        Func<Task> actInvalid = async () => await SharpS3Server.ValidateSignatureAsync(reqInvalidDate, settings, null, default);
        (await actInvalid.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);

        // Standard ISO8601 format that TryParse parses successfully
        string validIsoDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var headersIsoDate = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=TESTKEY/20261121/us-east-1/s3/aws4_request,SignedHeaders=host,Signature=0123456789abcdef" },
            ["x-amz-date"] = new[] { validIsoDate }
        };
        var reqIsoDate = new S3Request("GET", "/bucket/file.txt", new Dictionary<string, string>(), headersIsoDate, Stream.Null, settings);
        Func<Task> actIso = async () => await SharpS3Server.ValidateSignatureAsync(reqIsoDate, settings, null, default);
        (await actIso.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_RecoverUploadStateFromUploadInfo()
    {
        await _fileBackend.CreateBucketAsync("recover-bucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("recover-bucket", "recover.bin");

        // Create a new FileSystemStorageBackend pointing to the same directory (simulating restart where _ActiveUploads is empty)
        var restartedBackend = new FileSystemStorageBackend(_tempDir);

        using var dataStream = new MemoryStream(new byte[] { 1, 2, 3 });
        string partEtag = await restartedBackend.UploadPartAsync("recover-bucket", "recover.bin", init.UploadId, 1, dataStream, 3);
        partEtag.Should().NotBeNullOrEmpty();

        var partsList = await restartedBackend.ListPartsAsync("recover-bucket", "recover.bin", init.UploadId);
        partsList.Parts.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_ListParts_BucketKeyMismatch_ThrowsNoSuchUpload()
    {
        await _fileBackend.CreateBucketAsync("bkt-orig", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("bkt-orig", "orig.bin");

        // Mismatched bucket
        Func<Task> actBucket = async () => await _fileBackend.ListPartsAsync("bkt-other", "orig.bin", init.UploadId);
        (await actBucket.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);

        // Mismatched key
        Func<Task> actKey = async () => await _fileBackend.ListPartsAsync("bkt-orig", "other.bin", init.UploadId);
        (await actKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_CompleteMultipartUpload_ValidatePartETags_ValidMatch()
    {
        _fileBackend.ValidatePartETags = true;
        await _fileBackend.CreateBucketAsync("etag-match-bkt", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("etag-match-bkt", "matched.bin");

        using var data = new MemoryStream(new byte[] { 1, 2, 3 });
        string partEtag = await _fileBackend.UploadPartAsync("etag-match-bkt", "matched.bin", init.UploadId, 1, data, 3);

        var completeUpload = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = partEtag }
            }
        };

        var res = await _fileBackend.CompleteMultipartUploadAsync("etag-match-bkt", "matched.bin", init.UploadId, completeUpload);
        res.ETag.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_GetUploadState_CorruptedOrMissingInfo()
    {
        await _fileBackend.CreateBucketAsync("corrupt-bkt", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("corrupt-bkt", "file.bin");

        // Overwrite .uploadinfo with single line or corrupted data
        string stagingDir = Path.Combine(_tempDir, ".uploads", init.UploadId);
        string infoPath = Path.Combine(stagingDir, ".uploadinfo");
        File.WriteAllText(infoPath, "only_one_line");

        var newBackend = new FileSystemStorageBackend(_tempDir);
        // UploadState returns null because file has < 2 lines, fallback proceeds gracefully
        using var data = new MemoryStream(new byte[] { 1 });
        string partEtag = await newBackend.UploadPartAsync("corrupt-bkt", "file.bin", init.UploadId, 1, data, 1);
        partEtag.Should().NotBeNullOrEmpty();

        // Test file locked with FileShare.None to hit catch block
        using (var lockStream = new FileStream(infoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var data2 = new MemoryStream(new byte[] { 2 });
            string partEtag2 = await newBackend.UploadPartAsync("corrupt-bkt", "file.bin", init.UploadId, 1, data2, 1);
            partEtag2.Should().NotBeNullOrEmpty();
        }
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_Validation_And_Multipart_Branches()
    {
        var mem = new InMemoryStorageBackend();
        await mem.CreateBucketAsync("mem-bucket", null);
        var init = await mem.CreateMultipartUploadAsync("mem-bucket", "item.bin");

        // Invalid part numbers < 1 or > 10000 on UploadPartAsync
        using var stream1 = new MemoryStream(new byte[] { 1 });
        Func<Task> actZero = async () => await mem.UploadPartAsync("mem-bucket", "item.bin", init.UploadId, 0, stream1, 1);
        (await actZero.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        using var stream2 = new MemoryStream(new byte[] { 1 });
        Func<Task> actLarge = async () => await mem.UploadPartAsync("mem-bucket", "item.bin", init.UploadId, 10001, stream2, 1);
        (await actLarge.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        // Upload valid parts 1 and 2
        using var streamPart1 = new MemoryStream(new byte[] { 10, 20 });
        string etag1 = await mem.UploadPartAsync("mem-bucket", "item.bin", init.UploadId, 1, streamPart1, 2);
        using var streamPart2 = new MemoryStream(new byte[] { 30, 40 });
        string etag2 = await mem.UploadPartAsync("mem-bucket", "item.bin", init.UploadId, 2, streamPart2, 2);

        // CompleteMultipartUpload with bad order [2, 1]
        var badOrder = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 2, ETag = etag2 },
                new Part { PartNumber = 1, ETag = etag1 }
            }
        };
        Func<Task> actOrder = async () => await mem.CompleteMultipartUploadAsync("mem-bucket", "item.bin", init.UploadId, badOrder);
        (await actOrder.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPartOrder);

        // CompleteMultipartUpload with missing part
        var missingPart = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = etag1 },
                new Part { PartNumber = 5, ETag = etag2 }
            }
        };
        Func<Task> actMissing = async () => await mem.CompleteMultipartUploadAsync("mem-bucket", "item.bin", init.UploadId, missingPart);
        (await actMissing.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPart);

        // CompleteMultipartUpload with ETag mismatch and ValidatePartETags enabled
        mem.ValidatePartETags = true;
        var mismatchedEtag = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = "\"wrong-etag\"" },
                new Part { PartNumber = 2, ETag = etag2 }
            }
        };
        Func<Task> actEtag = async () => await mem.CompleteMultipartUploadAsync("mem-bucket", "item.bin", init.UploadId, mismatchedEtag);
        (await actEtag.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidPart);
    }

    [TestMethod]
    public async Task Dispatcher_Head_And_Range_304_NotModified()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);

        await _fileBackend.CreateBucketAsync("cache-bucket", null);
        using var data = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        string etag = await _fileBackend.PutObjectAsync("cache-bucket", "cached.txt", data, 5, "text/plain");

        // 1. HEAD request with matching If-None-Match -> 304
        var headReq = new S3Request(
            "HEAD",
            "/cache-bucket/cached.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { etag }
            },
            Stream.Null,
            settings);

        var headResp = await dispatcher.DispatchAsync(headReq);
        headResp.StatusCode.Should().Be(304);

        // 2. GET byte-range request with matching If-None-Match -> 304
        var rangeReq = new S3Request(
            "GET",
            "/cache-bucket/cached.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["range"] = new[] { "bytes=0-2" },
                ["if-none-match"] = new[] { etag }
            },
            Stream.Null,
            settings);

        var rangeResp = await dispatcher.DispatchAsync(rangeReq);
        rangeResp.StatusCode.Should().Be(304);

        // 3. GET byte-range request with mismatching If-Match -> 412 and disposes payload stream
        var rangeMismatchReq = new S3Request(
            "GET",
            "/cache-bucket/cached.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["range"] = new[] { "bytes=0-2" },
                ["if-match"] = new[] { "\"mismatched-etag\"" }
            },
            Stream.Null,
            settings);
        Func<Task> actRangeMismatch = async () => await dispatcher.DispatchAsync(rangeMismatchReq);
        (await actRangeMismatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);
    }

    [TestMethod]
    public async Task Dispatcher_PartNumberBounds_Enforced()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);

        await _fileBackend.CreateBucketAsync("bounds-bucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("bounds-bucket", "data.bin");

        // ObjectUploadPart with invalid PartNumber = 0
        var reqPartZero = new S3Request(
            "PUT",
            "/bounds-bucket/data.bin?partNumber=0&uploadId=" + init.UploadId,
            new Dictionary<string, string> { ["partNumber"] = "0", ["uploadId"] = init.UploadId },
            new Dictionary<string, string[]> { ["host"] = new[] { "localhost" } },
            Stream.Null,
            settings);
        Func<Task> act1 = async () => await dispatcher.DispatchAsync(reqPartZero);
        (await act1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        // ObjectUploadPartCopy with invalid PartNumber = 10001
        var reqCopyLarge = new S3Request(
            "PUT",
            "/bounds-bucket/data.bin?partNumber=10001&uploadId=" + init.UploadId,
            new Dictionary<string, string> { ["partNumber"] = "10001", ["uploadId"] = init.UploadId },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/bounds-bucket/source.bin" }
            },
            Stream.Null,
            settings);
        Func<Task> act2 = async () => await dispatcher.DispatchAsync(reqCopyLarge);
        (await act2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);
    }

    [TestMethod]
    public async Task Dispatcher_ConditionalHeaders_IfUnmodifiedSince_And_IfNoneMatch_NonGet()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);

        await _fileBackend.CreateBucketAsync("cond-bucket", null);
        using var data = new MemoryStream(new byte[] { 10, 20 });
        string etag = await _fileBackend.PutObjectAsync("cond-bucket", "obj.bin", data, 2, "application/octet-stream");

        // If-Unmodified-Since in past (object modified after that) -> 412 PreconditionFailed
        string pastDate = DateTime.UtcNow.AddDays(-1).ToString("r");
        var reqUnmod = new S3Request(
            "GET",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-unmodified-since"] = new[] { pastDate }
            },
            Stream.Null,
            settings);
        Func<Task> actUnmod = async () => await dispatcher.DispatchAsync(reqUnmod);
        (await actUnmod.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // If-None-Match on non-GET/HEAD (e.g. PUT) when ETag matches existing object -> 412 PreconditionFailed
        var reqPutMatch = new S3Request(
            "PUT",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { etag }
            },
            Stream.Null,
            settings);
        Func<Task> actPut = async () => await dispatcher.DispatchAsync(reqPutMatch);
        (await actPut.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // If-Unmodified-Since in future -> passes and returns 200
        string futureDate = DateTime.UtcNow.AddDays(1).ToString("r");
        var reqUnmodFuture = new S3Request(
            "GET",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-unmodified-since"] = new[] { futureDate }
            },
            Stream.Null,
            settings);
        var respUnmodFuture = await dispatcher.DispatchAsync(reqUnmodFuture);
        respUnmodFuture.StatusCode.Should().Be(200);
        respUnmodFuture.BodyStream?.Dispose();

        // If-None-Match with mismatching ETag on GET -> passes and returns 200
        var reqNoneMatchMismatch = new S3Request(
            "GET",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { "\"other-etag\"" }
            },
            Stream.Null,
            settings);
        var respNoneMatchMismatch = await dispatcher.DispatchAsync(reqNoneMatchMismatch);
        respNoneMatchMismatch.StatusCode.Should().Be(200);
        respNoneMatchMismatch.BodyStream?.Dispose();

        // If-Modified-Since in past on GET -> object was modified, returns 200
        var reqModSincePast = new S3Request(
            "GET",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-modified-since"] = new[] { pastDate }
            },
            Stream.Null,
            settings);
        var respModSincePast = await dispatcher.DispatchAsync(reqModSincePast);
        respModSincePast.StatusCode.Should().Be(200);
        respModSincePast.BodyStream?.Dispose();

        // PUT on non-existing object with If-Match -> PreconditionFailed
        var reqPutIfMatchNonExistent = new S3Request(
            "PUT",
            "/cond-bucket/nonexistent.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-match"] = new[] { "\"any-etag\"" }
            },
            Stream.Null,
            settings);
        Func<Task> actPutIfMatch = async () => await dispatcher.DispatchAsync(reqPutIfMatchNonExistent);
        (await actPutIfMatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // PUT on existing object with matching If-Match -> Precondition holds, succeeds with 200
        var reqPutIfMatchSuccess = new S3Request(
            "PUT",
            "/cond-bucket/obj.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-match"] = new[] { etag }
            },
            Stream.Null,
            settings);
        var respPutIfMatch = await dispatcher.DispatchAsync(reqPutIfMatchSuccess);
        respPutIfMatch.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task Dispatcher_CopySource_ConditionalHeaders_AllBranches()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);

        await _fileBackend.CreateBucketAsync("copysrc-bucket", null);
        using var data = new MemoryStream(new byte[] { 1, 2, 3 });
        string etag = await _fileBackend.PutObjectAsync("copysrc-bucket", "src.bin", data, 3, "application/octet-stream");

        // 1. CopySourceIfMatch mismatch -> PreconditionFailed
        var reqMatchFail = new S3Request(
            "PUT",
            "/copysrc-bucket/dest1.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/copysrc-bucket/src.bin" },
                ["x-amz-copy-source-if-match"] = new[] { "\"mismatch-etag\"" }
            },
            Stream.Null,
            settings);
        Func<Task> act1 = async () => await dispatcher.DispatchAsync(reqMatchFail);
        (await act1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // 2. CopySourceIfUnmodifiedSince in the past -> PreconditionFailed
        var reqUnmodFail = new S3Request(
            "PUT",
            "/copysrc-bucket/dest2.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/copysrc-bucket/src.bin" },
                ["x-amz-copy-source-if-unmodified-since"] = new[] { DateTime.UtcNow.AddDays(-1).ToString("r") }
            },
            Stream.Null,
            settings);
        Func<Task> act2 = async () => await dispatcher.DispatchAsync(reqUnmodFail);
        (await act2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // 3. CopySourceIfNoneMatch matches -> PreconditionFailed
        var reqNoneMatchFail = new S3Request(
            "PUT",
            "/copysrc-bucket/dest3.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/copysrc-bucket/src.bin" },
                ["x-amz-copy-source-if-none-match"] = new[] { etag }
            },
            Stream.Null,
            settings);
        Func<Task> act3 = async () => await dispatcher.DispatchAsync(reqNoneMatchFail);
        (await act3.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // 4. CopySourceIfModifiedSince in the future -> PreconditionFailed
        var reqModSinceFail = new S3Request(
            "PUT",
            "/copysrc-bucket/dest4.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/copysrc-bucket/src.bin" },
                ["x-amz-copy-source-if-modified-since"] = new[] { DateTime.UtcNow.AddDays(1).ToString("r") }
            },
            Stream.Null,
            settings);
        Func<Task> act4 = async () => await dispatcher.DispatchAsync(reqModSinceFail);
        (await act4.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);

        // 5. Valid copy with all preconditions matching -> Succeeds
        var reqSuccess = new S3Request(
            "PUT",
            "/copysrc-bucket/dest-ok.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/copysrc-bucket/src.bin" },
                ["x-amz-copy-source-if-match"] = new[] { etag },
                ["x-amz-copy-source-if-unmodified-since"] = new[] { DateTime.UtcNow.AddDays(1).ToString("r") },
                ["x-amz-copy-source-if-none-match"] = new[] { "\"other-etag\"" },
                ["x-amz-copy-source-if-modified-since"] = new[] { DateTime.UtcNow.AddDays(-1).ToString("r") }
            },
            Stream.Null,
            settings);
        var respSuccess = await dispatcher.DispatchAsync(reqSuccess);
        respSuccess.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public void AwsSignatureV4_BuildCanonicalQueryString_SkipsSignature()
    {
        string fullUrl = "http://localhost/mybucket/mykey?beta=2&X-Amz-Signature=abcdef0123456789&alpha=1";
        string canonical = AwsSignatureV4.BuildCanonicalQueryString(fullUrl);
        canonical.Should().Be("alpha=1&beta=2");
    }

    #endregion

    #region Branch-Coverage-Completion-Tests

    [TestMethod]
    public async Task ValidateV4Signature_WithTelemetryListener_CoversTelemetryMismatchBranches()
    {
        using var activityListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == S3ServerTelemetryNames.DefaultSourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref System.Diagnostics.ActivityCreationOptions<string> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activityListener);

        var settings = new SharpS3Settings { EnableSignatures = true, ValidateTimestampSkew = true };
        settings.Credentials["TESTKEY"] = "TESTSECRET";
        var server = new SharpS3Server(settings);

        // 1. Invalid expiration with telemetry active
        var queryInvalidExp = new Dictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = "TESTKEY/20260101/us-east-1/s3/aws4_request",
            ["X-Amz-Date"] = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact),
            ["X-Amz-Expires"] = "-1",
            ["X-Amz-SignedHeaders"] = "host",
            ["X-Amz-Signature"] = "bad"
        };
        var headers1 = new Dictionary<string, string[]> { ["host"] = new[] { "127.0.0.1:9000" } };

        Func<Task> act1 = async () => await server.ReceiveRequestAsync("GET", "/b/k", queryParameters: queryInvalidExp, headers: headers1);
        var ex1 = await act1.Should().ThrowAsync<S3Exception>();
        ex1.Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // 2. Request has expired with telemetry active
        var queryExpired = new Dictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = "TESTKEY/20260101/us-east-1/s3/aws4_request",
            ["X-Amz-Date"] = DateTime.UtcNow.AddSeconds(-120).ToString(Constants.AmazonTimestampFormatCompact),
            ["X-Amz-Expires"] = "60",
            ["X-Amz-SignedHeaders"] = "host",
            ["X-Amz-Signature"] = "bad"
        };
        var headers2 = new Dictionary<string, string[]> { ["host"] = new[] { "127.0.0.1:9000" } };

        Func<Task> act2 = async () => await server.ReceiveRequestAsync("GET", "/b/k", queryParameters: queryExpired, headers: headers2);
        var ex2 = await act2.Should().ThrowAsync<S3Exception>();
        ex2.Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // 3. RequestTimeTooSkewed with telemetry active
        var headers3 = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "127.0.0.1:9000" },
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=TESTKEY/20260101/us-east-1/s3/aws4_request, SignedHeaders=host, Signature=bad" },
            ["x-amz-date"] = new[] { DateTime.UtcNow.AddMinutes(-60).ToString(Constants.AmazonTimestampFormatCompact) }
        };

        Func<Task> act3 = async () => await server.ReceiveRequestAsync("GET", "/b/k", headers: headers3);
        var ex3 = await act3.Should().ThrowAsync<S3Exception>();
        ex3.Which.Error.Code.Should().Be(ErrorCode.RequestTimeTooSkewed);
    }

    [TestMethod]
    public async Task FileSystemBackend_NullUploadState_WhenUploadInfoMissing_CoversFalseBranches()
    {
        string fakeUploadId = Guid.NewGuid().ToString("N");
        string stagingDir = Path.Combine(_tempDir, ".uploads", fakeUploadId);
        Directory.CreateDirectory(stagingDir);

        // 1. CompleteMultipartUploadAsync when upload == null
        Func<Task> actComplete = async () => await _fileBackend.CompleteMultipartUploadAsync("some-bucket", "some-key", fakeUploadId, new CompleteMultipartUpload());
        await actComplete.Should().ThrowAsync<S3Exception>();

        // 2. AbortMultipartUploadAsync when upload == null
        await _fileBackend.AbortMultipartUploadAsync("some-bucket", "some-key", fakeUploadId);
        Directory.Exists(stagingDir).Should().BeFalse();

        // 3. ListPartsAsync when upload == null
        Directory.CreateDirectory(stagingDir);
        var listParts = await _fileBackend.ListPartsAsync("some-bucket", "some-key", fakeUploadId);
        listParts.Parts.Should().BeEmpty();
    }

    [TestMethod]
    public async Task FileSystemBackend_CompleteMultipartUpload_NullPartsVariations()
    {
        await _fileBackend.CreateBucketAsync("parts-bucket", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("parts-bucket", "parts.bin");
        string stagingDir = Path.Combine(_tempDir, ".uploads", init.UploadId);

        // Write a part file directly so fallback listing works
        File.WriteAllBytes(Path.Combine(stagingDir, "1.part"), new byte[] { 1, 2, 3 });

        // parts == null
        var res1 = await _fileBackend.CompleteMultipartUploadAsync("parts-bucket", "parts.bin", init.UploadId, null!);
        res1.Bucket.Should().Be("parts-bucket");

        // parts.Parts == null
        var init2 = await _fileBackend.CreateMultipartUploadAsync("parts-bucket", "parts2.bin");
        string stagingDir2 = Path.Combine(_tempDir, ".uploads", init2.UploadId);
        File.WriteAllBytes(Path.Combine(stagingDir2, "1.part"), new byte[] { 1, 2, 3 });

        var res2 = await _fileBackend.CompleteMultipartUploadAsync("parts-bucket", "parts2.bin", init2.UploadId, new CompleteMultipartUpload { Parts = null! });
        res2.Bucket.Should().Be("parts-bucket");
    }

    [TestMethod]
    public async Task FileSystemBackend_CompleteMultipartUpload_EtagMissingFallback()
    {
        _fileBackend.ValidatePartETags = true;
        await _fileBackend.CreateBucketAsync("etag-missing-bkt", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("etag-missing-bkt", "item.bin");
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        string partEtag = await _fileBackend.UploadPartAsync("etag-missing-bkt", "item.bin", init.UploadId, 1, ms, 3);

        // Delete .etag file to trigger File.Exists(etagPath) == false
        string etagPath = Path.Combine(_tempDir, ".uploads", init.UploadId, "1.part.etag");
        if (File.Exists(etagPath)) File.Delete(etagPath);

        // If part etag in CompleteMultipartUpload is "\"\"" or empty, recordedEtag falls back to "\"\"" and matches!
        var complete = new CompleteMultipartUpload
        {
            Parts = new List<Part> { new Part { PartNumber = 1, ETag = "\"\"" } }
        };
        var res = await _fileBackend.CompleteMultipartUploadAsync("etag-missing-bkt", "item.bin", init.UploadId, complete);
        res.Bucket.Should().Be("etag-missing-bkt");
    }

    [TestMethod]
    public async Task FileSystemBackend_DeleteMultipleObjects_WhenDeleteThrowsNonS3Exception_RecordsInternalError()
    {
        await _fileBackend.CreateBucketAsync("del-mult-bkt", null);

        var req = new DeleteMultiple
        {
            Objects = new List<SharpS3.Models.Object>
            {
                new SharpS3.Models.Object("bad\0key")
            }
        };

        var res = await _fileBackend.DeleteMultipleObjectsAsync("del-mult-bkt", req);
        res.Errors.Should().NotBeEmpty();
        res.Errors[0].Code.Should().Be(ErrorCode.InternalError);
    }

    [TestMethod]
    public async Task InMemoryBackend_CompleteMultipartUpload_NullPartsVariations()
    {
        await _memBackend.CreateBucketAsync("mem-bkt", null);
        var init = await _memBackend.CreateMultipartUploadAsync("mem-bkt", "obj.bin");
        await _memBackend.UploadPartAsync("mem-bkt", "obj.bin", init.UploadId, 1, new MemoryStream(new byte[] { 1 }), 1);

        // parts == null
        var res1 = await _memBackend.CompleteMultipartUploadAsync("mem-bkt", "obj.bin", init.UploadId, null!);
        res1.Bucket.Should().Be("mem-bkt");

        // parts.Parts == null
        var init2 = await _memBackend.CreateMultipartUploadAsync("mem-bkt", "obj2.bin");
        await _memBackend.UploadPartAsync("mem-bkt", "obj2.bin", init2.UploadId, 1, new MemoryStream(new byte[] { 1 }), 1);

        var res2 = await _memBackend.CompleteMultipartUploadAsync("mem-bkt", "obj2.bin", init2.UploadId, new CompleteMultipartUpload { Parts = null! });
        res2.Bucket.Should().Be("mem-bkt");
    }

    [TestMethod]
    public async Task S3RequestDispatcher_CorsEmptyOriginHeader_And_EmptyAllowedCorsOrigins()
    {
        // 1. AllowedCorsOrigins == null with Origin header -> covers AllowedCorsOrigins != null being false
        var settingsNullOrigins = new SharpS3Settings
        {
            EmitCorsHeaders = true,
            AllowedCorsOrigins = null
        };
        var dispNull = new S3RequestDispatcher(_memBackend, settingsNullOrigins);
        var reqNullOrigins = new S3Request(
            "GET",
            "/",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["origin"] = new[] { "https://client.example.com" }
            },
            Stream.Null,
            settingsNullOrigins);
        var respNull = await dispNull.DispatchAsync(reqNullOrigins);
        respNull.StatusCode.Should().Be(200);
        respNull.Headers["Access-Control-Allow-Origin"].Should().Be("*");

        // 2. AllowedCorsOrigins empty list (Count == 0) and origin header empty array (RetrieveHeaderValue null)
        var settingsEmpty = new SharpS3Settings
        {
            EmitCorsHeaders = true,
            AllowedCorsOrigins = new List<string>()
        };
        var dispatcher = new S3RequestDispatcher(_memBackend, settingsEmpty);

        var req = new S3Request(
            "GET",
            "/",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["origin"] = new string[0]
            },
            Stream.Null,
            settingsEmpty);

        var resp = await dispatcher.DispatchAsync(req);
        resp.StatusCode.Should().Be(200);
        resp.Headers.ContainsKey("Access-Control-Allow-Origin").Should().BeTrue();
    }

    [TestMethod]
    public async Task S3RequestDispatcher_ConditionalHeaders_CoverageCompletion()
    {
        var settings = new SharpS3Settings();
        var dispatcher = new S3RequestDispatcher(_memBackend, settings);

        // 1. EvaluateConditionalHeaders with etag == null
        var dummyReq = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]> { ["host"] = new[] { "localhost" } }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateConditionalHeaders(dummyReq, null, DateTime.UtcNow, out bool notMod);
        notMod.Should().BeFalse();

        // 2. IfMatch with "*"
        var reqIfMatchStar = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["if-match"] = new[] { "*" }
        }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateConditionalHeaders(reqIfMatchStar, "\"any-etag\"", DateTime.UtcNow, out bool _);

        // 3. IfNoneMatch with "*"
        var reqIfNoneMatchStar = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["if-none-match"] = new[] { "*" }
        }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateConditionalHeaders(reqIfNoneMatchStar, "\"any-etag\"", DateTime.UtcNow, out bool notModStar);
        notModStar.Should().BeTrue();

        // 4. IfModifiedSince with HEAD method
        var reqHeadMod = new S3Request("HEAD", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["if-modified-since"] = new[] { DateTime.UtcNow.AddMinutes(5).ToString("r") }
        }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateConditionalHeaders(reqHeadMod, "\"any-etag\"", DateTime.UtcNow, out bool headNotMod);
        headNotMod.Should().BeTrue();

        // 5. IfModifiedSince with non-GET/HEAD method (PUT)
        var reqPutMod = new S3Request("PUT", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["if-modified-since"] = new[] { DateTime.UtcNow.AddMinutes(5).ToString("r") }
        }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateConditionalHeaders(reqPutMod, "\"any-etag\"", DateTime.UtcNow, out bool putNotMod);
        putNotMod.Should().BeFalse();

        // 6. EvaluateCopySourceConditionalHeaders with etag == null
        S3RequestDispatcher.EvaluateCopySourceConditionalHeaders(dummyReq, null, DateTime.UtcNow);

        // 7. CopySourceIfMatch with "*"
        var reqCopyMatchStar = new S3Request("PUT", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-copy-source-if-match"] = new[] { "*" }
        }, Stream.Null, settings);
        S3RequestDispatcher.EvaluateCopySourceConditionalHeaders(reqCopyMatchStar, "\"any-etag\"", DateTime.UtcNow);

        // 8. CopySourceIfNoneMatch with "*"
        var reqCopyNoneMatchStar = new S3Request("PUT", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-copy-source-if-none-match"] = new[] { "*" }
        }, Stream.Null, settings);
        Action actCopyNone = () => S3RequestDispatcher.EvaluateCopySourceConditionalHeaders(reqCopyNoneMatchStar, "\"any-etag\"", DateTime.UtcNow);
        actCopyNone.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.PreconditionFailed);
    }

    [TestMethod]
    public async Task S3RequestDispatcher_PutObject_ConditionalNonExistent_And_EmptyIfMatch()
    {
        await _memBackend.CreateBucketAsync("new-bucket", null);
        var settings = new SharpS3Settings();
        var dispatcher = new S3RequestDispatcher(_memBackend, settings);

        // 1. PUT on non-existent object with If-None-Match: * (req.IfMatch is null)
        var reqPutIfNoneMatch = new S3Request(
            "PUT",
            "/new-bucket/new-key.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { "*" },
                ["content-length"] = new[] { "3" }
            },
            new MemoryStream(new byte[] { 1, 2, 3 }),
            settings);

        var resp1 = await dispatcher.DispatchAsync(reqPutIfNoneMatch);
        resp1.StatusCode.Should().Be(200);

        // 2. PUT with IfMatch empty list on non-existent object
        var reqEmptyIfMatch = new S3Request(
            "PUT",
            "/new-bucket/new-key-2.bin",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["if-none-match"] = new[] { "*" },
                ["content-length"] = new[] { "3" }
            },
            new MemoryStream(new byte[] { 1, 2, 3 }),
            settings);
        reqEmptyIfMatch.IfMatch = new List<string>();

        var resp2 = await dispatcher.DispatchAsync(reqEmptyIfMatch);
        resp2.StatusCode.Should().Be(200);
    }

    [TestMethod]
    public async Task S3RequestDispatcher_UploadPartCopy_PartNumberTooHigh_ThrowsInvalidArgument()
    {
        var settings = new SharpS3Settings();
        var dispatcher = new S3RequestDispatcher(_memBackend, settings);

        // 1. PartNumber < 1 (e.g. 0)
        var reqZero = new S3Request(
            "PUT",
            "/b/k",
            new Dictionary<string, string>
            {
                ["uploadId"] = "up1",
                ["partNumber"] = "0"
            },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/src-b/src-k" }
            },
            Stream.Null,
            settings);

        Func<Task> actZero = async () => await dispatcher.DispatchAsync(reqZero);
        var exZero = await actZero.Should().ThrowAsync<S3Exception>();
        exZero.Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);

        // 2. PartNumber > 10000 (e.g. 10001)
        var reqHigh = new S3Request(
            "PUT",
            "/b/k",
            new Dictionary<string, string>
            {
                ["uploadId"] = "up1",
                ["partNumber"] = "10001"
            },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-copy-source"] = new[] { "/src-b/src-k" }
            },
            Stream.Null,
            settings);

        Func<Task> actHigh = async () => await dispatcher.DispatchAsync(reqHigh);
        var ex = await actHigh.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.InvalidArgument);
    }

    #endregion

    #region New-Vulnerability-Remediation-Tests

    [TestMethod]
    public async Task ValidateTimestampSkew_DefaultIsTrue_RejectsOldHeaderRequests()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };
        settings.ValidateTimestampSkew.Should().BeTrue();
        settings.Credentials["TESTKEY"] = "TESTSECRET";

        // Request with timestamp 30 minutes in the past
        string oldTimestamp = DateTime.UtcNow.AddMinutes(-30).ToString(Constants.AmazonTimestampFormatCompact);
        var headers = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { oldTimestamp },
            ["authorization"] = new[] { $"AWS4-HMAC-SHA256 Credential=TESTKEY/20261121/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-date, Signature=dummy" }
        };

        var req = new S3Request("GET", "/mybucket/test.txt", new Dictionary<string, string>(), headers, Stream.Null, settings);

        Func<Task> act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.RequestTimeTooSkewed);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_WindowsReservedNames_InKeys_ThrowsInvalidRequest()
    {
        await _fileBackend.CreateBucketAsync("reserved-bkt", null);

        // Reserved base names like CON, AUX.txt, COM1/sub, PRN
        Func<Task> act1 = async () => _fileBackend.GetObjectMetadataAsync("reserved-bkt", "con");
        (await act1.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        Func<Task> act2 = async () => _fileBackend.GetObjectMetadataAsync("reserved-bkt", "folder/aux.txt");
        (await act2.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        Func<Task> act3 = async () => _fileBackend.GetObjectMetadataAsync("reserved-bkt", "sub/com1/file.dat");
        (await act3.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        Func<Task> act4 = async () => _fileBackend.GetObjectMetadataAsync("reserved-bkt", "prn");
        (await act4.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_MultipartUpload_KeyWithNewlines_PreservedCorrectly()
    {
        await _fileBackend.CreateBucketAsync("newline-bkt", null);
        string complexKey = "line1\nline2\r\nline3 with spaces.txt";

        var init = await _fileBackend.CreateMultipartUploadAsync("newline-bkt", complexKey);

        // Simulate restarting backend to force recovery from .uploadinfo
        var restartedBackend = new FileSystemStorageBackend(_tempDir);

        using var partData = new MemoryStream(new byte[] { 1, 2, 3 });
        string partEtag = await restartedBackend.UploadPartAsync("newline-bkt", complexKey, init.UploadId, 1, partData, 3);
        partEtag.Should().NotBeNullOrEmpty();

        var parts = await restartedBackend.ListPartsAsync("newline-bkt", complexKey, init.UploadId);
        parts.Key.Should().Be(complexKey);
        parts.Parts.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task Dispatcher_PutObject_PayloadSha256_ValidAndMismatch()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);
        await _fileBackend.CreateBucketAsync("sha-bkt", null);

        byte[] payloadData = Encoding.UTF8.GetBytes("hello sha256 verification test");
        using var sha = System.Security.Cryptography.SHA256.Create();
        string validSha = BitConverter.ToString(sha.ComputeHash(payloadData)).Replace("-", "").ToLowerInvariant();

        // 1. Valid SHA-256 matches body
        var validReq = new S3Request(
            "PUT",
            "/sha-bkt/item.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-content-sha256"] = new[] { validSha },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            settings);

        var validResp = await dispatcher.DispatchAsync(validReq);
        validResp.StatusCode.Should().Be(200);

        // Object exists and content is verified
        var meta = await _fileBackend.GetObjectMetadataAsync("sha-bkt", "item.txt");
        meta.Should().NotBeNull();

        // 2. Mismatched SHA-256 throws XAmzContentSHA256Mismatch and cleans up
        string wrongSha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var mismatchReq = new S3Request(
            "PUT",
            "/sha-bkt/mismatch.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-content-sha256"] = new[] { wrongSha },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            settings);

        Func<Task> actMismatch = async () => await dispatcher.DispatchAsync(mismatchReq);
        var ex = await actMismatch.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.XAmzContentSHA256Mismatch);

        // Cleaned up after mismatch failure
        var deletedMeta = await _fileBackend.GetObjectMetadataAsync("sha-bkt", "mismatch.txt");
        deletedMeta.Should().BeNull();
    }

    [TestMethod]
    public async Task Dispatcher_PutObject_ContentMd5_ValidAndMismatch()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);
        await _fileBackend.CreateBucketAsync("md5-bkt", null);

        byte[] payloadData = Encoding.UTF8.GetBytes("hello content-md5 validation");
        using var md5 = System.Security.Cryptography.MD5.Create();
        string validMd5Base64 = Convert.ToBase64String(md5.ComputeHash(payloadData));

        // 1. Valid Content-MD5
        var validReq = new S3Request(
            "PUT",
            "/md5-bkt/file.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["content-md5"] = new[] { validMd5Base64 },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            settings);

        var validResp = await dispatcher.DispatchAsync(validReq);
        validResp.StatusCode.Should().Be(200);

        // 2. Mismatched Content-MD5 throws BadDigest and cleans up
        string badMd5Base64 = Convert.ToBase64String(new byte[16]);
        var badReq = new S3Request(
            "PUT",
            "/md5-bkt/badfile.txt",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["content-md5"] = new[] { badMd5Base64 },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            settings);

        Func<Task> actBad = async () => await dispatcher.DispatchAsync(badReq);
        var ex = await actBad.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.BadDigest);

        var deletedMeta = await _fileBackend.GetObjectMetadataAsync("md5-bkt", "badfile.txt");
        deletedMeta.Should().BeNull();
    }

    [TestMethod]
    public async Task Dispatcher_UploadPart_ContentSha256_And_ContentMd5()
    {
        var settings = new SharpS3Settings { EnableSignatures = false };
        var dispatcher = new S3RequestDispatcher(_fileBackend, settings);
        await _fileBackend.CreateBucketAsync("part-bkt", null);
        var init = await _fileBackend.CreateMultipartUploadAsync("part-bkt", "part-data.bin");

        byte[] partBytes = Encoding.UTF8.GetBytes("part chunk payload");
        using var sha = System.Security.Cryptography.SHA256.Create();
        string validSha = BitConverter.ToString(sha.ComputeHash(partBytes)).Replace("-", "").ToLowerInvariant();

        using var md5 = System.Security.Cryptography.MD5.Create();
        string validMd5 = Convert.ToBase64String(md5.ComputeHash(partBytes));

        // 1. Valid SHA256 and MD5 on UploadPart
        var validPartReq = new S3Request(
            "PUT",
            "/part-bkt/part-data.bin",
            new Dictionary<string, string>
            {
                ["uploadId"] = init.UploadId,
                ["partNumber"] = "1"
            },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-content-sha256"] = new[] { validSha },
                ["content-md5"] = new[] { validMd5 },
                ["content-length"] = new[] { partBytes.Length.ToString() }
            },
            new MemoryStream(partBytes),
            settings);

        var validPartResp = await dispatcher.DispatchAsync(validPartReq);
        validPartResp.StatusCode.Should().Be(200);

        // 2. Mismatch SHA256 on UploadPart throws XAmzContentSHA256Mismatch
        var badShaPartReq = new S3Request(
            "PUT",
            "/part-bkt/part-data.bin",
            new Dictionary<string, string>
            {
                ["uploadId"] = init.UploadId,
                ["partNumber"] = "2"
            },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["x-amz-content-sha256"] = new[] { "0000000000000000000000000000000000000000000000000000000000000000" },
                ["content-length"] = new[] { partBytes.Length.ToString() }
            },
            new MemoryStream(partBytes),
            settings);

        Func<Task> actBadSha = async () => await dispatcher.DispatchAsync(badShaPartReq);
        (await actBadSha.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.XAmzContentSHA256Mismatch);

        // 3. Mismatch MD5 on UploadPart throws BadDigest
        var badMd5PartReq = new S3Request(
            "PUT",
            "/part-bkt/part-data.bin",
            new Dictionary<string, string>
            {
                ["uploadId"] = init.UploadId,
                ["partNumber"] = "3"
            },
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["content-md5"] = new[] { Convert.ToBase64String(new byte[16]) },
                ["content-length"] = new[] { partBytes.Length.ToString() }
            },
            new MemoryStream(partBytes),
            settings);

        Func<Task> actBadMd5 = async () => await dispatcher.DispatchAsync(badMd5PartReq);
        (await actBadMd5.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.BadDigest);
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_ExceedsMaxChunkSize_ThrowsEntityTooLarge()
    {
        // 1. Mode B (unbuffered): header declares chunk size > MaxChunkSize
        string chunkStreamData = "1000\r\n" + new string('a', 4096) + "\r\n0\r\n\r\n";
        using var rawStream = new MemoryStream(Encoding.UTF8.GetBytes(chunkStreamData));
        using var chunkStream = new Streams.AwsChunkedDecodingStream(rawStream)
        {
            MaxChunkSize = 2048 // 2 KB max, but chunk is 4096 (0x1000)
        };

        byte[] buf = new byte[512];
        Func<Task> actModeB = async () => await chunkStream.ReadAsync(buf.AsMemory(0, buf.Length));
        await actModeB.Should().ThrowAsync<InvalidDataException>();

        // 2. Mode A (signed): header declares chunk size > MaxChunkSize
        var dummyKey = new byte[32];
        var signer = new AwsChunkSigner("20261121T000000Z", "us-east-1", "s3", dummyKey, "seedsig");
        using var rawStreamSigned = new MemoryStream(Encoding.UTF8.GetBytes(chunkStreamData));
        using var chunkStreamSigned = new Streams.AwsChunkedDecodingStream(rawStreamSigned, signer)
        {
            MaxChunkSize = 1024
        };

        Func<Task> actModeA = async () => await chunkStreamSigned.ReadAsync(buf.AsMemory(0, buf.Length));
        await actModeA.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public void ValidateContentMd5_MalformedAndWrongLength_ThrowsBadDigest()
    {
        // Malformed Base64 throws FormatException -> BadDigest
        Action actMalformed = () => S3RequestDispatcher.ValidateContentMd5("!not-base64!", "\"d41d8cd98f00b204e9800998ecf8427e\"");
        actMalformed.Should().Throw<S3Exception>().Where(e => e.Error.Code == ErrorCode.BadDigest);

        // Valid Base64 but wrong length (8 bytes instead of 16) -> BadDigest
        string wrongLengthBase64 = Convert.ToBase64String(new byte[8]);
        Action actWrongLen = () => S3RequestDispatcher.ValidateContentMd5(wrongLengthBase64, "\"d41d8cd98f00b204e9800998ecf8427e\"");
        actWrongLen.Should().Throw<S3Exception>().Where(e => e.Error.Code == ErrorCode.BadDigest);

        // Null/empty contentMd5 or invalid hex etag -> no-op
        S3RequestDispatcher.ValidateContentMd5(null, "\"d41d8cd98f00b204e9800998ecf8427e\"");
        S3RequestDispatcher.ValidateContentMd5("", "\"d41d8cd98f00b204e9800998ecf8427e\"");
        S3RequestDispatcher.ValidateContentMd5("d41d8cd98f00b204e9800998ecf8427e", "");
        S3RequestDispatcher.ValidateContentMd5("d41d8cd98f00b204e9800998ecf8427e", null!);
        S3RequestDispatcher.ValidateContentMd5("d41d8cd98f00b204e9800998ecf8427e", "\"abc\"");
    }

    [TestMethod]
    public void Models_Coverage_Properties()
    {
        var logging = new LoggingSettings
        {
            HttpRequests = true,
            S3Requests = true,
            SignatureV4Validation = true
        };
        logging.HttpRequests.Should().BeTrue();
        logging.S3Requests.Should().BeTrue();
        logging.SignatureV4Validation.Should().BeTrue();

        var metadata = new ObjectMetadata
        {
            RestoreStatus = new RestoreStatus { OngoingRequest = false, ExpiryDate = DateTime.UtcNow }
        };
        metadata.RestoreStatus.Should().NotBeNull();
        metadata.RestoreStatus.OngoingRequest.Should().BeFalse();
    }

    [TestMethod]
    public async Task Sha256PayloadVerifyingStream_StreamProperties_ExerciseAllMethods()
    {
        Action actNullInner = () => new Streams.Sha256PayloadVerifyingStream(null!, "hash");
        actNullInner.Should().Throw<ArgumentNullException>();

        Action actNullHash = () => new Streams.Sha256PayloadVerifyingStream(Stream.Null, null!);
        actNullHash.Should().Throw<ArgumentNullException>();

        using var nonSeekable = new NonSeekableDummyStream();
        using var vNonSeek = new Streams.Sha256PayloadVerifyingStream(nonSeekable, "dummy");
        Action actNonSeekLen = () => { _ = vNonSeek.Length; };
        actNonSeekLen.Should().Throw<NotSupportedException>();

        byte[] data = Encoding.UTF8.GetBytes("sample verifying data stream");
        using var sha = System.Security.Cryptography.SHA256.Create();
        string expectedSha = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();

        using var ms = new MemoryStream(data);
        using var vStream = new Streams.Sha256PayloadVerifyingStream(ms, expectedSha);

        vStream.CanRead.Should().BeTrue();
        vStream.CanSeek.Should().BeFalse();
        vStream.CanWrite.Should().BeFalse();
        vStream.Length.Should().Be(data.Length);
        vStream.Position.Should().Be(0);
        vStream.ExpectedSha256.Should().Be(expectedSha);

        Action actPos = () => vStream.Position = 10;
        actPos.Should().Throw<NotSupportedException>();

        Action actSeek = () => vStream.Seek(0, SeekOrigin.Begin);
        actSeek.Should().Throw<NotSupportedException>();

        Action actSetLen = () => vStream.SetLength(10);
        actSetLen.Should().Throw<NotSupportedException>();

        Action actWrite = () => vStream.Write(new byte[1], 0, 1);
        actWrite.Should().Throw<NotSupportedException>();

        vStream.Flush();
        await vStream.FlushAsync(CancellationToken.None);

        // Read 0 count returns 0
        byte[] readBuf = new byte[vStream.Length];
        vStream.Read(readBuf, 0, 0).Should().Be(0);
        (await vStream.ReadAsync(readBuf, 0, 0, CancellationToken.None)).Should().Be(0);

        // Read synchronously
        int n = vStream.Read(readBuf, 0, readBuf.Length);
        n.Should().Be(data.Length);
        vStream.BytesRead.Should().Be(data.Length);

        // Verify succeeds
        vStream.Verify();
        // Calling verify twice is idempotent
        vStream.Verify();

        // Also test ReadAsync with data
        using var msAsync = new MemoryStream(data);
        using var vStreamAsync = new Streams.Sha256PayloadVerifyingStream(msAsync, expectedSha);
        byte[] asyncBuf = new byte[data.Length];
        int asyncRead = await vStreamAsync.ReadAsync(asyncBuf, 0, asyncBuf.Length, CancellationToken.None);
        asyncRead.Should().Be(data.Length);
        vStreamAsync.Verify();
    }

    #endregion

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        string trimmed = query.TrimStart('?');
        foreach (var part in trimmed.Split('&'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            int eq = part.IndexOf('=');
            if (eq >= 0)
            {
                dict[Uri.UnescapeDataString(part.Substring(0, eq))] = Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            else
            {
                dict[Uri.UnescapeDataString(part)] = string.Empty;
            }
        }
        return dict;
    }

    private sealed class NonSeekableDummyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
