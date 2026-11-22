# Authentication & AWS SigV4

SharpS3 features a comprehensive implementation of AWS Signature Version 4 (SigV4), SigV4 presigned URLs, streaming chunked signature verification, and legacy SigV2.

---

## 1. AWS Signature Version 4 (SigV4)

When `EnableSignatures = true`, SharpS3 validates the `Authorization` header on incoming requests:

```
Authorization: AWS4-HMAC-SHA256 
Credential=AKIAIOSFODNN7EXAMPLE/20261121/us-east-1/s3/aws4_request, 
SignedHeaders=host;range;x-amz-date, 
Signature=fe5f80f77d5fa3beca038a248ff027d0445342fe2855ddc963176630326e1024
```

### Signature Derivation Pipeline
1. **Canonical Request Construction:** Normalized HTTP method, canonical URI, canonical query string, canonical headers, signed headers list, and SHA-256 payload hash.
2. **String-to-Sign:** Formed by concatenating `AWS4-HMAC-SHA256`, the ISO 8601 timestamp (`20261121T220000Z`), credential scope (`20261121/us-east-1/s3/aws4_request`), and the SHA-256 hash of the canonical request.
3. **Key Derivation:** Sequentially derives `kDate = HMAC(kSecret, Date)`, `kRegion = HMAC(kDate, Region)`, `kService = HMAC(kRegion, "s3")`, and `kSigning = HMAC(kService, "aws4_request")`.
4. **Verification:** Computes `HMAC(kSigning, StringToSign)` and verifies it against the received signature in constant time using `ConstantTimeHelper` to prevent timing attacks.

---

## 2. Presigned URLs

SigV4 presigned URLs encode the signature parameters inside the query string rather than HTTP headers:

* `X-Amz-Algorithm=AWS4-HMAC-SHA256`
* `X-Amz-Credential=<AccessKey>/<Date>/<Region>/s3/aws4_request`
* `X-Amz-Date=<Timestamp>`
* `X-Amz-Expires=<Seconds>`
* `X-Amz-SignedHeaders=<Headers>`
* `X-Amz-Signature=<HexSignature>`

SharpS3 validates the expiration window (`X-Amz-Date` + `X-Amz-Expires`) against the server clock, verifies the signed headers, and checks the signature.

---

## 3. Streaming AWS-Chunked SigV4 Payloads

When uploading large files via AWS CLI or SDKs, clients frequently use `STREAMING-AWS4-HMAC-SHA256-PAYLOAD` or `STREAMING-AWS4-ECDSA-P256-SHA256-PAYLOAD`:

```
<chunk-size-hex>;chunk-signature=<chunk-signature>\r\n
<chunk-data>\r\n
...
0;chunk-signature=<final-signature>\r\n
\r\n
```

SharpS3 handles this transparently through `AwsChunkedDecodingStream`:
* Decodes chunks on the fly as data is consumed by the storage backend.
* Validates HMAC chunk signatures recursively using the previous chunk signature as seed.
* Verifies optional trailing checksum headers (`x-amz-checksum-sha256`, etc.).

---

## 4. Legacy Signature Version 2 (SigV2)

When `EnableSignatureV2 = true`, SharpS3 supports older legacy clients that send `Authorization: AWS <AccessKey>:<Signature>`.
