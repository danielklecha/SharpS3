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
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Streams;

public partial class SharpS3ServerTests
{
    private const string TestAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string TestSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [TestMethod]
    public async Task SharpS3Server_ReceiveRequestAsync_WithActiveActivityListener_ExercisesAllTelemetryStages()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == S3ServerTelemetryNames.DefaultSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            Credentials = { { TestAccessKey, TestSecretKey } }
        };
        var server = new SharpS3Server(settings);

        // 1. Valid request exercises rt?.StartStage(StageParse) and rt?.Describe(request)
        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { "UNSIGNED-PAYLOAD" }
        };

        var sigCalc = new AwsSignatureV4(
            timestamp,
            "GET",
            "http://localhost/mybucket/mykey",
            TestAccessKey,
            TestSecretKey,
            "us-east-1",
            "s3",
            headers,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.Unsigned);

        headers["authorization"] = new[] { sigCalc.AuthorizationHeader };

        var req = await server.ReceiveRequestAsync("GET", "/mybucket/mykey", headers: headers);
        req.Should().NotBeNull();

        // 2. Request validation error exercises rt?.MarkException(...) with non-null rt
        var invalidHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "/src/k" },
            ["x-amz-copy-source-range"] = new[] { "bytes=100-50" } // invalid range (start > end)
        };
        var actInvalid = async () => await server.ReceiveRequestAsync("PUT", "/dest/k", headers: invalidHeaders);
        await actInvalid.Should().ThrowAsync<S3Exception>();

        // 3. Signature validation failure exercises rt?.StartStage(StageSignatureValidation) with non-null rt
        headers["authorization"] = new[] { sigCalc.AuthorizationHeader.Replace(sigCalc.Signature, "badbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadb") };
        var actBadSig = async () => await server.ReceiveRequestAsync("GET", "/mybucket/mykey", headers: headers);
        (await actBadSig.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task SharpS3Server_ValidateSignatureAsync_NonNullTelemetry_CoversAllOutcomes()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            Credentials = { { TestAccessKey, TestSecretKey } }
        };

        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rt = new RequestTelemetry(telemetry, false);

        // 1. Missing access key -> rt?.MarkSignatureOutcome(OutcomeUnknownKey)
        var reqNoKey = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settings)
        {
            Signature = "somedummysig",
            AccessKey = null
        };
        var actNoKey = async () => await SharpS3Server.ValidateSignatureAsync(reqNoKey, settings, rt, default);
        (await actNoKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeUnknownKey);

        // 2. Unknown secret key -> rt?.MarkSignatureOutcome(OutcomeUnknownKey)
        var reqUnknownKey = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settings)
        {
            Signature = "somedummysig",
            AccessKey = "nonexistent_key"
        };
        var actUnknownKey = async () => await SharpS3Server.ValidateSignatureAsync(reqUnknownKey, settings, rt, default);
        (await actUnknownKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeUnknownKey);

        // 3. SigV2 disabled -> rt?.MarkSignatureOutcome(OutcomeUnsupported)
        var reqV2Disabled = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settings)
        {
            SignatureVersion = S3SignatureVersion.Version2,
            AccessKey = TestAccessKey,
            Signature = "somedummysig"
        };
        settings.EnableSignatureV2 = false;
        var actV2Disabled = async () => await SharpS3Server.ValidateSignatureAsync(reqV2Disabled, settings, rt, default);
        (await actV2Disabled.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeUnsupported);

        // 4. SigV4 Mismatch -> rt?.MarkSignatureOutcome(OutcomeMismatch)
        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);
        var headersMismatch = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp }
        };
        var reqV4Mismatch = new S3Request("GET", "/b/k", new Dictionary<string, string>(), headersMismatch, Stream.Null, settings)
        {
            SignatureVersion = S3SignatureVersion.Version4,
            AccessKey = TestAccessKey,
            Date = timestamp,
            Signature = "badbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadb"
        };
        var actV4Mismatch = async () => await SharpS3Server.ValidateSignatureAsync(reqV4Mismatch, settings, rt, default);
        (await actV4Mismatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeMismatch);

        // 5. SigV4 Success -> rt?.MarkSignatureOutcome(OutcomeSuccess)
        var headersMatch = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { "UNSIGNED-PAYLOAD" }
        };
        var sigV4Calc = new AwsSignatureV4(
            timestamp,
            "GET",
            "http://localhost/b/k",
            TestAccessKey,
            TestSecretKey,
            "us-east-1",
            "s3",
            headersMatch,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.Unsigned);
        headersMatch["authorization"] = new[] { sigV4Calc.AuthorizationHeader };

        var reqV4Match = new S3Request("GET", "/b/k", new Dictionary<string, string>(), headersMatch, Stream.Null, settings);
        await SharpS3Server.ValidateSignatureAsync(reqV4Match, settings, rt, default);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeSuccess);

        // 6. SigV2 Success & Mismatch -> rt?.MarkSignatureOutcome(OutcomeSuccess / OutcomeMismatch)
        settings.EnableSignatureV2 = true;
        var headersV2Valid = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["date"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" }
        };
        string correctSig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/b/k",
            "b",
            S3RequestStyle.PathStyle,
            headersV2Valid,
            new Dictionary<string, string>(),
            TestSecretKey);

        headersV2Valid["authorization"] = new[] { $"AWS {TestAccessKey}:{correctSig}" };
        var reqV2Success = new S3Request("GET", "/b/k", new Dictionary<string, string>(), headersV2Valid, Stream.Null, settings);
        await SharpS3Server.ValidateSignatureAsync(reqV2Success, settings, rt, default);
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeSuccess);

        headersV2Valid["authorization"] = new[] { $"AWS {TestAccessKey}:invalidsig" };
        var reqV2Mismatch = new S3Request("GET", "/b/k", new Dictionary<string, string>(), headersV2Valid, Stream.Null, settings);
        var actV2Mismatch = async () => await SharpS3Server.ValidateSignatureAsync(reqV2Mismatch, settings, rt, default);
        await actV2Mismatch.Should().ThrowAsync<S3Exception>();
        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeMismatch);
    }

    [TestMethod]
    public async Task SharpS3Server_ValidateV4Signature_Fallbacks_And_PathNormalization()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            Credentials = { { TestAccessKey, TestSecretKey } }
        };

        // 1. request.Date == null -> exercises DateTime.UtcNow fallback (line 286)
        // 2. request.Region == null -> exercises ?? "us-east-1" fallback (line 289)
        // 3. request.Host == null && request.Hostname == null -> exercises ?? "localhost" fallback (line 339)
        // 4. path without leading slash -> exercises if (!path.StartsWith("/")) path = "/" + path (line 341)
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var reqFallbacks = new S3Request("GET", "b/k", new Dictionary<string, string>(), headers, Stream.Null, settings)
        {
            SignatureVersion = S3SignatureVersion.Version4,
            AccessKey = TestAccessKey,
            Date = null,
            Region = null,
            Host = null,
            Hostname = null,
            Signature = "mismatched-sig"
        };

        var actFallback = async () => await SharpS3Server.ValidateSignatureAsync(reqFallbacks, settings, null, default);
        (await actFallback.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);

        // 5. Short timestamp (< 8 chars) and Region == null on Chunked stream (lines 312, 313)
        string shortTimestamp = "2026"; // Length is 4 < 8 -> exercises `: timestamp` on line 312
        var chunkHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { shortTimestamp },
            ["x-amz-content-sha256"] = new[] { "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" }
        };

        var sigChunk = new AwsSignatureV4(
            shortTimestamp,
            "PUT",
            "http://localhost/b/k",
            TestAccessKey,
            TestSecretKey,
            "us-east-1",
            "s3",
            chunkHeaders,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.StreamingSigned);

        chunkHeaders["authorization"] = new[] { sigChunk.AuthorizationHeader };
        using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("0\r\n\r\n"));
        var reqChunked = new S3Request("PUT", "/b/k", new Dictionary<string, string>(), chunkHeaders, bodyStream, settings)
        {
            Date = shortTimestamp,
            Region = null // Exercises ?? "us-east-1" on line 313
        };

        await SharpS3Server.ValidateSignatureAsync(reqChunked, settings, null, default);
        reqChunked.BodyStream.Should().BeOfType<AwsChunkedDecodingStream>();
        ((AwsChunkedDecodingStream)reqChunked.BodyStream).Signer.Should().NotBeNull();
    }
}
