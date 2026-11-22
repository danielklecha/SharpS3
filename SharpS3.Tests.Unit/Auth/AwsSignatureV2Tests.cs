namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
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

[TestClass]
public class AwsSignatureV2Tests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    [TestMethod]
    public void CalculateHeaderSignature_ProducesExpectedHmac()
    {
        var headers = new Dictionary<string, string[]>
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        sig.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void CalculateSignedUrlSignature_ProducesExpectedHmac()
    {
        var headers = new Dictionary<string, string[]>();
        var query = new Dictionary<string, string>();
        long expires = 1175139620;

        string sig = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            expires,
            SecretKey);

        sig.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public async Task Server_Validates_V2_Header_Successfully()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = true
        };
        settings.Credentials[AccessKey] = SecretKey;
        var server = new SharpS3Server(settings);

        string date = "Tue, 27 Mar 2007 19:36:42 +0000";
        var headers = new Dictionary<string, string[]>
        {
            { "date", new[] { date } }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        headers["authorization"] = new[] { $"AWS {AccessKey}:{sig}" };

        var req = await server.ReceiveGetRequestAsync("/mybucket/mykey.txt", query, headers);
        req.Should().NotBeNull();
        req.SignatureVersion.Should().Be(S3SignatureVersion.Version2);
        req.AccessKey.Should().Be(AccessKey);
    }

    [TestMethod]
    public async Task Server_Validates_V2_PresignedUrl_Successfully()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = true
        };
        settings.Credentials[AccessKey] = SecretKey;
        var server = new SharpS3Server(settings);

        long futureExpires = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var headers = new Dictionary<string, string[]>();
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            futureExpires,
            SecretKey);

        query["AWSAccessKeyId"] = AccessKey;
        query["Expires"] = futureExpires.ToString();
        query["Signature"] = sig;

        var req = await server.ReceiveGetRequestAsync("/mybucket/mykey.txt", query, headers);
        req.Should().NotBeNull();
        req.SignatureVersion.Should().Be(S3SignatureVersion.Version2);
        req.AccessKey.Should().Be(AccessKey);
    }

    [TestMethod]
    public async Task Server_V2_PresignedUrl_Expired_Throws_AccessDenied()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = true
        };
        settings.Credentials[AccessKey] = SecretKey;
        var server = new SharpS3Server(settings);

        long pastExpires = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var headers = new Dictionary<string, string[]>();
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            pastExpires,
            SecretKey);

        query["AWSAccessKeyId"] = AccessKey;
        query["Expires"] = pastExpires.ToString();
        query["Signature"] = sig;

        var act = async () => await server.ReceiveGetRequestAsync("/mybucket/mykey.txt", query, headers);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
    }

    [TestMethod]
    public async Task Server_V2_SignatureMismatch_Throws_SignatureDoesNotMatch()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = true
        };
        settings.Credentials[AccessKey] = SecretKey;
        var server = new SharpS3Server(settings);

        var headers = new Dictionary<string, string[]>
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } },
            { "authorization", new[] { $"AWS {AccessKey}:invalidsignaturevalue" } }
        };

        var act = async () => await server.ReceiveGetRequestAsync("/mybucket/mykey.txt", null, headers);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task Server_When_EnableSignatureV2_False_Throws_SignatureDoesNotMatch()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            EnableSignatureV2 = false // disabled
        };
        settings.Credentials[AccessKey] = SecretKey;
        var server = new SharpS3Server(settings);

        var headers = new Dictionary<string, string[]>
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } },
            { "authorization", new[] { $"AWS {AccessKey}:someSignature" } }
        };

        var act = async () => await server.ReceiveGetRequestAsync("/mybucket/mykey.txt", null, headers);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public void Validate_WithAmzDateHeaderOnly_Succeeds()
    {
        string amzDate = "Tue, 27 Mar 2007 19:36:42 +0000";
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "x-amz-date", new[] { amzDate } }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        headers["authorization"] = new[] { $"AWS {AccessKey}:{sig}" };

        var request = new S3Request(
            "GET",
            "/mybucket/mykey.txt",
            query,
            headers,
            Stream.Null);

        var act = () => AwsSignatureV2.Validate(request, SecretKey);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_WithBothAmzDateAndDateHeaders_Succeeds()
    {
        string amzDate = "Tue, 27 Mar 2007 19:36:42 +0000";
        string date = "Tue, 27 Mar 2007 19:36:42 +0000";
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "x-amz-date", new[] { amzDate } },
            { "date", new[] { date } }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        headers["authorization"] = new[] { $"AWS {AccessKey}:{sig}" };

        var request = new S3Request(
            "GET",
            "/mybucket/mykey.txt",
            query,
            headers,
            Stream.Null);

        var act = () => AwsSignatureV2.Validate(request, SecretKey);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Validate_WithNeitherDateNorAmzDate_Throws_SignatureDoesNotMatch()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "authorization", new[] { $"AWS {AccessKey}:someSignature" } }
        };
        var query = new Dictionary<string, string>();

        var request = new S3Request(
            "GET",
            "/mybucket/mykey.txt",
            query,
            headers,
            Stream.Null);

        var act = () => AwsSignatureV2.Validate(request, SecretKey);
        var ex = act.Should().Throw<S3Exception>().Which;
        ex.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public void CalculateHeaderSignature_WithAmzHeaders_HandlesNullEmptyAndMixedValues()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } },
            { "x-amz-null", null! },
            { "x-amz-empty", Array.Empty<string>() },
            { "x-amz-mixed", new string[] { null!, "  val1  ", null!, "val2  " } }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/mybucket/mykey.txt",
            "mybucket",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        sig.Should().NotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void BuildCanonicalizedResource_PathWithoutLeadingSlash_AppendsSlash()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } }
        };
        var query = new Dictionary<string, string>();

        string sigWithoutSlash = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        string sigWithSlash = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        sigWithoutSlash.Should().Be(sigWithSlash);

        long expires = 1175139620;
        string urlSigWithoutSlash = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            expires,
            SecretKey);

        string urlSigWithSlash = AwsSignatureV2.CalculateSignedUrlSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            expires,
            SecretKey);

        urlSigWithoutSlash.Should().Be(urlSigWithSlash);
    }

    [TestMethod]
    public void GetFirstHeaderValue_WithNullElementInHeaderArray_ReturnsEmptyString()
    {
        var headersWithNulls = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", new string[] { null! } },
            { "content-md5", new string[] { null! } },
            { "content-type", new string[] { null! } }
        };
        var emptyHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var query = new Dictionary<string, string>();

        string sigWithNulls = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headersWithNulls,
            query,
            SecretKey);

        string sigWithEmpty = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            emptyHeaders,
            query,
            SecretKey);

        sigWithNulls.Should().Be(sigWithEmpty);
    }

    [TestMethod]
    public void GenerateSignedUrl_WithHttpScheme_Succeeds()
    {
        string httpUrl = "http://s3.amazonaws.com/examplebucket/photos/puppy.jpg";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            httpUrl,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().StartWith(httpUrl + "?");
        signedUrl.Should().Contain("AWSAccessKeyId=" + Uri.EscapeDataString(AccessKey));
        signedUrl.Should().Contain("Expires=1175139620");
        signedUrl.Should().Contain("Signature=");
    }

    [TestMethod]
    public void GenerateSignedUrl_WithFtpScheme_ThrowsArgumentException()
    {
        string ftpUrl = "ftp://s3.amazonaws.com/examplebucket/photos/puppy.jpg";
        long expires = 1175139620;

        Action act = () => AwsSignatureV2.GenerateSignedUrl(
            "GET",
            ftpUrl,
            AccessKey,
            SecretKey,
            expires);

        act.Should().Throw<ArgumentException>()
            .WithParameterName("fullUrl")
            .WithMessage("*not an absolute HTTP or HTTPS URI*");
    }

    [TestMethod]
    public void GenerateSignedUrl_WithTrailingQuestionMark_OmitsDuplicateDelimiter()
    {
        string urlWithTrailingQuestionMark = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg?";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            urlWithTrailingQuestionMark,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().StartWith(urlWithTrailingQuestionMark + "AWSAccessKeyId=");
        signedUrl.Should().NotContain("?&");
        signedUrl.Should().NotContain("??");
    }

    [TestMethod]
    public void GenerateSignedUrl_WithTrailingAmpersand_OmitsDuplicateDelimiter()
    {
        string urlWithTrailingAmpersand = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg?acl&";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            urlWithTrailingAmpersand,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().StartWith(urlWithTrailingAmpersand + "AWSAccessKeyId=");
        signedUrl.Should().NotContain("&&");
    }

    [TestMethod]
    public void GenerateSignedUrl_WithConsecutiveAndEmptyQueryParameters_HandlesEmptySegments()
    {
        string urlWithEmptySegments = "https://s3.amazonaws.com/examplebucket/photos/puppy.jpg?acl&&prefix=foo&";
        long expires = 1175139620;

        string signedUrl = AwsSignatureV2.GenerateSignedUrl(
            "GET",
            urlWithEmptySegments,
            AccessKey,
            SecretKey,
            expires);

        signedUrl.Should().Contain("AWSAccessKeyId=");
        signedUrl.Should().Contain("Signature=");
    }

    [TestMethod]
    public void ParseQueryString_HandlesVariousInputs()
    {
        AwsSignatureV2.ParseQueryString(null).Should().BeEmpty();
        AwsSignatureV2.ParseQueryString(string.Empty).Should().BeEmpty();
        AwsSignatureV2.ParseQueryString("?").Should().BeEmpty();
        AwsSignatureV2.ParseQueryString("???").Should().BeEmpty();
        AwsSignatureV2.ParseQueryString("?&").Should().BeEmpty();
        AwsSignatureV2.ParseQueryString("?&&&").Should().BeEmpty();

        var result = AwsSignatureV2.ParseQueryString("?acl&prefix=test%20dir&tag=a+b&&versionId=123&");
        result.Should().HaveCount(4);
        result["acl"].Should().Be(string.Empty);
        result["prefix"].Should().Be("test dir");
        result["tag"].Should().Be("a b");
        result["versionId"].Should().Be("123");
    }

    [TestMethod]
    public void NormalizeHeaderValue_HandlesVariousInputs()
    {
        AwsSignatureV2.NormalizeHeaderValue(null).Should().Be(string.Empty);
        AwsSignatureV2.NormalizeHeaderValue(string.Empty).Should().Be(string.Empty);
        AwsSignatureV2.NormalizeHeaderValue("   \t  \r\n  ").Should().Be(string.Empty);
        AwsSignatureV2.NormalizeHeaderValue("simple").Should().Be("simple");
        AwsSignatureV2.NormalizeHeaderValue("  line1\r\n\tline2    line3  ").Should().Be("line1 line2 line3");
        AwsSignatureV2.NormalizeHeaderValue("line1\nline2\rline3").Should().Be("line1 line2 line3");
    }

    [TestMethod]
    public void CalculateHeaderSignature_WithAmzHeaderContainingEmptyAndNullEntries_NormalizesCleanly()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "date", new[] { "Tue, 27 Mar 2007 19:36:42 +0000" } },
            { "x-amz-meta-custom", new string?[] { null, "", "   ", "value1", "\r\n\tvalue2  " }! }
        };
        var query = new Dictionary<string, string>();

        string sig = AwsSignatureV2.CalculateHeaderSignature(
            "GET",
            "/photos/puppy.jpg",
            "johnsmith",
            S3RequestStyle.PathStyle,
            headers,
            query,
            SecretKey);

        sig.Should().NotBeNullOrWhiteSpace();
    }
}
