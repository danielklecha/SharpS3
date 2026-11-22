namespace SharpS3.Tests.Unit;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
public class SharpS3EndpointExtensionsTests
{
    private WebApplication? _app;
    private HttpClient? _client;
    private readonly InMemoryStorageBackend _backend = new();

    [TestInitialize]
    public async Task Setup()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
        });

        builder.Services.AddSingleton(new SharpS3Settings { EnableSignatures = false });
        builder.Services.AddSingleton<ISharpS3Server, SharpS3Server>();
        builder.Services.AddSingleton<IS3StorageBackend>(_backend);
        builder.Services.AddSingleton<S3RequestDispatcher>();

        _app = builder.Build();
        _app.MapSharpS3("{**path}");

        await _app.StartAsync();

        var serverAddresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var serviceUrl = serverAddresses!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(serviceUrl) };
    }

    [TestCleanup]
    public async Task Teardown()
    {
        _client?.Dispose();
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task MapSharpS3_Put_Get_Delete_Bucket_Works()
    {
        // 1. Put Bucket
        var putBucketResp = await _client!.PutAsync("/test-bucket", null);
        putBucketResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. Put Object
        var putObjResp = await _client.PutAsync("/test-bucket/file.txt", new StringContent("Hello Minimal API S3!", Encoding.UTF8, "text/plain"));
        putObjResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Get Object
        var getObjResp = await _client.GetAsync("/test-bucket/file.txt");
        getObjResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await getObjResp.Content.ReadAsStringAsync();
        content.Should().Be("Hello Minimal API S3!");

        // 4. Delete Object
        var delObjResp = await _client.DeleteAsync("/test-bucket/file.txt");
        delObjResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 5. Delete Bucket
        var delBucketResp = await _client.DeleteAsync("/test-bucket");
        delBucketResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [TestMethod]
    public async Task MapSharpS3_Returns_Xml_Error_On_NotFound()
    {
        var resp = await _client!.GetAsync("/non-existent-bucket/non-existent.txt");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("<Code>NoSuchBucket</Code>");
    }
}
