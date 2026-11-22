namespace SharpS3.Tests.Unit;

#pragma warning disable CS8625

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Auth;

[TestClass]
public class AwsChunkSignerTests
{
    private static readonly byte[] ValidSigningKey = new byte[32]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f, 0x20
    };

    private const string SeedSignature = "4f232daebe88864e04429ffd15dd1a5ec07ea76da520420a04f99bdd45f481fb";

    [TestMethod]
    public void Constructor_NullOrEmptyTimestamp_ThrowsArgumentNullException()
    {
        Action actNull = () => new AwsChunkSigner(null, "us-east-1", "s3", ValidSigningKey, SeedSignature);
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("timestamp");

        Action actEmpty = () => new AwsChunkSigner(string.Empty, "us-east-1", "s3", ValidSigningKey, SeedSignature);
        actEmpty.Should().Throw<ArgumentNullException>().WithParameterName("timestamp");
    }

    [TestMethod]
    public void Constructor_NullOrEmptyRegion_ThrowsArgumentNullException()
    {
        Action actNull = () => new AwsChunkSigner("20261119T143803Z", null, "s3", ValidSigningKey, SeedSignature);
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("region");

        Action actEmpty = () => new AwsChunkSigner("20261119T143803Z", string.Empty, "s3", ValidSigningKey, SeedSignature);
        actEmpty.Should().Throw<ArgumentNullException>().WithParameterName("region");
    }

    [TestMethod]
    public void Constructor_NullOrEmptyService_ThrowsArgumentNullException()
    {
        Action actNull = () => new AwsChunkSigner("20261119T143803Z", "us-east-1", null, ValidSigningKey, SeedSignature);
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("service");

        Action actEmpty = () => new AwsChunkSigner("20261119T143803Z", "us-east-1", string.Empty, ValidSigningKey, SeedSignature);
        actEmpty.Should().Throw<ArgumentNullException>().WithParameterName("service");
    }

    [TestMethod]
    public void Constructor_NullSigningKey_ThrowsArgumentNullException()
    {
        Action act = () => new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", null, SeedSignature);
        act.Should().Throw<ArgumentNullException>().WithParameterName("signingKey");
    }

    [TestMethod]
    public void Constructor_NullOrEmptySeedSignature_ThrowsArgumentNullException()
    {
        Action actNull = () => new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, null);
        actNull.Should().Throw<ArgumentNullException>().WithParameterName("seedSignature");

        Action actEmpty = () => new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, string.Empty);
        actEmpty.Should().Throw<ArgumentNullException>().WithParameterName("seedSignature");
    }

    [TestMethod]
    public void Constructor_TimestampLengthLessThan8_UsesFullTimestampAsDateStamp()
    {
        using var signer = new AwsChunkSigner("2026", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        signer.Timestamp.Should().Be("2026");
        signer.Scope.Should().Be("2026/us-east-1/s3/aws4_request");
        signer.CurrentSignature.Should().Be(SeedSignature);
    }

    [TestMethod]
    public void Constructor_TimestampLength8OrMore_ExtractsFirst8CharsAsDateStamp()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "eu-west-1", "s3", ValidSigningKey, SeedSignature);
        signer.Timestamp.Should().Be("20261119T143803Z");
        signer.Scope.Should().Be("20261119/eu-west-1/s3/aws4_request");
        signer.CurrentSignature.Should().Be(SeedSignature);
    }

    [TestMethod]
    public void ComputeChunkSignature_NullOrEmptyChunkData_UsesEmptySha256()
    {
        using var signer1 = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        string sigFromNull = signer1.ComputeChunkSignature(null);

        using var signer2 = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        string sigFromEmpty = signer2.ComputeChunkSignature(Array.Empty<byte>());

        sigFromNull.Should().NotBeNullOrEmpty();
        sigFromNull.Should().Be(sigFromEmpty);
        signer1.CurrentSignature.Should().Be(sigFromNull);
        signer2.CurrentSignature.Should().Be(sigFromEmpty);
    }

    [TestMethod]
    public void ComputeChunkSignature_NonEmptyData_ComputesHashAndAdvancesSignatureChain()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        byte[] chunk1 = Encoding.UTF8.GetBytes("Hello, AWS Chunked Streaming!");
        byte[] chunk2 = Encoding.UTF8.GetBytes("Second chunk payload.");

        string sig1 = signer.ComputeChunkSignature(chunk1);
        sig1.Should().NotBeNullOrEmpty();
        sig1.Should().NotBe(SeedSignature);
        signer.CurrentSignature.Should().Be(sig1);

        string sig2 = signer.ComputeChunkSignature(chunk2);
        sig2.Should().NotBeNullOrEmpty();
        sig2.Should().NotBe(sig1);
        signer.CurrentSignature.Should().Be(sig2);

        // Final zero-length chunk
        string finalSig = signer.ComputeChunkSignature(null);
        finalSig.Should().NotBeNullOrEmpty();
        finalSig.Should().NotBe(sig2);
        signer.CurrentSignature.Should().Be(finalSig);
    }

    [TestMethod]
    public void ValidateChunk_NullOrEmptySignature_ReturnsFalseWithoutAdvancingSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        byte[] chunkData = Encoding.UTF8.GetBytes("test payload");

        signer.ValidateChunk(chunkData, null).Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature);

        signer.ValidateChunk(chunkData, string.Empty).Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature);
    }

    [TestMethod]
    public void ValidateChunk_MismatchedSignature_ReturnsFalseAndRollsBackSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        byte[] chunkData = Encoding.UTF8.GetBytes("payload");

        bool result = signer.ValidateChunk(chunkData, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        result.Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature, "CurrentSignature must be preserved when chunk validation fails");
    }

    [TestMethod]
    public void ValidateChunk_ValidSignature_ReturnsTrueAndAdvancesSignature()
    {
        using var signerA = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        using var signerB = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);

        byte[] chunkData = Encoding.UTF8.GetBytes("Valid chunk data");
        string expectedSig = signerA.ComputeChunkSignature(chunkData);

        // Validating with expected signature (including case insensitivity)
        signerB.ValidateChunk(chunkData, expectedSig.ToUpperInvariant()).Should().BeTrue();
        signerB.CurrentSignature.Should().Be(expectedSig);
        signerB.CurrentSignature.Should().Be(signerA.CurrentSignature);
    }

    [TestMethod]
    public void ComputeTrailerSignature_NullHeaders_ThrowsArgumentNullException()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        Action act = () => signer.ComputeTrailerSignature(null);
        act.Should().Throw<ArgumentNullException>().WithParameterName("trailerHeaders");
    }

    [TestMethod]
    public void ComputeTrailerSignature_EmptyHeaders_ComputesExpectedSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        var emptyTrailers = new SortedDictionary<string, string>(StringComparer.Ordinal);

        string trailerSig = signer.ComputeTrailerSignature(emptyTrailers);
        trailerSig.Should().NotBeNullOrEmpty();
        signer.CurrentSignature.Should().Be(trailerSig);
    }

    [TestMethod]
    public void ComputeTrailerSignature_Headers_CanonicalizesKeysAndValues()
    {
        using var signer1 = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        var trailers1 = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Amz-Checksum-Crc32"] = "  12345678  ",
            ["x-amz-meta-notes"] = " some note "
        };

        using var signer2 = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        var trailers2 = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-checksum-crc32"] = "12345678",
            ["x-amz-meta-notes"] = "some note"
        };

        string sig1 = signer1.ComputeTrailerSignature(trailers1);
        string sig2 = signer2.ComputeTrailerSignature(trailers2);

        sig1.Should().Be(sig2);
        signer1.CurrentSignature.Should().Be(sig1);
    }

    [TestMethod]
    public void ValidateTrailer_NullOrEmptySignature_ReturnsFalseWithoutAdvancingSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        var trailers = new SortedDictionary<string, string> { ["x-amz-checksum-sha256"] = "abc" };

        signer.ValidateTrailer(trailers, null).Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature);

        signer.ValidateTrailer(trailers, string.Empty).Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature);
    }

    [TestMethod]
    public void ValidateTrailer_NullHeaders_ReturnsFalseWithoutAdvancingSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        signer.ValidateTrailer(null, "some-sig").Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature);
    }

    [TestMethod]
    public void ValidateTrailer_MismatchedSignature_ReturnsFalseAndRollsBackSignature()
    {
        using var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        var trailers = new SortedDictionary<string, string> { ["x-amz-checksum-crc32"] = "12345" };

        bool result = signer.ValidateTrailer(trailers, "bad-trailer-signature");
        result.Should().BeFalse();
        signer.CurrentSignature.Should().Be(SeedSignature, "CurrentSignature must be preserved when trailer validation fails");
    }

    [TestMethod]
    public void ValidateTrailer_ValidSignature_ReturnsTrueAndAdvancesSignature()
    {
        using var signerA = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        using var signerB = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);

        var trailers = new SortedDictionary<string, string> { ["x-amz-checksum-crc32c"] = "abc123" };
        string expectedSig = signerA.ComputeTrailerSignature(trailers);

        signerB.ValidateTrailer(trailers, expectedSig.ToUpperInvariant()).Should().BeTrue();
        signerB.CurrentSignature.Should().Be(expectedSig);
    }

    [TestMethod]
    public void DisposedSigner_ThrowsObjectDisposedExceptionOnAllOperations()
    {
        var signer = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        signer.Dispose();

        // Idempotent dispose
        signer.Dispose();

        Action actCurrentSignature = () => _ = signer.CurrentSignature;
        actCurrentSignature.Should().Throw<ObjectDisposedException>();

        Action actComputeChunk = () => signer.ComputeChunkSignature(new byte[] { 1 });
        actComputeChunk.Should().Throw<ObjectDisposedException>();

        Action actValidateChunk = () => signer.ValidateChunk(new byte[] { 1 }, "sig");
        actValidateChunk.Should().Throw<ObjectDisposedException>();

        Action actComputeTrailer = () => signer.ComputeTrailerSignature(new SortedDictionary<string, string>());
        actComputeTrailer.Should().Throw<ObjectDisposedException>();

        Action actValidateTrailer = () => signer.ValidateTrailer(new SortedDictionary<string, string>(), "sig");
        actValidateTrailer.Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    public void EndToEnd_StreamingWithTrailers_MatchesSignaturesAcrossSigners()
    {
        using var client = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);
        using var server = new AwsChunkSigner("20261119T143803Z", "us-east-1", "s3", ValidSigningKey, SeedSignature);

        byte[] chunk1 = Encoding.UTF8.GetBytes("Part 1 payload");
        byte[] chunk2 = Encoding.UTF8.GetBytes("Part 2 payload");

        // Chunk 1
        string clientSig1 = client.ComputeChunkSignature(chunk1);
        server.ValidateChunk(chunk1, clientSig1).Should().BeTrue();
        server.CurrentSignature.Should().Be(clientSig1);

        // Chunk 2
        string clientSig2 = client.ComputeChunkSignature(chunk2);
        server.ValidateChunk(chunk2, clientSig2).Should().BeTrue();
        server.CurrentSignature.Should().Be(clientSig2);

        // Final zero-length chunk
        string clientFinalSig = client.ComputeChunkSignature(Array.Empty<byte>());
        server.ValidateChunk(null, clientFinalSig).Should().BeTrue();
        server.CurrentSignature.Should().Be(clientFinalSig);

        // Trailers
        var trailers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-checksum-sha256"] = "qUqP5cyxm6YcTAhz05Hph5gvu9M="
        };
        string clientTrailerSig = client.ComputeTrailerSignature(trailers);
        server.ValidateTrailer(trailers, clientTrailerSig).Should().BeTrue();
        server.CurrentSignature.Should().Be(clientTrailerSig);
    }
}
