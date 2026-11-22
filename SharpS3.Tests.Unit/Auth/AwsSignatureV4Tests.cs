namespace SharpS3.Tests.Unit;

#pragma warning disable CS8625

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Auth;

[TestClass]
public partial class AwsSignatureV4Tests
{
    private const string ValidTimestamp = "20261119T145226Z";
    private const string ValidMethod = "GET";
    private const string ValidUrl = "http://s3.amazonaws.com/testbucket/testkey";
    private const string ValidAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string ValidSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

    private class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] buffer) : base(buffer) { }
        public override bool CanSeek => false;
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private class NullValuesNameValueCollection : NameValueCollection
    {
        public NullValuesNameValueCollection()
        {
            Add("dummy", "value");
        }

        public override string[]? GetValues(string? name) => null;
    }

    #region Constructor Parameter Validation (Lines 125-132)

    [TestMethod]
    public void Constructor_NullTimestamp_ThrowsArgumentNullException()
    {
        Action act = () => new AwsSignatureV4(
            null,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("timestamp");
    }

    [TestMethod]
    public void Constructor_TimestampLengthLessThan8_UsesFullTimestampAsDateStamp()
    {
        using var sig = new AwsSignatureV4(
            "short",
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        sig.DateStamp.Should().Be("short");
        sig.Scope.Should().Be("short/us-east-1/s3/aws4_request");
    }

    [TestMethod]
    public void Constructor_TimestampLengthGreaterOrEqualTo8_TruncatesDateStampTo8Chars()
    {
        using var sig = new AwsSignatureV4(
            "20261119T145226Z",
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        sig.DateStamp.Should().Be("20261119");
        sig.Scope.Should().Be("20261119/us-east-1/s3/aws4_request");
    }

    [TestMethod]
    public void Constructor_NullHttpMethod_ThrowsArgumentNullException()
    {
        Action act = () => new AwsSignatureV4(
            ValidTimestamp,
            null,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("httpMethod");
    }

    [TestMethod]
    public void Constructor_NullFullUrl_ThrowsArgumentNullException()
    {
        Action act = () => new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            null,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("fullUrl");
    }

    [TestMethod]
    public void Constructor_NullAccessKey_ThrowsArgumentNullException()
    {
        Action act = () => new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            null,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("accessKey");
    }

    [TestMethod]
    public void Constructor_NullSecretKey_ThrowsArgumentNullException()
    {
        Action act = () => new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            null,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("secretKey");
    }

    [TestMethod]
    public void Constructor_NullRegionAndService_DefaultsToUsEast1AndS3()
    {
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            null,
            null,
            new Dictionary<string, string>());

        sig.Region.Should().Be("us-east-1");
        sig.Service.Should().Be("s3");
        sig.Scope.Should().Be("20261119/us-east-1/s3/aws4_request");
    }

    [TestMethod]
    public void Constructor_CustomRegionAndService_UsesSpecifiedValues()
    {
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "eu-central-1",
            "dynamodb",
            new Dictionary<string, string>());

        sig.Region.Should().Be("eu-central-1");
        sig.Service.Should().Be("dynamodb");
        sig.Scope.Should().Be("20261119/eu-central-1/dynamodb/aws4_request");
    }

    #endregion

    #region BuildCanonicalUri (Line 188)

    [TestMethod]
    public void BuildCanonicalUri_HostOnlyWithoutTrailingSlash_ReturnsSingleSlash()
    {
        // Covers line 188 where path is empty after extracting from scheme
        AwsSignatureV4.BuildCanonicalUri("http://example.com").Should().Be("/");
        AwsSignatureV4.BuildCanonicalUri("https://s3.amazonaws.com").Should().Be("/");
    }

    [TestMethod]
    public void BuildCanonicalUri_HostWithQueryOnly_ReturnsSingleSlash()
    {
        // Covers line 188 when url has scheme and query but empty path
        AwsSignatureV4.BuildCanonicalUri("http://example.com?foo=bar").Should().Be("/");
    }

    [TestMethod]
    public void BuildCanonicalUri_QueryOnlyWithoutSchemeOrPath_ReturnsSingleSlash()
    {
        // Covers line 188 when url is "?foo=bar" and path before '?' is empty
        AwsSignatureV4.BuildCanonicalUri("?foo=bar").Should().Be("/");
        AwsSignatureV4.BuildCanonicalUri("?").Should().Be("/");
    }

    [TestMethod]
    public void BuildCanonicalUri_NullOrEmptyUrl_ReturnsSingleSlash()
    {
        AwsSignatureV4.BuildCanonicalUri("").Should().Be("/");
        AwsSignatureV4.BuildCanonicalUri(null).Should().Be("/");
    }

    [TestMethod]
    public void BuildCanonicalUri_RelativePathWithoutLeadingSlash_PrependsSlash()
    {
        AwsSignatureV4.BuildCanonicalUri("mybucket/mykey").Should().Be("/mybucket/mykey");
    }

    [TestMethod]
    public void BuildCanonicalUri_WithPathAndQuery_StripsQueryAndPreservesPath()
    {
        AwsSignatureV4.BuildCanonicalUri("http://example.com/mybucket/mykey?versionId=123").Should().Be("/mybucket/mykey");
        AwsSignatureV4.BuildCanonicalUri("/mybucket/mykey?foo=bar").Should().Be("/mybucket/mykey");
    }

    [TestMethod]
    public void BuildCanonicalUri_EscapesSpecialCharactersExceptSlash()
    {
        AwsSignatureV4.BuildCanonicalUri("http://example.com/my bucket/special@key").Should().Be("/my%20bucket/special%40key");
    }

    #endregion

    #region BuildCanonicalQueryString (Lines 203, 211, 239)

    [TestMethod]
    public void BuildCanonicalQueryString_UrlEndingWithQuestionMark_ReturnsEmptyString()
    {
        // Covers line 203 where query string after '?' is empty
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/path?").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("/path?").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("?").Should().Be(string.Empty);
    }

    [TestMethod]
    public void BuildCanonicalQueryString_UrlWithoutQueryString_ReturnsEmptyString()
    {
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/path").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("/path").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString("").Should().Be(string.Empty);
        AwsSignatureV4.BuildCanonicalQueryString(null).Should().Be(string.Empty);
    }

    [TestMethod]
    public void BuildCanonicalQueryString_ConsecutiveOrTrailingAmpersands_SkipsEmptyParts()
    {
        // Covers line 211 (continue when part is empty)
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?&&a=1&&b=2&")
            .Should().Be("a=1&b=2");

        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?&")
            .Should().Be(string.Empty);
    }

    [TestMethod]
    public void BuildCanonicalQueryString_DuplicateKeys_SortsByValue()
    {
        // Covers line 239 (cmp == 0 branch comparing values for identical keys)
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?tag=zebra&tag=apple")
            .Should().Be("tag=apple&tag=zebra");

        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?b=2&a=2&a=1&b=1")
            .Should().Be("a=1&a=2&b=1&b=2");
    }

    [TestMethod]
    public void BuildCanonicalQueryString_ValuelessParameters_AppendsEqualsSign()
    {
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?acl&lifecycle")
            .Should().Be("acl=&lifecycle=");
    }

    [TestMethod]
    public void BuildCanonicalQueryString_UnescapesPlusAndReencodes()
    {
        AwsSignatureV4.BuildCanonicalQueryString("http://example.com/test?prefix=foo+bar&title=hello%20world")
            .Should().Be("prefix=foo%20bar&title=hello%20world");
    }

    #endregion

    #region BuildCanonicalHeaders (Lines 294, 314)

    [TestMethod]
    public void BuildCanonicalHeaders_HeadersWithNullOrEmptyKey_SkipsHeader()
    {
        // Covers line 294 (continue when string.IsNullOrEmpty(h.Key))
        var headers = new List<KeyValuePair<string, string>>
        {
            new(string.Empty, "empty-key-value"),
            new(null!, "null-key-value"),
            new("x-custom-header", "valid-value")
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers);

        sig.CanonicalHeaders.Should().Be("x-custom-header:valid-value\n");
        sig.SignedHeaders.Should().Be("x-custom-header");
    }

    [TestMethod]
    public void BuildCanonicalHeaders_HeaderWithNullValue_HandlesAsEmptyString()
    {
        // Covers line 314 (h.Value ?? string.Empty when h.Value is null)
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-null-header", null!)
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers);

        sig.CanonicalHeaders.Should().Be("x-null-header:\n");
        sig.SignedHeaders.Should().Be("x-null-header");
    }

    [TestMethod]
    public void BuildCanonicalHeaders_HeaderWithMultipleSpaces_NormalizesToSingleSpace()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-spaces", "  value    with   multiple     spaces  ")
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers);

        sig.CanonicalHeaders.Should().Be("x-spaces:value with multiple spaces\n");
    }

    [TestMethod]
    public void BuildCanonicalHeaders_DuplicateHeaderKeys_CombinesWithComma()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-dup", "first"),
            new("x-dup", "second")
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers);

        sig.CanonicalHeaders.Should().Be("x-dup:first,second\n");
        sig.SignedHeaders.Should().Be("x-dup");
    }

    [TestMethod]
    public void BuildCanonicalHeaders_SignedHeadersFilter_OnlyIncludesSpecifiedHeaders()
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", "example.com"),
            new("x-amz-date", ValidTimestamp),
            new("x-ignored", "not-signed")
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers,
            signedHeaders: new[] { "host", "x-amz-date" });

        sig.CanonicalHeaders.Should().Contain("host:example.com\n");
        sig.CanonicalHeaders.Should().Contain($"x-amz-date:{ValidTimestamp}\n");
        sig.CanonicalHeaders.Should().NotContain("x-ignored");
        sig.SignedHeaders.Should().Be("host;x-amz-date");
    }

    [TestMethod]
    public void BuildCanonicalHeaders_NullHeaders_HandlesGracefully()
    {
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: (IEnumerable<KeyValuePair<string, string>>?)null);

        sig.CanonicalHeaders.Should().Be(string.Empty);
        sig.SignedHeaders.Should().Be(string.Empty);
    }

    #endregion

    #region ComputePayloadHash (Line 368)

    [TestMethod]
    public void ComputePayloadHash_SeekableStream_RestoresPosition()
    {
        byte[] data = Encoding.UTF8.GetBytes("test seekable stream content");
        using var ms = new MemoryStream(data)
        {
            Position = 5
        };

        string hash = AwsSignatureV4.ComputePayloadHash(ms, AwsPayloadHashMode.Signed);
        hash.Should().NotBeNullOrEmpty();
        ms.Position.Should().Be(5);
    }

    [TestMethod]
    public void ComputePayloadHash_NonSeekableStream_ComputesHashWithoutRestoringPosition()
    {
        // Covers line 368 where stream.CanSeek is false
        byte[] data = Encoding.UTF8.GetBytes("test non-seekable stream content");
        using var stream = new NonSeekableStream(data);

        string hash = AwsSignatureV4.ComputePayloadHash(stream, AwsPayloadHashMode.Signed);
        hash.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region FlattenHeaders (Lines 381, 385)

    [TestMethod]
    public void FlattenHeaders_NullHeadersCollection_YieldsEmpty()
    {
        // Covers line 381 (yield break when headers == null)
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: (IEnumerable<KeyValuePair<string, string[]>>?)null);

        sig.CanonicalHeaders.Should().Be(string.Empty);
        sig.SignedHeaders.Should().Be(string.Empty);
    }

    [TestMethod]
    public void FlattenHeaders_HeaderWithNullValueArray_SkipsHeader()
    {
        // Covers line 385 (continue when kvp.Value == null)
        var headers = new List<KeyValuePair<string, string[]>>
        {
            new("x-null-array", null!),
            new("x-valid", new[] { "val1", "val2" })
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: headers);

        sig.CanonicalHeaders.Should().Be("x-valid:val1,val2\n");
        sig.SignedHeaders.Should().Be("x-valid");
    }

    #endregion

    #region ConvertHeaders (Lines 396, 400)

    [TestMethod]
    public void ConvertHeaders_NullNameValueCollection_YieldsEmpty()
    {
        // Covers line 395 (yield break when nvc == null)
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: (NameValueCollection?)null);

        sig.CanonicalHeaders.Should().Be(string.Empty);
        sig.SignedHeaders.Should().Be(string.Empty);
    }

    [TestMethod]
    public void ConvertHeaders_NameValueCollectionWithNullKey_SkipsNullKey()
    {
        // Covers line 396 and line 398 (continue when key == null)
        var nvc = new NameValueCollection
        {
            { null, "val-with-null-key" },
            { "x-custom", "valid-custom-value" }
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: nvc);

        sig.CanonicalHeaders.Should().Be("x-custom:valid-custom-value\n");
        sig.SignedHeaders.Should().Be("x-custom");
    }

    [TestMethod]
    public void ConvertHeaders_NameValueCollectionWithNullValues_SkipsGracefully()
    {
        // Covers line 400 (when GetValues returns null)
        var nvc = new NullValuesNameValueCollection();

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: nvc);

        sig.CanonicalHeaders.Should().Be(string.Empty);
        sig.SignedHeaders.Should().Be(string.Empty);
    }

    [TestMethod]
    public void ConvertHeaders_NameValueCollectionWithMultipleValuesPerKey_FlattensCorrectly()
    {
        var nvc = new NameValueCollection
        {
            { "x-multi", "v1" },
            { "x-multi", "v2" }
        };

        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            headers: nvc);

        sig.CanonicalHeaders.Should().Be("x-multi:v1,v2\n");
        sig.SignedHeaders.Should().Be("x-multi");
    }

    #endregion

    #region Static Helpers and General Functionality

    [TestMethod]
    public void UriEncode_NullOrEmpty_ReturnsEmptyString()
    {
        AwsSignatureV4.UriEncode("").Should().Be(string.Empty);
        AwsSignatureV4.UriEncode(null!).Should().Be(string.Empty);
    }

    [TestMethod]
    public void UriEncode_UnreservedCharacters_Preserved()
    {
        string unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~";
        AwsSignatureV4.UriEncode(unreserved).Should().Be(unreserved);
    }

    [TestMethod]
    public void UriEncode_SlashEncoding_ControlledByFlag()
    {
        AwsSignatureV4.UriEncode("a/b", encodeSlash: true).Should().Be("a%2Fb");
        AwsSignatureV4.UriEncode("a/b", encodeSlash: false).Should().Be("a/b");
    }

    [TestMethod]
    public void DeriveSigningKey_ProducesValidHmacKey()
    {
        byte[] key = AwsSignatureV4.DeriveSigningKey(ValidSecretKey, "20261119", "us-east-1", "s3");
        key.Should().NotBeNull();
        key.Length.Should().Be(32);
    }

    [TestMethod]
    public void AuthorizationHeader_ConstructedProperly()
    {
        using var sig = new AwsSignatureV4(
            ValidTimestamp,
            "GET",
            "http://s3.amazonaws.com/testbucket/testkey",
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>
            {
                { "host", "s3.amazonaws.com" },
                { "x-amz-date", ValidTimestamp }
            },
            new[] { "host", "x-amz-date" });

        sig.AuthorizationHeader.Should().StartWith("AWS4-HMAC-SHA256 Credential=");
        sig.AuthorizationHeader.Should().Contain("SignedHeaders=host;x-amz-date");
        sig.AuthorizationHeader.Should().Contain($"Signature={sig.Signature}");
    }

    [TestMethod]
    public void Dispose_CanBeCalledWithoutException()
    {
        var sig = new AwsSignatureV4(
            ValidTimestamp,
            ValidMethod,
            ValidUrl,
            ValidAccessKey,
            ValidSecretKey,
            "us-east-1",
            "s3",
            new Dictionary<string, string>());

        Action act = () => sig.Dispose();
        act.Should().NotThrow();
    }

    #endregion
}
