namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;

/// <summary>
/// In-memory HTTP message handler that routes requests directly to ISharpS3Server and S3RequestDispatcher
/// without running an ASP.NET Core WebApplication or opening physical TCP ports.
/// </summary>
public class SharpS3TestHttpHandler : HttpMessageHandler
{
    private readonly ISharpS3Server _s3Server;
    private readonly S3RequestDispatcher _dispatcher;

    public SharpS3TestHttpHandler(ISharpS3Server s3Server, S3RequestDispatcher dispatcher)
    {
        _s3Server = s3Server ?? throw new ArgumentNullException(nameof(s3Server));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? new Uri("http://localhost/");
        var path = uri.AbsolutePath;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var queryString = uri.Query.TrimStart('?');
            foreach (var part in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]);
                var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                query[key] = val;
            }
        }

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            headers[h.Key] = h.Value.ToArray();
        }
        if (request.Content != null)
        {
            foreach (var h in request.Content.Headers)
            {
                headers[h.Key] = h.Value.ToArray();
            }
        }

        Stream bodyStream = Stream.Null;
        if (request.Content != null)
        {
            bodyStream = await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var s3Request = await _s3Server.ReceiveRequestAsync(
                request.Method.Method,
                path,
                query,
                headers,
                bodyStream,
                cancellationToken).ConfigureAwait(false);

            using var s3Response = await _dispatcher.DispatchAsync(s3Request, cancellationToken).ConfigureAwait(false);

            var httpResponse = new HttpResponseMessage((HttpStatusCode)s3Response.StatusCode);

            if (s3Response.BodyStream != Stream.Null)
            {
                var ms = new MemoryStream();
                await s3Response.BodyStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                httpResponse.Content = new StreamContent(ms);
            }
            else
            {
                httpResponse.Content = new ByteArrayContent(Array.Empty<byte>());
            }

            if (!string.IsNullOrEmpty(s3Response.ContentType))
            {
                if (MediaTypeHeaderValue.TryParse(s3Response.ContentType, out var parsedContentType))
                {
                    httpResponse.Content.Headers.ContentType = parsedContentType;
                }
                else
                {
                    httpResponse.Content.Headers.TryAddWithoutValidation("Content-Type", s3Response.ContentType);
                }
            }

            foreach (var h in s3Response.Headers)
            {
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(h.Value, out var cl))
                {
                    httpResponse.Content.Headers.ContentLength = cl;
                    continue;
                }
                if (!httpResponse.Headers.TryAddWithoutValidation(h.Key, h.Value))
                {
                    httpResponse.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            return httpResponse;
        }
        catch (Exception ex)
        {
            var s3Ex = ex as S3Exception ?? new S3Exception(new Error(ErrorCode.InternalError));
            using var error = _s3Server.CreateErrorResponse(s3Ex, Guid.NewGuid().ToString("N"));
            var httpResponse = new HttpResponseMessage((HttpStatusCode)error.StatusCode);

            var ms = new MemoryStream();
            if (error.BodyStream != Stream.Null)
            {
                await error.BodyStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
            }
            httpResponse.Content = new StreamContent(ms);

            string errContentType = error.ContentType ?? "application/xml";
            if (MediaTypeHeaderValue.TryParse(errContentType, out var parsedErrType))
            {
                httpResponse.Content.Headers.ContentType = parsedErrType;
            }
            else
            {
                httpResponse.Content.Headers.TryAddWithoutValidation("Content-Type", errContentType);
            }

            foreach (var h in error.Headers)
            {
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && long.TryParse(h.Value, out var cl))
                {
                    httpResponse.Content.Headers.ContentLength = cl;
                    continue;
                }
                if (!httpResponse.Headers.TryAddWithoutValidation(h.Key, h.Value))
                {
                    httpResponse.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            return httpResponse;
        }
    }
}

/// <summary>
/// Custom HttpClientFactory for Amazon S3 SDK allowing AWS SDK clients to use in-memory HttpMessageHandler.
/// </summary>
public class TestHttpClientFactory : Amazon.Runtime.HttpClientFactory
{
    private readonly HttpClient _client;

    public TestHttpClientFactory(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => _client;
}
