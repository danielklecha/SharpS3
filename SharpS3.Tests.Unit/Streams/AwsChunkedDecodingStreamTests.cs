namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Auth;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Streams;

[TestClass]
public partial class AwsChunkedDecodingStreamTests
{
    private readonly byte[] _SigningKey = Encoding.UTF8.GetBytes("test-signing-key-32-bytes-long!!");
    private const string SeedSignature = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [TestMethod]
    public async Task Can_Decode_AwsChunked_Stream_Without_Signer()
    {
        // Build raw aws-chunked stream with 2 chunks + final chunk
        string chunk1 = "Hello, ";
        string chunk2 = "World from aws-chunked!";
        string rawChunked = $"{chunk1.Length:X}\r\n{chunk1}\r\n{chunk2.Length:X}\r\n{chunk2}\r\n0\r\n\r\n";

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawChunked));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        using var destMs = new MemoryStream();
        await chunkedStream.CopyToAsync(destMs);

        string result = Encoding.UTF8.GetString(destMs.ToArray());
        result.Should().Be("Hello, World from aws-chunked!");
    }

    [TestMethod]
    public async Task Can_Decode_And_Verify_Valid_Chunked_Signatures()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunk1Data = Encoding.UTF8.GetBytes("Chunk Number One payload data. ");
        byte[] chunk2Data = Encoding.UTF8.GetBytes("Chunk Number Two payload data.");

        string sig1 = clientSigner.ComputeChunkSignature(chunk1Data);
        string sig2 = clientSigner.ComputeChunkSignature(chunk2Data);
        string sigFinal = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunk1Data.Length:X};chunk-signature={sig1}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunk1Data)).Append("\r\n");
        rawSb.Append($"{chunk2Data.Length:X};chunk-signature={sig2}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunk2Data)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={sigFinal}\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        using var destMs = new MemoryStream();
        await chunkedStream.CopyToAsync(destMs);

        string result = Encoding.UTF8.GetString(destMs.ToArray());
        result.Should().Be("Chunk Number One payload data. Chunk Number Two payload data.");
    }

    [TestMethod]
    public async Task Throws_SignatureDoesNotMatch_When_Chunk_Data_Tampered()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] originalChunk = Encoding.UTF8.GetBytes("Valid data content.");
        string sig = clientSigner.ComputeChunkSignature(originalChunk);
        string sigFinal = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        // Tamper with data but keep the original signature
        byte[] tamperedChunk = Encoding.UTF8.GetBytes("Tampered content!!!");

        var rawSb = new StringBuilder();
        rawSb.Append($"{tamperedChunk.Length:X};chunk-signature={sig}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(tamperedChunk)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={sigFinal}\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };

        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task Throws_SignatureDoesNotMatch_When_Chunk_Signature_Tampered()
    {
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Data to upload.");
        string badSig = "0000000000000000000000000000000000000000000000000000000000000000";

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={badSig}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunkData)).Append("\r\n");
        rawSb.Append("0;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };

        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task Throws_SignatureDoesNotMatch_When_Final_Chunk_Signature_Tampered()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Legitimate single chunk.");
        string validSig = clientSigner.ComputeChunkSignature(chunkData);
        string badFinalSig = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={validSig}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunkData)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={badFinalSig}\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };

        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task Can_Decode_And_Verify_Chunk_With_Trailers()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Payload with checksum trailer.");
        string chunkSig = clientSigner.ComputeChunkSignature(chunkData);
        string finalSig = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var trailers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "x-amz-checksum-crc32", "NSR52w==" }
        };
        string trailerSig = clientSigner.ComputeTrailerSignature(trailers);

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={chunkSig}\r\n");
        rawSb.Append(Encoding.UTF8.GetString(chunkData)).Append("\r\n");
        rawSb.Append($"0;chunk-signature={finalSig}\r\n");
        rawSb.Append("x-amz-checksum-crc32: NSR52w==\r\n");
        rawSb.Append($"x-amz-trailer-signature: {trailerSig}\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        using var destMs = new MemoryStream();
        await chunkedStream.CopyToAsync(destMs);

        string result = Encoding.UTF8.GetString(destMs.ToArray());
        result.Should().Be("Payload with checksum trailer.");
        chunkedStream.Trailers.ContainsKey("x-amz-checksum-crc32").Should().BeTrue();
        chunkedStream.Trailers["x-amz-checksum-crc32"].Should().Be("NSR52w==");
    }

    [TestMethod]
    public void AwsChunkedStream_StreamProperties_And_UnsupportedOperations()
    {
        using var memoryStream = new MemoryStream();
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        chunkedStream.CanRead.Should().BeTrue();
        chunkedStream.CanSeek.Should().BeFalse();
        chunkedStream.CanWrite.Should().BeFalse();

        Action actLen = () => { var _ = chunkedStream.Length; };
        actLen.Should().Throw<NotSupportedException>();

        Action actPosGet = () => { var _ = chunkedStream.Position; };
        actPosGet.Should().Throw<NotSupportedException>();

        Action actPosSet = () => chunkedStream.Position = 0;
        actPosSet.Should().Throw<NotSupportedException>();

        chunkedStream.Flush();

        Action actSeek = () => chunkedStream.Seek(0, SeekOrigin.Begin);
        actSeek.Should().Throw<NotSupportedException>();

        Action actSetLength = () => chunkedStream.SetLength(0);
        actSetLength.Should().Throw<NotSupportedException>();

        Action actWrite = () => chunkedStream.Write(new byte[1], 0, 1);
        actWrite.Should().Throw<NotSupportedException>();

        Action actAttachNull = () => chunkedStream.AttachSigner(null!);
        actAttachNull.Should().Throw<ArgumentNullException>();

        var validSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        chunkedStream.AttachSigner(validSigner);
    }

    [TestMethod]
    public async Task AwsChunkedStream_SynchronousRead_And_ChunkWithoutExtensions()
    {
        string raw = "5\r\nHello\r\n0\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        byte[] buf = new byte[10];
        int read = chunkedStream.Read(buf, 0, 10);
        read.Should().Be(5);
        Encoding.UTF8.GetString(buf, 0, 5).Should().Be("Hello");

        // Subsequent read returns 0
        read = chunkedStream.Read(buf, 0, 10);
        read.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ChunkSignatureWithMultipleExtensions()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] data = Encoding.UTF8.GetBytes("MultiExt");
        string sig = clientSigner.ComputeChunkSignature(data);
        string finalSig = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        string raw = $"{data.Length:X};chunk-signature={sig};foo=bar\r\nMultiExt\r\n0;chunk-signature={finalSig};extra=param\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        using var destMs = new MemoryStream();
        await chunkedStream.CopyToAsync(destMs);
        Encoding.UTF8.GetString(destMs.ToArray()).Should().Be("MultiExt");
    }

    [TestMethod]
    public async Task AwsChunkedStream_InvalidHexSizeHeader_ThrowsInvalidDataException()
    {
        string raw = "XYZ;chunk-signature=123\r\nSomeData\r\n0\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [TestMethod]
    public async Task AwsChunkedStream_UnexpectedEofInChunk_ThrowsEndOfStreamException()
    {
        // Declares 10 bytes (0xA) but only provides 3 with signer attached
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        string raw = "A;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\n123";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [TestMethod]
    public async Task AwsChunkedStream_BadTrailerSignature_ThrowsSignatureDoesNotMatch()
    {
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Payload");
        string chunkSig = clientSigner.ComputeChunkSignature(chunkData);
        string finalSig = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        var rawSb = new StringBuilder();
        rawSb.Append($"{chunkData.Length:X};chunk-signature={chunkSig}\r\n");
        rawSb.Append("Payload\r\n");
        rawSb.Append($"0;chunk-signature={finalSig}\r\n");
        rawSb.Append("x-amz-checksum-crc32: NSR52w==\r\n");
        rawSb.Append("x-amz-trailer-signature: 0000000000000000000000000000000000000000000000000000000000000000\r\n\r\n");

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSb.ToString()));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        var act = async () =>
        {
            using var destMs = new MemoryStream();
            await chunkedStream.CopyToAsync(destMs);
        };
        var ex = await act.Should().ThrowAsync<S3Exception>();
        ex.Which.Error.Code.Should().Be(ErrorCode.SignatureDoesNotMatch);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadAsync_WhenEndOfStreamAlreadyReached_ReturnsZero()
    {
        // Line 91: if (_EndOfStream) return 0;
        string raw = "5\r\nHello\r\n0\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        byte[] buf = new byte[10];
        int read1 = await chunkedStream.ReadAsync(buf, 0, 10);
        read1.Should().Be(5);

        // Reaches EOF
        int readEof = await chunkedStream.ReadAsync(buf, 0, 10);
        readEof.Should().Be(0);

        // Line 91: Calling ReadAsync again after _EndOfStream is true
        int readAfterEof = await chunkedStream.ReadAsync(buf, 0, 10);
        readAfterEof.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadAsync_WhenCountIsZeroOrNegative_ReturnsZero()
    {
        // Line 93: if (count <= 0) return 0;
        string raw = "5\r\nHello\r\n0\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        byte[] buf = new byte[10];
        int readZero = await chunkedStream.ReadAsync(buf, 0, 0);
        readZero.Should().Be(0);

        int readNegative = await chunkedStream.ReadAsync(buf, 0, -1);
        readNegative.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadAsync_SignerMode_HitsEndOfStreamInLoop()
    {
        // Line 105: with signer, reading to EOF reaches break in while loop and hits 'if (_EndOfStream) return 0;'
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        var clientSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Chunk");
        string chunkSig = clientSigner.ComputeChunkSignature(chunkData);
        string finalSig = clientSigner.ComputeChunkSignature(Array.Empty<byte>());

        string raw = $"{chunkData.Length:X};chunk-signature={chunkSig}\r\nChunk\r\n0;chunk-signature={finalSig}\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        byte[] buf = new byte[10];
        int read1 = await chunkedStream.ReadAsync(buf, 0, 10);
        read1.Should().Be(5);

        // This call will read the 0-byte final chunk, set _EndOfStream = true, break from while loop, and return 0 at line 105
        int readEof = await chunkedStream.ReadAsync(buf, 0, 10);
        readEof.Should().Be(0);

        // And reading again hits line 91 in signer mode as well
        int readAgain = await chunkedStream.ReadAsync(buf, 0, 10);
        readAgain.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ExtractSignature_MissingSignatureParam_ReturnsNull()
    {
        // Line 256: ExtractSignature returns null when chunk header does not contain "chunk-signature="
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);

        // Header has extensions or just size, but no chunk-signature
        string raw = "5;other-param=value\r\nHello\r\n0\r\n\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner);

        byte[] buf = new byte[10];
        int read = await chunkedStream.ReadAsync(buf, 0, 10);
        read.Should().Be(5);
        Encoding.UTF8.GetString(buf, 0, 5).Should().Be("Hello");
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadLineAsync_BareLineFeed_BreaksCorrectly()
    {
        // Line 307: ReadLineAsync encounters '\n' without preceding '\r'
        string raw = "5\nHello\n0\n\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        byte[] buf = new byte[10];
        int read = await chunkedStream.ReadAsync(buf, 0, 10);
        read.Should().Be(5);
        Encoding.UTF8.GetString(buf, 0, 5).Should().Be("Hello");

        int readEof = await chunkedStream.ReadAsync(buf, 0, 10);
        readEof.Should().Be(0);
    }

    [TestMethod]
    public void AwsChunkedStream_Constructor_NullInnerStream_ThrowsArgumentNullException()
    {
        // Line 44: _Inner = inner ?? throw new ArgumentNullException(nameof(inner));
        Action act = () => new AwsChunkedDecodingStream(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadLineAsync_CarriageReturnAtEndOfStream_HandledCorrectly()
    {
        // Line 302: next <= 0 branch when '\r' is immediately followed by EOF
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("5\r"));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        int read = await chunkedStream.ReadAsync(buf, 0, 10);
        read.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ConsumeCrLfAsync_EofImmediatelyAfterChunkData_HandledCorrectly()
    {
        // Line 319: n <= 0 branch when inner stream reaches EOF right after chunk payload without trailing CRLF
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("5\r\nHello"));
        using var chunkedStream = new AwsChunkedDecodingStream(ms);

        byte[] buf = new byte[10];
        int read = await chunkedStream.ReadAsync(buf, 0, 10);
        read.Should().Be(5);
        Encoding.UTF8.GetString(buf, 0, 5).Should().Be("Hello");

        int readEof = await chunkedStream.ReadAsync(buf, 0, 10);
        readEof.Should().Be(0);
    }

    [TestMethod]
    public async Task AwsChunkedStream_ChunkSizeExceedingBufferLimit_WithSigner_ThrowsInvalidDataException()
    {
        // Covers line 234: chunk size exceeding supported in-memory buffer limit (e.g. > int.MaxValue / Array.MaxLength)
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        // 0x100000000 = 4,294,967,296 bytes (> int.MaxValue)
        string raw = "100000000;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner)
        {
            MaxChunkSize = long.MaxValue
        };

        byte[] buf = new byte[16];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        var ex = await act.Should().ThrowAsync<InvalidDataException>();
        ex.WithMessage("*exceeds supported in-memory buffer limit.*");
    }

    [TestMethod]
    public async Task AwsChunkedStream_ChunkSizeExceedingArrayMaxLength_WithSigner_ThrowsInvalidDataException()
    {
        // 0x80000000 = 2,147,483,648 (> Array.MaxLength)
        var serverSigner = new AwsChunkSigner("20261114", "us-east-1", "s3", _SigningKey, SeedSignature);
        string raw = "80000000;chunk-signature=0000000000000000000000000000000000000000000000000000000000000000\r\n";
        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream, serverSigner)
        {
            MaxChunkSize = long.MaxValue
        };

        byte[] buf = new byte[16];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        var ex = await act.Should().ThrowAsync<InvalidDataException>();
        ex.WithMessage("*exceeds supported in-memory buffer limit.*");
    }

    [TestMethod]
    public async Task AwsChunkedStream_ReadLineAsync_CarriageReturnFollowedByNonNewline_ExceedingMaxLineLength_ThrowsInvalidDataException()
    {
        // Covers line 347: \r followed by non-\n character where total accumulated line length exceeds MaxLineLength
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("ABCD\rX"));
        using var chunkedStream = new AwsChunkedDecodingStream(ms)
        {
            MaxLineLength = 4
        };

        byte[] buf = new byte[10];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        var ex = await act.Should().ThrowAsync<InvalidDataException>();
        ex.WithMessage("*Header line exceeds maximum length of 4 characters.*");
    }

    [TestMethod]
    public void AwsChunkedStream_PropertyLimits_Validation_ThrowsArgumentOutOfRangeException()
    {
        using var memoryStream = new MemoryStream();
        using var chunkedStream = new AwsChunkedDecodingStream(memoryStream);

        // MaxChunkSize cannot be negative
        Action setNegativeChunkSize = () => chunkedStream.MaxChunkSize = -1;
        setNegativeChunkSize.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("value");

        // MaxLineLength must be positive
        Action setZeroLineLength = () => chunkedStream.MaxLineLength = 0;
        setZeroLineLength.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("value");

        Action setNegativeLineLength = () => chunkedStream.MaxLineLength = -5;
        setNegativeLineLength.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("value");

        // MaxTrailersCount cannot be negative
        Action setNegativeTrailers = () => chunkedStream.MaxTrailersCount = -1;
        setNegativeTrailers.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("value");
    }
}
