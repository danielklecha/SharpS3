namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Models;
using SharpS3.Serialization;

[TestClass]
public class SerializationHelperTests
{
    [TestMethod]
    public void Error_AllErrorCodes_HaveValidMessageAndStatusCode()
    {
        foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
        {
            var err = new Error(code, "test-key", "v1", "req-1", "resource-1");
            err.Code.Should().Be(code);
            err.Key.Should().Be("test-key");
            err.VersionId.Should().Be("v1");
            err.RequestId.Should().Be("req-1");
            err.Resource.Should().Be("resource-1");

            err.Message.Should().NotBeNullOrWhiteSpace();
            err.HttpStatusCode.Should().BeInRange(300, 505);

            // Custom message overrides default message
            err.Message = "Custom override";
            err.Message.Should().Be("Custom override");

            // ShouldSerialize checks
            err.ShouldSerializeKey().Should().BeTrue();
            err.ShouldSerializeVersionId().Should().BeTrue();
            err.ShouldSerializeRequestId().Should().BeTrue();
            err.ShouldSerializeResource().Should().BeTrue();
        }

        // Default code for invalid enum
        var unknownErr = new Error((ErrorCode)99999);
        unknownErr.Message.Should().Be("An error of an unknown type was encountered.");
        unknownErr.HttpStatusCode.Should().Be(500);

        // ActualObjectSize property
        var sizeErr = new Error();
        sizeErr.ActualObjectSize = 100;
        sizeErr.ActualObjectSize.Should().Be(100);
        sizeErr.ShouldSerializeActualObjectSize().Should().BeTrue();

        Action actNegativeSize = () => sizeErr.ActualObjectSize = -1;
        actNegativeSize.Should().Throw<ArgumentOutOfRangeException>();

        // Other ShouldSerialize methods
        sizeErr.BucketName = "b";
        sizeErr.ShouldSerializeBucketName().Should().BeTrue();
        sizeErr.UploadId = "u";
        sizeErr.ShouldSerializeUploadId().Should().BeTrue();
        sizeErr.ArgumentName = "arg";
        sizeErr.ShouldSerializeArgumentName().Should().BeTrue();
        sizeErr.ArgumentValue = "val";
        sizeErr.ShouldSerializeArgumentValue().Should().BeTrue();
        sizeErr.Condition = "cond";
        sizeErr.ShouldSerializeCondition().Should().BeTrue();
        sizeErr.RangeRequested = "range";
        sizeErr.ShouldSerializeRangeRequested().Should().BeTrue();
        sizeErr.HostId = "host";
        sizeErr.ShouldSerializeHostId().Should().BeTrue();
    }

    [TestMethod]
    public void ByteRange_AllVariations_And_Formatting()
    {
        var r1 = ByteRange.FromStartEnd(10, 29);
        r1.Start.Should().Be(10);
        r1.End.Should().Be(29);
        r1.ToString().Should().Be("bytes=10-29");

        var r2 = ByteRange.FromSuffix(50);
        r2.SuffixLength.Should().Be(50);
        r2.ToString().Should().Be("bytes=-50");

        var r3 = ByteRange.FromStart(100);
        r3.Start.Should().Be(100);
        r3.End.Should().BeNull();
        r3.ToString().Should().Be("bytes=100-");

        var r4 = new ByteRange(0, 10);
        r4.Start.Should().Be(0);
        r4.End.Should().Be(10);
        r4.ToString().Should().Be("bytes=0-10");

        var emptyRange = new ByteRange();
        emptyRange.ToString().Should().BeEmpty();
    }

