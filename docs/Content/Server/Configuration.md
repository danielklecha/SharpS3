# Server Configuration & Settings

`SharpS3Settings` controls protocol features, signature validation, routing rules, request pipeline hooks, operation limits, and telemetry.

---

## 1. Authentication & Signatures

```csharp
var settings = new SharpS3Settings
{
    // Enable or disable AWS SigV4 signature verification (default: false)
    EnableSignatures = true,

    // Enable legacy AWS Signature Version 2 validation (default: false)
    EnableSignatureV2 = false,

    // Validate request timestamps to prevent replay attacks (default: true)
    ValidateTimestampSkew = true,

    // Maximum allowed clock skew (default: 15 minutes per AWS S3 specification)
    MaxClockSkew = TimeSpan.FromMinutes(15)
};
```

### Static Credentials vs. Dynamic Secret Key Retrieval

You can populate known credentials statically via the `Credentials` dictionary or retrieve them dynamically (e.g. from a database or secret vault) using `GetSecretKeyAsync`:

```csharp
// Static dictionary: AccessKey -> SecretKey
settings.Credentials["AKIAIOSFODNN7EXAMPLE"] = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";

// Dynamic lookup callback
settings.GetSecretKeyAsync = async (string accessKey) =>
{
    return await mySecretStore.FindSecretAsync(accessKey);
};

// Anonymous requests policy
settings.IsAnonymousRequestAllowedAsync = async (IS3Request req) =>
{
    // Allow unauthenticated GET requests to public assets
    return req.Method == "GET" && req.Key?.StartsWith("public/") == true;
};
```

---

## 2. Virtual-Hosted vs. Path-Style Routing

SharpS3 automatically supports both path-style (`http://localhost:8000/my-bucket/key`) and virtual-hosted style (`http://my-bucket.s3.company.com/key`) requests.

```csharp
// Configure base domains for virtual-hosted requests
settings.BaseDomains.Add("s3.company.com");
settings.BaseDomains.Add("s3.amazonaws.com");

// Restrict virtual-hosted bucket routing strictly to configured BaseDomains (default: false)
settings.RestrictVirtualHostingToBaseDomains = true;
```

---

## 3. Pipeline Hooks

SharpS3 provides four lifecycle hooks to customize request processing:

```csharp
// 1. PreRequestHandler: runs before signature validation (or after if ValidateSignaturesBeforePreRequestHandler is true)
settings.PreRequestHandler = async (IS3Request req) =>
{
    if (req.Headers.ContainsKey("X-Blocked-Client"))
    {
        return new S3ServerResponse(403, "Client blocked", "text/plain");
    }
    return null; // Continue routing
};

// 2. AuthenticatedRequestHandler: runs after successful signature validation
settings.AuthenticatedRequestHandler = async (IS3Request req) =>
{
    Console.WriteLine($"Authenticated request from AccessKey: {req.AccessKey}");
    return null; // Continue routing
};

// 3. DefaultRequestHandler: fallback when no matching S3 API handler exists
settings.DefaultRequestHandler = async (IS3Request req) =>
{
    return new S3ServerResponse(404, "Custom not found handler", "text/plain");
};

// 4. PostRequestHandler: invoked after the response has been generated
settings.PostRequestHandler = async (IS3Request req, S3ServerResponse resp) =>
{
    Console.WriteLine($"Handled {req.Method} {req.Path} -> {resp.StatusCode}");
};
```

---

## 4. CORS (Cross-Origin Resource Sharing)

```csharp
settings.EmitCorsHeaders = true;
settings.AllowedCorsOrigins.Add("https://app.example.com");
settings.AllowedCorsOrigins.Add("https://admin.example.com");
```

---

## 5. Operation Limits

`OperationLimitsSettings` guards the server against denial-of-service and memory exhaustion:

```csharp
settings.OperationLimits = new OperationLimitsSettings
{
    MaxPayloadSize = 5L * 1024 * 1024 * 1024, // 5 GB max single PUT
    MaxPartSize = 5L * 1024 * 1024 * 1024,    // 5 GB max part size
    MaxParts = 10000,                          // Up to 10,000 parts per multipart upload
    MaxKeyLength = 1024                        // 1024 character max object key length
};
```
