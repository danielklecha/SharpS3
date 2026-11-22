namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Models;
using SharpS3.Protocol;

[TestClass]
public class S3RequestTests
{
    private static S3Request CreateRequest(
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
    public void Constructor_NullMethod_ThrowsArgumentNullException()
    {
        Action act = () => new S3Request(null!, "/", null!, null!, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void S3Request_PropertyDefaults_And_HelperMethods()
    {
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prefix"] = "pre/",
            ["max-keys"] = "50",
            ["key1"] = "val1"
        };
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "localhost:8080" },
            ["custom-hdr"] = new[] { "valA", "valB" }
        };

        var req = CreateRequest("GET", "/mybucket/file.txt", query, headers);

        req.HttpMethod.Should().Be("GET");
        req.RawPath.Should().Be("/mybucket/file.txt");
        req.Bucket.Should().Be("mybucket");
        req.Key.Should().Be("file.txt");
        req.Prefix.Should().Be("pre/");
        req.MaxKeys.Should().Be(50);

        req.QuerystringExists("prefix").Should().BeTrue();
        req.QuerystringExists("missing").Should().BeFalse();
        req.QuerystringExistsAny("missing", "key1").Should().BeTrue();
        req.QuerystringExistsAny("missing1", "missing2").Should().BeFalse();

        req.RetrieveQueryValue("key1").Should().Be("val1");
        req.RetrieveQueryValue("missing").Should().BeNull();
        req.RetrieveFirstQueryValue("nope", "key1").Should().Be("val1");
        req.RetrieveFirstQueryValue("nope1", "nope2").Should().BeNull();

        req.TryRetrieveQueryInt(out int parsedInt, "max-keys").Should().BeTrue();
        parsedInt.Should().Be(50);
        req.TryRetrieveQueryInt(out int notAnInt, "key1").Should().BeFalse();
        notAnInt.Should().Be(0);

        req.HeaderExists("custom-hdr").Should().BeTrue();
        req.HeaderExists("missing-hdr").Should().BeFalse();
        req.RetrieveHeaderValue("custom-hdr").Should().Be("valA");
        req.RetrieveHeaderValue("missing-hdr").Should().BeNull();
        req.RetrieveHeaderValueIfExists("custom-hdr").Should().Be("valA");

        req.Metadata = "some-metadata";
        req.Metadata.Should().Be("some-metadata");
    }

