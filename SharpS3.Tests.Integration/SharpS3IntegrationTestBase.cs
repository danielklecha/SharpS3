namespace SharpS3.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Moq.Protected;
using SharpS3;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;

public abstract class SharpS3IntegrationTestBase
{
    protected static readonly Uri TestEndpoint = new("http://127.0.0.1:8000");

    protected static Mock<HttpMessageHandler> GetMockOfHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
    {
        var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(handlerFunc);
        return handlerMock;
    }

    protected static SharpS3Client CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc,
        SharpS3ClientOptions? clientOptions = null)
    {
        var options = clientOptions ?? new SharpS3ClientOptions
        {
            Endpoint = TestEndpoint,
            ForcePathStyle = true,
            AccessKey = "test-access-key",
            SecretKey = "test-secret-key"
        };

        var handlerMock = GetMockOfHttpMessageHandler(handlerFunc);
        var httpClient = new HttpClient(handlerMock.Object);
        return new SharpS3Client(options, httpClient);
    }

    protected static async Task<S3Request> ReceiveServerRequestAsync(
        ISharpS3Server server,
        HttpRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        string method = request.Method.Method;
        string path = request.RequestUri!.AbsolutePath;
        var query = ParseQuery(request.RequestUri.Query);
        var headers = ParseHeaders(request);
        Stream bodyStream = Stream.Null;

        if (request.Content != null)
        {
            var contentStream = await request.Content.ReadAsStreamAsync(cancellationToken);
            var ms = new MemoryStream();
            await contentStream.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;
            bodyStream = ms;
        }

        var s3Req = await server.ReceiveRequestAsync(method, path, query, headers, bodyStream, cancellationToken);
        return (S3Request)s3Req;
    }

    protected static S3ServerResponse CreateEmptyResponse(int statusCode, params (string Key, string Value)[] headers)
    {
        var resp = new S3ServerResponse { StatusCode = statusCode };
        foreach (var (k, v) in headers)
        {
            resp.Headers[k] = v;
        }
        return resp;
    }

    protected static S3ServerResponse CreateXmlResponse<T>(int statusCode, T model, params (string Key, string Value)[] headers)
    {
        string xml = SerializationHelper.SerializeXml(model, true);
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        var resp = new S3ServerResponse
        {
            StatusCode = statusCode,
            ContentType = "application/xml",
            BodyStream = new MemoryStream(bytes)
        };
        resp.Headers["Content-Length"] = bytes.Length.ToString();
        foreach (var (k, v) in headers)
        {
            resp.Headers[k] = v;
        }
        return resp;
    }

    protected static S3ServerResponse CreateStreamResponse(
        int statusCode,
        Stream stream,
        string contentType,
        params (string Key, string Value)[] headers)
    {
        var resp = new S3ServerResponse
        {
            StatusCode = statusCode,
            ContentType = contentType,
            BodyStream = stream
        };
        foreach (var (k, v) in headers)
        {
            resp.Headers[k] = v;
        }
        return resp;
    }

    protected static HttpResponseMessage ToHttpResponseMessage(S3ServerResponse serverResponse)
    {
        var httpResponse = new HttpResponseMessage((HttpStatusCode)serverResponse.StatusCode);

        if (serverResponse.BodyStream != Stream.Null)
        {
            var ms = new MemoryStream();
            serverResponse.BodyStream.CopyTo(ms);
            ms.Position = 0;
            httpResponse.Content = new StreamContent(ms);
        }
        else
        {
            httpResponse.Content = new ByteArrayContent(Array.Empty<byte>());
        }

        if (!string.IsNullOrEmpty(serverResponse.ContentType))
        {
            httpResponse.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(serverResponse.ContentType);
        }

        foreach (var (key, value) in serverResponse.Headers)
        {
            if (!httpResponse.Headers.TryAddWithoutValidation(key, value))
            {
                httpResponse.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return httpResponse;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        if (query.StartsWith("?")) query = query.Substring(1);

        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0)
            {
                string key = Uri.UnescapeDataString(part);
                dict[key] = string.Empty;
            }
            else
            {
                string key = Uri.UnescapeDataString(part.Substring(0, eq));
                string val = Uri.UnescapeDataString(part.Substring(eq + 1));
                dict[key] = val;
            }
        }
        return dict;
    }

    private static Dictionary<string, string[]> ParseHeaders(HttpRequestMessage request)
    {
        var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
        {
            dict[h.Key] = h.Value.ToArray();
        }
        if (request.Content != null)
        {
            foreach (var h in request.Content.Headers)
            {
                dict[h.Key] = h.Value.ToArray();
            }
        }
        return dict;
    }
}