    [TestMethod]
    public void DeleteResult_And_DeleteErrorCollection_QuietMode()
    {
        var delObj = new Deleted("file.txt", "v1", true, "dm-v1");
        delObj.ShouldSerializeVersionId().Should().BeTrue();
        delObj.ShouldSerializeDeleteMarker().Should().BeTrue();
        delObj.ShouldSerializeDeleteMarkerVersionId().Should().BeTrue();

        var err = new Error(ErrorCode.AccessDenied, "file2.txt");
        var deleteResult = new DeleteResult(new List<Deleted> { delObj }, new List<Error> { err });
        deleteResult.DeletedObjects.Count.Should().Be(1);
        deleteResult.Errors.Count.Should().Be(1);

        // Collection directly
        var unboundCollection = new DeleteErrorCollection();
        unboundCollection.Add(new DeleteError(new Error(ErrorCode.NoSuchKey)));

        var boundCollection = new DeleteErrorCollection(deleteResult, new[] { new Error(ErrorCode.InternalError) });
        boundCollection.Add(new DeleteError(new Error(ErrorCode.NoSuchBucket)));
        deleteResult.Errors.Count.Should().Be(2);

        Action actNullOwner = () => new DeleteErrorCollection(null!, null!);
        actNullOwner.Should().Throw<ArgumentNullException>();

        // XmlErrors property
        deleteResult.XmlErrors.Count.Should().Be(2);

        // Normal serialization (Quiet = false)
        string xmlNormal = ResponseSerializationContext.Run(1, false, false, () => SerializationHelper.SerializeXml(deleteResult));
        xmlNormal.Should().Contain("<Deleted>");
        xmlNormal.Should().Contain("<Key>file.txt</Key>");

        // Quiet mode (Quiet = true) -> Deleted entries omitted
        string xmlQuiet = ResponseSerializationContext.Run(1, false, true, () => SerializationHelper.SerializeXml(deleteResult));
        xmlQuiet.Should().NotContain("<Deleted>");
        xmlQuiet.Should().Contain("<Error>");

        // Set null to Errors
        deleteResult.Errors = null!;
        deleteResult.Errors.Should().BeEmpty();
    }

    [TestMethod]
    public void ListVersionsResult_And_ListVersionsEntryCollection_FullCoverage()
    {
        var lvr = new ListVersionsResult();
        lvr.Name = "my-bucket";
        lvr.Prefix = "photos/";
        lvr.KeyMarker = "km";
        lvr.VersionIdMarker = "vm";
        lvr.MaxKeys = 1000;
        lvr.IsTruncated = true;
        lvr.NextKeyMarker = "nkm";
        lvr.NextVersionIdMarker = "nvm";
        lvr.Delimiter = "/";
        lvr.EncodingType = "url";
        lvr.CommonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("photos/2026/") };

        var ov = new ObjectVersion
        {
            Key = "photos/cat.jpg",
            VersionId = "v1",
            IsLatest = true,
            LastModified = DateTime.UtcNow,
            ETag = "\"abc\"",
            Size = 1024,
            StorageClass = StorageClassEnum.STANDARD,
            Owner = new Owner("owner-1", "DisplayName")
        };
        var dm = new DeleteMarker
        {
            Key = "photos/dog.jpg",
            VersionId = "v2",
            IsLatest = true,
            LastModified = DateTime.UtcNow,
            Owner = new Owner("owner-1", "DisplayName")
        };
        dm.ShouldSerializeStorageClass().Should().BeFalse();

        // 1. Serialize using fallback (Versions and DeleteMarkers when Entries is empty)
        lvr.Versions.Add(ov);
        lvr.DeleteMarkers.Add(dm);
        lvr.ShouldSerializeVersions().Should().BeTrue();
        lvr.ShouldSerializeDeleteMarkers().Should().BeTrue();

        string xmlFallback = SerializationHelper.SerializeXml(lvr, true);
        xmlFallback.Should().Contain("<Version");
        xmlFallback.Should().Contain("<DeleteMarker");

        // 2. Serialize using Entries
        lvr.Entries.Add(ov);
        lvr.Entries.Add(dm);
        string xmlEntries = SerializationHelper.SerializeXml(lvr, true);
        xmlEntries.Should().Contain("<Version");
        xmlEntries.Should().Contain("<DeleteMarker");

        // 3. Deserialize round-trip
        var deserialized = SerializationHelper.DeserializeXml<ListVersionsResult>(xmlEntries);
        deserialized.Name.Should().Be("my-bucket");
        deserialized.Entries.Count.Should().Be(2);

