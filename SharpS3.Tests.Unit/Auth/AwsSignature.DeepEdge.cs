namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Auth;
using SharpS3.Models;

[TestClass]
public class AwsSignatureDeepEdgeTests
{
    [TestMethod]
    public void AwsSignatureV2_CanonicalResource_Subresources_And_Headers()
    {
        var headers = new Dictionary<string, string[]>
        {
            ["x-amz-date"] = new[] { "Tue, 27 Mar 2007 19:36:41 +0000" },
            ["x-amz-storage-class"] = new[] { "REDUCED_REDUNDANCY" },
            ["x-amz-meta-custom"] = new[] { "value1, value2" },
            ["Content-Type"] = new[] { "text/plain" },
            ["Content-MD5"] = new[] { "Q2hlY2sgSW50ZWdyaXR5IQ==" }
        };

        var query = new Dictionary<string, string>
        {
            ["acl"] = "",
            ["versionId"] = "v123",
            ["unrelated"] = "ignored"
        };

        // 1. CalculateHeaderSignature with x-amz-date and subresources in query
        string sig = AwsSignatureV2.CalculateHeaderSignature(
            httpMethod: "GET",
            headers: headers,
            rawPath: "/my-object",
            bucket: "my-bucket",
            requestStyle: S3RequestStyle.PathStyle,
            queryParameters: query,
            secretKey: "secret"
        );
        sig.Should().NotBeNullOrEmpty();

        // 2. CalculateSignedUrlSignature with VirtualHostedStyle and empty path
        string urlSig = AwsSignatureV2.CalculateSignedUrlSignature(
            httpMethod: "PUT",
            headers: new Dictionary<string, string[]> { ["Date"] = new[] { "Wed, 28 Mar 2007 01:29:59 +0000" } },
            rawPath: "",
            bucket: "my-bucket",
            requestStyle: S3RequestStyle.VirtualHostedStyle,
            queryParameters: new Dictionary<string, string>(),
            expires: 1234567890L,
            secretKey: "secret"
        );
        urlSig.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void AwsSignatureV4_CanonicalUri_Headers_And_PayloadHash()
    {
        // 1. BuildCanonicalUri variations
        AwsSignatureV4.BuildCanonicalUri("").Should().Be("/");
        AwsSignatureV4.BuildCanonicalUri("http://example.com").Should().Be("/");
        AwsSignatureV4.BuildCanonicalUri("http://example.com/test/path?foo=bar").Should().Be("/test/path");
        AwsSignatureV4.BuildCanonicalUri("test/path").Should().Be("/test/path");
        AwsSignatureV4.BuildCanonicalUri("/test/path").Should().Be("/test/path");

        // 2. BuildCanonicalQueryString variations
        AwsSignatureV4.BuildCanonicalQueryString("").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com?").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com?a=1&b=2").Should().Be("a=1&b=2");

        // 3. AwsSignatureV4 constructor with HeaderIgnoreList (signedHeaders is null)
        var headers = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "s3.amazonaws.com" },
            ["user-agent"] = new[] { "aws-sdk-net" },
            ["connection"] = new[] { "keep-alive" },
            ["expect"] = new[] { "100-continue" },
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 ..." },
            ["custom-header"] = new[] { "value" }
        };

        using var sig = new AwsSignatureV4(
            timestamp: "20260101T000000Z",
            httpMethod: "GET",
            fullUrl: "http://s3.amazonaws.com/test",
            accessKey: "key",
            secretKey: "secret",
            region: "us-east-1",
            service: "s3",
            headers: headers,
            signedHeaders: null
        );

        sig.CanonicalHeaders.Should().Contain("custom-header:value\n");
        sig.CanonicalHeaders.Should().Contain("host:s3.amazonaws.com\n");
        sig.CanonicalHeaders.Should().NotContain("user-agent:");
        sig.CanonicalHeaders.Should().NotContain("connection:");
        sig.CanonicalHeaders.Should().NotContain("expect:");
        sig.SignedHeaders.Should().Be("custom-header;host");

        // 4. ComputePayloadHash modes
        AwsSignatureV4.ComputePayloadHash(null, AwsPayloadHashMode.Unsigned).Should().Be("UNSIGNED-PAYLOAD");
        AwsSignatureV4.ComputePayloadHash(null, AwsPayloadHashMode.IsStreaming).Should().Be("STREAMING-UNSIGNED-PAYLOAD-TRAILER");
        AwsSignatureV4.ComputePayloadHash(null, AwsPayloadHashMode.StreamingSigned).Should().Be("STREAMING-AWS4-HMAC-SHA256-PAYLOAD");
        AwsSignatureV4.ComputePayloadHash(null, AwsPayloadHashMode.StreamingSignedTrailer).Should().Be("STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER");

        // Signed mode body variants
        AwsSignatureV4.ComputePayloadHash(null, AwsPayloadHashMode.Signed).Should().Be(AwsSignatureV4.EmptySha256);
        AwsSignatureV4.ComputePayloadHash(Array.Empty<byte>(), AwsPayloadHashMode.Signed).Should().Be(AwsSignatureV4.EmptySha256);
        AwsSignatureV4.ComputePayloadHash("", AwsPayloadHashMode.Signed).Should().Be(AwsSignatureV4.EmptySha256);
        AwsSignatureV4.ComputePayloadHash("hello", AwsPayloadHashMode.Signed).Should().NotBe(AwsSignatureV4.EmptySha256);

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        AwsSignatureV4.ComputePayloadHash(ms, AwsPayloadHashMode.Signed).Should().NotBe(AwsSignatureV4.EmptySha256);
        ms.Position.Should().Be(0); // Position restored

        // Unsupported body type throws ArgumentException
        Action actBadBody = () => AwsSignatureV4.ComputePayloadHash(12345, AwsPayloadHashMode.Signed);
        actBadBody.Should().Throw<ArgumentException>();

        // Invalid mode throws ArgumentOutOfRangeException
        Action actBadMode = () => AwsSignatureV4.ComputePayloadHash(null, (AwsPayloadHashMode)9999);
        actBadMode.Should().Throw<ArgumentOutOfRangeException>();
    }
}
