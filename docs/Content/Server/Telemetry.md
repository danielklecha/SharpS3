# Telemetry & Observability

SharpS3 includes built-in observability instrumented using standard .NET `System.Diagnostics.Metrics` and `System.Diagnostics.ActivitySource`. It provides zero-overhead distributed tracing and metrics that integrate natively with OpenTelemetry, Prometheus, Grafana, and Azure Application Insights.

---

## 1. Pipeline Tracing Stages

Each request processed by `SharpS3Server` and `S3RequestDispatcher` generates an activity span tracking execution stages:

| Stage Name | Description |
|---|---|
| `StageParse` | Header decoding, query string parsing, URL normalization, and `S3Request` initialization. |
| `StageSignatureValidation` | SigV4 / SigV2 authorization header parsing, key derivation, and HMAC calculation. |
| `StagePreRequest` | Execution of user-defined `PreRequestHandler` hook. |
| `StageDispatch` | Operation matching and routing. |
| `StageStorage` | Execution of `IS3StorageBackend` physical operations (disk I/O, streams). |
| `StageSerialize` | Formatting `S3ServerResponse` and XML payload serialization. |

---

## 2. Metrics

SharpS3 instruments the following metric instruments:

* `s3.server.requests`: Counter tracking total S3 requests partitioned by HTTP method, action, bucket, and status code.
* `s3.server.request.duration`: Histogram tracking request latency in milliseconds across stages.
* `s3.server.bytes.received`: Total bytes uploaded via PUT and POST operations.
* `s3.server.bytes.sent`: Total bytes downloaded via GET operations.
* `s3.server.active_requests`: UpDownCounter tracking concurrent active requests.

---

## 3. Configuring OpenTelemetry in ASP.NET Core

Add OpenTelemetry packages to your server and register the SharpS3 sources:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SharpS3.Telemetry;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(S3ServerTelemetryNames.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(S3ServerTelemetryNames.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());
```
