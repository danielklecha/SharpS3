namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Auth;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Storage;
using SharpS3.Streams;

public partial class AwsChunkedDecodingStreamTests
{
    #region AwsChunkedDecodingStream Sync & Async Edge Tests

    [TestMethod]
    public void AwsChunkedStream_SyncRead_WithSigner_And_Span_Success()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunk1Data = Encoding.UTF8.GetBytes("Chunk one data. ");
        byte[] chunk2Data = Encoding.UTF8.GetBytes("Chunk two data.");
        string sig1 = clientSigner.ComputeChunkSignature(chunk1Data);
        string sig2 = clientSigner.ComputeChunkSignature(chunk2Data);
        string sigFinal = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunk1Data.Length:X};chunk-signature={sig1}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunk1Data)).Append("\r\n");
        rawSb.Append($"{chunk2Data.Length:X};chunk-signature={sig2}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunk2Data)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={sigFinal}\r\n\r\n");

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        // Read using buffer overload
        byte[] buffer = new byte[chunk1Data.Length];
        int read1 = chunkedStream.Read(buffer, 0, buffer.Length);
        read1.Should().Be(chunk1Data.Length);
        Encoding.UTF8.GetString(buffer).Should().Be("Chunk one data. ");

        // Read using Span overload
        Span<byte> span = new byte[chunk2Data.Length];
        int read2 = chunkedStream.Read(span);
        read2.Should().Be(chunk2Data.Length);
        Encoding.UTF8.GetString(span.ToArray()).Should().Be("Chunk two data.");

        // Read at EOF using span first (hits line 314) then buffer
        chunkedStream.Read(span).Should().Be(0);
        chunkedStream.Read(buffer, 0, buffer.Length).Should().Be(0);
        chunkedStream.Read(buffer, 0, 0).Should().Be(0);
        chunkedStream.Read(Span<byte>.Empty).Should().Be(0);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_Unbuffered_Success()
    {
        string chunk1 = "Unbuffered chunk 1. ";
        string chunk2 = "Unbuffered chunk 2.";
        string raw = $"{chunk1.Length:X}\r\n{chunk1}\r\n{chunk2.Length:X}\r\n{chunk2}\r\n0\r\n\r\n";

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        // Span read
        Span<byte> span = new byte[chunk1.Length];
        int r1 = chunkedStream.Read(span);
        r1.Should().Be(chunk1.Length);
        Encoding.UTF8.GetString(span.ToArray()).Should().Be(chunk1);

        // Array read
        byte[] buf = new byte[chunk2.Length];
        int r2 = chunkedStream.Read(buf, 0, buf.Length);
        r2.Should().Be(chunk2.Length);
        Encoding.UTF8.GetString(buf).Should().Be(chunk2);

        // Read at EOF using span first (hits lines 341-344) then buffer
        chunkedStream.Read(span).Should().Be(0);
        chunkedStream.Read(buf, 0, buf.Length).Should().Be(0);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_WithTrailers_Success()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Payload with trailer.");
        string sig1 = clientSigner.ComputeChunkSignature(chunkData);
        string sigFinal = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var trailers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            { "x-amz-checksum-crc32", "AAAAAA==" }
        };
        string trailerSig = clientSigner.ComputeTrailerSignature(trailers);

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={sig1}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunkData)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={sigFinal}\r\n");
        rawSb.Append($"x-amz-checksum-crc32:AAAAAA==\r\n");
        rawSb.Append($"x-amz-trailer-signature:{trailerSig}\r\n\r\n");

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        byte[] buf = new byte[100];
        int read = chunkedStream.Read(buf, 0, buf.Length);
        read.Should().Be(chunkData.Length);

        // EOF read processes the final chunk and trailers
        chunkedStream.Read(buf, 0, buf.Length).Should().Be(0);

        chunkedStream.Trailers.Should().ContainKey("x-amz-checksum-crc32");
        chunkedStream.Trailers["x-amz-checksum-crc32"].Should().Be("AAAAAA==");
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_ChunkSignatureMismatch_Throws()
    {
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        string raw = "5;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\nHello\r\n0\r\n\r\n";

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        byte[] buf = new byte[10];
        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_FinalChunkSignatureMismatch_Throws()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Data");
        string sig1 = clientSigner.ComputeChunkSignature(chunkData);

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={sig1}\r\nData\r\n");
        rawSb.Append("0;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\n\r\n");

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        byte[] buf = new byte[10];
        int r1 = chunkedStream.Read(buf, 0, buf.Length);
        r1.Should().Be(4);

        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_TrailerSignatureMismatch_Throws()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Data");
        string sig1 = clientSigner.ComputeChunkSignature(chunkData);
        string sigFinal = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={sig1}\r\nData\r\n");
        rawSb.Append($"0;chunk-signature={sigFinal}\r\n");
        rawSb.Append("x-amz-checksum:123\r\n");
        rawSb.Append("x-amz-trailer-signature=invalid-trailer-sig\r\nx-amz-trailer-signature:badhexsig\r\n\r\n");

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        byte[] buf = new byte[10];
        chunkedStream.Read(buf, 0, buf.Length).Should().Be(4);

        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<S3Exception>().Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_PrematureEofInChunkData_Throws()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        string raw = "64;chunk-signature=123\r\nShort"; // declares 100 bytes (0x64), only has 5 bytes

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner);

        byte[] buf = new byte[100];
        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<EndOfStreamException>();
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_MaxLineLengthExceeded_Throws()
    {
        string longHeader = new string('A', 8193) + "\r\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(longHeader));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_MaxTrailersExceeded_Throws()
    {
        var sb = new StringBuilder();
        sb.Append("0\r\n");
        for (int i = 0; i < 102; i++)
        {
            sb.Append($"trailer-{i}: val\r\n");
        }
        sb.Append("\r\n");

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void AwsChunkedStream_SyncReadLine_Variations()
    {
        // Line ending with single \n
        string raw = "5\nHello\r\n0\r\n\r\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[5];
        chunkedStream.Read(buf, 0, 5).Should().Be(5);
        Encoding.UTF8.GetString(buf).Should().Be("Hello");
    }

    [TestMethod]
    public void AwsChunkedStream_SyncReadLine_CarriageReturnNonNewline_AppendsAndValidatesLength()
    {
        // \r followed by non-\n (hits lines 740-746)
        string raw = "5\r;chunk-sig\r\nHello\r\n0\r\n\r\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[5];
        chunkedStream.Read(buf, 0, 5).Should().Be(5);
        Encoding.UTF8.GetString(buf).Should().Be("chunk");

        // \r followed by non-\n exceeding MaxLineLength
        string longRaw = new string('A', 8192) + "\rX";
        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(longRaw));
        using var chunkedStream2 = new AwsChunkedDecodingStream(ms2);
        byte[] buf2 = new byte[10];
        var act = () => chunkedStream2.Read(buf2, 0, 10);
        act.Should().Throw<InvalidDataException>();
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_ChunkSizeExceedsLimit_Throws()
    {
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        string raw = "FFFFFFFF;chunk-signature=123\r\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms, serverSigner)
        {
            MaxChunkSize = long.MaxValue
        };
        byte[] buf = new byte[10];
        var act = () => chunkedStream.Read(buf, 0, buf.Length);
        act.Should().Throw<InvalidDataException>().WithMessage("*exceeds supported in-memory buffer limit*");
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_EmptyHeaderLine_ReturnsZero()
    {
        // Header line is completely empty -> returns 0
        using var ms = new MemoryStream(Array.Empty<byte>());
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        chunkedStream.Read(buf, 0, buf.Length).Should().Be(0);

        Span<byte> span = new byte[10];
        using var ms2 = new MemoryStream(Array.Empty<byte>());
        using var chunkedStream2 = new AwsChunkedDecodingStream(ms2);
        chunkedStream2.Read(span).Should().Be(0);

        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        using var ms3 = new MemoryStream(Array.Empty<byte>());
        using var chunkedStream3 = new AwsChunkedDecodingStream(ms3, serverSigner);
        chunkedStream3.Read(buf, 0, buf.Length).Should().Be(0);
    }

    [TestMethod]
    public void AwsChunkedStream_SyncRead_PrematureEofInUnbuffered_ReturnsZero()
    {
        // Chunk claims 20 bytes, stream provides 5 and ends
        string raw = "14\r\nHello";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        int r1 = chunkedStream.Read(buf, 0, 10);
        r1.Should().Be(5);

        // Next read encounters inner read <= 0
        int r2 = chunkedStream.Read(buf, 0, 10);
        r2.Should().Be(0);

        // Repeat with Span
        using var ms2 = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream2 = new AwsChunkedDecodingStream(ms2);
        Span<byte> span = new byte[10];
        chunkedStream2.Read(span).Should().Be(5);
        chunkedStream2.Read(span).Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_AsyncRead_MemoryEmpty_And_EdgeCases()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("5\r\nHello\r\n0\r\n\r\n"));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        // Memory.Empty returns 0
        int emptyRead = await chunkedStream.ReadAsync(Memory<byte>.Empty);
        emptyRead.Should().Be(0);

        // Empty stream async read
        using var emptyMs = new MemoryStream(Array.Empty<byte>());
        using var chunkedEmpty = new AwsChunkedDecodingStream(emptyMs);
        Memory<byte> mem = new byte[10];
        int rEmpty = await chunkedEmpty.ReadAsync(mem);
        rEmpty.Should().Be(0);

        // Premature EOF in unbuffered async read
        using var shortMs = new MemoryStream(Encoding.UTF8.GetBytes("14\r\nHello"));
        using var chunkedShort = new AwsChunkedDecodingStream(shortMs);
        int r1 = await chunkedShort.ReadAsync(mem);
        r1.Should().Be(5);
        int r2 = await chunkedShort.ReadAsync(mem);
        r2.Should().Be(0);
    }

    #endregion

    #region Sha256PayloadVerifyingStream Empty Buffer Tests

    [TestMethod]
    public async Task Sha256PayloadVerifyingStream_EmptyBuffer_ReturnsZero()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
        using var s = new Sha256PayloadVerifyingStream(ms, "some-hash");

        s.Read(Span<byte>.Empty).Should().Be(0);
        (await s.ReadAsync(Memory<byte>.Empty)).Should().Be(0);
    }

    #endregion

    #region ETagHelper Coverage Tests

    [TestMethod]
    public void ETagHelper_ComputeMultipartETag_NonIList_And_InvalidElements()
    {
        // 1. Non-IList IEnumerable
        static IEnumerable<byte[]> GenerateDigests()
        {
            yield return new byte[16];
        }

        string etag1 = ETagHelper.ComputeMultipartETag(GenerateDigests());
        etag1.Should().EndWith("-1\"");

        // 2. Elements that are null or not 16 bytes
        var invalidList = new List<byte[]>
        {
            null!,
            new byte[10]
        };
        string etag2 = ETagHelper.ComputeMultipartETag(invalidList);
        etag2.Should().EndWith("-2\"");
    }

    #endregion

    #region S3RequestDispatcher ValidateContentMd5 FormatException Test

    [TestMethod]
    public void S3RequestDispatcher_ValidateContentMd5_InvalidHex_CatchesFormatException()
    {
        string validMd5Base64 = Convert.ToBase64String(new byte[16]);
        // "zzzz" has even length 4, but invalid hex characters -> Convert.FromHexString throws FormatException
        Action act = () => S3RequestDispatcher.ValidateContentMd5(validMd5Base64, "\"zzzz\"");
        act.Should().NotThrow();
    }

    #endregion

    #region FileSystemStorageBackend DeleteBucket Directory Marker Test

    [TestMethod]
    public async Task FileSystemStorageBackend_DeleteBucket_DirectoryMarkerWithoutFiles_ThrowsBucketNotEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sharps3_test_fs_" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new FileSystemStorageBackend(tempDir);
            await backend.CreateBucketAsync("testbucket", null);

            // Create an empty subdirectory inside the bucket directory
            string bucketDir = Path.Combine(tempDir, "testbucket");
            string subDir = Path.Combine(bucketDir, "emptysubdir");
            Directory.CreateDirectory(subDir);

            // Calling DeleteBucketAsync should detect IsIncludedDirectory == true and throw BucketNotEmpty
            var act = async () => await backend.DeleteBucketAsync("testbucket");
            var ex = await act.Should().ThrowAsync<S3Exception>();
            ex.Which.Error.Code.Should().Be(ErrorCode.BucketNotEmpty);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    #endregion

    #region InMemoryStorageBackend Multipart Fallback MD5 Test

    [TestMethod]
    public async Task InMemoryStorageBackend_CompleteMultipartUpload_Non16ByteETag_ComputesFallbackMD5()
    {
        var backend = new InMemoryStorageBackend();
        await backend.CreateBucketAsync("mybucket", null);
        var init = await backend.CreateMultipartUploadAsync("mybucket", "mykey");

        byte[] partData = Encoding.UTF8.GetBytes("Part data for multipart fallback test");
        await backend.UploadPartAsync("mybucket", "mykey", init.UploadId, 1, new MemoryStream(partData), partData.Length);

        // Modify stored part's ETag to non-hex/non-16-byte
        backend.Uploads[init.UploadId].Parts[1].ETag = "\"custom-not-16-bytes\"";

        var completeUpload = new CompleteMultipartUpload
        {
            Parts = new List<Part>
            {
                new Part { PartNumber = 1, ETag = "\"custom-not-16-bytes\"" }
            }
        };

        var result = await backend.CompleteMultipartUploadAsync("mybucket", "mykey", init.UploadId, completeUpload);
        result.Should().NotBeNull();
        result.ETag.Should().EndWith("-1\"");
    }

    #endregion

    #region SharpS3Client NonDisposingStream & Operations Tests

    [TestMethod]
    public async Task SharpS3Client_NonDisposingStream_AllMembers()
    {
        using var inner = new MemoryStream();
        inner.Write(new byte[] { 1, 2, 3, 4, 5 });
        inner.Position = 0;

        using var s = new SharpS3Client.NonDisposingStream(inner);

        s.CanRead.Should().BeTrue();
        s.CanSeek.Should().BeTrue();
        s.CanWrite.Should().BeTrue();
        s.Length.Should().Be(5);
        s.Position.Should().Be(0);
        s.Position = 1;
        s.Position.Should().Be(1);

        s.Flush();
        await s.FlushAsync(CancellationToken.None);
        s.Seek(0, SeekOrigin.Begin).Should().Be(0);

        byte[] buf = new byte[2];
        s.Read(buf, 0, 2).Should().Be(2);

        Span<byte> span = new byte[1];
        s.Read(span).Should().Be(1);

        Memory<byte> mem = new byte[1];
        (await s.ReadAsync(mem)).Should().Be(1);

        s.Write(new byte[] { 9 }, 0, 1);
        await s.WriteAsync(new byte[] { 8 }, 0, 1);
        s.Write(new ReadOnlySpan<byte>(new byte[] { 7 }));
        await s.WriteAsync(new ReadOnlyMemory<byte>(new byte[] { 6 }));

        s.SetLength(10);
        s.Length.Should().Be(10);

        s.Dispose();
        // Inner stream remains open and readable
        inner.CanRead.Should().BeTrue();
    }

    [TestMethod]
    public async Task SharpS3Client_DownloadFileAsync_FailureDeletesDestinationFile()
    {
        // Custom HttpHandler that returns a stream that throws during CopyToAsync
        var mockHandler = new DelegatingMockHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue("\"testetag\"");
            response.Content = new StreamContent(new FaultyReadStream());
            return response;
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(mockHandler);
        using var client = new SharpS3Client(options, httpClient);

        string tempPath = Path.Combine(Path.GetTempPath(), "test_download_" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var act = async () => await client.DownloadFileAsync("bucket", "key", tempPath);
            await act.Should().ThrowAsync<IOException>();
            File.Exists(tempPath).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    [TestMethod]
    public async Task SharpS3Client_DownloadFileAsync_FileDeleteThrows_InnerCatchSwallows()
    {
        string tempPath = Path.Combine(Path.GetTempPath(), "test_download_lock_" + Guid.NewGuid().ToString("N") + ".tmp");
        var mockHandler = new DelegatingMockHandler(req =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.ETag = new EntityTagHeaderValue("\"testetag\"");
            response.Content = new StreamContent(new FaultyReadStreamWithLock(tempPath));
            return response;
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(mockHandler);
        using var client = new SharpS3Client(options, httpClient);

        try
        {
            var act = async () => await client.DownloadFileAsync("bucket", "key", tempPath);
            await act.Should().ThrowAsync<IOException>();
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
    }

    [TestMethod]
    public async Task SharpS3Client_UploadPartCopyAsync_Validations_And_Parameters()
    {
        var mockHandler = new DelegatingMockHandler(req =>
        {
            // Verify query parameters and headers
            req.RequestUri!.Query.Should().Contain("partNumber=1");
            req.RequestUri.Query.Should().Contain("uploadId=test-upload");
            req.Headers.Contains("x-amz-copy-source").Should().BeTrue();
            req.Headers.GetValues("x-amz-copy-source").First().Should().Contain("versionId=v1");
            req.Headers.Contains("x-amz-copy-source-range").Should().BeTrue();

            string xml = @"<CopyPartResult xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <LastModified>2026-11-22T00:00:00.000Z</LastModified>
  <ETag>&quot;copypartetag&quot;</ETag>
</CopyPartResult>";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
            return response;
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(mockHandler);
        using var client = new SharpS3Client(options, httpClient);

        // Part number range exceptions
        var act0 = async () => await client.UploadPartCopyAsync("srcb", "srck", "destb", "destk", "up1", 0);
        await act0.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var act10001 = async () => await client.UploadPartCopyAsync("srcb", "srck", "destb", "destk", "up1", 10001);
        await act10001.Should().ThrowAsync<ArgumentOutOfRangeException>();

        // Argument null checks
        var actNullSrcB = async () => await client.UploadPartCopyAsync("", "srck", "destb", "destk", "up1", 1);
        await actNullSrcB.Should().ThrowAsync<ArgumentNullException>();

        var actNullSrcK = async () => await client.UploadPartCopyAsync("srcb", "", "destb", "destk", "up1", 1);
        await actNullSrcK.Should().ThrowAsync<ArgumentNullException>();

        var actNullDestB = async () => await client.UploadPartCopyAsync("srcb", "srck", "", "destk", "up1", 1);
        await actNullDestB.Should().ThrowAsync<ArgumentNullException>();

        var actNullDestK = async () => await client.UploadPartCopyAsync("srcb", "srck", "destb", "", "up1", 1);
        await actNullDestK.Should().ThrowAsync<ArgumentNullException>();

        var actNullUpId = async () => await client.UploadPartCopyAsync("srcb", "srck", "destb", "destk", "", 1);
        await actNullUpId.Should().ThrowAsync<ArgumentNullException>();

        // Success with sourceVersionId and sourceRange
        var res = await client.UploadPartCopyAsync("srcb", "srck", "destb", "destk", "test-upload", 1, sourceVersionId: "v1", sourceRange: new ByteRange(0, 100));
        res.Should().NotBeNull();
        res.ETag.Should().Be("\"copypartetag\"");
    }

    [TestMethod]
    public async Task SharpS3Client_ListMultipartUploadsAsync_Parameters_And_Validation()
    {
        var mockHandler = new DelegatingMockHandler(req =>
        {
            req.RequestUri!.Query.Should().Contain("prefix=pre");
            req.RequestUri.Query.Should().Contain("delimiter=%2F");
            req.RequestUri.Query.Should().Contain("key-marker=km");
            req.RequestUri.Query.Should().Contain("upload-id-marker=um");
            req.RequestUri.Query.Should().Contain("max-uploads=25");

            string xml = @"<ListMultipartUploadsResult xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Bucket>testbucket</Bucket>
  <KeyMarker>km</KeyMarker>
  <UploadIdMarker>um</UploadIdMarker>
  <NextKeyMarker>km2</NextKeyMarker>
  <NextUploadIdMarker>um2</NextUploadIdMarker>
  <MaxUploads>25</MaxUploads>
  <IsTruncated>false</IsTruncated>
</ListMultipartUploadsResult>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(mockHandler);
        using var client = new SharpS3Client(options, httpClient);

        // Null bucket
        var actNull = async () => await client.ListMultipartUploadsAsync("");
        await actNull.Should().ThrowAsync<ArgumentNullException>();

        var res = await client.ListMultipartUploadsAsync("testbucket", prefix: "pre", delimiter: "/", keyMarker: "km", uploadIdMarker: "um", maxUploads: 25);
        res.Should().NotBeNull();
        res.Bucket.Should().Be("testbucket");
    }

    [TestMethod]
    public async Task SharpS3Client_ListPartsAsync_Parameters_And_Validation()
    {
        var mockHandler = new DelegatingMockHandler(req =>
        {
            req.RequestUri!.Query.Should().Contain("uploadId=up123");
            req.RequestUri.Query.Should().Contain("max-parts=50");
            req.RequestUri.Query.Should().Contain("part-number-marker=10");

            string xml = @"<ListPartsResult xmlns=""http://s3.amazonaws.com/doc/2006-03-01/"">
  <Bucket>testbucket</Bucket>
  <Key>testkey</Key>
  <UploadId>up123</UploadId>
  <PartNumberMarker>10</PartNumberMarker>
  <NextPartNumberMarker>15</NextPartNumberMarker>
  <MaxParts>50</MaxParts>
  <IsTruncated>false</IsTruncated>
</ListPartsResult>";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            };
        });

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(mockHandler);
        using var client = new SharpS3Client(options, httpClient);

        // Validation
        var actNullB = async () => await client.ListPartsAsync("", "key", "up");
        await actNullB.Should().ThrowAsync<ArgumentNullException>();

        var actNullK = async () => await client.ListPartsAsync("b", "", "up");
        await actNullK.Should().ThrowAsync<ArgumentNullException>();

        var actNullU = async () => await client.ListPartsAsync("b", "key", "");
        await actNullU.Should().ThrowAsync<ArgumentNullException>();

        var res = await client.ListPartsAsync("testbucket", "testkey", "up123", maxParts: 50, partNumberMarker: 10);
        res.Should().NotBeNull();
        res.UploadId.Should().Be("up123");
    }

    [TestMethod]
    public async Task SharpS3Client_Governance_ArgumentChecks()
    {
        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            AccessKey = "test",
            SecretKey = "test"
        };
        using var httpClient = new HttpClient(new DelegatingMockHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new SharpS3Client(options, httpClient);

        // LegalHold Get
        var actLhGet1 = async () => await client.GetObjectLegalHoldAsync("", "key");
        await actLhGet1.Should().ThrowAsync<ArgumentNullException>();
        var actLhGet2 = async () => await client.GetObjectLegalHoldAsync("b", "");
        await actLhGet2.Should().ThrowAsync<ArgumentNullException>();

        // LegalHold Set
        var actLhSet1 = async () => await client.SetObjectLegalHoldAsync("", "key", new LegalHold());
        await actLhSet1.Should().ThrowAsync<ArgumentNullException>();
        var actLhSet2 = async () => await client.SetObjectLegalHoldAsync("b", "", new LegalHold());
        await actLhSet2.Should().ThrowAsync<ArgumentNullException>();
        var actLhSet3 = async () => await client.SetObjectLegalHoldAsync("b", "key", null!);
        await actLhSet3.Should().ThrowAsync<ArgumentNullException>();

        // Retention Get
        var actRetGet1 = async () => await client.GetObjectRetentionAsync("", "key");
        await actRetGet1.Should().ThrowAsync<ArgumentNullException>();
        var actRetGet2 = async () => await client.GetObjectRetentionAsync("b", "");
        await actRetGet2.Should().ThrowAsync<ArgumentNullException>();

        // Retention Set
        var actRetSet1 = async () => await client.SetObjectRetentionAsync("", "key", new Retention());
        await actRetSet1.Should().ThrowAsync<ArgumentNullException>();
        var actRetSet2 = async () => await client.SetObjectRetentionAsync("b", "", new Retention());
        await actRetSet2.Should().ThrowAsync<ArgumentNullException>();
        var actRetSet3 = async () => await client.SetObjectRetentionAsync("b", "key", null!);
        await actRetSet3.Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion

    #region Helper Classes for Testing

    private class DelegatingMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private class FaultyReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated network stream abort");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException("Simulated network stream abort");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("Simulated network stream abort");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private class FaultyReadStreamWithLock : Stream
    {
        private readonly string _targetPath;

        public FaultyReadStreamWithLock(string targetPath)
        {
            _targetPath = targetPath;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1024;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            File.SetAttributes(_targetPath, FileAttributes.ReadOnly);
            throw new IOException("Simulated network stream abort with read-only target");
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            File.SetAttributes(_targetPath, FileAttributes.ReadOnly);
            throw new IOException("Simulated network stream abort with read-only target");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion

    #region Final 100 Percent Coverage Tests for Remaining Partial Branches and Lines

    [TestMethod]
    public void SharpS3Client_NonDisposingStream_NullInner_ThrowsArgumentNullException()
    {
        Action act = () => new SharpS3Client.NonDisposingStream(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");
    }

    [TestMethod]
    public void AwsChunkedDecodingStream_ReadLineSync_EndsImmediatelyWithCarriageReturn()
    {
        // "0\r" ends immediately with \r so _Inner.Read returns 0, covering the next > 0 == false branch on line 739
        byte[] data = Encoding.UTF8.GetBytes("0\r");
        using var ms = new MemoryStream(data);
        using var stream = new AwsChunkedDecodingStream(ms);
        byte[] buf = new byte[10];
        int read = stream.Read(buf, 0, buf.Length);
        read.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_ReadAsync_Memory_AtEndOfStream_ReturnsZero()
    {
        byte[] data = Encoding.UTF8.GetBytes("5\r\nHello\r\n0\r\n\r\n");
        using var ms = new MemoryStream(data);
        using var stream = new AwsChunkedDecodingStream(ms);
        Memory<byte> buf = new byte[10];
        int read1 = await stream.ReadAsync(buf);
        read1.Should().Be(5);
        int read2 = await stream.ReadAsync(buf);
        read2.Should().Be(0);
        // Call again when _EndOfStream is true and buffer.Length > 0 -> hits line 370 _EndOfStream branch
        int read3 = await stream.ReadAsync(buf);
        read3.Should().Be(0);
    }

    [TestMethod]
    public async Task FileSystemStorageBackend_DeleteBucket_WithS3BucketXml_Succeeds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sharps3_bktxml_" + Guid.NewGuid().ToString("N"));
        try
        {
            var backend = new FileSystemStorageBackend(tempDir);
            await backend.CreateBucketAsync("test-bucket", null);
            string bucketDir = Path.Combine(tempDir, "test-bucket");
            // Create .s3bucket.xml file (covers Path.GetFileName(f).Equals(".s3bucket.xml") == true on line 556)
            File.WriteAllText(Path.Combine(bucketDir, ".s3bucket.xml"), "<BucketMetadata />");
            // Also create a .s3meta.xml file to verify both parts of the condition
            File.WriteAllText(Path.Combine(bucketDir, "obj.s3meta.xml"), "<Metadata />");

            // DeleteBucketAsync should treat these files as non-objects and delete the bucket successfully
            await backend.DeleteBucketAsync("test-bucket");
            Directory.Exists(bucketDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task S3RequestDispatcher_PutObject_DeleteCleanupThrows_CatchesAndRethrows()
    {
        // Tests S3RequestDispatcher line 528: try { await _Backend.DeleteObjectAsync(...) } catch { }
        var backend = new CallbackStorageBackend
        {
            PutObject = (b, k, s, l, ct, meta, token) => Task.FromResult("\"0102030405060708090a0b0c0d0e0f10\""),
            DeleteObject = (b, k, v, token) => throw new IOException("Disk failure during cleanup")
        };
        var dispatcher = new S3RequestDispatcher(backend);
        byte[] payloadData = "test data"u8.ToArray();
        string badMd5Base64 = Convert.ToBase64String(new byte[16]);
        var req = new S3Request(
            "PUT",
            "/test-bucket/test-key",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["content-md5"] = new[] { badMd5Base64 },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            new SharpS3Settings()
        );

        Func<Task> act = async () => await dispatcher.DispatchAsync(req);
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.BadDigest);
    }

    [TestMethod]
    public async Task S3RequestDispatcher_PutObject_NoVerifyingStreamOrMd5_PutThrows_RethrowsDirectly()
    {
        // Tests S3RequestDispatcher line 526 false branch, line 530 throw; and line 531
        var backend = new CallbackStorageBackend
        {
            PutObject = (b, k, s, l, ct, meta, token) => throw new InvalidOperationException("Backend storage error")
        };
        var dispatcher = new S3RequestDispatcher(backend);
        byte[] payloadData = "test data"u8.ToArray();
        var req = new S3Request(
            "PUT",
            "/test-bucket/test-key",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]>
            {
                ["host"] = new[] { "localhost" },
                ["content-length"] = new[] { payloadData.Length.ToString() }
            },
            new MemoryStream(payloadData),
            new SharpS3Settings()
        );

        Func<Task> act = async () => await dispatcher.DispatchAsync(req);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Backend storage error");
    }

    #endregion

    #region Telemetry Candidates 1 & 2 Tests

    [TestMethod]
    public void RequestTelemetry_RecordPutCleanupFailure_Branches()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (inst, l) => l.EnableMeasurementEvents(inst);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);

        // 1. null exception: early return
        var rt = telemetry.BeginRequest()!;
        rt.RecordPutCleanupFailure(null);

        // 2. with activity and exception without stacktrace
        var exNoStack = new InvalidOperationException("No stack trace error");
        rt.RecordPutCleanupFailure(exNoStack);

        // 3. with activity and exception with stacktrace
        try
        {
            throw new IOException("Cleanup disk failure");
        }
        catch (Exception exWithStack)
        {
            rt.RecordPutCleanupFailure(exWithStack);
        }

        // 4. without activity
        var reqWithoutActivity = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        typeof(RequestTelemetry).GetField("_Telemetry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reqWithoutActivity, telemetry);
        rt.Complete(200); // end rt activity

        reqWithoutActivity.RecordPutCleanupFailure(exNoStack);

        // 5. catch block: uninitialized RequestTelemetry where _Telemetry is null
        var uninit = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        Action actCatch = () => uninit.RecordPutCleanupFailure(exNoStack);
        actCatch.Should().NotThrow();
    }

    [TestMethod]
    public void RequestTelemetry_RecordDeleteMultipleResult_Branches()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (inst, l) => l.EnableMeasurementEvents(inst);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);

        // 1. null result: early return
        var rt = telemetry.BeginRequest()!;
        rt.RecordDeleteMultipleResult(null);

        // 2. with activity, errorCount == 0
        var cleanResult = new DeleteResult
        {
            DeletedObjects = new List<Deleted> { new Deleted("key1") }
        };
        rt.RecordDeleteMultipleResult(cleanResult);

        // 3. with activity, errorCount > 0
        var partialResult = new DeleteResult
        {
            DeletedObjects = new List<Deleted> { new Deleted("key1") },
            Errors = new List<Error> { new Error(ErrorCode.InternalError, "Failed to delete key2") }
        };
        rt.RecordDeleteMultipleResult(partialResult);

        // 4. DeletedObjects == null
        var nullDeletedResult = new DeleteResult
        {
            DeletedObjects = null!
        };
        rt.RecordDeleteMultipleResult(nullDeletedResult);

        // 5. Errors == null via reflection
        var nullErrorsResult = new DeleteResult();
        typeof(DeleteResult).GetField("_Errors", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(nullErrorsResult, null);
        rt.RecordDeleteMultipleResult(nullErrorsResult);

        // 6. without activity, errorCount > 0 and errorCount == 0
        var reqWithoutActivity = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        typeof(RequestTelemetry).GetField("_Telemetry", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(reqWithoutActivity, telemetry);
        rt.Complete(200);

        reqWithoutActivity.RecordDeleteMultipleResult(partialResult);
        reqWithoutActivity.RecordDeleteMultipleResult(cleanResult);

        // 7. catch block: uninitialized RequestTelemetry where _Telemetry is null
        var uninit = (RequestTelemetry)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(RequestTelemetry));
        Action actCatch = () => uninit.RecordDeleteMultipleResult(partialResult);
        actCatch.Should().NotThrow();
    }

    [TestMethod]
    public async Task S3RequestDispatcher_PutObject_DeleteCleanupThrows_WithActiveTelemetry()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (inst, l) => l.EnableMeasurementEvents(inst);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rt = telemetry.BeginRequest()!;
        RequestTelemetry.Current = rt;

        try
        {
            var backend = new CallbackStorageBackend
            {
                PutObject = (b, k, s, l, ct, meta, token) => Task.FromResult("\"0102030405060708090a0b0c0d0e0f10\""),
                DeleteObject = (b, k, v, token) => throw new IOException("Disk failure during cleanup")
            };
            var dispatcher = new S3RequestDispatcher(backend);
            byte[] payloadData = "test data"u8.ToArray();
            string badMd5Base64 = Convert.ToBase64String(new byte[16]);
            var req = new S3Request(
                "PUT",
                "/test-bucket/test-key",
                new Dictionary<string, string>(),
                new Dictionary<string, string[]>
                {
                    ["host"] = new[] { "localhost" },
                    ["content-md5"] = new[] { badMd5Base64 },
                    ["content-length"] = new[] { payloadData.Length.ToString() }
                },
                new MemoryStream(payloadData),
                new SharpS3Settings()
            );

            Func<Task> act = async () => await dispatcher.DispatchAsync(req);
            var ex = await act.Should().ThrowAsync<S3Exception>();
            ex.Which.Error.Code.Should().Be(ErrorCode.BadDigest);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }
    }

    [TestMethod]
    public async Task S3RequestDispatcher_DeleteMultiple_WithActiveTelemetry()
    {
        using var actListener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(actListener);

        using var meterListener = new System.Diagnostics.Metrics.MeterListener();
        meterListener.InstrumentPublished = (inst, l) => l.EnableMeasurementEvents(inst);
        meterListener.Start();

        var settings = new SharpS3Settings();
        using var telemetry = new S3ServerTelemetry(new S3ServerTelemetrySettings(), () => settings, () => true);
        var rt = telemetry.BeginRequest()!;
        RequestTelemetry.Current = rt;

        try
        {
            var backend = new CallbackStorageBackend
            {
                DeleteMultipleObjects = (b, delReq, token) => Task.FromResult(new DeleteResult
                {
                    DeletedObjects = new List<Deleted> { new Deleted("key1") },
                    Errors = new List<Error> { new Error(ErrorCode.NoSuchKey, "Key not found") }
                })
            };
            var dispatcher = new S3RequestDispatcher(backend);
            string xml = "<Delete><Object><Key>key1</Key></Object><Object><Key>key2</Key></Object></Delete>";
            byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
            var req = new S3Request(
                "POST",
                "/test-bucket?delete",
                new Dictionary<string, string> { ["delete"] = "" },
                new Dictionary<string, string[]>
                {
                    ["host"] = new[] { "localhost" },
                    ["content-length"] = new[] { xmlBytes.Length.ToString() }
                },
                new MemoryStream(xmlBytes),
                new SharpS3Settings()
            );
            req.RequestType = S3RequestType.ObjectDeleteMultiple;

            var resp = await dispatcher.DispatchAsync(req);
            resp.StatusCode.Should().Be(200);
        }
        finally
        {
            RequestTelemetry.Current = null;
        }
    }

    #endregion
}
