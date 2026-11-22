namespace SharpS3.Tests.Unit;

#pragma warning disable CS8602
#pragma warning disable CS8604

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Storage;
using SharpS3.Streams;

[TestClass]
public class StreamsReleaseReadinessTests
{
    

    private static (SharpS3Client client, MockHttpMessageHandler mockHandler) CreateMockClient()
    {
        var mockHandler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(mockHandler);
        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            ForcePathStyle = true,
            AccessKey = "test-key",
            SecretKey = "test-secret"
        };
        var client = new SharpS3Client(options, httpClient);
        return (client, mockHandler);
    }

    [TestMethod]
    public void ETagHelper_ComputeMultipartETag_And_HexToBytes()
    {
        // HexToBytes edge cases
        ETagHelper.HexToBytes(null).Should().BeEmpty();
        ETagHelper.HexToBytes("").Should().BeEmpty();
        ETagHelper.HexToBytes("abc").Should().BeEmpty(); // not 32 chars
        ETagHelper.HexToBytes("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz").Should().BeEmpty(); // invalid hex chars

        byte[] original = new byte[16] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };
        string hex = BitConverter.ToString(original).Replace("-", "").ToLowerInvariant();
        byte[] parsed = ETagHelper.HexToBytes(hex);
        parsed.Should().Equal(original);

        // Quoted hex
        byte[] parsedQuoted = ETagHelper.HexToBytes("\"" + hex + "\"");
        parsedQuoted.Should().Equal(original);

        // ComputeMultipartETag
        var part1 = original;
        var part2 = (byte[])original.Clone();
        part2[0] = 0xFF;

        string composite = ETagHelper.ComputeMultipartETag(new[] { part1, part2 });
        composite.Should().StartWith("\"");
        composite.Should().EndWith("-2\"");

        // Manual verification
        byte[] combined = new byte[32];
        Buffer.BlockCopy(part1, 0, combined, 0, 16);
        Buffer.BlockCopy(part2, 0, combined, 16, 16);
        using var md5 = MD5.Create();
        string expectedHex = BitConverter.ToString(md5.ComputeHash(combined)).Replace("-", "").ToLowerInvariant();
        composite.Should().Be($"\"{expectedHex}-2\"");
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_CompleteMultipartUpload_CompositeETag_And_ReadOnlyStream()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("test-bucket", null);

        // Test GetObject returns read-only MemoryStream
        byte[] objBytes = Encoding.UTF8.GetBytes("hello");
        using (var putStream = new MemoryStream(objBytes))
        {
            await backend.PutObjectAsync("test-bucket", "obj.txt", putStream, objBytes.Length, "text/plain");
        }
        var getObj = await backend.GetObjectAsync("test-bucket", "obj.txt");
        getObj.Stream.CanWrite.Should().BeFalse();
        Action writeAttempt = () => getObj.Stream.WriteByte(0xFF);
        writeAttempt.Should().Throw<NotSupportedException>();

        // Test multipart upload composite ETag
        var init = await backend.CreateMultipartUploadAsync("test-bucket", "large.bin");
        byte[] part1Bytes = Encoding.UTF8.GetBytes("part1-data");
        byte[] part2Bytes = Encoding.UTF8.GetBytes("part2-data");

        using var p1 = new MemoryStream(part1Bytes);
        using var p2 = new MemoryStream(part2Bytes);
        string etag1 = await backend.UploadPartAsync("test-bucket", "large.bin", init.UploadId, 1, p1, part1Bytes.Length);
        string etag2 = await backend.UploadPartAsync("test-bucket", "large.bin", init.UploadId, 2, p2, part2Bytes.Length);

        var complete = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = etag1 },
                new Part { PartNumber = 2, ETag = etag2 }
            }
        };

        var compRes = await backend.CompleteMultipartUploadAsync("test-bucket", "large.bin", init.UploadId, complete);
        compRes.ETag.Should().EndWith("-2\"");
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_CompleteMultipartUpload_CompositeETag_And_BucketNotEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SharpS3_Readiness_" + Guid.NewGuid().ToString("N"));
        var backend = new FileSystemStorageBackend(tempDir);
        try
        {
            await backend.CreateBucketAsync("fs-bucket", null);

            var init = await backend.CreateMultipartUploadAsync("fs-bucket", "fs-large.bin");
            byte[] part1Bytes = Encoding.UTF8.GetBytes("part1-data-fs");
            byte[] part2Bytes = Encoding.UTF8.GetBytes("part2-data-fs");

            using var p1 = new MemoryStream(part1Bytes);
            using var p2 = new MemoryStream(part2Bytes);
            string etag1 = await backend.UploadPartAsync("fs-bucket", "fs-large.bin", init.UploadId, 1, p1, part1Bytes.Length);
            string etag2 = await backend.UploadPartAsync("fs-bucket", "fs-large.bin", init.UploadId, 2, p2, part2Bytes.Length);

            var complete = new CompleteMultipartUpload
            {
                Parts = new List<Part>
                {
                    new Part { PartNumber = 1, ETag = etag1 },
                    new Part { PartNumber = 2, ETag = etag2 }
                }
            };

            var compRes = await backend.CompleteMultipartUploadAsync("fs-bucket", "fs-large.bin", init.UploadId, complete);
            compRes.ETag.Should().EndWith("-2\"");

            // Test DeleteBucket when bucket contains a directory marker or subfolder
            string subDir = Path.Combine(tempDir, "fs-bucket", "subfolder");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "file.txt"), "data");

            Func<Task> deleteAction = async () => await backend.DeleteBucketAsync("fs-bucket");
            await deleteAction.Should().ThrowAsync<S3Exception>().Where(e => e.Error.Code == ErrorCode.BucketNotEmpty);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }

    [TestMethod]
    public async Task SharpS3Client_StreamOwnership_PutObject_And_UploadPart()
    {
        var (client, mock) = CreateMockClient();
        mock.Handler = req =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK);
            res.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag123\"");
            return res;
        };

        // PutObject
        var memStream = new MemoryStream(Encoding.UTF8.GetBytes("hello stream ownership"));
        await client.PutObjectAsync("mybucket", "test.txt", memStream, memStream.Length, "text/plain");
        memStream.CanRead.Should().BeTrue("PutObjectAsync should not dispose the caller's stream");

        // UploadPart
        memStream.Position = 0;
        await client.UploadPartAsync("mybucket", "test.txt", "uploadId1", 1, memStream, memStream.Length);
        memStream.CanRead.Should().BeTrue("UploadPartAsync should not dispose the caller's stream");

        memStream.Dispose();
    }

    [TestMethod]
    public async Task SharpS3Client_DownloadFile_CleansUpOnFailure()
    {
        var (client, mock) = CreateMockClient();
        mock.Handler = req => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        string tempFile = Path.Combine(Path.GetTempPath(), "test-download-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Func<Task> action = async () => await client.DownloadFileAsync("mybucket", "test.txt", tempFile);
            await action.Should().ThrowAsync<SharpS3ClientException>();
            File.Exists(tempFile).Should().BeFalse("DownloadFileAsync should delete incomplete target file on failure");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public async Task SharpS3Client_Governance_Operations()
    {
        var (client, mock) = CreateMockClient();

        // 1. GetObjectLegalHold
        mock.Handler = req =>
        {
            req.RequestUri!.Query.Should().Contain("legal-hold");
            string xml = "<LegalHold><Status>ON</Status></LegalHold>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        };
        var legalHold = await client.GetObjectLegalHoldAsync("mybucket", "mykey");
        legalHold.Status.Should().Be("ON");

        // 2. SetObjectLegalHold
        mock.Handler = req =>
        {
            req.RequestUri!.Query.Should().Contain("legal-hold");
            req.Method.Should().Be(HttpMethod.Put);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        await client.SetObjectLegalHoldAsync("mybucket", "mykey", new LegalHold { Status = "OFF" });

        // 3. GetObjectRetention
        mock.Handler = req =>
        {
            req.RequestUri!.Query.Should().Contain("retention");
            string xml = "<Retention><Mode>GOVERNANCE</Mode><RetainUntilDate>2030-01-01T00:00:00Z</RetainUntilDate></Retention>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        };
        var retention = await client.GetObjectRetentionAsync("mybucket", "mykey");
        retention.Mode.Should().Be(RetentionModeEnum.Governance);

        // 4. SetObjectRetention
        mock.Handler = req =>
        {
            req.RequestUri!.Query.Should().Contain("retention");
            req.Method.Should().Be(HttpMethod.Put);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        await client.SetObjectRetentionAsync("mybucket", "mykey", new Retention { Mode = RetentionModeEnum.Compliance });
    }

    [TestMethod]
    public async Task SharpS3Client_MultipartOperations_CopyParts_ListParts_ListUploads()
    {
        var (client, mock) = CreateMockClient();

        // 1. UploadPartCopy
        mock.Handler = req =>
        {
            req.Method.Should().Be(HttpMethod.Put);
            req.RequestUri!.Query.Should().Contain("partNumber=1");
            req.RequestUri!.Query.Should().Contain("uploadId=u123");
            req.Headers.GetValues("x-amz-copy-source").Should().Contain("/source-bucket/source-key");
            req.Headers.GetValues("x-amz-copy-source-range").Should().Contain("bytes=0-100");

            string xml = "<CopyPartResult><ETag>\"etag-copy\"</ETag><LastModified>2026-01-01T00:00:00Z</LastModified></CopyPartResult>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        };

        var copyRes = await client.UploadPartCopyAsync(
            "source-bucket", "source-key",
            "dest-bucket", "dest-key",
            "u123", 1,
            sourceRange: new ByteRange(0, 100));
        copyRes.ETag.Should().Be("\"etag-copy\"");

        // 2. ListParts
        mock.Handler = req =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.Query.Should().Contain("uploadId=u123");
            string xml = "<ListPartsResult><Bucket>dest-bucket</Bucket><Key>dest-key</Key><UploadId>u123</UploadId></ListPartsResult>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        };
        var parts = await client.ListPartsAsync("dest-bucket", "dest-key", "u123", maxParts: 10, partNumberMarker: 0);
        parts.UploadId.Should().Be("u123");

        // 3. ListMultipartUploads
        mock.Handler = req =>
        {
            req.Method.Should().Be(HttpMethod.Get);
            req.RequestUri!.Query.Should().Contain("uploads");
            string xml = "<ListMultipartUploadsResult><Bucket>dest-bucket</Bucket></ListMultipartUploadsResult>";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
        };
        var uploads = await client.ListMultipartUploadsAsync("dest-bucket", prefix: "prefix/", delimiter: "/");
        uploads.Bucket.Should().Be("dest-bucket");
    }

    [TestMethod]
    public void AwsChunkedDecodingStream_SyncRead_And_SpanMemoryRead()
    {
        string payload = "b;chunk-signature=dummy\r\nHello World\r\n0;chunk-signature=dummy\r\n\r\n";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var inner = new MemoryStream(payloadBytes);
        using var chunked = new AwsChunkedDecodingStream(inner);

        // Test synchronous Read(byte[], int, int)
        byte[] buffer = new byte[5];
        int bytesRead = chunked.Read(buffer, 0, 5);
        bytesRead.Should().Be(5);
        Encoding.UTF8.GetString(buffer).Should().Be("Hello");

        // Test Span<byte> Read
        Span<byte> spanBuffer = new byte[6];
        int spanRead = chunked.Read(spanBuffer);
        spanRead.Should().Be(6);
        Encoding.UTF8.GetString(spanBuffer).Should().Be(" World");

        // End of stream
        int eof = chunked.Read(buffer, 0, buffer.Length);
        eof.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_MemoryReadAsync()
    {
        string payload = "5;chunk-signature=dummy\r\nChunk\r\n0;chunk-signature=dummy\r\n\r\n";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var inner = new MemoryStream(payloadBytes);
        using var chunked = new AwsChunkedDecodingStream(inner);

        Memory<byte> memoryBuffer = new byte[10];
        int read = await chunked.ReadAsync(memoryBuffer);
        read.Should().Be(5);
        Encoding.UTF8.GetString(memoryBuffer.Slice(0, 5).Span).Should().Be("Chunk");
    }

    [TestMethod]
    public void Sha256PayloadVerifyingStream_SpanRead()
    {
        byte[] data = Encoding.UTF8.GetBytes("Data to hash synchronously");
        using var sha256 = SHA256.Create();
        string expectedHex = BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", "").ToLowerInvariant();

        using var inner = new MemoryStream(data);
        using var stream = new Sha256PayloadVerifyingStream(inner, expectedHex);

        Span<byte> span = new byte[data.Length];
        int sRead = stream.Read(span);
        sRead.Should().Be(data.Length);
        Encoding.UTF8.GetString(span).Should().Be("Data to hash synchronously");

        // EOF read triggers SHA256 verification
        int eof = stream.Read(span);
        eof.Should().Be(0);
    }

    [TestMethod]
    public async Task Sha256PayloadVerifyingStream_MemoryReadAsync()
    {
        byte[] data = Encoding.UTF8.GetBytes("Data to hash asynchronously");
        using var sha256 = SHA256.Create();
        string expectedHex = BitConverter.ToString(sha256.ComputeHash(data)).Replace("-", "").ToLowerInvariant();

        using var inner = new MemoryStream(data);
        using var stream = new Sha256PayloadVerifyingStream(inner, expectedHex);

        Memory<byte> mem = new byte[100];
        int mRead = await stream.ReadAsync(mem);
        mRead.Should().Be(data.Length);

        // EOF read triggers SHA256 verification
        int eof = await stream.ReadAsync(mem);
        eof.Should().Be(0);
    }

    [TestMethod]
    public void RequestTelemetry_FlowsAcrossAsyncContext()
    {
        var telemetry = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));

        RequestTelemetry.Current = telemetry;
        RequestTelemetry.Current.Should().NotBeNull();
        RequestTelemetry.Current.Should().BeSameAs(telemetry);

        Task.Run(() =>
        {
            RequestTelemetry.Current.Should().NotBeNull();
            RequestTelemetry.Current.Should().BeSameAs(telemetry);
        }).Wait();

        RequestTelemetry.Current = null;
        RequestTelemetry.Current.Should().BeNull();
    }
}
