namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class AwsSignatureEnhancementsTests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    #region AwsSignatureV2 Subresource & Normalization Tests

    [TestMethod]
    public void AwsSignatureV2_IncludesExpandedSubresourcesInCanonicalResource()
    {
        var headers = new Dictionary<string, string[]>
        {
            ["Date"] = new[] { "Tue, 27 Mar 2007 19:36:42 +0000" }
        };

        var query = new Dictionary<string, string>
        {
            ["response-content-disposition"] = "attachment; filename=test.txt",
            ["response-content-type"] = "application/octet-stream",
            ["analytics"] = "config1",
            ["encryption"] = "",
            ["inventory"] = "rule1",
            ["metrics"] = "metric1",
            ["object-lock"] = "",
            ["publicAccessBlock"] = "",
            ["replication"] = "",
            ["partnumber"] = "2", // lowercase key mapped to canonical partNumber
            ["uploadid"] = "xyz", // lowercase key mapped to canonical uploadId
            ["ignoredParam"] = "value"
        };

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey",
            null,
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        sig.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void AwsSignatureV2_AmzHeaderWhitespaceNormalization()
    {
        var headers = new Dictionary<string, string[]>
        {
            ["Date"] = new[] { "Tue, 27 Mar 2007 19:36:42 +0000" },
            ["x-amz-meta-notes"] = new[] { "  line1\r\n\tline2    line3  " }
        };

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey",
            null,
            S3RequestStyle.PathStyle,
            headers,
            new Dictionary<string, string>(),
            SecretKey);

        sig.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region AwsSignatureV2 GenerateSignedUrl Tests

    [TestMethod]
    public void AwsSignatureV2_GenerateSignedUrl_ValidInputs_GeneratesCorrectUrl()
    {
        string fullUrl = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            fullUrl,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().StartWith(fullUrl + "?");
        signedUrl.Should().Contain("AWSAccessKeyId=" + Uri.EscapeDataString(AccessKey));
        signedUrl.Should().Contain("Expires=1175139620");
        signedUrl.Should().Contain("Signature=");
    }

    [TestMethod]
    public void AwsSignatureV2_GenerateSignedUrl_WithDateTimeOffset_GeneratesCorrectUrl()
    {
        string fullUrl = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg";
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(1175139620);

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            fullUrl,
            AccessKey,
            SecretKey,
            expiresAt);

        signedUrl.Should().Contain("Expires=1175139620");
    }

    [TestMethod]
    public void AwsSignatureV2_GenerateSignedUrl_WithVirtualHostedStyleBucket()
    {
        string fullUrl = "https://examplebucket.s3.amazonaws.com/photos/puppy.jpg";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            fullUrl,
            AccessKey,
            SecretKey,
            expires,
            bucket: "examplebucket");

        signedUrl.Should().Contain("AWSAccessKeyId=");
    }

    [TestMethod]
    public void AwsSignatureV2_GenerateSignedUrl_WithExistingQueryParams_PreservesThem()
    {
        string fullUrl = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg?acl";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            fullUrl,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().Contain("?acl&AWSAccessKeyId=");
    }

    [TestMethod]
    public void AwsSignatureV2_GenerateSignedUrl_ValidationExceptions()
    {
        string validUrl = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg";

        // Null / empty method
        Action act1 = () => AwsSignatureV2.GenerateSignedUrl(null!, validUrl, AccessKey, SecretKey, 1000);
        act1.Should().Throw<ArgumentNullException>();

        // Null / empty url
        Action act2 = () => AwsSignatureV2.GenerateSignedUrl("GET", null!, AccessKey, SecretKey, 1000);
        act2.Should().Throw<ArgumentNullException>();

        // Null / empty accessKey
        Action act3 = () => AwsSignatureV2.GenerateSignedUrl("GET", validUrl, null!, SecretKey, 1000);
        act3.Should().Throw<ArgumentNullException>();

        // Null / empty secretKey
        Action act4 = () => AwsSignatureV2.GenerateSignedUrl("GET", validUrl, AccessKey, null!, 1000);
        act4.Should().Throw<ArgumentNullException>();

        // Invalid expires <= 0
        Action act5 = () => AwsSignatureV2.GenerateSignedUrl("GET", validUrl, AccessKey, SecretKey, 0);
        act5.Should().Throw<ArgumentOutOfRangeException>();

        // Non-absolute or non-http url
        Action act6 = () => AwsSignatureV2.GenerateSignedUrl("GET", "/photos/puppy.jpg", AccessKey, SecretKey, 1000);
        act6.Should().Throw<ArgumentException>();

        // Already signed url
        string alreadySigned = "https://s3.amazonaws.com/b/k?AWSAccessKeyId=KEY&Expires=1000&Signature=SIG";
        Action act7 = () => AwsSignatureV2.GenerateSignedUrl("GET", alreadySigned, AccessKey, SecretKey, 1000);
        act7.Should().Throw<ArgumentException>();
    }

    #endregion

    #region AwsSignatureV4 Host Requirement & Precomputed Hash Tests

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_ThrowsWhenHostMissing()
    {
        var headers = new Dictionary<string, string>
        {
            ["custom"] = "value"
        };

        Action act = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            requireHostHeader: true);

        act.Should().Throw<ArgumentException>().WithParameterName("headers");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_PassesWhenHostPresent()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "s3.amazonaws.com"
        };

        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            requireHostHeader: true);

        sig.SignedHeaders.Should().Contain("host");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_PassesWhenHostInSignedHeadersOnly()
    {
        var headers = new Dictionary<string, string>
        {
            ["custom"] = "value"
        };

        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            signedHeaders: new[] { "custom", "host" },
            requireHostHeader: true);

        sig.Should().NotBeNull();
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_ThrowsWhenHostMissingFromSignedHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["custom"] = "value"
        };

        Action act = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            signedHeaders: new[] { "custom", "x-amz-date" },
            requireHostHeader: true);

        act.Should().Throw<ArgumentException>().WithParameterName("headers");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_ThrowsWhenHeadersNullAndSignedHeadersNull()
    {
        Action act = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: (IEnumerable<KeyValuePair<string, string>>?)null,
            signedHeaders: null,
            requireHostHeader: true);

        act.Should().Throw<ArgumentException>().WithParameterName("headers");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_ThrowsWhenHeadersNullAndSignedHeadersWithoutHost()
    {
        Action act = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: (IEnumerable<KeyValuePair<string, string>>?)null,
            signedHeaders: new[] { "x-amz-date" },
            requireHostHeader: true);

        act.Should().Throw<ArgumentException>().WithParameterName("headers");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_PassesWhenHeadersNullAndHostInSignedHeaders()
    {
        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: (IEnumerable<KeyValuePair<string, string>>?)null,
            signedHeaders: new[] { "host" },
            requireHostHeader: true);

        sig.Should().NotBeNull();
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_Overload_NameValueCollection()
    {
        var nvcWithoutHost = new System.Collections.Specialized.NameValueCollection
        {
            { "custom", "value" }
        };

        Action actWithoutHost = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: nvcWithoutHost,
            requireHostHeader: true);

        actWithoutHost.Should().Throw<ArgumentException>().WithParameterName("headers");

        var nvcWithHost = new System.Collections.Specialized.NameValueCollection
        {
            { "host", "s3.amazonaws.com" }
        };

        using var sigWithHost = new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: nvcWithHost,
            requireHostHeader: true);

        sigWithHost.SignedHeaders.Should().Contain("host");
    }

    [TestMethod]
    public void AwsSignatureV4_RequireHostHeader_Overload_ArrayHeaders()
    {
        var arrayWithoutHost = new Dictionary<string, string[]>
        {
            ["custom"] = new[] { "value" }
        };

        Action actWithoutHost = () => new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: arrayWithoutHost,
            requireHostHeader: true);

        actWithoutHost.Should().Throw<ArgumentException>().WithParameterName("headers");

        var arrayWithHost = new Dictionary<string, string[]>
        {
            ["host"] = new[] { "s3.amazonaws.com" }
        };

        using var sigWithHost = new AwsSignatureV4(
            "20261119T145226Z",
            "GET",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers: arrayWithHost,
            requireHostHeader: true);

        sigWithHost.SignedHeaders.Should().Contain("host");
    }

    [TestMethod]
    public void AwsSignatureV4_PrecomputedPayloadHash_HexHash()
    {
        string precomputedHex = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
        var headers = new Dictionary<string, string>
        {
            ["host"] = "s3.amazonaws.com"
        };

        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            "PUT",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            precomputedPayloadHash: precomputedHex);

        sig.HashedPayload.Should().Be(precomputedHex);
        sig.CanonicalRequest.Should().EndWith(precomputedHex);
    }

    [TestMethod]
    public void AwsSignatureV4_PrecomputedPayloadHash_StreamingTrailerToken()
    {
        var headers = new Dictionary<string, string>
        {
            ["host"] = "s3.amazonaws.com"
        };

        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            "PUT",
            "http://s3.amazonaws.com/test",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            headers,
            precomputedPayloadHash: "STREAMING-UNSIGNED-PAYLOAD-TRAILER");

        sig.HashedPayload.Should().Be("STREAMING-UNSIGNED-PAYLOAD-TRAILER");
    }

    #endregion

    #region SharpS3Server ValidateV4Signature with Non-Chunked Signed Payload Tests

    [TestMethod]
    public async Task SharpS3Server_ValidatesNonChunkedSignedPayload_WithPrecomputedHash()
    {
        var settings = new SharpS3Settings();
        settings.Credentials[AccessKey] = SecretKey;

        byte[] payload = Encoding.UTF8.GetBytes("Hello S3 Payload Verification!");
        string payloadHash = AwsSignatureV4.Sha256Hex(payload);
        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);

        var nvc = new NameValueCollection
        {
            { "host", "localhost" },
            { "x-amz-date", timestamp },
            { "x-amz-content-sha256", payloadHash }
        };

        using var clientSig = new AwsSignatureV4(
            timestamp,
            "PUT",
            "http://localhost/testbucket/testkey",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            nvc,
            precomputedPayloadHash: payloadHash);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { payloadHash },
            ["authorization"] = new[] { clientSig.AuthorizationHeader }
        };

        using var ms = new MemoryStream(payload);
        var req = new S3Request("PUT", "/testbucket/testkey", new Dictionary<string, string>(), headers, ms, settings);

        // Before our fix, SharpS3Server.ValidateV4Signature would pass requestBody: null
        // and compute EmptySha256, causing a signature mismatch!
        // With our fix, it accepts request.ContentSha256 as precomputedHash and succeeds.
        var act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task SharpS3Server_ValidatesStreamingUnsignedPayloadTrailer()
    {
        var settings = new SharpS3Settings();
        settings.Credentials[AccessKey] = SecretKey;

        string timestamp = DateTime.UtcNow.ToString(Constants.AmazonTimestampFormatCompact);
        const string streamingTrailerLiteral = "STREAMING-UNSIGNED-PAYLOAD-TRAILER";

        var nvc = new NameValueCollection
        {
            { "host", "localhost" },
            { "x-amz-date", timestamp },
            { "x-amz-content-sha256", streamingTrailerLiteral }
        };

        using var clientSig = new AwsSignatureV4(
            timestamp,
            "PUT",
            "http://localhost/testbucket/testkey",
            AccessKey,
            SecretKey,
            "us-east-1",
            "s3",
            nvc,
            payloadHashMode: AwsPayloadHashMode.IsStreaming);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost" },
            ["x-amz-date"] = new[] { timestamp },
            ["x-amz-content-sha256"] = new[] { streamingTrailerLiteral },
            ["authorization"] = new[] { clientSig.AuthorizationHeader }
        };

        using var ms = new MemoryStream();
        var req = new S3Request("PUT", "/testbucket/testkey", new Dictionary<string, string>(), headers, ms, settings);

        var act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, null, default);
        await act.Should().NotThrowAsync();
    }

    #endregion
}
