namespace SharpS3.Tests.Unit;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal class MockHttpMessageHandler : HttpMessageHandler, IHttpClientFactory
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncHandler { get; set; }
    public HttpRequestMessage? LastRequest { get; private set; }

    public MockHttpMessageHandler() { }
    public HttpClient CreateClient(string name) => new HttpClient(this, disposeHandler: false);

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        Handler = handler;
    }

    public void RegisterHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        AsyncHandler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (AsyncHandler != null)
        {
            return AsyncHandler(request, cancellationToken);
        }
        if (Handler != null)
        {
            return Task.FromResult(Handler(request));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