        // Parameterized constructor
        var paramLvr = new ListVersionsResult(
            "b",
            new List<ObjectVersion> { ov },
            new List<DeleteMarker> { dm },
            500,
            "prefix",
            "km",
            "vm",
            false,
            "nkm",
            "nvm",
            "/",
            new List<CommonPrefixes>(),
            "url",
            "us-east-1");
        paramLvr.Name.Should().Be("b");
        paramLvr.MaxKeys.Should().Be(500);

        Action actEmptyName = () => new ListVersionsResult(
            "",
            new List<ObjectVersion>(),
            new List<DeleteMarker>(),
            100);
        actEmptyName.Should().Throw<ArgumentNullException>();

        Action actNegativeMaxKeys = () => lvr.MaxKeys = -1;
        actNegativeMaxKeys.Should().Throw<ArgumentOutOfRangeException>();

        // Entry collection
        var unboundEntryCol = new ListVersionsEntryCollection();
        unboundEntryCol.Add(ov);

        var boundEntryCol = new ListVersionsEntryCollection(lvr, new VersionedEntity[] { ov });
        boundEntryCol.Add(dm);

        Action actNullEntryOwner = () => new ListVersionsEntryCollection(null!, null!);
        actNullEntryOwner.Should().Throw<ArgumentNullException>();

