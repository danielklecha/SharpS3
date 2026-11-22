# ASP.NET Core Integration

SharpS3 is designed to be **transport-agnostic** with zero dependency on ASP.NET Core or web host runtimes. It seamlessly integrates into ASP.NET Core 8.0, 9.0, and 10.0 Minimal APIs, running inside your existing Kestrel web host on the same port alongside your normal application endpoints.

Because the core `SharpS3` library does not take a dependency on `Microsoft.AspNetCore.App`, an extension method is used to bridge ASP.NET Core HTTP requests to SharpS3.

---

## 1. Minimal API Extension Method (`SharpS3EndpointExtensions`)

Add the following extension class to your ASP.NET Core project. It binds all standard Amazon S3 REST verbs (`GET`, `PUT`, `POST`, `DELETE`, `HEAD`) to `ISharpS3Server` and streams responses back through Kestrel:

```csharp
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
```

---

## 2. Minimal API Endpoint Registration (`Program.cs`)

Once the extension class above is added, register SharpS3 services in your dependency injection container and map the catch-all endpoint:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Extensions;
using SharpS3.Storage;

var builder = WebApplication.CreateBuilder(args);

// 1. Register SharpS3 settings and services
var settings = new SharpS3Settings();
builder.Configuration.GetSection("SharpS3").Bind(settings);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<ISharpS3Server, SharpS3Server>();
builder.Services.AddSingleton<IS3StorageBackend>(_ => new FileSystemStorageBackend(@"C:\Data\S3Storage"));
builder.Services.AddSingleton<S3RequestDispatcher>();

var app = builder.Build();

// 2. Map S3 REST API catch-all endpoint
app.MapSharpS3("{**path}");

app.Run();
```

---

## 3. Kestrel Streaming Configuration

When streaming large files (up to several gigabytes), ensure Kestrel does not enforce default request body limits:

```csharp
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestBodySize = null; // Unlimited for S3 streaming uploads
});
```

---

## 4. Configuration via `appsettings.json`

SharpS3 settings can be configured directly in your `appsettings.json`:

```json
{
  "SharpS3": {
    "EnableSignatures": true,
    "EnableSignatureV2": false,
    "RouteSuffixRangesToReadRange": true,
    "EmitCorsHeaders": true,
    "AllowedCorsOrigins": [
      "https://example.com"
    ],
    "StorageBackend": "FileSystem",
    "FileSystem": {
      "RootDirectory": "C:\\Data\\S3Storage"
    },
    "Credentials": {
      "AKIAIOSFODNN7EXAMPLE": "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"
    }
  }
}
```

---

## 5. Windows Service & systemd Daemon Hosting

SharpS3 can be deployed as an autonomous OS service:

```csharp
// Windows Service
builder.Host.UseWindowsService();

// Linux systemd Daemon
builder.Host.UseSystemd();
```
