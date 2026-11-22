namespace SharpS3;

using System;
using System.Threading.Tasks;
using SharpS3.Models;
using SharpS3.Protocol;

/// <summary>
/// Configuration settings for the SharpS3 engine.
/// </summary>
public class SharpS3Settings
{
    /// <summary>
    /// Enable or disable AWS signature validation. Dependent upon GetSecretKeyAsync being set.
    /// AWS Signature V4 is validated when this value is true.
    /// </summary>
    public bool EnableSignatures { get; set; } = false;

    /// <summary>
    /// Enable or disable legacy AWS Signature V2 validation.
    /// Has no effect unless EnableSignatures is true.
    /// </summary>
    public bool EnableSignatureV2 { get; set; } = false;

    /// <summary>
    /// When true, suffix range requests (Range: bytes=-N) route to ObjectReadRange with RangeSuffixLength set.
    /// Default is true.
    /// </summary>
    public bool RouteSuffixRangesToReadRange { get; set; } = true;

    /// <summary>
    /// When true, standard CORS headers are emitted for requests carrying an Origin header.
    /// </summary>
    public bool EmitCorsHeaders { get; set; } = false;

    /// <summary>
    /// Optional allowed CORS origins. If populated, Access-Control-Allow-Origin only reflects matches from this list.
    /// If empty, wildcard '*' is used when EmitCorsHeaders is true.
    /// </summary>
    public System.Collections.Generic.List<string> AllowedCorsOrigins { get; set; } = new();

    /// <summary>
    /// When true, validates that request timestamps for header-based requests fall within MaxClockSkew of the server clock.
    /// Defaults to true in production to prevent replay attacks.
    /// </summary>
    public bool ValidateTimestampSkew { get; set; } = true;

    /// <summary>
    /// Maximum allowed clock skew between request timestamp and server time when ValidateTimestampSkew is true.
    /// Default is 15 minutes per AWS S3 specification.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When true, restricts virtual-hosted bucket routing strictly to configured BaseDomains,
    /// rejecting fallback resolution for arbitrary hostnames. Default is false.
    /// </summary>
    public bool RestrictVirtualHostingToBaseDomains { get; set; } = false;

    /// <summary>
    /// Base domains for virtual-hosted bucket routing (e.g. "s3.amazonaws.com").
    /// If the request hostname is not in BaseDomains, the first segment before the dot is assumed
    /// to be the subdomain representing the bucket/folder in structure, and the remainder is the domain without subdomain.
    /// Can be populated directly or loaded from configuration (e.g. SharpS3:BaseDomains).
    /// </summary>
    public System.Collections.Generic.List<string> BaseDomains { get; set; } = new();

    /// <summary>
    /// Function to resolve base domains for virtual-hosted bucket routing (e.g., bucket.s3.domain.com -> s3.domain.com).
    /// If null, BaseDomains list and fallback logic are used.
    /// </summary>
    public Func<string, string?>? FindMatchingBaseDomain { get; set; }

    /// <summary>
    /// Resolves the matching base domain for a given hostname.
    /// If hostname is not in BaseDomains, assumes domain without subdomain and subdomain is folder in structure.
    /// </summary>
    /// <param name="hostname">Request hostname without port.</param>
    /// <returns>The matched base domain, or null if none matched.</returns>
    public string? GetMatchingBaseDomain(string hostname)
    {
        if (FindMatchingBaseDomain != null)
        {
            return FindMatchingBaseDomain(hostname);
        }

        if (string.IsNullOrWhiteSpace(hostname))
            return null;

        // If hostname is directly in BaseDomains, it is the domain itself without a subdomain (PathStyle)
        foreach (var baseDomain in BaseDomains)
        {
            if (string.IsNullOrWhiteSpace(baseDomain))
                continue;
            string domain = baseDomain.Trim().TrimStart('.');
            if (hostname.Equals(domain, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        // Check if hostname matches any configured base domain suffix (e.g. bucket.s3.amazonaws.com -> s3.amazonaws.com)
        foreach (var baseDomain in BaseDomains)
        {
            if (string.IsNullOrWhiteSpace(baseDomain))
                continue;
            string domain = baseDomain.Trim().TrimStart('.');
            if (hostname.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase))
            {
                return domain;
            }
        }

        if (hostname.EndsWith(".", StringComparison.Ordinal))
            return null;

        // If RestrictVirtualHostingToBaseDomains is false, fallback to assuming subdomain is bucket and domain is base domain.
        if (!RestrictVirtualHostingToBaseDomains)
        {
            int firstDot = hostname.IndexOf('.');
            if (firstDot > 0 && firstDot < hostname.Length - 1)
            {
                return hostname.Substring(firstDot + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// Known access keys and secret access keys for signature validation.
    /// Used when GetSecretKeyAsync is not explicitly specified.
    /// Can be populated directly or loaded from configuration (e.g. SharpS3:Credentials).
    /// </summary>
    public System.Collections.Generic.Dictionary<string, string> Credentials { get; set; } = new(System.StringComparer.Ordinal);

    /// <summary>
    /// Asynchronous function to retrieve the secret key corresponding to an AWS access key.
    /// Required when EnableSignatures is true and Credentials dictionary does not contain the key.
    /// </summary>
    public Func<string, Task<string?>>? GetSecretKeyAsync { get; set; }

    /// <summary>
    /// Determines whether an anonymous request without credentials is permitted.
    /// </summary>
    public Func<IS3Request, Task<bool>>? IsAnonymousRequestAllowedAsync { get; set; }

    /// <summary>
    /// When true, signature validation runs before PreRequestHandler, so PreRequestHandler only sees authenticated
    /// (or anonymously permitted) requests. When false (default), PreRequestHandler runs first and sees unauthenticated input.
    /// Has no effect when EnableSignatures is false.
    /// </summary>
    public bool ValidateSignaturesBeforePreRequestHandler { get; set; } = false;

    /// <summary>
    /// Optional pre-request hook. Return an S3ServerResponse to short-circuit and send an immediate response.
    /// </summary>
    public Func<IS3Request, Task<S3ServerResponse?>>? PreRequestHandler { get; set; }

    /// <summary>
    /// Callback method invoked after signature validation (when EnableSignatures is true) and before the request is routed
    /// to an operation handler. Return an S3ServerResponse to terminate the request, or null to continue routing.
    /// </summary>
    public Func<IS3Request, Task<S3ServerResponse?>>? AuthenticatedRequestHandler { get; set; }

    /// <summary>
    /// Callback method invoked when no matching S3 API handler could be found or for custom fallback handling.
    /// </summary>
    public Func<IS3Request, Task<S3ServerResponse?>>? DefaultRequestHandler { get; set; }

    /// <summary>
    /// Optional post-request hook invoked after a response has been generated.
    /// </summary>
    public Func<IS3Request, S3ServerResponse, Task>? PostRequestHandler { get; set; }

    /// <summary>
    /// Logging settings.
    /// </summary>
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>
    /// Operation limits (payload size, max parts, etc.).
    /// </summary>
    public OperationLimitsSettings OperationLimits { get; set; } = new();

    /// <summary>
    /// OpenTelemetry settings.
    /// </summary>
    public S3ServerTelemetrySettings Telemetry { get; set; } = new();

    /// <summary>
    /// Custom logger delegate.
    /// </summary>
    public Action<string>? Logger { get; set; }
}
