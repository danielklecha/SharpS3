# Request Validation & Error Handling

SharpS3 implements robust client-side validation alongside typed error handling to ensure requests conform to Amazon S3 standards.

---

## 1. Bucket Naming Validation

Per the Amazon S3 specification, bucket names must conform to strict rules:

* Must be between 3 and 63 characters long.
* Can consist only of lowercase letters, numbers, dots (`.`), and hyphens (`-`).
* Must begin and end with a letter or number.
* Must not contain adjacent periods (`..`) or formatted like an IP address (`192.168.5.4`).

`SharpS3Client` validates bucket names before making network calls, preventing invalid requests from being sent.

---

## 2. Object Key Validation

* Key names can be up to 1,024 bytes long when UTF-8 encoded.
* Key names cannot be null or empty.
* Path traversals (`../`) are safely rejected or handled depending on client configuration.

---

## 3. S3 Error Codes & `SharpS3ClientException`

When an S3 server returns an HTTP error status code, it includes an XML payload detailing the exact Amazon S3 error code. `SharpS3Client` extracts these fields into `SharpS3ClientException`:

| Property | Type | Description |
|---|---|---|
| `ErrorCode` | `string?` | Amazon S3 error code string (e.g. `NoSuchBucket`, `NoSuchKey`, `AccessDenied`, `BucketAlreadyExists`). |
| `StatusCode` | `HttpStatusCode` | HTTP response code (e.g. `404 NotFound`, `403 Forbidden`, `409 Conflict`). |
| `Resource` | `string?` | The bucket or key that caused the failure. |
| `RequestId` | `string?` | AWS request identifier for tracing. |
| `HostId` | `string?` | AWS host identifier. |

### Common S3 Error Codes Reference

| Error Code | HTTP Status | Description |
|---|---|---|
| `NoSuchBucket` | 404 | The specified bucket does not exist. |
| `NoSuchKey` | 404 | The specified key does not exist. |
| `BucketAlreadyExists` | 409 | The requested bucket name is not available. |
| `BucketNotEmpty` | 409 | The bucket you tried to delete is not empty. |
| `AccessDenied` | 403 | Access denied by ACL or signature mismatch. |
| `InvalidPart` | 400 | One or more of the specified parts was not found or has an invalid ETag. |
| `EntityTooLarge` | 400 | The proposed upload exceeds maximum allowed size. |
