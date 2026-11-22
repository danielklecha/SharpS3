namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Models;
using SharpS3.Protocol;
using SharpS3.Storage;

[TestClass]
public class CallbackStorageBackendTests
{
    [TestMethod]
    public async Task CallbackStorageBackend_WhenCallbacksAreNull_ReturnsDefaults()
    {
        var backend = new CallbackStorageBackend();
        var dummyReq = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);

        (await backend.ListBucketsAsync(dummyReq)).Should().NotBeNull();
        (await backend.BucketExistsAsync("b")).Should().BeFalse();
        await backend.CreateBucketAsync("b", null);
        await backend.DeleteBucketAsync("b");
        (await backend.GetBucketLocationAsync("b")).Should().NotBeNull();
        (await backend.ListObjectsAsync("b", dummyReq)).Name.Should().Be("b");
        (await backend.ListObjectVersionsAsync("b")).Name.Should().Be("b");
        (await backend.ListMultipartUploadsAsync("b")).Bucket.Should().Be("b");
        (await backend.GetBucketAclAsync("b")).Should().NotBeNull();
        await backend.SetBucketAclAsync("b", new AccessControlPolicy());
        await backend.DeleteBucketAclAsync("b");

        var actGetTags = async () => await backend.GetBucketTagsAsync("b");
        (await actGetTags.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchTagSet);

        await backend.SetBucketTagsAsync("b", new Tagging());
        await backend.DeleteBucketTagsAsync("b");
        (await backend.GetBucketVersioningAsync("b")).Should().NotBeNull();
        await backend.SetBucketVersioningAsync("b", new VersioningConfiguration());

        var actGetWebsite = async () => await backend.GetBucketWebsiteAsync("b");
        (await actGetWebsite.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchWebsiteConfiguration);

        await backend.SetBucketWebsiteAsync("b", new WebsiteConfiguration());
        await backend.DeleteBucketWebsiteAsync("b");
        (await backend.GetBucketLoggingAsync("b")).Should().NotBeNull();
        await backend.SetBucketLoggingAsync("b", new BucketLoggingStatus());

        (await backend.GetObjectMetadataAsync("b", "k")).Should().BeNull();

        var actGetObject = async () => await backend.GetObjectAsync("b", "k");
        (await actGetObject.Should().ThrowAsync<S3Exception>())
            .Which.Error.Code.Should().Be(ErrorCode.NoSuchKey);

