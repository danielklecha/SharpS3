namespace SharpS3.Extensions;

using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;

/// <summary>
/// ASP.NET Core extension methods for mapping SharpS3 endpoints.
/// </summary>
public static class SharpS3EndpointExtensions
{
    /// <summary>
    /// Maps Amazon S3 REST API methods on the specified path pattern.
    /// </summary>
    public static IEndpointConventionBuilder MapSharpS3(this IEndpointRouteBuilder app, string pattern = "{**path}")
    {
        return app.MapMethods(pattern, ["GET", "PUT", "POST", "DELETE", "HEAD"], async (
            HttpContext context,
            ISharpS3Server s3Server,
            S3RequestDispatcher dispatcher) =>
        {
            var req = context.Request;
            var query = req.Query.ToDictionary(k => k.Key, v => v.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var headers = req.Headers.ToDictionary(k => k.Key, v => v.Value.Select(x => x ?? "").ToArray(), StringComparer.OrdinalIgnoreCase);

            try
            {
                var s3Request = await s3Server.ReceiveRequestAsync(
                    req.Method,
                    req.Path.Value ?? "/",
                    query,
                    headers,
                    req.Body,
                    context.RequestAborted).ConfigureAwait(false);

                using var response = await dispatcher.DispatchAsync(s3Request, context.RequestAborted).ConfigureAwait(false);

                context.Response.StatusCode = response.StatusCode;
                if (!string.IsNullOrEmpty(response.ContentType))
                {
                    context.Response.ContentType = response.ContentType;
                }

                foreach (var h in response.Headers)
                {
                    context.Response.Headers[h.Key] = h.Value;
                }

                if (response.BodyStream != Stream.Null)
                {
                    await response.BodyStream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!context.Response.HasStarted)
                {
                    var s3Ex = ex as S3Exception ?? new S3Exception(new Error(ErrorCode.InternalError));
                    using var error = s3Server.CreateErrorResponse(s3Ex, context.TraceIdentifier);
                    context.Response.StatusCode = error.StatusCode;
                    context.Response.ContentType = error.ContentType ?? "application/xml";

                    foreach (var h in error.Headers)
                    {
                        context.Response.Headers[h.Key] = h.Value;
                    }

                    if (error.BodyStream != Stream.Null)
                    {
                        await error.BodyStream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
                    }
                }
            }
        });
    }
}