    [TestMethod]
    public void RouteSuffixRangeAsObjectRead_Transitions_Correctly()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["range"] = new[] { "bytes=-500" }
        };
        var req = CreateRequest("GET", "/b/k", headers: headers);
        req.RequestType.Should().Be(S3RequestType.ObjectReadRange);
        req.RangeStart.Should().BeNull();
        req.RangeSuffixLength.Should().Be(500);

        req.RouteSuffixRangeAsObjectRead();
        req.RequestType.Should().Be(S3RequestType.ObjectRead);

        // If RangeStart is set, RouteSuffixRangeAsObjectRead has no effect
        var headers2 = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["range"] = new[] { "bytes=0-500" }
        };
        var req2 = CreateRequest("GET", "/b/k", headers: headers2);
        req2.RequestType.Should().Be(S3RequestType.ObjectReadRange);
        req2.RouteSuffixRangeAsObjectRead();
        req2.RequestType.Should().Be(S3RequestType.ObjectReadRange);
    }

    [TestMethod]
    public void TryParseRangeHeader_VariousFormats()
    {
        // Invalid inputs
        S3Request.TryParseRangeHeader(null!, out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("notbytes=0-10", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=0-10,20-30", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=0", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=0-1-2", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=-", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=abc-", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=-abc", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=100-50", out _, out _, out _).Should().BeFalse();
        S3Request.TryParseRangeHeader("bytes=10-abc", out _, out _, out _).Should().BeFalse();

        // Valid start-end
        S3Request.TryParseRangeHeader("bytes=0-499", out var s1, out var e1, out var suf1).Should().BeTrue();
        s1.Should().Be(0);
        e1.Should().Be(499);
        suf1.Should().BeNull();

        // Valid start only
        S3Request.TryParseRangeHeader("bytes=500-", out var s2, out var e2, out var suf2).Should().BeTrue();
        s2.Should().Be(500);
        e2.Should().BeNull();
        suf2.Should().BeNull();

        // Valid suffix only
        S3Request.TryParseRangeHeader("bytes=-1000", out var s3, out var e3, out var suf3).Should().BeTrue();
        s3.Should().BeNull();
        e3.Should().BeNull();
        suf3.Should().Be(1000);
    }

    [TestMethod]
    public void ConditionalHeaderParser_ParseEntityTagList_And_HttpDate()
    {
        ConditionalHeaderParser.ParseEntityTagList(null).Should().BeNull();
        ConditionalHeaderParser.ParseEntityTagList("").Should().BeNull();
        ConditionalHeaderParser.ParseEntityTagList("   ").Should().BeNull();

        var tags = ConditionalHeaderParser.ParseEntityTagList("\"etag1\", \"etag2\", W/\"weak\", *");
        tags.Should().NotBeNull();
        tags!.Count.Should().Be(4);
        tags[0].Should().Be("\"etag1\"");
        tags[1].Should().Be("\"etag2\"");
        tags[2].Should().Be("W/\"weak\"");
        tags[3].Should().Be("*");

        var tagsWithComma = ConditionalHeaderParser.ParseEntityTagList("\"etag,with,comma\", \"plain\"");
        tagsWithComma!.Count.Should().Be(2);
        tagsWithComma[0].Should().Be("\"etag,with,comma\"");
        tagsWithComma[1].Should().Be("\"plain\"");

        // HttpDate
        ConditionalHeaderParser.ParseHttpDate(null).Should().BeNull();
        ConditionalHeaderParser.ParseHttpDate("").Should().BeNull();
        ConditionalHeaderParser.ParseHttpDate("   ").Should().BeNull();
        ConditionalHeaderParser.ParseHttpDate("not a date").Should().BeNull();

        var rfc1123 = ConditionalHeaderParser.ParseHttpDate("Sun, 06 Nov 1994 08:49:37 GMT");
        rfc1123.Should().NotBeNull();
        rfc1123!.Value.Year.Should().Be(1994);
        rfc1123.Value.Month.Should().Be(11);
        rfc1123.Value.Day.Should().Be(6);

        var rfc850 = ConditionalHeaderParser.ParseHttpDate("Sunday, 06-Nov-94 08:49:37 GMT");
        rfc850.Should().NotBeNull();
        rfc850!.Value.Year.Should().Be(1994);

        var asctime = ConditionalHeaderParser.ParseHttpDate("Sun Nov  6 08:49:37 1994");
        asctime.Should().NotBeNull();
        asctime!.Value.Year.Should().Be(1994);

        var asctime2 = ConditionalHeaderParser.ParseHttpDate("Sun Nov 06 08:49:37 1994");
        asctime2.Should().NotBeNull();
        asctime2!.Value.Year.Should().Be(1994);
    }

    [TestMethod]
    public void ConditionalHeaderParser_ParseEntityTagList_EmptyAndSeparatorsOnly_ReturnsNull()
    {
        // Only commas and/or whitespace should result in no tags and return null
        ConditionalHeaderParser.ParseEntityTagList(",").Should().BeNull();
        ConditionalHeaderParser.ParseEntityTagList(",,").Should().BeNull();
        ConditionalHeaderParser.ParseEntityTagList(" , ,").Should().BeNull();
        ConditionalHeaderParser.ParseEntityTagList("  ,  ,  ,  ").Should().BeNull();

        // Empty items surrounded by commas should be filtered out while preserving valid tags
        var tagsWithEmptySlots = ConditionalHeaderParser.ParseEntityTagList(", \"etag1\", , \"etag2\", ");
        tagsWithEmptySlots.Should().NotBeNull();
        tagsWithEmptySlots!.Count.Should().Be(2);
        tagsWithEmptySlots[0].Should().Be("\"etag1\"");
        tagsWithEmptySlots[1].Should().Be("\"etag2\"");

        var singleWithSeparators = ConditionalHeaderParser.ParseEntityTagList(",,,*,,,");
        singleWithSeparators.Should().NotBeNull();
        singleWithSeparators!.Count.Should().Be(1);
        singleWithSeparators[0].Should().Be("*");
    }

    [TestMethod]
    public void S3Request_AuthorizationHeaders_SigV2_And_SigV4()
    {
        // SigV2 header
        var headersV2 = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "AWS AKIAIOSFODNN7EXAMPLE:q61qOm55DONiGlmgZvQtAQEgPl7=" }
        };
        var reqV2 = CreateRequest("GET", "/bucket/obj", headers: headersV2);
        reqV2.SignatureVersion.Should().Be(S3SignatureVersion.Version2);
        reqV2.AccessKey.Should().Be("AKIAIOSFODNN7EXAMPLE");
        reqV2.Signature.Should().Be("q61qOm55DONiGlmgZvQtAQEgPl7=");

        // SigV4 header
        var headersV4 = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;x-amz-date, Signature=fe5f80f779d70c27e082dfb1f348aece4f47d4d18be959f6e4256f142261ab03" }
        };
        var reqV4 = CreateRequest("GET", "/bucket/obj", headers: headersV4);
        reqV4.SignatureVersion.Should().Be(S3SignatureVersion.Version4);
        reqV4.AccessKey.Should().Be("AKIAIOSFODNN7EXAMPLE");
        reqV4.Region.Should().Be("us-east-1");
        reqV4.SignedHeaders.Should().Contain(new[] { "host", "x-amz-date" });
        reqV4.Signature.Should().Be("fe5f80f779d70c27e082dfb1f348aece4f47d4d18be959f6e4256f142261ab03");

        // Query param auth
        var queryAuth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AWSAccessKeyId"] = "MYKEY",
            ["Expires"] = "1234567890",
            ["Signature"] = "MYSIG"
        };
        var reqQuery = CreateRequest("GET", "/bucket/obj", query: queryAuth);
        reqQuery.SignatureVersion.Should().Be(S3SignatureVersion.Version2);
        reqQuery.AccessKey.Should().Be("MYKEY");
        reqQuery.Signature.Should().Be("MYSIG");
        reqQuery.Expires.Should().Be("1234567890");
    }

    [TestMethod]
    public void S3Request_CopySource_And_Conditional_Headers()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "/src-bucket/src-key.txt?versionId=v42" },
            ["x-amz-copy-source-range"] = new[] { "bytes=100-200" },
            ["if-match"] = new[] { "\"etag1\"" },
            ["if-none-match"] = new[] { "\"etag2\"" },
            ["if-modified-since"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" },
            ["if-unmodified-since"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" },
            ["x-amz-copy-source-if-match"] = new[] { "\"etag3\"" },
            ["x-amz-copy-source-if-none-match"] = new[] { "\"etag4\"" },
            ["x-amz-copy-source-if-modified-since"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" },
            ["x-amz-copy-source-if-unmodified-since"] = new[] { "Sun, 06 Nov 1994 08:49:37 GMT" }
        };

        var req = CreateRequest("PUT", "/dest-bucket/dest-key.txt", headers: headers);
        req.RequestType.Should().Be(S3RequestType.ObjectCopy);
        req.CopySourceBucket.Should().Be("src-bucket");
        req.CopySourceKey.Should().Be("src-key.txt");
        req.CopySourceVersionId.Should().Be("v42");
        req.CopySourceRangeStart.Should().Be(100);
        req.CopySourceRangeEnd.Should().Be(200);

        req.IfMatch.Should().ContainSingle().Which.Should().Be("\"etag1\"");
        req.IfNoneMatch.Should().ContainSingle().Which.Should().Be("\"etag2\"");
        req.IfModifiedSince.Should().NotBeNull();
        req.IfUnmodifiedSince.Should().NotBeNull();
        req.CopySourceIfMatch.Should().ContainSingle().Which.Should().Be("\"etag3\"");
        req.CopySourceIfNoneMatch.Should().ContainSingle().Which.Should().Be("\"etag4\"");
        req.CopySourceIfModifiedSince.Should().NotBeNull();
        req.CopySourceIfUnmodifiedSince.Should().NotBeNull();

        // Copy source without key
        var headersNoKey = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "justbucket" }
        };
        var reqNoKey = CreateRequest("PUT", "/dest/key", headers: headersNoKey);
        reqNoKey.CopySourceBucket.Should().Be("justbucket");
        reqNoKey.CopySourceKey.Should().BeNull();
    }

    [TestMethod]
    public void S3Request_AwsChunkedStreaming_And_DecodedLength()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-content-sha256"] = new[] { "STREAMING-AWS4-HMAC-SHA256-PAYLOAD" },
            ["x-amz-decoded-content-length"] = new[] { "12345" }
        };
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("0\r\n\r\n"));
        var req = CreateRequest("PUT", "/bucket/obj", headers: headers, body: body);

        req.Chunked.Should().BeTrue();
        req.DecodedContentLength.Should().Be(12345);
        req.BodyStream.Should().BeOfType<SharpS3.Streams.AwsChunkedDecodingStream>();
    }

    [TestMethod]
    public void S3Request_VirtualHosted_And_PathStyle_Routing()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains.Add("s3.example.com");

        // Virtual hosted style: mybucket.s3.example.com
        var headersVh = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "mybucket.s3.example.com:443" }
        };
        var reqVh = CreateRequest("GET", "/images/pic.png", headers: headersVh, settings: settings);
        reqVh.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        reqVh.Bucket.Should().Be("mybucket");
        reqVh.Key.Should().Be("images/pic.png");

        // Virtual hosted style root: mybucket.s3.example.com/
        var reqVhRoot = CreateRequest("GET", "/", headers: headersVh, settings: settings);
        reqVhRoot.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        reqVhRoot.Bucket.Should().Be("mybucket");
        reqVhRoot.Key.Should().BeNull();
        reqVhRoot.RequestType.Should().Be(S3RequestType.BucketRead);

        // Path style: s3.example.com/mybucket/images/pic.png
        var headersPath = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "s3.example.com" }
        };
        var reqPath = CreateRequest("GET", "/mybucket/images/pic.png", headers: headersPath, settings: settings);
        reqPath.RequestStyle.Should().Be(S3RequestStyle.PathStyle);
        reqPath.Bucket.Should().Be("mybucket");
        reqPath.Key.Should().Be("images/pic.png");

        // IP address host is always PathStyle
        var headersIp = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "127.0.0.1:9000" }
        };
        var reqIp = CreateRequest("GET", "/mybucket", headers: headersIp, settings: settings);
        reqIp.RequestStyle.Should().Be(S3RequestStyle.PathStyle);
        reqIp.Bucket.Should().Be("mybucket");
        reqIp.Key.Should().BeNull();

        // Custom FindMatchingBaseDomain delegate
        settings.FindMatchingBaseDomain = host => host.EndsWith(".custom.local") ? "custom.local" : null;
        var headersCustom = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { "b.custom.local" }
        };
        var reqCustom = CreateRequest("GET", "/file.txt", headers: headersCustom, settings: settings);
        reqCustom.RequestStyle.Should().Be(S3RequestStyle.VirtualHostedStyle);
        reqCustom.Bucket.Should().Be("b");
    }

    [TestMethod]
    public void S3Request_AllRequestTypes_Classification()
    {
        // HEAD
        CreateRequest("HEAD", "/").RequestType.Should().Be(S3RequestType.ServiceExists);
        CreateRequest("HEAD", "/b").RequestType.Should().Be(S3RequestType.BucketExists);
        CreateRequest("HEAD", "/b/k").RequestType.Should().Be(S3RequestType.ObjectExists);

        // GET Service & Bucket
        CreateRequest("GET", "/").RequestType.Should().Be(S3RequestType.ListBuckets);
        CreateRequest("GET", "/b", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadAcl);
        CreateRequest("GET", "/b", new() { ["location"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadLocation);
        CreateRequest("GET", "/b", new() { ["logging"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadLogging);
        CreateRequest("GET", "/b", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadTags);
        CreateRequest("GET", "/b", new() { ["uploads"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadMultipartUploads);
        CreateRequest("GET", "/b", new() { ["versions"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadVersions);
        CreateRequest("GET", "/b", new() { ["versioning"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadVersioning);
        CreateRequest("GET", "/b", new() { ["website"] = "" }).RequestType.Should().Be(S3RequestType.BucketReadWebsite);
        CreateRequest("GET", "/b").RequestType.Should().Be(S3RequestType.BucketRead);

        // GET Object
        CreateRequest("GET", "/b/k", headers: new() { ["range"] = new[] { "bytes=0-10" } }).RequestType.Should().Be(S3RequestType.ObjectReadRange);
        CreateRequest("GET", "/b/k", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.ObjectReadAcl);
        CreateRequest("GET", "/b/k", new() { ["legal-hold"] = "" }).RequestType.Should().Be(S3RequestType.ObjectReadLegalHold);
        CreateRequest("GET", "/b/k", new() { ["uploadId"] = "123" }).RequestType.Should().Be(S3RequestType.ObjectReadParts);
        CreateRequest("GET", "/b/k", new() { ["retention"] = "" }).RequestType.Should().Be(S3RequestType.ObjectReadRetention);
        CreateRequest("GET", "/b/k", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.ObjectReadTags);
        CreateRequest("GET", "/b/k").RequestType.Should().Be(S3RequestType.ObjectRead);

        // PUT Bucket & Object
        CreateRequest("PUT", "/b", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.BucketWriteAcl);
        CreateRequest("PUT", "/b", new() { ["logging"] = "" }).RequestType.Should().Be(S3RequestType.BucketWriteLogging);
        CreateRequest("PUT", "/b", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.BucketWriteTags);
        CreateRequest("PUT", "/b", new() { ["versioning"] = "" }).RequestType.Should().Be(S3RequestType.BucketWriteVersioning);
        CreateRequest("PUT", "/b", new() { ["website"] = "" }).RequestType.Should().Be(S3RequestType.BucketWriteWebsite);
        CreateRequest("PUT", "/b").RequestType.Should().Be(S3RequestType.BucketWrite);

        CreateRequest("PUT", "/b/k", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.ObjectWriteTags);
        CreateRequest("PUT", "/b/k", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.ObjectWriteAcl);
        CreateRequest("PUT", "/b/k", new() { ["legal-hold"] = "" }).RequestType.Should().Be(S3RequestType.ObjectWriteLegalHold);
        CreateRequest("PUT", "/b/k", new() { ["retention"] = "" }).RequestType.Should().Be(S3RequestType.ObjectWriteRetention);
        CreateRequest("PUT", "/b/k", new() { ["partNumber"] = "1", ["uploadId"] = "u1" }).RequestType.Should().Be(S3RequestType.ObjectUploadPart);
        CreateRequest("PUT", "/b/k", new() { ["partNumber"] = "1", ["uploadId"] = "u1" }, headers: new() { ["x-amz-copy-source"] = new[] { "/src/k" } }).RequestType.Should().Be(S3RequestType.ObjectUploadPartCopy);
        CreateRequest("PUT", "/b/k", headers: new() { ["x-amz-copy-source"] = new[] { "/src/k" } }).RequestType.Should().Be(S3RequestType.ObjectCopy);
        CreateRequest("PUT", "/b/k").RequestType.Should().Be(S3RequestType.ObjectWrite);

        // POST
        CreateRequest("POST", "/b", new() { ["delete"] = "" }).RequestType.Should().Be(S3RequestType.ObjectDeleteMultiple);
        CreateRequest("POST", "/b/k", new() { ["restore"] = "" }).RequestType.Should().Be(S3RequestType.ObjectRestore);
        CreateRequest("POST", "/b/k", new() { ["select"] = "", ["select-type"] = "2" }).RequestType.Should().Be(S3RequestType.ObjectSelectContent);
        CreateRequest("POST", "/b/k", new() { ["uploadId"] = "u1" }).RequestType.Should().Be(S3RequestType.ObjectCompleteMultipartUpload);
        CreateRequest("POST", "/b/k", new() { ["uploads"] = "" }).RequestType.Should().Be(S3RequestType.ObjectCreateMultipartUpload);

        // DELETE
        CreateRequest("DELETE", "/").RequestType.Should().Be(S3RequestType.Unknown);
        CreateRequest("DELETE", "/b", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.BucketDeleteAcl);
        CreateRequest("DELETE", "/b", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.BucketDeleteTags);
        CreateRequest("DELETE", "/b", new() { ["website"] = "" }).RequestType.Should().Be(S3RequestType.BucketDeleteWebsite);
        CreateRequest("DELETE", "/b").RequestType.Should().Be(S3RequestType.BucketDelete);

        CreateRequest("DELETE", "/b/k", new() { ["acl"] = "" }).RequestType.Should().Be(S3RequestType.ObjectDeleteAcl);
        CreateRequest("DELETE", "/b/k", new() { ["tagging"] = "" }).RequestType.Should().Be(S3RequestType.ObjectDeleteTags);
        CreateRequest("DELETE", "/b/k", new() { ["uploadId"] = "u1" }).RequestType.Should().Be(S3RequestType.ObjectAbortMultipartUpload);
        CreateRequest("DELETE", "/b/k").RequestType.Should().Be(S3RequestType.ObjectDelete);
    }

    [TestMethod]
    public void S3Request_Constructor_NullPathAndBodyStream_DefaultsProperly()
    {
        var req = new S3Request("GET", null!, null!, null!, null!);
        req.RawPath.Should().Be("/");
        req.BodyStream.Should().BeSameAs(Stream.Null);
        req.QueryParameters.Should().BeEmpty();
        req.Headers.Should().BeEmpty();
    }

    [TestMethod]
    public void S3Request_EmptyBucketWithKey_ClassifiesAsUnknown()
    {
        var settings = new SharpS3Settings();
        settings.BaseDomains.Add("s3.example.com");
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = new[] { ".s3.example.com" }
        };

        var reqHead = CreateRequest("HEAD", "/mykey", headers: headers, settings: settings);
        reqHead.Bucket.Should().Be(string.Empty);
        reqHead.Key.Should().Be("mykey");
        reqHead.RequestType.Should().Be(S3RequestType.Unknown);

        var reqGet = CreateRequest("GET", "/mykey", headers: headers, settings: settings);
        reqGet.Bucket.Should().Be(string.Empty);
        reqGet.Key.Should().Be("mykey");
        reqGet.RequestType.Should().Be(S3RequestType.Unknown);
    }

    [TestMethod]
    public void S3Request_ContentEncodingChunked_And_AdditionalQueryInts()
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-encoding"] = new[] { "aws-chunked" }
        };
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["max-parts"] = "10",
            ["part-number-marker"] = "5",
            ["max-uploads"] = "20"
        };
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("0\r\n\r\n"));
        var req = CreateRequest("POST", "/bucket", query: query, headers: headers, body: body);
        req.Chunked.Should().BeTrue();
        req.MaxParts.Should().Be(10);
        req.PartNumberMarker.Should().Be(5);
        req.MaxUploads.Should().Be(20);
    }
}
