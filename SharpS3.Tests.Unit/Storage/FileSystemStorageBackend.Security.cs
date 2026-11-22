namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Storage;
using SharpS3.Streams;

public partial class FileSystemStorageBackendTests
{
    

    #region UploadId-Path-Traversal-Defense

    [TestMethod]
    public async Task AbortMultipartUpload_PathTraversal_DoesNotDeleteHostDirectories()
    {
        // Create a sensitive canary directory outside _UploadsDirectory
        string canaryDir = Path.Combine(_TempDir, "CanaryImportantDir");
        Directory.CreateDirectory(canaryDir);
        File.WriteAllText(Path.Combine(canaryDir, "important.txt"), "DO NOT DELETE");

        await _Backend.CreateBucketAsync("test-bucket", null);

        string[] maliciousUploadIds = new[]
        {
            "..",
            "../CanaryImportantDir",
            @"..\CanaryImportantDir",
            "../../",
            @"..\..\",
            Path.Combine("..", "CanaryImportantDir")
        };

        foreach (var badUploadId in maliciousUploadIds)
        {
            var act = () => _Backend.AbortMultipartUploadAsync("test-bucket", "some-key", badUploadId);
            (await act.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
        }

        // Verify the canary directory and file were protected and still exist
        Directory.Exists(canaryDir).Should().BeTrue();
        File.Exists(Path.Combine(canaryDir, "important.txt")).Should().BeTrue();
    }

    [TestMethod]
    public async Task UploadPart_PathTraversal_UploadId_ThrowsNoSuchUpload()
    {
        await _Backend.CreateBucketAsync("test-bucket", null);

        using var data = new MemoryStream(new byte[] { 1, 2, 3 });
        var act = () => _Backend.UploadPartAsync("test-bucket", "some-key", "../escape", 1, data, 3);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task CompleteMultipartUpload_PathTraversal_UploadId_ThrowsNoSuchUpload()
    {
        await _Backend.CreateBucketAsync("test-bucket", null);

        var parts = new CompleteMultipartUpload();
        var act = () => _Backend.CompleteMultipartUploadAsync("test-bucket", "some-key", "../escape", parts);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    [TestMethod]
    public async Task ListParts_PathTraversal_UploadId_ThrowsNoSuchUpload()
    {
        await _Backend.CreateBucketAsync("test-bucket", null);

        var act = () => _Backend.ListPartsAsync("test-bucket", "some-key", "../escape");
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchUpload);
    }

    #endregion

    #region Bucket-Name-Hardening

    [TestMethod]
    public async Task BucketName_StartingWithDot_Or_SystemDirectoryCollision_Rejected()
    {
        string[] dotBuckets = new[]
        {
            ".uploads",
            ".hidden",
            ".",
            "..",
            "...test",
            "mybucket."
        };

        foreach (var badBucket in dotBuckets)
        {
            var actCreate = () => _Backend.CreateBucketAsync(badBucket, null);
            (await actCreate.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

            var actExists = () => _Backend.BucketExistsAsync(badBucket);
            (await actExists.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);
        }
    }

    [TestMethod]
    public async Task BucketName_WindowsReservedDeviceNames_Rejected()
    {
        string[] reservedNames = new[]
        {
            "con", "CON", "prn", "PRN", "aux", "AUX", "nul", "NUL",
            "com1", "COM9", "lpt1", "LPT9"
        };

        foreach (var reserved in reservedNames)
        {
            var actCreate = () => _Backend.CreateBucketAsync(reserved, null);
            (await actCreate.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);

            var actExists = () => _Backend.BucketExistsAsync(reserved);
            (await actExists.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);
        }
    }

    [TestMethod]
    public async Task InMemoryStorageBackend_BucketNameValidation_RejectsInvalidNames()
    {
        var memBackend = new InMemoryStorageBackend();

        string[] invalidNames = new[]
        {
            "",
            "   ",
            ".uploads",
            ".hidden",
            "trailingdot.",
            "slash/in/name",
            "backslash\\in\\name"
        };

        foreach (var badName in invalidNames)
        {
            var act = () => memBackend.CreateBucketAsync(badName, null);
            (await act.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidBucketName);
        }
    }

    #endregion

    #region Metadata-and-ADS-Protection

    [TestMethod]
    public async Task ObjectKey_DirectMetadataFileAccess_Forbidden()
    {
        await _Backend.CreateBucketAsync("meta-test-bucket", null);

        string[] metaKeys = new[]
        {
            ".s3bucket.xml",
            "/.s3bucket.xml",
            "file.txt.s3meta.xml",
            "dir/nested.bin.s3meta.xml"
        };

        foreach (var metaKey in metaKeys)
        {
            // Get
            var actGet = () => _Backend.GetObjectAsync("meta-test-bucket", metaKey);
            (await actGet.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

            // Put
            using var putStream = new MemoryStream(Encoding.UTF8.GetBytes("<xml/>"));
            var actPut = () => _Backend.PutObjectAsync("meta-test-bucket", metaKey, putStream, 6, "text/xml");
            (await actPut.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);

            // Delete
            var actDel = () => _Backend.DeleteObjectAsync("meta-test-bucket", metaKey);
            (await actDel.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.AccessDenied);
        }
    }

    [TestMethod]
    public async Task ObjectKey_NtfsAlternateDataStreamColon_Forbidden()
    {
        await _Backend.CreateBucketAsync("ads-test-bucket", null);

        string[] adsKeys = new[]
        {
            "file.txt:stream",
            "file.txt:$DATA",
            "folder:hidden/file.bin"
        };

        foreach (var adsKey in adsKeys)
        {
            var actGet = () => _Backend.GetObjectAsync("ads-test-bucket", adsKey);
            (await actGet.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

            using var putStream = new MemoryStream(new byte[] { 1 });
            var actPut = () => _Backend.PutObjectAsync("ads-test-bucket", adsKey, putStream, 1, "text/plain");
            (await actPut.Should().ThrowAsync<S3Exception>())
                .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
        }
    }

    [TestMethod]
    public async Task ObjectKey_DotAndDoubleDot_RejectedAsInvalidRequest()
    {
        await _Backend.CreateBucketAsync("dot-test-bucket", null);

        var actDot = () => _Backend.GetObjectAsync("dot-test-bucket", ".");
        (await actDot.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);

        var actDotDot = () => _Backend.GetObjectAsync("dot-test-bucket", "..");
        (await actDotDot.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
    }

    #endregion

    #region Stream-Hardening

    [TestMethod]
    public async Task AwsChunkedDecodingStream_MaxChunkSize_Exceeded_ThrowsInvalidDataException()
    {
        // 0x100 = 256 bytes declared in header
        var payload = Encoding.ASCII.GetBytes("100\r\n" + new string('a', 256) + "\r\n0\r\n\r\n");
        using var ms = new MemoryStream(payload);
        using var chunkedStream = new AwsChunkedDecodingStream(ms)
        {
            MaxChunkSize = 128 // Limit to 128 bytes
        };

        byte[] buf = new byte[512];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*exceeds maximum allowed limit*");
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_MaxLineLength_Exceeded_ThrowsInvalidDataException()
    {
        // Very long header line exceeding MaxLineLength
        string longLine = new string('F', 200) + "\r\n";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(longLine));
        using var chunkedStream = new AwsChunkedDecodingStream(ms)
        {
            MaxLineLength = 50 // Limit to 50 characters
        };

        byte[] buf = new byte[64];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*exceeds maximum length*");
    }

    [TestMethod]
    public async Task AwsChunkedDecodingStream_MaxTrailersCount_Exceeded_ThrowsInvalidDataException()
    {
        // 0-length chunk with 4 trailers
        var payload = "0\r\nTrailer-A: 1\r\nTrailer-B: 2\r\nTrailer-C: 3\r\nTrailer-D: 4\r\n\r\n";
        using var ms = new MemoryStream(Encoding.ASCII.GetBytes(payload));
        using var chunkedStream = new AwsChunkedDecodingStream(ms)
        {
            MaxTrailersCount = 2 // Limit to 2 trailers
        };

        byte[] buf = new byte[64];
        var act = () => chunkedStream.ReadAsync(buf, 0, buf.Length);
        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*exceeds maximum limit*");
    }

    #endregion
}
