namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
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
    private static S3Request CreateTestRequest(
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

    [TestMethod]
    public async Task ValidateSignature_Unsigned_And_Anonymous_Requests()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };

        // Unsigned request without anonymous allowed throws AccessDenied
        var req = CreateTestRequest("GET", "/mybucket/key");
        var actUnsigned = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        (await actUnsigned.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // Anonymous allowed returns successfully
        settings.IsAnonymousRequestAllowedAsync = r => Task.FromResult(true);
        await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);

        // Anonymous allowed returns false throws AccessDenied
        settings.IsAnonymousRequestAllowedAsync = r => Task.FromResult(false);
        var actAnonDenied = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        (await actAnonDenied.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
    }

    [TestMethod]
    public async Task ValidateSignature_Missing_Or_Unknown_AccessKey_ThrowsAccessDenied()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };

        // Auth header but no access key
        var headersNoKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "MALFORMED-AUTH" }
        };
        var reqNoKey = CreateTestRequest("GET", "/b/k", headers: headersNoKey);
        reqNoKey.Signature = "dummy-sig";
        var actNoKey = async () => await SharpS3Server.ValidateSignatureAsync(reqNoKey, settings, null, default);
        (await actNoKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // Access key not in Credentials and no GetSecretKeyAsync
        var headersWithKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "AWS ghostkey:sig" }
        };
        var reqUnknownKey = CreateTestRequest("GET", "/b/k", headers: headersWithKey);
        var actUnknownKey = async () => await SharpS3Server.ValidateSignatureAsync(reqUnknownKey, settings, null, default);
        (await actUnknownKey.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // GetSecretKeyAsync returns null
        settings.GetSecretKeyAsync = k => Task.FromResult<string?>(null);
        var actSecretNull = async () => await SharpS3Server.ValidateSignatureAsync(reqUnknownKey, settings, null, default);
        (await actSecretNull.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
    }

    [TestMethod]
    public async Task ValidateSignature_SigV2_Disabled_And_Enabled_Scenarios()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = false
        };
        settings.Credentials["myaccess"] = "mysecret";

        var headersV2 = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "AWS myaccess:invalidsig" },
            ["date"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" }
        };
        var reqV2 = CreateTestRequest("GET", "/b/k", headers: headersV2);

        // SigV2 disabled throws SignatureDoesNotMatch
        var actDisabled = async () => await SharpS3Server.ValidateSignatureAsync(reqV2, settings, null, default);
        (await actDisabled.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);

        // Enable SigV2
        settings.EnableSignatureV2 = true;

        // Signature mismatch
        var actMismatch = async () => await SharpS3Server.ValidateSignatureAsync(reqV2, settings, null, default);
        (await actMismatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);

        // Correct SigV2 Header signature
        string correctSig = AwsSignatureV2.CalculateHeaderSignature(
            reqV2.HttpMethod,
            reqV2.RawPath,
            reqV2.Bucket,
            reqV2.RequestStyle,
            reqV2.Headers,
            reqV2.QueryParameters,
            "mysecret");

        var headersV2Valid = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { $"AWS myaccess:{correctSig}" },
            ["date"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" }
        };
        var reqV2Valid = CreateTestRequest("GET", "/b/k", headers: headersV2Valid);
        await SharpS3Server.ValidateSignatureAsync(reqV2Valid, settings, null, default);
    }

    [TestMethod]
    public async Task ValidateSignature_SigV2_PresignedUrl_Scenarios()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = true
        };
        settings.Credentials["myaccess"] = "mysecret";

        // Expired presigned URL
        var queryExpired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AWSAccessKeyId"] = "myaccess",
            ["Expires"] = "1000",
            ["Signature"] = "dummy"
        };
        var reqExpired = CreateTestRequest("GET", "/b/k", query: queryExpired);
        var actExpired = async () => await SharpS3Server.ValidateSignatureAsync(reqExpired, settings, null, default);
        (await actExpired.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // Non-integer expires
        var queryBadExpires = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AWSAccessKeyId"] = "myaccess",
            ["Expires"] = "not-a-number",
            ["Signature"] = "dummy"
        };
        var reqBadExpires = CreateTestRequest("GET", "/b/k", query: queryBadExpires);
        var actBadExpires = async () => await SharpS3Server.ValidateSignatureAsync(reqBadExpires, settings, null, default);
        (await actBadExpires.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        // Valid future presigned URL
        long futureExpires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
        var queryValid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AWSAccessKeyId"] = "myaccess",
            ["Expires"] = futureExpires.ToString()
        };
        var reqForSig = CreateTestRequest("GET", "/b/k", query: queryValid);
        string expectedSig = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "/b/k",
            "b",
            S3RequestStyle.PathStyle,
            reqForSig.Headers,
            queryValid,
            futureExpires,
            "mysecret");

        queryValid["Signature"] = expectedSig;
        var reqValid = CreateTestRequest("GET", "/b/k", query: queryValid);
        await SharpS3Server.ValidateSignatureAsync(reqValid, settings, null, default);

        // Signature mismatch
        queryValid["Signature"] = "mismatch";
        var reqMismatch = CreateTestRequest("GET", "/b/k", query: queryValid);
        var actMismatch = async () => await SharpS3Server.ValidateSignatureAsync(reqMismatch, settings, null, default);
        (await actMismatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task ValidateSignature_SigV4_Full_Lifecycle_And_Payload_Modes()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true
        };
        settings.Credentials["AKIAIOSFODNN7EXAMPLE"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);

        // 1. Unsigned payload mode
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { "UNSIGNED-PAYLOAD" }
        };

        var sigCalc = new AwsSignatureV4(
            timestamp,
            "GET",
            "http://localhost/testbucket/testkey",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "s3",
            headers,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.Unsigned);

        headers["authorization"] = new[] { sigCalc.AuthorizationHeader };

        var req = CreateTestRequest("GET", "/testbucket/testkey", headers: headers);
        await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);

        // 2. Mismatch throws SignatureDoesNotMatch
        headers["authorization"] = new[] { sigCalc.AuthorizationHeader.Replace(sigCalc.Signature, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef") };
        var reqMismatch = CreateTestRequest("GET", "/testbucket/testkey", headers: headers);
        var actMismatch = async () => await SharpS3Server.ValidateSignatureAsync(reqMismatch, settings, null, default);
        (await actMismatch.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);

        // 3. Streaming signed with ChunkSigner attachment
        var streamHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" }
        };

        var sigStream = new AwsSignatureV4(
            timestamp,
            "PUT",
            "http://localhost/testbucket/testkey",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "s3",
            streamHeaders,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.StreamingSigned);

        streamHeaders["authorization"] = new[] { sigStream.AuthorizationHeader };
        using var bodyStream = new MemoryStream(Encoding.UTF8.GetBytes("0\r\n\r\n"));
        var reqStream = CreateTestRequest("PUT", "/testbucket/testkey", headers: streamHeaders, body: bodyStream);
        await SharpS3Server.ValidateSignatureAsync(reqStream, settings, null, default);
        reqStream.BodyStream.Should().BeOfType<AwsChunkedDecodingStream>();

        // 4. Streaming trailer payload hash mode
        var trailerHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { "STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER" }
        };
        var sigTrailer = new AwsSignatureV4(
            timestamp,
            "PUT",
            "http://localhost/testbucket/testkey",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            "us-east-1",
            "s3",
            trailerHeaders,
            new[] { "host", "x-amz-content-sha256", "x-amz-date" },
            null,
            AwsPayloadHashMode.StreamingSignedTrailer);

        trailerHeaders["authorization"] = new[] { sigTrailer.AuthorizationHeader };
        using var trailerBody = new MemoryStream(Encoding.UTF8.GetBytes("0\r\n\r\n"));
        var reqTrailer = CreateTestRequest("PUT", "/testbucket/testkey", headers: trailerHeaders, body: trailerBody);
        await SharpS3Server.ValidateSignatureAsync(reqTrailer, settings, null, default);
    }

    [TestMethod]
    public void AwsSignatureV4_Constructor_Body_Types_And_Helpers()
    {
        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);

        // Body as byte[]
        byte[] bytes = Encoding.UTF8.GetBytes("data");
        var sig1 = new AwsSignatureV4(
            timestamp, "PUT", "http://localhost/b/k", "key", "sec", "us-east-1", "s3",
            new Dictionary<string, string[]>(), null, bytes, AwsPayloadHashMode.Signed);
        sig1.HashedPayload.Should().NotBeNullOrEmpty();

        // Body as string
        var sig2 = new AwsSignatureV4(
            timestamp, "PUT", "http://localhost/b/k", "key", "sec", "us-east-1", "s3",
            new Dictionary<string, string[]>(), null, "hello", AwsPayloadHashMode.Signed);
        sig2.HashedPayload.Should().NotBeNullOrEmpty();

        // Body as seekable stream
        using var ms = new MemoryStream(bytes);
        var sig3 = new AwsSignatureV4(
            timestamp, "PUT", "http://localhost/b/k", "key", "sec", "us-east-1", "s3",
            new Dictionary<string, string[]>(), null, ms, AwsPayloadHashMode.Signed);
        sig3.HashedPayload.Should().Be(sig1.HashedPayload);

        // Body as NameValueCollection headers
        var nvc = new NameValueCollection { { "host", "localhost" } };
        var sig4 = new AwsSignatureV4(
            timestamp, "GET", "http://localhost/b/k", "key", "sec", "us-east-1", "s3",
            nvc, new[] { "host" }, null, AwsPayloadHashMode.Unsigned);
        sig4.SignedHeaders.Should().Be("host");

        // Static helpers
        byte[] hash = AwsSignatureV4.HmacSha256(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("message"));
        hash.Should().NotBeNullOrEmpty();
        string hex = AwsSignatureV4.Sha256Hex(Encoding.UTF8.GetBytes("hello"));
        hex.Should().Be(AwsSignatureV4.BytesToHex(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("hello"))).ToLowerInvariant());

        sig1.Dispose();
    }

    [TestMethod]
    public void AwsChunkSigner_Signature_Calculations()
    {
        byte[] keyBytes = new byte[32];
        var signer1 = new AwsChunkSigner("20261116", "us-east-1", "s3", keyBytes, "seed-signature");

        byte[] chunkData = Encoding.UTF8.GetBytes("chunk payload");
        string chunkSig = signer1.ComputeChunkSignature(chunkData);
        chunkSig.Should().NotBeNullOrEmpty();

        var signer2 = new AwsChunkSigner("20261116", "us-east-1", "s3", keyBytes, "seed-signature");
        signer2.ValidateChunk(chunkData, chunkSig).Should().BeTrue();
        signer2.ValidateChunk(chunkData, "bad-sig").Should().BeFalse();

        var trailers = new SortedDictionary<string, string>
        {
            ["x-amz-checksum-crc32"] = "12345"
        };
        string trailerSig = signer1.ComputeTrailerSignature(trailers);
        trailerSig.Should().NotBeNullOrEmpty();

        var signer3 = new AwsChunkSigner("20261116", "us-east-1", "s3", keyBytes, chunkSig);
        signer3.ValidateTrailer(trailers, trailerSig).Should().BeTrue();
        signer3.ValidateTrailer(trailers, "bad-trailer-sig").Should().BeFalse();

        signer1.Dispose();
        signer2.Dispose();
        signer3.Dispose();
    }
}
