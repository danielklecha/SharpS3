#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604, CS8620

namespace SharpS3.Dispatch;

using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Serialization;
using SharpS3.Storage;
using SharpS3.Streams;

/// <summary>
/// Dispatches parsed IS3Request instances to the corresponding IS3StorageBackend operations
/// and formats the resulting S3ServerResponse.
/// </summary>
public class S3RequestDispatcher
{
    private readonly IS3StorageBackend _Backend;
    private readonly SharpS3Settings _Settings;

    /// <summary>
    /// Storage backend associated with this dispatcher.
    /// </summary>
    public IS3StorageBackend Backend => _Backend;

    /// <summary>
    /// Server settings associated with this dispatcher.
    /// </summary>
    public SharpS3Settings Settings => _Settings;

    /// <summary>
    /// Initializes a new S3RequestDispatcher with a storage backend and optional settings.
    /// </summary>
    public S3RequestDispatcher(IS3StorageBackend backend, SharpS3Settings? settings = null)
    {
        _Backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _Settings = settings ?? new SharpS3Settings();
    }

    /// <summary>
    /// Dispatches an S3 request to the storage backend and returns an S3ServerResponse.
    /// </summary>
    public async Task<S3ServerResponse> DispatchAsync(IS3Request genericRequest, CancellationToken cancellationToken = default)
    {
        if (genericRequest is not S3Request req)
            throw new ArgumentException("Request must be of type S3Request", nameof(genericRequest));

        RequestTelemetry? rt = RequestTelemetry.Current;

        // 1. If ValidateSignaturesBeforePreRequestHandler is false (default), run PreRequestHandler first
        if (!_Settings.ValidateSignaturesBeforePreRequestHandler && _Settings.PreRequestHandler != null)
        {
            var preResponse = await _Settings.PreRequestHandler(req).ConfigureAwait(false);
            if (preResponse != null)
            {
                if (rt != null)
                {
                    rt.Handler = S3ServerTelemetryNames.HandlerPreRequest;
                    rt.Complete(preResponse.StatusCode);
                }

                return ApplyStandardResponseHeaders(req, preResponse);
            }
        }

        // 2. Validate signatures if enabled and not already validated
        if (_Settings.EnableSignatures && !req.IsSignatureValidated)
        {
            using (var sigStage = rt?.StartStage(S3ServerTelemetryNames.StageSignatureValidation))
            {
                try
                {
                    await SharpS3Server.ValidateSignatureAsync(req, _Settings, rt, cancellationToken).ConfigureAwait(false);
                    req.IsSignatureValidated = true;
                }
                catch (Exception ex)
                {
                    TelemetryStage.Fail(sigStage, ex);
                    throw;
                }
            }
        }

        // 3. If ValidateSignaturesBeforePreRequestHandler is true, run PreRequestHandler after signature validation
        if (_Settings.ValidateSignaturesBeforePreRequestHandler && _Settings.PreRequestHandler != null)
        {
            var preResponse = await _Settings.PreRequestHandler(req).ConfigureAwait(false);
            if (preResponse != null)
            {
                if (rt != null)
                {
                    rt.Handler = S3ServerTelemetryNames.HandlerPreRequest;
                    rt.Complete(preResponse.StatusCode);
                }

                return ApplyStandardResponseHeaders(req, preResponse);
            }
        }

        // 4. AuthenticatedRequestHandler hook
        if (_Settings.AuthenticatedRequestHandler != null)
        {
            var authResponse = await _Settings.AuthenticatedRequestHandler(req).ConfigureAwait(false);
            if (authResponse != null)
            {
                if (rt != null)
                {
                    rt.Handler = S3ServerTelemetryNames.HandlerAuthenticatedRequest;
                    rt.Complete(authResponse.StatusCode);
                }

                return ApplyStandardResponseHeaders(req, authResponse);
            }
        }

        S3ServerResponse response;
        try
        {
            response = await InternalDispatchAsync(req, rt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rt?.MarkException(ex, S3ServerTelemetryNames.StageCallback);
            throw;
        }

        ApplyStandardResponseHeaders(req, response);

        // Check post-request hook
        if (_Settings.PostRequestHandler != null)
        {
            await _Settings.PostRequestHandler(req, response).ConfigureAwait(false);
        }

        rt?.Complete(response.StatusCode);
        return response;
    }

    private S3ServerResponse ApplyStandardResponseHeaders(S3Request req, S3ServerResponse response)
    {
        if (!string.IsNullOrEmpty(req.RequestId) && !response.Headers.ContainsKey("x-amz-request-id"))
        {
            response.Headers["x-amz-request-id"] = req.RequestId;
        }
        if (!string.IsNullOrEmpty(req.TraceId) && !response.Headers.ContainsKey("x-amz-id-2"))
        {
            response.Headers["x-amz-id-2"] = req.TraceId;
        }
        if (_Settings.EmitCorsHeaders && req.HeaderExists("origin"))
        {
            string origin = req.RetrieveHeaderValue("origin") ?? "*";
            string? allowedOrigin = "*";
            if (_Settings.AllowedCorsOrigins != null && _Settings.AllowedCorsOrigins.Count > 0)
            {
                allowedOrigin = _Settings.AllowedCorsOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                    ? origin
                    : null;
            }

            if (!string.IsNullOrEmpty(allowedOrigin))
            {
                if (!response.Headers.ContainsKey("Access-Control-Allow-Origin"))
                    response.Headers["Access-Control-Allow-Origin"] = allowedOrigin!;
                if (!response.Headers.ContainsKey("Access-Control-Allow-Methods"))
                    response.Headers["Access-Control-Allow-Methods"] = "GET, PUT, POST, DELETE, HEAD, OPTIONS";
                if (!response.Headers.ContainsKey("Access-Control-Allow-Headers"))
                    response.Headers["Access-Control-Allow-Headers"] = "*";
                if (!response.Headers.ContainsKey("Access-Control-Expose-Headers"))
                    response.Headers["Access-Control-Expose-Headers"] = "ETag, x-amz-request-id, x-amz-id-2, Content-Length, Content-Type, Last-Modified";
            }
        }
        return response;
    }

    private async Task<S3ServerResponse> InternalDispatchAsync(S3Request req, RequestTelemetry? rt, CancellationToken cancellationToken)
    {
        using var callbackStage = rt?.StartCallback(req.RequestType.ToString());
        switch (req.RequestType)
            {
                #region Service

                case S3RequestType.ServiceExists:
                    return CreateEmptyResponse(200, ("x-amz-bucket-region", "us-east-1"));

                case S3RequestType.ListBuckets:
                    var buckets = await _Backend.ListBucketsAsync(req, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, buckets);

                #endregion

                #region Bucket

                case S3RequestType.BucketExists:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    bool exists = await _Backend.BucketExistsAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return exists ? CreateEmptyResponse(200) : CreateEmptyResponse(404);

                case S3RequestType.BucketWrite:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.InvalidBucketName));
                    CreateBucketConfiguration? config = null;
                    if (req.BodyStream != Stream.Null && req.BodyStream.CanRead)
                    {
                        string body = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(body))
                            config = DeserializeXmlPayload<CreateBucketConfiguration>(body);
                    }
                    await _Backend.CreateBucketAsync(req.Bucket, config, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.BucketDelete:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    await _Backend.DeleteBucketAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.BucketRead:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var objects = await _Backend.ListObjectsAsync(req.Bucket, req, cancellationToken).ConfigureAwait(false);
                    bool isV2 = req.RetrieveQueryValue("list-type") == "2";
                    string xml = ResponseSerializationContext.Run(isV2 ? 2 : 1, isV2 && !req.FetchOwner, false, () => SerializationHelper.SerializeXml(objects, true));
                    byte[] bytes = Encoding.UTF8.GetBytes(xml);
                    var bucketReadResp = new S3ServerResponse
                    {
                        StatusCode = 200,
                        ContentType = "application/xml",
                        BodyStream = new MemoryStream(bytes)
                    };
                    bucketReadResp.Headers["Content-Length"] = bytes.Length.ToString();
                    return bucketReadResp;

                case S3RequestType.BucketReadLocation:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var location = await _Backend.GetBucketLocationAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, location);

