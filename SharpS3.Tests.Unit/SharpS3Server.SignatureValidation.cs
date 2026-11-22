namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Dispatch;
using SharpS3.Extensions;
using SharpS3.Models;
using SharpS3.Storage;

public partial class SharpS3ServerTests
{
    private const string ValidAccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string ValidSecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string AltAccessKey = "AKIA5F2T7Q9M3P8R1X4Z";
    private const string AltSecretKey = "v9bK8xP2qL3mN7wF5zR1yT4uE6jH8sD0gA2cE4iK";

    private readonly InMemoryStorageBackend _Backend = new();
    private SharpS3TestHttpHandler _Handler = null!;

    [TestInitialize]
    public void Setup()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true
        };
        settings.Credentials[ValidAccessKey] = ValidSecretKey;
        settings.Credentials[AltAccessKey] = AltSecretKey;

        var server = new SharpS3Server(settings);
        var dispatcher = new S3RequestDispatcher(_Backend);
        _Handler = new SharpS3TestHttpHandler(server, dispatcher);
    }

    private SharpS3Client CreateClient(string? accessKey, string? secretKey)
    {
        return new SharpS3Client(new SharpS3ClientOptions
        {
            Endpoint = new Uri("http://localhost:8000"),
            ForcePathStyle = true,
            AccessKey = accessKey,
            SecretKey = secretKey
        }, new HttpClient(_Handler, disposeHandler: false));
    }

    [TestMethod]
    public async Task ValidCredentials_Succeeds()
    {
        using var client = CreateClient(ValidAccessKey, ValidSecretKey);
        await client.CreateBucketAsync("test-auth-bucket");
        var result = await client.ListBucketsAsync();
        result.Buckets.BucketList.Should().Contain(b => b.Name == "test-auth-bucket");
    }

    [TestMethod]
    public async Task AlternateValidCredentials_Succeeds()
    {
        using var client = CreateClient(AltAccessKey, AltSecretKey);
        await client.CreateBucketAsync("test-alt-bucket");
        var result = await client.ListBucketsAsync();
        result.Buckets.BucketList.Should().Contain(b => b.Name == "test-alt-bucket");
    }

    [TestMethod]
    public async Task AnonymousRequest_WhenSignaturesEnabled_ReturnsAccessDenied()
    {
        using var client = CreateClient(null, null);
        var act = async () => await client.ListBucketsAsync();
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task UnknownAccessKey_ReturnsAccessDenied()
    {
        using var client = CreateClient("UNKNOWNACCESSKEY1234", "dummysecretkeyvalue1234567890abcdefghij");
        var act = async () => await client.ListBucketsAsync();
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [TestMethod]
    public async Task InvalidSecretKey_ReturnsSignatureDoesNotMatch()
    {
        using var client = CreateClient(ValidAccessKey, "WRONGSECRETKEY1234567890abcdefghijklmnop");
        var act = async () => await client.ListBucketsAsync();
        var ex = await act.Should().ThrowAsync<SharpS3ClientException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
