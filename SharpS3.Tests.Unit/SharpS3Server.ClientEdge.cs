namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;

public partial class SharpS3ServerTests
{
    [TestMethod]
    public async Task SharpS3Server_ReceiveRequest_Convenience_Methods()
    {
        var server = new SharpS3Server();

        var getReq = await server.ReceiveGetRequestAsync("/b/k");
        getReq.HttpMethod.Should().Be("GET");
        getReq.Bucket.Should().Be("b");
        getReq.Key.Should().Be("k");

        var headReq = await server.ReceiveHeadRequestAsync("/b/k");
        headReq.HttpMethod.Should().Be("HEAD");

        using var putStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var putReq = await server.ReceivePutRequestAsync("/b/k", putStream);
        putReq.HttpMethod.Should().Be("PUT");

        using var postStream = new MemoryStream(new byte[] { 4, 5, 6 });
        var postReq = await server.ReceivePostRequestAsync("/b/k", postStream);
        postReq.HttpMethod.Should().Be("POST");

        var delReq = await server.ReceiveDeleteRequestAsync("/b/k");
        delReq.HttpMethod.Should().Be("DELETE");
    }

    [TestMethod]
    public async Task SharpS3Server_ReceiveRequest_ValidationError_And_SuffixRouting()
    {
        // 1. ValidationError in request (e.g. invalid copy source range)
        var server = new SharpS3Server();
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-amz-copy-source"] = new[] { "/src/k" },
            ["x-amz-copy-source-range"] = new[] { "bytes=100-50" } // invalid range (start > end)
        };

        var act = async () => await server.ReceiveRequestAsync("PUT", "/dest/k", headers: headers);
        await act.Should().ThrowAsync<S3Exception>();