        // Set null checks
        lvr.Versions = null!;
        lvr.DeleteMarkers = null!;
        lvr.Entries = null!;
        lvr.Prefix = null!;
        lvr.Prefix.Should().BeEmpty();
    }

    [TestMethod]
    public void WebsiteConfiguration_And_RoutingRules_Serialization()
    {
        var config = new WebsiteConfiguration
        {
            IndexDocument = new IndexDocument("index.html"),
            ErrorDocument = new ErrorDocument("error.html"),
            RedirectAllRequestsTo = new RedirectAllRequestsTo("example.com", ProtocolEnum.Https)
        };

        var rule = new RoutingRule
        {
            Condition = new Condition { HttpErrorCodeReturnedEquals = "404", KeyPrefixEquals = "docs/" },
            Redirect = new Redirect
            {
                HostName = "archive.example.com",
                HttpRedirectCode = 301,
                Protocol = ProtocolEnum.Https,
                ReplaceKeyPrefixWith = "old-docs/",
                ReplaceKeyWith = "target.html"
            }
        };
        config.RoutingRules = new RoutingRules(new List<RoutingRule> { rule });

        string xml = SerializationHelper.SerializeXml(config, true);
        xml.Should().Contain("index.html");
        xml.Should().Contain("error.html");
        xml.Should().Contain("example.com");
        xml.Should().Contain("docs/");
        xml.Should().Contain("archive.example.com");

        var deserialized = SerializationHelper.DeserializeXml<WebsiteConfiguration>(xml);
        deserialized.IndexDocument.Suffix.Should().Be("index.html");
        deserialized.ErrorDocument.Key.Should().Be("error.html");
        deserialized.RedirectAllRequestsTo.HostName.Should().Be("example.com");
        deserialized.RoutingRules.Rules.Count.Should().Be(1);
    }

    [TestMethod]
    public void SelectObjectContent_And_Serializations()
    {
        var req = new SelectObjectContentRequest
        {
            Expression = "SELECT * FROM s3object s",
            ExpressionType = ExpressionTypeEnum.SQL,
            InputSerialization = new InputSerialization
            {
                CompressionType = CompressionTypeEnum.GZIP,
                CSV = new CsvInputSerialization
                {
                    FileHeaderInfo = FileHeaderInfoEnum.USE,
                    FieldDelimiter = ",",
                    RecordDelimiter = "\n",
                    QuoteCharacter = "\"",
                    QuoteEscapeCharacter = "\\",
                    Comments = "#",
                    AllowQuotedRecordDelimiter = true
                },
                JSON = new JsonInputSerialization
                {
                    Type = JsonTypeEnum.LINES
                },
                Parquet = new ParquetInputSerialization()
            },
            OutputSerialization = new OutputSerialization
            {
                CSV = new CsvOutputSerialization
                {
                    FieldDelimiter = ";",
                    RecordDelimiter = "\r\n",
                    QuoteCharacter = "'",
                    QuoteEscapeCharacter = "/",
                    QuoteFields = QuoteFieldsEnum.ALWAYS
                },
                JSON = new JsonOutputSerialization
                {
                    RecordDelimiter = "\n"
                }
            },
            RequestProgress = new RequestProgress { Enabled = true },
            ScanRange = new ScanRange { Start = 0, End = 1000 }
        };
        req.ScanRange.Start.Should().Be(0);
        req.ScanRange.End.Should().Be(1000);

        Action actNegativeStart = () => req.ScanRange.Start = -1;
        actNegativeStart.Should().Throw<ArgumentOutOfRangeException>();

        Action actNegativeEnd = () => req.ScanRange.End = -1;
        actNegativeEnd.Should().Throw<ArgumentOutOfRangeException>();

        string xml = SerializationHelper.SerializeXml(req, true);
        xml.Should().Contain("SELECT * FROM s3object s");
        xml.Should().Contain("GZIP");
        xml.Should().Contain("USE");

        var deserialized = SerializationHelper.DeserializeXml<SelectObjectContentRequest>(xml);
        deserialized.Expression.Should().Be("SELECT * FROM s3object s");
        deserialized.ExpressionType.Should().Be(ExpressionTypeEnum.SQL);
        deserialized.InputSerialization.CompressionType.Should().Be(CompressionTypeEnum.GZIP);
    }

    [TestMethod]
    public void AccessControlPolicy_And_Grantees()
    {
        var canonical = new CanonicalUser { ID = "user-1", DisplayName = "User One" };
        var group = new Group { URI = "http://acs.amazonaws.com/groups/global/AllUsers" };
        var email = new AmazonCustomerByEmail { EmailAddress = "user@example.com" };

        canonical.ShouldSerializeID().Should().BeTrue();
        canonical.ShouldSerializeDisplayName().Should().BeTrue();
        group.ShouldSerializeURI().Should().BeTrue();
        email.ShouldSerializeEmailAddress().Should().BeTrue();

        var acl = new AccessControlList(new List<Grant>
        {
            new Grant(canonical, PermissionEnum.FullControl),
            new Grant(group, PermissionEnum.Read),
            new Grant(email, PermissionEnum.Write)
        });

        var policy = new AccessControlPolicy(new Owner("owner-id", "OwnerName"), acl);
        string xml = SerializationHelper.SerializeXml(policy, true);
        xml.Should().Contain("CanonicalUser");
        xml.Should().Contain("Group");
        xml.Should().Contain("AmazonCustomerByEmail");
        xml.Should().Contain("FULL_CONTROL");

        var deserialized = SerializationHelper.DeserializeXml<AccessControlPolicy>(xml);
        deserialized.Owner.ID.Should().Be("owner-id");
        deserialized.Acl.Grants.Count.Should().Be(3);

        // Grantee error handling
        var g = new Grantee();
        Action actEmptyType = () => g.GranteeType = "";
        actEmptyType.Should().Throw<ArgumentNullException>();

        Action actUnknownType = () => g.GranteeType = "UnknownType";
        actUnknownType.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void BucketLoggingStatus_And_Retention_LegalHold()
    {
        var logging = new BucketLoggingStatus
        {
            Enabled = new LoggingEnabled
            {
                TargetBucket = "log-bucket",
                TargetPrefix = "s3-logs/",
                TargetGrants = new TargetGrants(new List<Grant>
                {
                    new Grant(new CanonicalUser { ID = "log-delivery-id" }, PermissionEnum.Write)
                })
            }
        };
        string xmlLogging = SerializationHelper.SerializeXml(logging, true);
        xmlLogging.Should().Contain("log-bucket");

        var deserializedLogging = SerializationHelper.DeserializeXml<BucketLoggingStatus>(xmlLogging);
        deserializedLogging.Enabled.TargetBucket.Should().Be("log-bucket");

        var legalHold = new LegalHold("ON");
        legalHold.Status.Should().Be("ON");
        string xmlLegal = SerializationHelper.SerializeXml(legalHold);
        xmlLegal.Should().Contain("ON");

        Action actInvalidLegal = () => legalHold.Status = "INVALID";
        actInvalidLegal.Should().Throw<ArgumentException>();

        var retention = new Retention(RetentionModeEnum.Compliance, DateTime.UtcNow.AddDays(30));
        retention.Mode.Should().Be(RetentionModeEnum.Compliance);
        string xmlRetention = SerializationHelper.SerializeXml(retention);
        xmlRetention.Should().Contain("COMPLIANCE");
    }

    [TestMethod]
    public void RestoreRequest_And_RestoreStatus()
    {
        var req = new RestoreRequest
        {
            Days = 7,
            GlacierJobParameters = new GlacierJobParameters { Tier = RestoreTierEnum.Expedited },
            Tier = RestoreTierEnum.Expedited,
            Description = "desc"
        };
        string xml = SerializationHelper.SerializeXml(req);
        xml.Should().Contain("<Days>7</Days>");
        xml.Should().Contain("Expedited");

        var deserialized = SerializationHelper.DeserializeXml<RestoreRequest>(xml);
        deserialized.Days.Should().Be(7);
        deserialized.GlacierJobParameters.Tier.Should().Be(RestoreTierEnum.Expedited);

        var status = new RestoreStatus
        {
            OngoingRequest = true,
            ExpiryDate = DateTime.UtcNow.AddDays(7)
        };
        status.OngoingRequest.Should().BeTrue();
        status.ExpiryDate.Should().NotBeNull();
        status.HeaderValue.Should().Contain("ongoing-request=\"true\"");

        status.OngoingRequest = false;
        status.HeaderValue.Should().Contain("ongoing-request=\"false\"");
        status.HeaderValue.Should().Contain("expiry-date=");

        var statusNoExpiry = new RestoreStatus { OngoingRequest = false, ExpiryDate = null };
        statusNoExpiry.HeaderValue.Should().Be("ongoing-request=\"false\"");
    }

    [TestMethod]
    public void ListMultipartUploads_And_ListPartsResult()
    {
        var upload = new Upload
        {
            Key = "large-file.bin",
            UploadId = "up-123",
            Initiator = new Owner("init-id", "Initiator"),
            Owner = new Owner("owner-id", "Owner"),
            StorageClass = StorageClassEnum.STANDARD,
            Initiated = DateTime.UtcNow,
            ChecksumAlgorithm = ChecksumAlgorithmEnum.SHA256,
            IncludeChecksumAlgorithm = true
        };
        upload.ShouldSerializeChecksumAlgorithm().Should().BeTrue();

        var lmur = new ListMultipartUploadsResult
        {
            Bucket = "multi-bucket",
            KeyMarker = "km",
            UploadIdMarker = "uim",
            NextKeyMarker = "nkm",
            NextUploadIdMarker = "nuim",
            MaxUploads = 100,
            IsTruncated = false,
            Uploads = new List<Upload> { upload },
            CommonPrefixes = new List<CommonPrefixes> { new CommonPrefixes("folder/") },
            Delimiter = "/",
            Prefix = "large-",
            EncodingType = "url"
        };
        lmur.ShouldSerializeUploads().Should().BeTrue();
        lmur.ShouldSerializeCommonPrefixes().Should().BeTrue();
        lmur.ShouldSerializeEncodingType().Should().BeTrue();

        string xmlLmur = SerializationHelper.SerializeXml(lmur, true);
        xmlLmur.Should().Contain("up-123");

        var deserializedLmur = SerializationHelper.DeserializeXml<ListMultipartUploadsResult>(xmlLmur);
        deserializedLmur.Bucket.Should().Be("multi-bucket");
        deserializedLmur.Uploads.Count.Should().Be(1);

        var part = new Part
        {
            PartNumber = 1,
            LastModified = DateTime.UtcNow,
            ETag = "\"part-etag\"",
            Size = 5242880,
            ChecksumCRC32 = "crc32",
            ChecksumCRC32C = "crc32c",
            ChecksumSHA1 = "sha1",
            ChecksumSHA256 = "sha256"
        };
        part.ShouldSerializeChecksumCRC32().Should().BeTrue();
        part.ShouldSerializeChecksumCRC32C().Should().BeTrue();
        part.ShouldSerializeChecksumSHA1().Should().BeTrue();
        part.ShouldSerializeChecksumSHA256().Should().BeTrue();

        var lpr = new ListPartsResult
        {
            Bucket = "part-bucket",
            Key = "large-file.bin",
            UploadId = "up-123",
            PartNumberMarker = 0,
            NextPartNumberMarker = 1,
            MaxParts = 1000,
            IsTruncated = false,
            Parts = new List<Part> { part },
            Initiator = new Owner("init-id", "Init"),
            Owner = new Owner("owner-id", "Owner"),
            StorageClass = StorageClassEnum.STANDARD,
            ChecksumAlgorithm = ChecksumAlgorithmEnum.CRC32,
            IncludeChecksumAlgorithm = true
        };
        lpr.ShouldSerializeChecksumAlgorithm().Should().BeTrue();

        string xmlLpr = SerializationHelper.SerializeXml(lpr, true);
        xmlLpr.Should().Contain("part-etag");

        var deserializedLpr = SerializationHelper.DeserializeXml<ListPartsResult>(xmlLpr);
        deserializedLpr.Parts.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task S3ObjectResponse_SyncAndAsyncDispose()
    {
        var httpResp = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var mem = new MemoryStream(Encoding.UTF8.GetBytes("Object content"));
        var resp = new S3ObjectResponse(
            httpResp,
            mem,
            mem.Length,
            "text/plain",
            "\"etag-123\"",
            DateTimeOffset.UtcNow,
            new Dictionary<string, string> { { "custom", "value" } });

        resp.ContentLength.Should().Be(mem.Length);
        resp.ContentType.Should().Be("text/plain");
        resp.ETag.Should().Be("\"etag-123\"");
        resp.Metadata["custom"].Should().Be("value");
        resp.Stream.Should().BeSameAs(mem);

        var bytes = await resp.ReadAsByteArrayAsync();
        bytes.Length.Should().Be((int)mem.Length);

        // Reset stream position for string read
        mem.Position = 0;
        var text = await resp.ReadAsStringAsync();
        text.Should().Be("Object content");

        resp.Dispose();
        Action actReadDisposed = () => mem.ReadByte();
        actReadDisposed.Should().Throw<ObjectDisposedException>();

        var httpResp2 = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        var mem2 = new MemoryStream(Encoding.UTF8.GetBytes("Async content"));
        var resp2 = new S3ObjectResponse(httpResp2, mem2, mem2.Length, null, null, null, null!);
        await resp2.DisposeAsync();
        Action actReadDisposed2 = () => mem2.ReadByte();
        actReadDisposed2.Should().Throw<ObjectDisposedException>();
    }

    [TestMethod]
    public void SerializationHelper_CopyObject_And_PreambleHandling()
    {
        var original = new LocationConstraint("us-west-2");
        var copy = SerializationHelper.CopyObject(original);
        copy.Text.Should().Be("us-west-2");

        SerializationHelper.CopyObject<LocationConstraint>(null!).Should().BeNull();

        // Preamble UTF8 string
        string xml = SerializationHelper.SerializeXml(original, pretty: false);
        string withBom = "\uFEFF" + xml;
        var fromBom = SerializationHelper.DeserializeXml<LocationConstraint>(withBom);
        fromBom.Text.Should().Be("us-west-2");

        // Deserialize from bytes
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        var fromBytes = SerializationHelper.DeserializeXml<LocationConstraint>(bytes);
        fromBytes.Text.Should().Be("us-west-2");

        Action actNullBytes = () => SerializationHelper.DeserializeXml<LocationConstraint>((byte[])null!);
        actNullBytes.Should().Throw<ArgumentNullException>();

        Action actEmptyString = () => SerializationHelper.DeserializeXml<LocationConstraint>("");
        actEmptyString.Should().Throw<ArgumentNullException>();

        Action actNullObject = () => SerializationHelper.SerializeXml(null!);
        actNullObject.Should().Throw<ArgumentNullException>();
    }
}