        (await backend.PutObjectAsync("b", "k", Stream.Null, 0, null)).Should().NotBeNullOrEmpty();
        await backend.DeleteObjectAsync("b", "k");
        (await backend.CopyObjectAsync("b1", "k1", "b2", "k2")).Should().NotBeNull();
        (await backend.DeleteMultipleObjectsAsync("b", new DeleteMultiple())).Should().NotBeNull();
        (await backend.GetObjectTagsAsync("b", "k")).Should().NotBeNull();
        await backend.SetObjectTagsAsync("b", "k", new Tagging());
        await backend.DeleteObjectTagsAsync("b", "k");
        (await backend.GetLegalHoldAsync("b", "k")).Should().NotBeNull();
        await backend.SetLegalHoldAsync("b", "k", new LegalHold());
        (await backend.GetRetentionAsync("b", "k")).Should().NotBeNull();
        await backend.SetRetentionAsync("b", "k", new Retention());
        (await backend.GetObjectAclAsync("b", "k")).Should().NotBeNull();
        await backend.SetObjectAclAsync("b", "k", new AccessControlPolicy());
        await backend.DeleteObjectAclAsync("b", "k");
        (await backend.RestoreObjectAsync("b", "k", new RestoreRequest())).Should().NotBeNull();
        (await backend.CreateMultipartUploadAsync("b", "k")).Should().NotBeNull();
        (await backend.UploadPartAsync("b", "k", "u", 1, Stream.Null, 0)).Should().NotBeNullOrEmpty();
        (await backend.UploadPartCopyAsync("b1", "k1", "b2", "k2", "u", 1)).Should().NotBeNull();
        (await backend.CompleteMultipartUploadAsync("b", "k", "u", new CompleteMultipartUpload())).Should().NotBeNull();
        await backend.AbortMultipartUploadAsync("b", "k", "u");
        (await backend.ListPartsAsync("b", "k", "u")).Should().NotBeNull();
    }

    [TestMethod]
    public async Task CallbackStorageBackend_WhenCallbacksAreSet_InvokesDelegates()
    {
        var backend = new CallbackStorageBackend();
        var dummyReq = new S3Request("GET", "/", new Dictionary<string, string>(), new Dictionary<string, string[]>(), Stream.Null);

        bool listBucketsCalled = false;
        backend.ListBuckets = (req, ct) => { listBucketsCalled = true; return Task.FromResult(new ListAllMyBucketsResult()); };

        bool bucketExistsCalled = false;
        backend.BucketExists = (b, ct) => { bucketExistsCalled = true; return Task.FromResult(true); };

        bool createBucketCalled = false;
        backend.CreateBucket = (b, conf, ct) => { createBucketCalled = true; return Task.CompletedTask; };

        bool deleteBucketCalled = false;
        backend.DeleteBucket = (b, ct) => { deleteBucketCalled = true; return Task.CompletedTask; };

        bool getBucketLocationCalled = false;
        backend.GetBucketLocation = (b, ct) => { getBucketLocationCalled = true; return Task.FromResult(new LocationConstraint("us-east-1")); };

        bool listObjectsCalled = false;
        backend.ListObjects = (b, req, ct) => { listObjectsCalled = true; return Task.FromResult(new ListBucketResult { Name = b }); };

        bool listObjectVersionsCalled = false;
        backend.ListObjectVersions = (b, ct) => { listObjectVersionsCalled = true; return Task.FromResult(new ListVersionsResult { Name = b }); };

        bool listMultipartUploadsCalled = false;
        backend.ListMultipartUploads = (b, ct) => { listMultipartUploadsCalled = true; return Task.FromResult(new ListMultipartUploadsResult { Bucket = b }); };

        bool getBucketAclCalled = false;
        backend.GetBucketAcl = (b, ct) => { getBucketAclCalled = true; return Task.FromResult(new AccessControlPolicy()); };

        bool setBucketAclCalled = false;
        backend.SetBucketAcl = (b, acl, ct) => { setBucketAclCalled = true; return Task.CompletedTask; };

        bool deleteBucketAclCalled = false;
        backend.DeleteBucketAcl = (b, ct) => { deleteBucketAclCalled = true; return Task.CompletedTask; };

        bool getBucketTagsCalled = false;
        backend.GetBucketTags = (b, ct) => { getBucketTagsCalled = true; return Task.FromResult(new Tagging()); };

        bool setBucketTagsCalled = false;
        backend.SetBucketTags = (b, t, ct) => { setBucketTagsCalled = true; return Task.CompletedTask; };

        bool deleteBucketTagsCalled = false;
        backend.DeleteBucketTags = (b, ct) => { deleteBucketTagsCalled = true; return Task.CompletedTask; };

        bool getBucketVersioningCalled = false;
        backend.GetBucketVersioning = (b, ct) => { getBucketVersioningCalled = true; return Task.FromResult(new VersioningConfiguration()); };

        bool setBucketVersioningCalled = false;
        backend.SetBucketVersioning = (b, conf, ct) => { setBucketVersioningCalled = true; return Task.CompletedTask; };

        bool getBucketWebsiteCalled = false;
        backend.GetBucketWebsite = (b, ct) => { getBucketWebsiteCalled = true; return Task.FromResult(new WebsiteConfiguration()); };

        bool setBucketWebsiteCalled = false;
        backend.SetBucketWebsite = (b, conf, ct) => { setBucketWebsiteCalled = true; return Task.CompletedTask; };

        bool deleteBucketWebsiteCalled = false;
        backend.DeleteBucketWebsite = (b, ct) => { deleteBucketWebsiteCalled = true; return Task.CompletedTask; };

        bool getBucketLoggingCalled = false;
        backend.GetBucketLogging = (b, ct) => { getBucketLoggingCalled = true; return Task.FromResult(new BucketLoggingStatus()); };

        bool setBucketLoggingCalled = false;
        backend.SetBucketLogging = (b, l, ct) => { setBucketLoggingCalled = true; return Task.CompletedTask; };

        bool getObjectMetadataCalled = false;
        backend.GetObjectMetadata = (b, k, ct) => { getObjectMetadataCalled = true; return Task.FromResult<ObjectMetadata?>(new ObjectMetadata()); };

        bool getObjectCalled = false;
        backend.GetObject = (b, k, start, end, ct) => { getObjectCalled = true; return Task.FromResult(new S3ObjectPayload { Stream = Stream.Null, ContentLength = 0, ContentType = "text/plain", ETag = "\"etag\"" }); };

        bool putObjectCalled = false;
        backend.PutObject = (b, k, stream, len, type, meta, ct) => { putObjectCalled = true; return Task.FromResult("\"etag-put\""); };

        bool deleteObjectCalled = false;
        backend.DeleteObject = (b, k, vid, ct) => { deleteObjectCalled = true; return Task.CompletedTask; };

        bool copyObjectCalled = false;
        backend.CopyObject = (sb, sk, db, dk, ct) => { copyObjectCalled = true; return Task.FromResult(new CopyObjectResult()); };

        bool deleteMultipleObjectsCalled = false;
        backend.DeleteMultipleObjects = (b, objs, ct) => { deleteMultipleObjectsCalled = true; return Task.FromResult(new DeleteResult()); };

        bool getObjectTagsCalled = false;
        backend.GetObjectTags = (b, k, ct) => { getObjectTagsCalled = true; return Task.FromResult(new Tagging()); };

        bool setObjectTagsCalled = false;
        backend.SetObjectTags = (b, k, t, ct) => { setObjectTagsCalled = true; return Task.CompletedTask; };

        bool deleteObjectTagsCalled = false;
        backend.DeleteObjectTags = (b, k, ct) => { deleteObjectTagsCalled = true; return Task.CompletedTask; };

        bool getLegalHoldCalled = false;
        backend.GetLegalHold = (b, k, ct) => { getLegalHoldCalled = true; return Task.FromResult(new LegalHold()); };

        bool setLegalHoldCalled = false;
        backend.SetLegalHold = (b, k, lh, ct) => { setLegalHoldCalled = true; return Task.CompletedTask; };

        bool getRetentionCalled = false;
        backend.GetRetention = (b, k, ct) => { getRetentionCalled = true; return Task.FromResult(new Retention()); };

        bool setRetentionCalled = false;
        backend.SetRetention = (b, k, r, ct) => { setRetentionCalled = true; return Task.CompletedTask; };

        bool getObjectAclCalled = false;
        backend.GetObjectAcl = (b, k, ct) => { getObjectAclCalled = true; return Task.FromResult(new AccessControlPolicy()); };

        bool setObjectAclCalled = false;
        backend.SetObjectAcl = (b, k, acl, ct) => { setObjectAclCalled = true; return Task.CompletedTask; };

        bool deleteObjectAclCalled = false;
        backend.DeleteObjectAcl = (b, k, ct) => { deleteObjectAclCalled = true; return Task.CompletedTask; };

        bool restoreObjectCalled = false;
        backend.RestoreObject = (b, k, r, ct) => { restoreObjectCalled = true; return Task.FromResult(new RestoreObjectResult()); };

        bool createMultipartUploadCalled = false;
        backend.CreateMultipartUpload = (b, k, ct) => { createMultipartUploadCalled = true; return Task.FromResult(new InitiateMultipartUploadResult()); };

        bool uploadPartCalled = false;
        backend.UploadPart = (b, k, u, num, s, len, ct) => { uploadPartCalled = true; return Task.FromResult("\"etag-part\""); };

        bool uploadPartCopyCalled = false;
        backend.UploadPartCopy = (sb, sk, db, dk, u, num, start, end, ct) => { uploadPartCopyCalled = true; return Task.FromResult(new CopyPartResult()); };

        bool completeMultipartUploadCalled = false;
        backend.CompleteMultipartUpload = (b, k, u, parts, ct) => { completeMultipartUploadCalled = true; return Task.FromResult(new CompleteMultipartUploadResult()); };

        bool abortMultipartUploadCalled = false;
        backend.AbortMultipartUpload = (b, k, u, ct) => { abortMultipartUploadCalled = true; return Task.CompletedTask; };

        bool listPartsCalled = false;
        backend.ListParts = (b, k, u, ct) => { listPartsCalled = true; return Task.FromResult(new ListPartsResult()); };

        // Execute all methods
        await backend.ListBucketsAsync(dummyReq);
        await backend.BucketExistsAsync("b");
        await backend.CreateBucketAsync("b", null);
        await backend.DeleteBucketAsync("b");
        await backend.GetBucketLocationAsync("b");
        await backend.ListObjectsAsync("b", dummyReq);
        await backend.ListObjectVersionsAsync("b");
        await backend.ListMultipartUploadsAsync("b");
        await backend.GetBucketAclAsync("b");
        await backend.SetBucketAclAsync("b", new AccessControlPolicy());
        await backend.DeleteBucketAclAsync("b");
        await backend.GetBucketTagsAsync("b");
        await backend.SetBucketTagsAsync("b", new Tagging());
        await backend.DeleteBucketTagsAsync("b");
        await backend.GetBucketVersioningAsync("b");
        await backend.SetBucketVersioningAsync("b", new VersioningConfiguration());
        await backend.GetBucketWebsiteAsync("b");
        await backend.SetBucketWebsiteAsync("b", new WebsiteConfiguration());
        await backend.DeleteBucketWebsiteAsync("b");
        await backend.GetBucketLoggingAsync("b");
        await backend.SetBucketLoggingAsync("b", new BucketLoggingStatus());

        await backend.GetObjectMetadataAsync("b", "k");
        await backend.GetObjectAsync("b", "k");
        await backend.PutObjectAsync("b", "k", Stream.Null, 0, null);
        await backend.DeleteObjectAsync("b", "k");
        await backend.CopyObjectAsync("b1", "k1", "b2", "k2");
        await backend.DeleteMultipleObjectsAsync("b", new DeleteMultiple());
        await backend.GetObjectTagsAsync("b", "k");
        await backend.SetObjectTagsAsync("b", "k", new Tagging());
        await backend.DeleteObjectTagsAsync("b", "k");
        await backend.GetLegalHoldAsync("b", "k");
        await backend.SetLegalHoldAsync("b", "k", new LegalHold());
        await backend.GetRetentionAsync("b", "k");
        await backend.SetRetentionAsync("b", "k", new Retention());
        await backend.GetObjectAclAsync("b", "k");
        await backend.SetObjectAclAsync("b", "k", new AccessControlPolicy());
        await backend.DeleteObjectAclAsync("b", "k");
        await backend.RestoreObjectAsync("b", "k", new RestoreRequest());
        await backend.CreateMultipartUploadAsync("b", "k");
        await backend.UploadPartAsync("b", "k", "u", 1, Stream.Null, 0);
        await backend.UploadPartCopyAsync("b1", "k1", "b2", "k2", "u", 1);
        await backend.CompleteMultipartUploadAsync("b", "k", "u", new CompleteMultipartUpload());
        await backend.AbortMultipartUploadAsync("b", "k", "u");
        await backend.ListPartsAsync("b", "k", "u");

        // Assert all were invoked
        listBucketsCalled.Should().BeTrue();
        bucketExistsCalled.Should().BeTrue();
        createBucketCalled.Should().BeTrue();
        deleteBucketCalled.Should().BeTrue();
        getBucketLocationCalled.Should().BeTrue();
        listObjectsCalled.Should().BeTrue();
        listObjectVersionsCalled.Should().BeTrue();
        listMultipartUploadsCalled.Should().BeTrue();
        getBucketAclCalled.Should().BeTrue();
        setBucketAclCalled.Should().BeTrue();
        deleteBucketAclCalled.Should().BeTrue();
        getBucketTagsCalled.Should().BeTrue();
        setBucketTagsCalled.Should().BeTrue();
        deleteBucketTagsCalled.Should().BeTrue();
        getBucketVersioningCalled.Should().BeTrue();
        setBucketVersioningCalled.Should().BeTrue();
        getBucketWebsiteCalled.Should().BeTrue();
        setBucketWebsiteCalled.Should().BeTrue();
        deleteBucketWebsiteCalled.Should().BeTrue();
        getBucketLoggingCalled.Should().BeTrue();
        setBucketLoggingCalled.Should().BeTrue();
        getObjectMetadataCalled.Should().BeTrue();
        getObjectCalled.Should().BeTrue();
        putObjectCalled.Should().BeTrue();
        deleteObjectCalled.Should().BeTrue();
        copyObjectCalled.Should().BeTrue();
        deleteMultipleObjectsCalled.Should().BeTrue();
        getObjectTagsCalled.Should().BeTrue();
        setObjectTagsCalled.Should().BeTrue();
        deleteObjectTagsCalled.Should().BeTrue();
        getLegalHoldCalled.Should().BeTrue();
        setLegalHoldCalled.Should().BeTrue();
        getRetentionCalled.Should().BeTrue();
        setRetentionCalled.Should().BeTrue();
        getObjectAclCalled.Should().BeTrue();
        setObjectAclCalled.Should().BeTrue();
        deleteObjectAclCalled.Should().BeTrue();
        restoreObjectCalled.Should().BeTrue();
        createMultipartUploadCalled.Should().BeTrue();
        uploadPartCalled.Should().BeTrue();
        uploadPartCopyCalled.Should().BeTrue();
        completeMultipartUploadCalled.Should().BeTrue();
        abortMultipartUploadCalled.Should().BeTrue();
        listPartsCalled.Should().BeTrue();
    }
}