        // 2. RouteSuffixRangesToReadRange = false
        var settingsNoSuffix = new SharpS3Settings { RouteSuffixRangesToReadRange = false };
        var serverNoSuffix = new SharpS3Server(settingsNoSuffix);
        var suffixHeaders = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["range"] = new[] { "bytes=-200" }
        };
        var req = await serverNoSuffix.ReceiveRequestAsync("GET", "/b/k", headers: suffixHeaders);
        req.RequestType.Should().Be(S3RequestType.ObjectRead);
    }

    [TestMethod]
    public async Task SharpS3Server_CreateResponseAsync_And_CreateErrorResponse()
    {
        var server = new SharpS3Server();

        // CreateResponseAsync
        var dummy = new S3ServerResponse
        {
            StatusCode = 201,
            ContentType = "application/json",
            BodyStream = new MemoryStream(Encoding.UTF8.GetBytes("{}"))
        };
        dummy.Headers["x-custom"] = "value";

        var created = await server.CreateResponseAsync(dummy);
        created.StatusCode.Should().Be(201);
        created.ContentType.Should().Be("application/json");
        created.Headers["x-custom"].Should().Be("value");
        created.BodyStream.Should().BeSameAs(dummy.BodyStream);

        // CreateErrorResponse with null exception
        var errRespNull = server.CreateErrorResponse(null!);
        errRespNull.StatusCode.Should().Be(500);
        errRespNull.ContentType.Should().Be("application/xml");

        // CreateErrorResponse with S3Exception and requestId
        var ex = new S3Exception(new Error(ErrorCode.NoSuchBucket, "Bucket missing", "b"));
        var errRespWithId = server.CreateErrorResponse(ex, "req-999");
        errRespWithId.StatusCode.Should().Be(404);
        errRespWithId.Headers["x-amz-request-id"].Should().Be("req-999");
        errRespWithId.Headers["x-amz-id-2"].Should().Be("req-999");

        using var reader = new StreamReader(errRespWithId.BodyStream);
        string errXml = await reader.ReadToEndAsync();
        errXml.Should().Contain("<Code>NoSuchBucket</Code>");
        errXml.Should().Contain("<RequestId>req-999</RequestId>");
    }

    [TestMethod]
    public void SharpS3Client_Options_And_Disposal()
    {
        // Null options throws
        Action actNullOpt = () => new SharpS3Client(null!);
        actNullOpt.Should().Throw<ArgumentNullException>();

        // Default internal HttpClient disposal
        var options = new SharpS3ClientOptions { Endpoint = new Uri("http://localhost:9000") };
        var client1 = new SharpS3Client(options);
        client1.Dispose();
        client1.Dispose(); // idempotent

        // External HttpClient is NOT disposed
        var externalHttp = new HttpClient();
        var client2 = new SharpS3Client(options, externalHttp);
        client2.Dispose();
        // externalHttp can still be used without ObjectDisposedException
        externalHttp.Timeout.Should().Be(TimeSpan.FromSeconds(100));
        externalHttp.Dispose();
    }

    [TestMethod]
    public void SharpS3ClientException_Properties_And_Constructors()
    {
        var inner = new InvalidOperationException("boom");
        var ex1 = new SharpS3ClientException(HttpStatusCode.NotFound, "Not found", inner);
        ex1.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex1.Message.Should().Be("Not found");
        ex1.InnerException.Should().BeSameAs(inner);

        var err = new Error(ErrorCode.BucketAlreadyExists)
        {
            Resource = "my-bucket",
            RequestId = "req-123"
        };
        var ex2 = new SharpS3ClientException(HttpStatusCode.Conflict, err);
        ex2.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ex2.ErrorCode.Should().Be("BucketAlreadyExists");
        ex2.S3ErrorMessage.Should().Contain("The requested bucket name is not available");
        ex2.Resource.Should().Be("my-bucket");
        ex2.RequestId.Should().Be("req-123");
    }

    [TestMethod]
    public void SharpS3Client_ArgumentValidations()
    {
        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:9000"),
            AccessKey = "key",
            SecretKey = "secret"
        };
        var client = new SharpS3Client(options);

        // Bucket operations
        client.Invoking(c => c.BucketExistsAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.CreateBucketAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.DeleteBucketAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.GetBucketAclAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.SetBucketAclAsync(null!, new AccessControlPolicy())).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.SetBucketAclAsync("b", null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.GetBucketLocationAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.GetBucketLoggingAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.SetBucketLoggingAsync(null!, new BucketLoggingStatus())).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.SetBucketLoggingAsync("b", null!)).Should().ThrowAsync<ArgumentNullException>();

        // Object operations
        client.Invoking(c => c.GetObjectAsync(null!, "k")).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.GetObjectAsync("b", null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.PutObjectAsync(null!, "k", new byte[1])).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.PutObjectAsync("b", null!, new byte[1])).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.PutObjectAsync("b", "k", (byte[])null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.PutObjectAsync("b", "k", (string)null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.PutObjectAsync("b", "k", (Stream)null!, 0)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.DeleteObjectAsync(null!, "k")).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.DeleteObjectAsync("b", null!)).Should().ThrowAsync<ArgumentNullException>();

        // Multipart operations
        client.Invoking(c => c.InitiateMultipartUploadAsync(null!, "k")).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.InitiateMultipartUploadAsync("b", null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.UploadPartAsync(null!, "k", "u", 1, new byte[1])).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.UploadPartAsync("b", null!, "u", 1, new byte[1])).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.UploadPartAsync("b", "k", null!, 1, new byte[1])).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.UploadPartAsync("b", "k", "u", 1, (byte[])null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.CompleteMultipartUploadAsync(null!, "k", "u", new CompleteMultipartUpload())).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.CompleteMultipartUploadAsync("b", null!, "u", new CompleteMultipartUpload())).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.CompleteMultipartUploadAsync("b", "k", null!, new CompleteMultipartUpload())).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.CompleteMultipartUploadAsync("b", "k", "u", null!)).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.AbortMultipartUploadAsync(null!, "k", "u")).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.AbortMultipartUploadAsync("b", null!, "u")).Should().ThrowAsync<ArgumentNullException>();
        client.Invoking(c => c.AbortMultipartUploadAsync("b", "k", null!)).Should().ThrowAsync<ArgumentNullException>();

        // UploadFile non-existent
        client.Invoking(c => c.UploadFileAsync("b", "k", "non-existent-file-xyz.txt")).Should().ThrowAsync<FileNotFoundException>();

        // GeneratePresignedUrl
        client.Invoking(c => c.GeneratePresignedUrl(null!, "k", HttpMethod.Get, TimeSpan.FromHours(1))).Should().Throw<ArgumentNullException>();
        client.Invoking(c => c.GeneratePresignedUrl("b", "k", HttpMethod.Get, TimeSpan.Zero)).Should().Throw<ArgumentOutOfRangeException>();
        client.Invoking(c => c.GeneratePresignedUrl("b", "k", HttpMethod.Get, TimeSpan.FromDays(8))).Should().Throw<ArgumentOutOfRangeException>();

        // GeneratePresignedUrl with credentials returns signature query
        var presignedUrl = client.GeneratePresignedUrl("b", "mykey.txt", HttpMethod.Get, TimeSpan.FromHours(1));
        presignedUrl.Query.Should().Contain("X-Amz-Signature=");
        presignedUrl.Query.Should().Contain("X-Amz-Credential=");

        // GeneratePresignedUrl without credentials returns plain URL
        var unauthClient = new SharpS3Client(new SharpS3ClientOptions { Endpoint = new Uri("http://localhost:9000") });
        var plainUrl = unauthClient.GeneratePresignedUrl("b", "mykey.txt", HttpMethod.Get, TimeSpan.FromHours(1));
        plainUrl.Query.Should().BeEmpty();
    }

    [TestMethod]
    public async Task SharpS3Server_ReceiveRequest_ParseStage_Catch_WhenExceptionThrown()
    {
        var server = new SharpS3Server();
        var act = async () => await server.ReceiveRequestAsync(null!, "/b/k");
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [TestMethod]
    public async Task SharpS3Server_ReceiveRequest_SignatureValidationStage_Catch_WhenExceptionThrown()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            ValidateSignaturesBeforePreRequestHandler = true,
            ValidateTimestampSkew = false
        };
        settings.Credentials["key"] = "secret";
        var server = new SharpS3Server(settings);

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=key/20261117/us-east-1/s3/aws4_request, SignedHeaders=host, Signature=invalid" },
            ["x-amz-date"] = new[] { "20261117T000000Z" }
        };

        var act = async () => await server.ReceiveRequestAsync("GET", "/b/k", headers: headers);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task SharpS3Server_ValidateSignatureAsync_UnsupportedSignatureVersion_ThrowsAccessDenied()
    {
        var settings = new SharpS3Settings { EnableSignatures = true };
        settings.Credentials["validkey"] = "validsecret";

        // 1. Direct call to ValidateSignatureAsync with S3SignatureVersion.Unknown
        var req = new S3Request("GET", "/b/k", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null, settings)
        {
            AccessKey = "validkey",
            Signature = "somedummysig",
            SignatureVersion = S3SignatureVersion.Unknown
        };

        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rt = new RequestTelemetry(telemetry, false);

        var act = async () => await SharpS3Server.ValidateSignatureAsync(req, settings, rt, CancellationToken.None);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

        rt.SignatureOutcome.Should().Be(S3ServerTelemetryNames.OutcomeUnsupported);

        // 2. ReceiveRequestAsync with query params lacking expires -> SignatureVersion stays Unknown
        var server = new SharpS3Server(settings);
        var queryParams = new Dictionary<string, string>
        {
            ["AWSAccessKeyId"] = "validkey",
            ["Signature"] = "somedummysig"
        };
        var actReceive = async () => await server.ReceiveRequestAsync("GET", "/b/k", queryParameters: queryParams);
        (await actReceive.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
    }
}
