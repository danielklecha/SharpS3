namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Extensions;
using SharpS3.Storage;

[TestClass]
public class FileSystemServerTests
{
    private string _TempDir = null!;
    private SharpS3Client? _Client;

    [TestInitialize]
    public void Setup()
    {
        _TempDir = Path.Combine(Path.GetTempPath(), "SharpS3_ServerTests_" + Guid.NewGuid().ToString("N"));

        var settings = new SharpS3Settings { EnableSignatures = false };
        var backend = new FileSystemStorageBackend(_TempDir);
        var server = new SharpS3Server(settings);
        var dispatcher = new S3RequestDispatcher(backend);
        var handler = new SharpS3TestHttpHandler(server, dispatcher);
        var httpClient = new HttpClient(handler);

        var options = new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            ForcePathStyle = true
        };

        _Client = new SharpS3Client(options, httpClient);
    }

    [TestCleanup]
    public void Teardown()
    {
        _Client?.Dispose();

        try
        {
            if (Directory.Exists(_TempDir))
                Directory.Delete(_TempDir, recursive: true);
        }
        catch
        {
        }
    }

    [TestMethod]
    public async Task Server_With_FileSystemBackend_Persists_And_Serves_Objects()
    {
        const string bucket = "persistent-bucket";
        const string key = "logs/today.txt";
        const string content = "Logging line 1: server started.\nLogging line 2: request received.";

        await _Client!.CreateBucketAsync(bucket);

        // Verify directory was created on physical disk
        string bucketDir = Path.Combine(_TempDir, bucket);
        Directory.Exists(bucketDir).Should().BeTrue();

        // Put object
        await _Client.PutObjectAsync(bucket, key, content, "text/plain");

        // Verify file and sidecar on disk
        string filePath = Path.Combine(bucketDir, "logs", "today.txt");
        string metaPath = Path.Combine(bucketDir, "logs", "today.txt.s3meta.xml");
        File.Exists(filePath).Should().BeTrue();
        File.Exists(metaPath).Should().BeTrue();

        // Read back via S3 API
        using (var resp = await _Client.GetObjectAsync(bucket, key))
        {
            string body = await resp.ReadAsStringAsync();
            body.Should().Be(content);
        }

        // Delete via S3 API
        await _Client.DeleteObjectAsync(bucket, key);
        File.Exists(filePath).Should().BeFalse();
        File.Exists(metaPath).Should().BeFalse();

        // Delete bucket
        await _Client.DeleteBucketAsync(bucket);
        Directory.Exists(bucketDir).Should().BeFalse();
    }
}
