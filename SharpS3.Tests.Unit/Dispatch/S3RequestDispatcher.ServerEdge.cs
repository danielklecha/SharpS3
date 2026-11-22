namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Dispatch;
using SharpS3.Exceptions;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

public partial class S3RequestDispatcherTests
{
    private InMemoryStorageBackend _backend = null!;
    private SharpS3Settings _settings = null!;
    private S3RequestDispatcher _dispatcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _backend = new InMemoryStorageBackend();
        _settings = new SharpS3Settings();
        _dispatcher = new S3RequestDispatcher(_backend, _settings);
    }

    [TestMethod]
    public async Task Dispatcher_NullOrEmpty_Bucket_Or_Key_Validation_For_All_RequestTypes()
    {
        var typesToTest = new[]
        {
            S3RequestType.ObjectExists,
            S3RequestType.ObjectRead,
            S3RequestType.ObjectReadRange,
            S3RequestType.ObjectWrite,
            S3RequestType.ObjectDelete,
            S3RequestType.ObjectReadLegalHold,
            S3RequestType.ObjectWriteLegalHold,
            S3RequestType.ObjectReadRetention,
            S3RequestType.ObjectWriteRetention,
            S3RequestType.ObjectReadTags,
            S3RequestType.ObjectWriteTags,
            S3RequestType.ObjectDeleteTags,
            S3RequestType.ObjectReadAcl,
            S3RequestType.ObjectWriteAcl,
            S3RequestType.ObjectDeleteAcl,
            S3RequestType.ObjectRestore,
            S3RequestType.ObjectSelectContent,
            S3RequestType.ObjectCreateMultipartUpload,
            S3RequestType.ObjectUploadPart,
            S3RequestType.ObjectUploadPartCopy,
            S3RequestType.ObjectCompleteMultipartUpload,
            S3RequestType.ObjectAbortMultipartUpload,
            S3RequestType.ObjectReadParts
        };

        foreach (var reqType in typesToTest)
        {
            var reqEmpty = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
            reqEmpty.RequestType = reqType;
            reqEmpty.Bucket = "";
            reqEmpty.Key = "";

            Func<Task> actEmpty = async () => await _dispatcher.DispatchAsync(reqEmpty);
            await actEmpty.Should().ThrowAsync<S3Exception>();

            var reqEmptyKey = new S3Request("GET", "/my-bucket/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
            reqEmptyKey.RequestType = reqType;
            reqEmptyKey.Bucket = "my-bucket";
            reqEmptyKey.Key = "";

            Func<Task> actEmptyKey = async () => await _dispatcher.DispatchAsync(reqEmptyKey);
            await actEmptyKey.Should().ThrowAsync<S3Exception>();
        }
    }

    [TestMethod]
    public async Task Dispatcher_EnableSignatures_And_ValidateSignaturesBeforePreRequestHandler()
    {
        var settings = new SharpS3Settings
        {
            EnableSignatures = true,
            ValidateSignaturesBeforePreRequestHandler = true,
            PreRequestHandler = req =>
            {
                if (req.Headers.ContainsKey("x-pre-abort"))
                {
                    return Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 418 });
                }
                return Task.FromResult<S3ServerResponse?>(null);
            }
        };

        var dispatcher = new S3RequestDispatcher(_backend, settings);

        // 1. Signature validation failure when authorization header is invalid
        var reqInvalidSig = new S3Request(
            "GET",
            "/bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { ["authorization"] = new[] { "AWS4-HMAC-SHA256 Credential=bad/..." } },
            Stream.Null
        );
        reqInvalidSig.RequestType = S3RequestType.BucketExists;
        reqInvalidSig.Bucket = "bucket";

        Func<Task> actInvalidSig = async () => await dispatcher.DispatchAsync(reqInvalidSig);
        await actInvalidSig.Should().ThrowAsync<S3Exception>();

        // 2. PreRequestHandler returning non-null after signature validation (when ValidateSignaturesBeforePreRequestHandler is true)
        var reqPreAbort = new S3Request(
            "GET",
            "/bucket",
            new Dictionary<string, string>(),
            new Dictionary<string, string[]> { ["x-pre-abort"] = new[] { "true" } },
            Stream.Null
        );
        reqPreAbort.RequestType = S3RequestType.BucketExists;
        reqPreAbort.Bucket = "bucket";
        reqPreAbort.IsSignatureValidated = true;

        var respPre = await dispatcher.DispatchAsync(reqPreAbort);
        respPre.StatusCode.Should().Be(418);
    }

    [TestMethod]
    public async Task Dispatcher_DefaultRequestHandler_And_Unknown_RequestType()
    {
        var settings = new SharpS3Settings
        {
            DefaultRequestHandler = req => Task.FromResult<S3ServerResponse?>(new S3ServerResponse { StatusCode = 204 })
        };

        var dispatcher = new S3RequestDispatcher(_backend, settings);

        var reqUnknown = new S3Request("CUSTOM", "/custom", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);
        reqUnknown.RequestType = S3RequestType.Unknown;

        var resp = await dispatcher.DispatchAsync(reqUnknown);
        resp.StatusCode.Should().Be(204);

        settings.DefaultRequestHandler = req => Task.FromResult<S3ServerResponse?>(null);
        Func<Task> act = async () => await dispatcher.DispatchAsync(reqUnknown);
        (await act.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.InvalidRequest);
    }
}
