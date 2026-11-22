namespace SharpS3;

using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpS3.Exceptions;
using SharpS3.Protocol;

/// <summary>
/// Transport-agnostic S3 protocol engine interface.
/// Operates entirely on raw HTTP metadata and System.IO.Stream with zero dependency on any web server.
/// </summary>
public interface ISharpS3Server
{
    /// <summary>
    /// Parses an incoming HTTP request of any method into a strongly-typed S3 request model.
    /// </summary>
    /// <param name="httpMethod">HTTP method (GET, PUT, POST, DELETE, HEAD).</param>
    /// <param name="path">Request path (e.g. /bucket/key or /).</param>
    /// <param name="queryParameters">Query string parameters.</param>
    /// <param name="headers">HTTP headers.</param>
    /// <param name="bodyStream">Input body stream from the HTTP request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Strongly typed IS3Request.</returns>
    Task<IS3Request> ReceiveRequestAsync(
        string httpMethod,
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        Stream? bodyStream = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an incoming HTTP GET request into a strongly-typed S3 request model.
    /// </summary>
    Task<IS3Request> ReceiveGetRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an incoming HTTP HEAD request into a strongly-typed S3 request model.
    /// </summary>
    Task<IS3Request> ReceiveHeadRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an incoming HTTP PUT request into a strongly-typed S3 request model.
    /// </summary>
    Task<IS3Request> ReceivePutRequestAsync(
        string path,
        Stream bodyStream,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an incoming HTTP POST request into a strongly-typed S3 request model.
    /// </summary>
    Task<IS3Request> ReceivePostRequestAsync(
        string path,
        Stream bodyStream,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an incoming HTTP DELETE request into a strongly-typed S3 request model.
    /// </summary>
    Task<IS3Request> ReceiveDeleteRequestAsync(
        string path,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, string[]>? headers = null,
        Stream? bodyStream = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes a typed S3 response model into HTTP status code, headers, and output stream.
    /// </summary>
    /// <param name="response">Strongly typed S3 response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>S3ServerResponse ready to be written to the HTTP response.</returns>
    Task<S3ServerResponse> CreateResponseAsync(
        IS3Response response,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Formats an S3 exception into the standard Amazon S3 XML error envelope.
    /// </summary>
    /// <param name="exception">The S3 exception.</param>
    /// <param name="requestId">Optional request identifier for tracing.</param>
    /// <returns>S3ServerResponse with proper error status code and XML payload.</returns>
    S3ServerResponse CreateErrorResponse(S3Exception exception, string? requestId = null);
}