                case S3RequestType.BucketReadVersions:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var versions = await _Backend.ListObjectVersionsAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, versions);

                case S3RequestType.BucketReadMultipartUploads:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var uploads = await _Backend.ListMultipartUploadsAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, uploads);

                case S3RequestType.BucketReadAcl:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var bAcl = await _Backend.GetBucketAclAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, bAcl);

                case S3RequestType.BucketWriteAcl:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string bAclBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    AccessControlPolicy? bAclPolicy = null;
                    if (!string.IsNullOrWhiteSpace(bAclBody))
                        bAclPolicy = DeserializeXmlPayload<AccessControlPolicy>(bAclBody);
                    else
                        bAclPolicy = new AccessControlPolicy(new Owner("owner", "owner"), new AccessControlList());
                    await _Backend.SetBucketAclAsync(req.Bucket, bAclPolicy, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.BucketDeleteAcl:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    await _Backend.DeleteBucketAclAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.BucketReadTags:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var bTags = await _Backend.GetBucketTagsAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, bTags);

                case S3RequestType.BucketWriteTags:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string bTagsBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var bTagging = DeserializeXmlPayload<Tagging>(bTagsBody);
                    await _Backend.SetBucketTagsAsync(req.Bucket, bTagging, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.BucketDeleteTags:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    await _Backend.DeleteBucketTagsAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.BucketReadVersioning:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var bVersioning = await _Backend.GetBucketVersioningAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, bVersioning);

                case S3RequestType.BucketWriteVersioning:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string bVerBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var bVerConfig = DeserializeXmlPayload<VersioningConfiguration>(bVerBody);
                    await _Backend.SetBucketVersioningAsync(req.Bucket, bVerConfig, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.BucketReadWebsite:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var bWebsite = await _Backend.GetBucketWebsiteAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, bWebsite);

                case S3RequestType.BucketWriteWebsite:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string bWebBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var bWebConfig = DeserializeXmlPayload<WebsiteConfiguration>(bWebBody);
                    await _Backend.SetBucketWebsiteAsync(req.Bucket, bWebConfig, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.BucketDeleteWebsite:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    await _Backend.DeleteBucketWebsiteAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.BucketReadLogging:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    var bLogging = await _Backend.GetBucketLoggingAsync(req.Bucket, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, bLogging);

                case S3RequestType.BucketWriteLogging:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string bLogBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var bLogStatus = DeserializeXmlPayload<BucketLoggingStatus>(bLogBody);
                    await _Backend.SetBucketLoggingAsync(req.Bucket, bLogStatus, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                #endregion

                #region Object

                case S3RequestType.ObjectExists:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var metadata = await _Backend.GetObjectMetadataAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    if (metadata == null) return CreateEmptyResponse(404);

                    EvaluateConditionalHeaders(req, metadata.ETag, metadata.LastModified, out bool headNotModified);
                    if (headNotModified)
                    {
                        var head304 = CreateEmptyResponse(304);
                        if (!string.IsNullOrEmpty(metadata.ETag)) head304.Headers["ETag"] = metadata.ETag;
                        head304.Headers["Last-Modified"] = metadata.LastModified.ToString("r");
                        return head304;
                    }

                    var headResp = CreateEmptyResponse(200);
                    headResp.Headers["Content-Length"] = metadata.Size.ToString();
                    headResp.Headers["Content-Type"] = metadata.ContentType ?? "binary/octet-stream";
                    headResp.Headers["ETag"] = metadata.ETag;
                    headResp.Headers["Last-Modified"] = metadata.LastModified.ToString("r");
                    if (metadata.Metadata != null)
                    {
                        foreach (var kvp in metadata.Metadata)
                        {
                            string headerName = kvp.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase)
                                ? kvp.Key
                                : "x-amz-meta-" + kvp.Key;
                            headResp.Headers[headerName] = kvp.Value;
                        }
                    }
                    return headResp;

                case S3RequestType.ObjectRead:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var objPayload = await _Backend.GetObjectAsync(req.Bucket, req.Key, null, null, cancellationToken).ConfigureAwait(false);
                    rt?.RecordObjectSize(objPayload.ContentLength);

                    try
                    {
                        EvaluateConditionalHeaders(req, objPayload.ETag, objPayload.LastModified, out bool notModified);
                        if (notModified)
                        {
                            objPayload.Dispose();
                            var notModifiedResp = CreateEmptyResponse(304);
                            if (!string.IsNullOrEmpty(objPayload.ETag)) notModifiedResp.Headers["ETag"] = objPayload.ETag;
                            notModifiedResp.Headers["Last-Modified"] = objPayload.LastModified.ToString("r");
                            return notModifiedResp;
                        }
                    }
                    catch
                    {
                        objPayload.Dispose();
                        throw;
                    }

                    var getResp = new S3ServerResponse
                    {
                        StatusCode = 200,
                        ContentType = objPayload.ContentType,
                        BodyStream = objPayload.Stream
                    };
                    getResp.Headers["Content-Length"] = objPayload.ContentLength.ToString();
                    if (!string.IsNullOrEmpty(objPayload.ETag)) getResp.Headers["ETag"] = objPayload.ETag;
                    getResp.Headers["Last-Modified"] = objPayload.LastModified.ToString("r");
                    getResp.Headers["Accept-Ranges"] = "bytes";
                    if (objPayload.Headers != null)
                    {
                        foreach (var h in objPayload.Headers)
                        {
                            getResp.Headers[h.Key] = h.Value;
                        }
                    }
                    return getResp;

                case S3RequestType.ObjectReadRange:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var rangePayload = await _Backend.GetObjectAsync(req.Bucket, req.Key, req.RangeStart, req.RangeEnd, cancellationToken).ConfigureAwait(false);
                    rt?.RecordObjectSize(rangePayload.ContentLength);

                    try
                    {
                        EvaluateConditionalHeaders(req, rangePayload.ETag, rangePayload.LastModified, out bool rangeNotModified);
                        if (rangeNotModified)
                        {
                            rangePayload.Dispose();
                            var range304 = CreateEmptyResponse(304);
                            if (!string.IsNullOrEmpty(rangePayload.ETag)) range304.Headers["ETag"] = rangePayload.ETag;
                            range304.Headers["Last-Modified"] = rangePayload.LastModified.ToString("r");
                            return range304;
                        }
                    }
                    catch
                    {
                        rangePayload.Dispose();
                        throw;
                    }

                    var rangeResp = new S3ServerResponse
                    {
                        StatusCode = 206,
                        ContentType = rangePayload.ContentType,
                        BodyStream = rangePayload.Stream
                    };
                    rangeResp.Headers["Content-Length"] = rangePayload.ContentLength.ToString();
                    if (!string.IsNullOrEmpty(rangePayload.ETag)) rangeResp.Headers["ETag"] = rangePayload.ETag;
                    rangeResp.Headers["Last-Modified"] = rangePayload.LastModified.ToString("r");
                    rangeResp.Headers["Content-Range"] = $"bytes {rangePayload.RangeStart}-{rangePayload.RangeEnd}/{rangePayload.TotalSize}";
                    if (rangePayload.Headers != null)
                    {
                        foreach (var h in rangePayload.Headers)
                        {
                            rangeResp.Headers[h.Key] = h.Value;
                        }
                    }
                    return rangeResp;

                case S3RequestType.ObjectWrite:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));

                    if (req.IfMatch != null || req.IfNoneMatch != null || req.IfModifiedSince != null || req.IfUnmodifiedSince != null)
                    {
                        var existingMeta = await _Backend.GetObjectMetadataAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                        if (existingMeta != null)
                        {
                            EvaluateConditionalHeaders(req, existingMeta.ETag, existingMeta.LastModified, out _);
                        }
                        else if (req.IfMatch != null && req.IfMatch.Count > 0)
                        {
                            throw new S3Exception(new Error(ErrorCode.PreconditionFailed));
                        }
                    }

                    long contentLength = req.DecodedContentLength ?? 0;
                    if (contentLength <= 0 && !req.Chunked)
                    {
                        if (req.Headers.TryGetValue("content-length", out var clVals) && clVals.Length > 0)
                            long.TryParse(clVals[0], out contentLength);
                    }
                    var userMetadata = ExtractUserMetadata(req);
                    Stream putStream = req.BodyStream;
                    var putLimits = _Settings.OperationLimits;
                    if (putLimits != null)
                    {
                        if (contentLength > putLimits.MaxPutObjectSize)
                        {
                            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
                        }

                        if (req.BodyStream != Stream.Null)
                        {
                            putStream = new BoundedReadStream(req.BodyStream, putLimits.MaxPutObjectSize, leaveOpen: true);
                        }
                    }
                    Sha256PayloadVerifyingStream? verifyingStream = null;
                    if (!string.IsNullOrEmpty(req.ContentSha256) &&
                        !string.Equals(req.ContentSha256, "UNSIGNED-PAYLOAD", StringComparison.OrdinalIgnoreCase) &&
                        !req.ContentSha256.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase))
                    {
                        verifyingStream = new Sha256PayloadVerifyingStream(putStream, req.ContentSha256);
                        putStream = verifyingStream;
                    }

                    string putEtag = string.Empty;
                    Exception? putException = null;
                    try
                    {
                        putEtag = await _Backend.PutObjectAsync(req.Bucket, req.Key, putStream, contentLength, req.ContentType, userMetadata.Count > 0 ? userMetadata : null, cancellationToken).ConfigureAwait(false);
                        verifyingStream?.Verify();
                        ValidateContentMd5(req.ContentMd5, putEtag);
                    }
                    catch (Exception ex)
                    {
                        putException = ex;
                    }

                    if (putException != null)
                    {
                        if (verifyingStream != null || !string.IsNullOrEmpty(req.ContentMd5))
                        {
                            try { await _Backend.DeleteObjectAsync(req.Bucket, req.Key, null, cancellationToken).ConfigureAwait(false); }
                            catch (Exception cleanupEx)
                            {
                                rt?.RecordPutCleanupFailure(cleanupEx);
                            }
                        }
                        throw putException;
                    }

                    rt?.RecordObjectSize(contentLength);
                    var putResp = CreateEmptyResponse(200);
                    putResp.Headers["ETag"] = putEtag;
                    return putResp;

                case S3RequestType.ObjectCopy:
                    if (string.IsNullOrEmpty(req.CopySourceBucket) || string.IsNullOrEmpty(req.CopySourceKey) ||
                        string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));

                    if (req.CopySourceIfMatch != null || req.CopySourceIfNoneMatch != null || req.CopySourceIfModifiedSince != null || req.CopySourceIfUnmodifiedSince != null)
                    {
                        var srcMeta = await _Backend.GetObjectMetadataAsync(req.CopySourceBucket, req.CopySourceKey, cancellationToken).ConfigureAwait(false);
                        if (srcMeta != null)
                        {
                            EvaluateCopySourceConditionalHeaders(req, srcMeta.ETag, srcMeta.LastModified);
                        }
                    }

                    var copyResult = await _Backend.CopyObjectAsync(req.CopySourceBucket, req.CopySourceKey, req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, copyResult);

                case S3RequestType.ObjectDelete:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    await _Backend.DeleteObjectAsync(req.Bucket, req.Key, req.VersionId, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.ObjectDeleteMultiple:
                    if (string.IsNullOrEmpty(req.Bucket))
                        throw new S3Exception(new Error(ErrorCode.NoSuchBucket));
                    string deleteBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var deleteRequest = DeserializeXmlPayload<DeleteMultiple>(deleteBody);
                    var deleteResult = await _Backend.DeleteMultipleObjectsAsync(req.Bucket, deleteRequest, cancellationToken).ConfigureAwait(false);
                    rt?.RecordDeleteMultipleResult(deleteResult);
                    string delXml = ResponseSerializationContext.Run(1, false, deleteRequest.Quiet, () => SerializationHelper.SerializeXml(deleteResult, true));
                    byte[] delBytes = Encoding.UTF8.GetBytes(delXml);
                    var delResp = new S3ServerResponse
                    {
                        StatusCode = 200,
                        ContentType = "application/xml",
                        BodyStream = new MemoryStream(delBytes)
                    };
                    delResp.Headers["Content-Length"] = delBytes.Length.ToString();
                    return delResp;

                case S3RequestType.ObjectDeleteAcl:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    await _Backend.DeleteObjectAclAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.ObjectReadLegalHold:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var legalHold = await _Backend.GetLegalHoldAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, legalHold);

                case S3RequestType.ObjectWriteLegalHold:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    string lhBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var lhPayload = DeserializeXmlPayload<LegalHold>(lhBody);
                    await _Backend.SetLegalHoldAsync(req.Bucket, req.Key, lhPayload, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.ObjectReadRetention:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var retention = await _Backend.GetRetentionAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, retention);

                case S3RequestType.ObjectWriteRetention:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    string retBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var retPayload = DeserializeXmlPayload<Retention>(retBody);
                    await _Backend.SetRetentionAsync(req.Bucket, req.Key, retPayload, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.ObjectRestore:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    string restoreBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var restoreRequest = DeserializeXmlPayload<RestoreRequest>(restoreBody);
                    var restoreResult = await _Backend.RestoreObjectAsync(req.Bucket, req.Key, restoreRequest, cancellationToken).ConfigureAwait(false);
                    var restoreResp = CreateEmptyResponse(restoreResult.AlreadyRestored ? 200 : 202);
                    if (!string.IsNullOrEmpty(restoreResult.RestoreOutputPath))
                        restoreResp.Headers[Constants.HeaderRestoreOutputPath] = restoreResult.RestoreOutputPath;
                    return restoreResp;

                case S3RequestType.ObjectSelectContent:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    return CreateEmptyResponse(200);

                #endregion

                #region Multipart-Uploads

                case S3RequestType.ObjectCreateMultipartUpload:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var initResult = await _Backend.CreateMultipartUploadAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, initResult);

                case S3RequestType.ObjectUploadPart:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key) || string.IsNullOrEmpty(req.UploadId))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));
                    if (req.PartNumber < 1 || req.PartNumber > 10000)
                        throw new S3Exception(new Error(ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000."));
                    long partLength = req.DecodedContentLength ?? 0;
                    if (partLength <= 0 && !req.Chunked)
                    {
                        if (req.Headers.TryGetValue("content-length", out var partClVals) && partClVals.Length > 0)
                            long.TryParse(partClVals[0], out partLength);
                    }
                    Stream partStream = req.BodyStream;
                    var partLimits = _Settings.OperationLimits;
                    if (partLimits != null)
                    {
                        if (partLength > partLimits.MaxUploadPartSize)
                        {
                            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
                        }

                        if (req.BodyStream != Stream.Null)
                        {
                            partStream = new BoundedReadStream(req.BodyStream, partLimits.MaxUploadPartSize, leaveOpen: true);
                        }
                    }
                    Sha256PayloadVerifyingStream? verifyingPartStream = null;
                    if (!string.IsNullOrEmpty(req.ContentSha256) &&
                        !string.Equals(req.ContentSha256, "UNSIGNED-PAYLOAD", StringComparison.OrdinalIgnoreCase) &&
                        !req.ContentSha256.StartsWith("STREAMING-", StringComparison.OrdinalIgnoreCase))
                    {
                        verifyingPartStream = new Sha256PayloadVerifyingStream(partStream, req.ContentSha256);
                        partStream = verifyingPartStream;
                    }

                    string partEtag = await _Backend.UploadPartAsync(req.Bucket, req.Key, req.UploadId, req.PartNumber, partStream, partLength, cancellationToken).ConfigureAwait(false);
                    verifyingPartStream?.Verify();
                    ValidateContentMd5(req.ContentMd5, partEtag);

                    rt?.RecordObjectSize(partLength);
                    var partResp = CreateEmptyResponse(200);
                    partResp.Headers["ETag"] = partEtag;
                    return partResp;

                case S3RequestType.ObjectUploadPartCopy:
                    if (string.IsNullOrEmpty(req.CopySourceBucket) || string.IsNullOrEmpty(req.CopySourceKey) ||
                        string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key) || string.IsNullOrEmpty(req.UploadId))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));
                    if (req.PartNumber < 1 || req.PartNumber > 10000)
                        throw new S3Exception(new Error(ErrorCode.InvalidArgument, "Part number must be an integer between 1 and 10000."));
                    var copyPartResult = await _Backend.UploadPartCopyAsync(
                        req.CopySourceBucket, req.CopySourceKey, req.Bucket, req.Key, req.UploadId, req.PartNumber,
                        req.CopySourceRangeStart, req.CopySourceRangeEnd, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, copyPartResult);

                case S3RequestType.ObjectCompleteMultipartUpload:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key) || string.IsNullOrEmpty(req.UploadId))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));
                    string completeBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var completeRequest = DeserializeXmlPayload<CompleteMultipartUpload>(completeBody);
                    var completeResult = await _Backend.CompleteMultipartUploadAsync(req.Bucket, req.Key, req.UploadId, completeRequest, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, completeResult);

                case S3RequestType.ObjectAbortMultipartUpload:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key) || string.IsNullOrEmpty(req.UploadId))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));
                    await _Backend.AbortMultipartUploadAsync(req.Bucket, req.Key, req.UploadId, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.ObjectReadParts:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key) || string.IsNullOrEmpty(req.UploadId))
                        throw new S3Exception(new Error(ErrorCode.InvalidRequest));
                    var partsResult = await _Backend.ListPartsAsync(req.Bucket, req.Key, req.UploadId, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, partsResult);

                #endregion

                #region Tags-and-Acl

                case S3RequestType.ObjectReadTags:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var tags = await _Backend.GetObjectTagsAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, tags);

                case S3RequestType.ObjectWriteTags:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    string tagsBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var tagging = DeserializeXmlPayload<Tagging>(tagsBody);
                    await _Backend.SetObjectTagsAsync(req.Bucket, req.Key, tagging, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                case S3RequestType.ObjectDeleteTags:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    await _Backend.DeleteObjectTagsAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(204);

                case S3RequestType.ObjectReadAcl:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    var acl = await _Backend.GetObjectAclAsync(req.Bucket, req.Key, cancellationToken).ConfigureAwait(false);
                    return CreateXmlResponse(200, acl);

                case S3RequestType.ObjectWriteAcl:
                    if (string.IsNullOrEmpty(req.Bucket) || string.IsNullOrEmpty(req.Key))
                        throw new S3Exception(new Error(ErrorCode.NoSuchKey));
                    string aclBody = await ReadBodyStringAsync(req.BodyStream, cancellationToken).ConfigureAwait(false);
                    var policy = DeserializeXmlPayload<AccessControlPolicy>(aclBody);
                    await _Backend.SetObjectAclAsync(req.Bucket, req.Key, policy, cancellationToken).ConfigureAwait(false);
                    return CreateEmptyResponse(200);

                #endregion

                default:
                    if (_Settings.DefaultRequestHandler != null)
                    {
                        var defaultResponse = await _Settings.DefaultRequestHandler(req).ConfigureAwait(false);
                        if (defaultResponse != null) return defaultResponse;
                    }
                    throw new S3Exception(new Error(ErrorCode.InvalidRequest));
            }
    }

    private static S3ServerResponse CreateEmptyResponse(int statusCode, params (string Key, string Value)[] headers)
    {
        var resp = new S3ServerResponse { StatusCode = statusCode };
        foreach (var h in headers)
            resp.Headers[h.Key] = h.Value;
        return resp;
    }

    private static S3ServerResponse CreateXmlResponse<T>(int statusCode, T model)
    {
        string xml = SerializationHelper.SerializeXml(model!, true);
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        var resp = new S3ServerResponse
        {
            StatusCode = statusCode,
            ContentType = "application/xml",
            BodyStream = new MemoryStream(bytes)
        };
        resp.Headers["Content-Length"] = bytes.Length.ToString();
        return resp;
    }

    private async Task<string> ReadBodyStringAsync(Stream? stream, CancellationToken cancellationToken)
    {
        long maxBytes = _Settings.OperationLimits?.MaxXmlPayloadSize ?? (10 * 1024 * 1024);
        return await ReadBodyStringAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodyStringAsync(Stream? stream, long maxBytes, CancellationToken cancellationToken)
    {
        if (stream == null || stream == Stream.Null)
            return string.Empty;

        if (stream.CanSeek && (stream.Length - stream.Position) > maxBytes)
        {
            throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
        }

        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        long totalBytesRead = 0;

        while (true)
        {
            int toRead = (int)Math.Min((long)buffer.Length, maxBytes - totalBytesRead + 1);
            int read = await stream.ReadAsync(buffer, 0, toRead, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;

            totalBytesRead += read;
            if (totalBytesRead > maxBytes)
            {
                throw new S3Exception(new Error(ErrorCode.EntityTooLarge));
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Dictionary<string, string> ExtractUserMetadata(S3Request req)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in req.Headers)
        {
            if (h.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
            {
                string key = h.Key.Substring("x-amz-meta-".Length);
                dict[key] = string.Join(",", h.Value);
            }
        }
        return dict;
    }

    private static T DeserializeXmlPayload<T>(string xml) where T : class
    {
        try
        {
            return SerializationHelper.DeserializeXml<T>(xml);
        }
        catch (XmlException)
        {
            throw new S3Exception(new Error(ErrorCode.MalformedXML));
        }
    }

    /// <summary>
    /// Evaluates standard HTTP conditional headers (If-Match, If-Unmodified-Since, If-None-Match, If-Modified-Since).
    /// </summary>
    internal static void EvaluateConditionalHeaders(S3Request req, string? etag, DateTime lastModified, out bool notModified)
    {
        notModified = false;
        string cleanEtag = (etag ?? string.Empty).Trim('"');

        // 1. If-Match
        if (req.IfMatch != null && req.IfMatch.Count > 0)
        {
            bool match = req.IfMatch.Any(t => t == "*" || string.Equals(t.Trim('"'), cleanEtag, StringComparison.Ordinal));
            if (!match)
            {
                throw new S3Exception(new Error(ErrorCode.PreconditionFailed, "At least one of the pre-conditions you specified did not hold."));
            }
        }

        // 2. If-Unmodified-Since
        if (req.IfUnmodifiedSince.HasValue)
        {
            long lastModSec = new DateTimeOffset(lastModified).ToUnixTimeSeconds();
            long unmodSec = new DateTimeOffset(req.IfUnmodifiedSince.Value).ToUnixTimeSeconds();
            if (lastModSec > unmodSec)
            {
                throw new S3Exception(new Error(ErrorCode.PreconditionFailed, "At least one of the pre-conditions you specified did not hold."));
            }
        }

        // 3. If-None-Match
        if (req.IfNoneMatch != null && req.IfNoneMatch.Count > 0)
        {
            bool match = req.IfNoneMatch.Any(t => t == "*" || string.Equals(t.Trim('"'), cleanEtag, StringComparison.Ordinal));
            if (match)
            {
                if (req.HttpMethod == "GET" || req.HttpMethod == "HEAD")
                {
                    notModified = true;
                    return;
                }
                throw new S3Exception(new Error(ErrorCode.PreconditionFailed, "At least one of the pre-conditions you specified did not hold."));
            }
        }

        // 4. If-Modified-Since
        if (req.IfModifiedSince.HasValue && (req.HttpMethod == "GET" || req.HttpMethod == "HEAD"))
        {
            long lastModSec = new DateTimeOffset(lastModified).ToUnixTimeSeconds();
            long modSec = new DateTimeOffset(req.IfModifiedSince.Value).ToUnixTimeSeconds();
            if (lastModSec <= modSec)
            {
                notModified = true;
                return;
            }
        }
    }

    /// <summary>
    /// Evaluates S3 copy-source conditional headers (x-amz-copy-source-if-*).
    /// </summary>
    internal static void EvaluateCopySourceConditionalHeaders(S3Request req, string? etag, DateTime lastModified)
    {
        string cleanEtag = (etag ?? string.Empty).Trim('"');

        if (req.CopySourceIfMatch != null && req.CopySourceIfMatch.Count > 0)
        {
            bool match = req.CopySourceIfMatch.Any(t => t == "*" || string.Equals(t.Trim('"'), cleanEtag, StringComparison.Ordinal));
            if (!match) throw new S3Exception(new Error(ErrorCode.PreconditionFailed));
        }

        if (req.CopySourceIfUnmodifiedSince.HasValue)
        {
            long lastModSec = new DateTimeOffset(lastModified).ToUnixTimeSeconds();
            long unmodSec = new DateTimeOffset(req.CopySourceIfUnmodifiedSince.Value).ToUnixTimeSeconds();
            if (lastModSec > unmodSec) throw new S3Exception(new Error(ErrorCode.PreconditionFailed));
        }

        if (req.CopySourceIfNoneMatch != null && req.CopySourceIfNoneMatch.Count > 0)
        {
            bool match = req.CopySourceIfNoneMatch.Any(t => t == "*" || string.Equals(t.Trim('"'), cleanEtag, StringComparison.Ordinal));
            if (match) throw new S3Exception(new Error(ErrorCode.PreconditionFailed));
        }

        if (req.CopySourceIfModifiedSince.HasValue)
        {
            long lastModSec = new DateTimeOffset(lastModified).ToUnixTimeSeconds();
            long modSec = new DateTimeOffset(req.CopySourceIfModifiedSince.Value).ToUnixTimeSeconds();
            if (lastModSec <= modSec) throw new S3Exception(new Error(ErrorCode.PreconditionFailed));
        }
    }

    /// <summary>
    /// Validates an incoming Content-MD5 header against the object's computed ETag.
    /// </summary>
    internal static void ValidateContentMd5(string? contentMd5, string etag)
    {
        if (string.IsNullOrEmpty(contentMd5)) return;
        string hex = (etag ?? string.Empty).Trim('"');
        if (hex.Length == 0 || hex.Length % 2 != 0) return;

        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromBase64String(contentMd5);
            if (expectedBytes.Length != 16)
            {
                throw new S3Exception(new Error(ErrorCode.BadDigest));
            }
        }
        catch (FormatException)
        {
            throw new S3Exception(new Error(ErrorCode.BadDigest));
        }

#if NET6_0_OR_GREATER
        byte[] actualBytes;
        try
        {
            actualBytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return;
        }
#else
        byte[] actualBytes = new byte[hex.Length / 2];
        try
        {
            for (int i = 0; i < actualBytes.Length; i++)
            {
                actualBytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
        }
        catch (FormatException)
        {
            return;
        }
#endif
        if (!ConstantTimeHelper.FixedTimeEquals(actualBytes, expectedBytes))
        {
            throw new S3Exception(new Error(ErrorCode.BadDigest));
        }
    }
}
