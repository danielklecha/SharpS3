namespace SharpS3.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3;
using SharpS3.Models;
using SharpS3.Serialization;

[TestClass]
public class ModelsConstructorsTests
{
    [TestMethod]
    public void Model_Parameterized_Constructors_And_Properties()
    {
        var owner = new Owner("owner-id", "owner-name");

        // ObjectVersion
        var dt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ov = new ObjectVersion("k1", "v1", true, dt, "\"etag\"", 100, owner, StorageClassEnum.STANDARD);
        ov.Key.Should().Be("k1");
        ov.VersionId.Should().Be("v1");
        ov.IsLatest.Should().BeTrue();
        ov.LastModified.Should().Be(dt);
        ov.ETag.Should().Be("\"etag\"");
        ov.Size.Should().Be(100);
        ov.Owner.Should().Be(owner);
        ov.StorageClass.Should().Be(StorageClassEnum.STANDARD);

        // DeleteMarker
        var dm = new DeleteMarker("k2", "v2", false, dt, owner);
        dm.Key.Should().Be("k2");
        dm.VersionId.Should().Be("v2");
        dm.IsLatest.Should().BeFalse();
        dm.LastModified.Should().Be(dt);
        dm.Owner.Should().Be(owner);

        // DeleteError
        var deFromError = new DeleteError(new Error(ErrorCode.AccessDenied, "k3", "v3", "req", "res"));
        deFromError.Key.Should().Be("k3");
        deFromError.VersionId.Should().Be("v3");
        deFromError.Code.Should().Be(ErrorCode.AccessDenied);

        // DeleteMultiple
        var delMulti = new DeleteMultiple(true, new List<SharpS3.Models.Object> { new SharpS3.Models.Object("k4", "v4") });
        delMulti.Quiet.Should().BeTrue();
        delMulti.Objects.Count.Should().Be(1);

        // SharpS3.Models.Object
        var obj = new SharpS3.Models.Object("k5", "v5");
        obj.Key.Should().Be("k5");
        obj.VersionId.Should().Be("v5");

        // ObjectMetadata
        var objMeta = new ObjectMetadata("k5", dt, "\"etag2\"", 200, owner, StorageClassEnum.STANDARD);
        objMeta.Key.Should().Be("k5");
        objMeta.LastModified.Should().Be(dt);
        objMeta.Size.Should().Be(200);
        objMeta.ETag.Should().Be("\"etag2\"");
        objMeta.Owner.Should().Be(owner);
        objMeta.StorageClass.Should().Be(StorageClassEnum.STANDARD);

        // Condition, Redirect, RoutingRule, WebsiteConfiguration
        var cond = new Condition("404", "images/");
        cond.HttpErrorCodeReturnedEquals.Should().Be("404");
        cond.KeyPrefixEquals.Should().Be("images/");

        var redir = new Redirect("cdn.example.com", 301, ProtocolEnum.Https, "new-images/", "index.html");
        redir.HostName.Should().Be("cdn.example.com");
        redir.HttpRedirectCode.Should().Be(301);
        redir.Protocol.Should().Be(ProtocolEnum.Https);
        redir.ReplaceKeyPrefixWith.Should().Be("new-images/");
        redir.ReplaceKeyWith.Should().Be("index.html");

        var rule = new RoutingRule(cond, redir);
        rule.Condition.Should().Be(cond);
        rule.Redirect.Should().Be(redir);

        var webConfig = new WebsiteConfiguration(
            new RedirectAllRequestsTo("example.com", ProtocolEnum.Https),
            new IndexDocument("index.html"),
            new ErrorDocument("error.html"),
            new RoutingRules(new List<RoutingRule> { rule }));
        webConfig.IndexDocument.Suffix.Should().Be("index.html");
        webConfig.ErrorDocument.Key.Should().Be("error.html");
        webConfig.RedirectAllRequestsTo.HostName.Should().Be("example.com");
        webConfig.RoutingRules.Rules.Count.Should().Be(1);

        // CreateBucketConfiguration
        var cbc = new CreateBucketConfiguration("us-west-1");
        cbc.LocationConstraint.Should().Be("us-west-1");

        // LoggingEnabled, BucketLoggingStatus
        var grant = new Grant(new Grantee("grantee1", "Grantee", "uri", "CanonicalUser", "email"), PermissionEnum.FullControl);
        var le = new LoggingEnabled("target-bucket", "logs/", new TargetGrants(new List<Grant> { grant }));
        le.TargetBucket.Should().Be("target-bucket");
        le.TargetPrefix.Should().Be("logs/");
        le.TargetGrants.Grants.Count.Should().Be(1);

        var bls = new BucketLoggingStatus(le);
        bls.Enabled.Should().Be(le);

        // RestoreRequest
        var rr = new RestoreRequest { Days = 10, Tier = RestoreTierEnum.Bulk, Description = "restore description" };
        rr.Days.Should().Be(10);
        rr.Tier.Should().Be(RestoreTierEnum.Bulk);
        rr.Description.Should().Be("restore description");

        // Grantee
        var gEmail = new Grantee("id", "disp", "uri", "AmazonCustomerByEmail", "user@example.com");
        gEmail.EmailAddress.Should().Be("user@example.com");
        gEmail.GranteeType.Should().Be("AmazonCustomerByEmail");

        var gGroup = new Grantee("id", "disp", "http://acs.amazonaws.com/groups/global/AllUsers", "Group", "email");
        gGroup.URI.Should().Be("http://acs.amazonaws.com/groups/global/AllUsers");

        var gCanon = new Grantee("c-id", "display", "uri", "CanonicalUser", "email");
        gCanon.ID.Should().Be("c-id");
        gCanon.DisplayName.Should().Be("display");

        // AccessControlPolicy
        var acp = new AccessControlPolicy(owner, new AccessControlList(new List<Grant> { grant }));
        acp.Owner.Should().Be(owner);
        acp.Acl.Grants.Count.Should().Be(1);
    }

    [TestMethod]
    public void Listing_UrlEncoding_Roundtrip()
    {
        // When urlEncodeListingValues is enabled and EncodingType is "url"
        var owner = new Owner("owner-id", "owner-name");
        var lbr = new ListBucketResult
        {
            Name = "my-bucket",
            Prefix = "folder with spaces & special <chars>/",
            Delimiter = "/",
            EncodingType = "url",
            Contents = new List<ObjectMetadata>
            {
                new ObjectMetadata("folder with spaces & special <chars>/file#1?.txt", DateTime.UtcNow, "\"etag\"", 10, owner)
            }
        };

        lbr.ShouldSerializeEncodingType().Should().BeTrue();
        string xml = ResponseSerializationContext.Run(2, false, true, () => SerializationHelper.SerializeXml(lbr));
        xml.Should().Contain("<EncodingType>url</EncodingType>");

        var deserialized = SerializationHelper.DeserializeXml<ListBucketResult>(xml);
        deserialized.Name.Should().Be("my-bucket");
        deserialized.EncodingType.Should().Be("url");
    }

    [TestMethod]
    public void S3Exception_Message_And_Properties()
    {
        var err = new Error(ErrorCode.NoSuchKey, "file.txt", "v1", "req-1", "/b/file.txt");
        var ex = new S3Exception(err);
        ex.Error.Should().Be(err);
        ex.HttpStatusCode.Should().Be(404);
        ex.Message.Should().Contain("The specified key does not exist.");
        ex.ToString().Should().Contain("The specified key does not exist.");
    }

    [TestMethod]
    public void S3Exception_Constructors_And_EdgeCases()
    {
        // Parameterless / default constructor (error = null, inner = null)
        var defaultEx = new S3Exception();
        defaultEx.Error.Should().BeNull();
        defaultEx.InnerException.Should().BeNull();
        defaultEx.HttpStatusCode.Should().Be(500);
        defaultEx.Message.Should().NotBeNullOrWhiteSpace();

        // Null error with inner exception
        var inner = new InvalidOperationException("Inner failure");
        var nullErrEx = new S3Exception(null, inner);
        nullErrEx.Error.Should().BeNull();
        nullErrEx.InnerException.Should().BeSameAs(inner);
        ((Exception)nullErrEx).InnerException.Should().BeSameAs(inner);
        nullErrEx.HttpStatusCode.Should().Be(500);

        // Error with inner exception
        var err = new Error(ErrorCode.AccessDenied);
        var exWithInner = new S3Exception(err, inner);
        exWithInner.Error.Should().BeSameAs(err);
        exWithInner.InnerException.Should().BeSameAs(inner);
        ((Exception)exWithInner).InnerException.Should().BeSameAs(inner);
        exWithInner.HttpStatusCode.Should().Be(403);
        exWithInner.Message.Should().Be(err.Message);

        // Property setters
        exWithInner.Error = null;
        exWithInner.HttpStatusCode.Should().Be(500);
        exWithInner.Error = err;
        exWithInner.HttpStatusCode.Should().Be(403);
        var inner2 = new ArgumentException("Second inner");
        exWithInner.InnerException = inner2;
        exWithInner.InnerException.Should().BeSameAs(inner2);
    }

    [TestMethod]
    public void TagSet_Constructors_And_PropertyFallback()
    {
        // Parameterless constructor
        var ts1 = new TagSet();
        ts1.Tags.Should().NotBeNull();
        ts1.Tags.Should().BeEmpty();

        // Constructor with list
        var tags = new List<Tag> { new Tag("k1", "v1"), new Tag("k2", "v2") };
        var ts2 = new TagSet(tags);
        ts2.Tags.Should().BeSameAs(tags);
        ts2.Tags.Count.Should().Be(2);

        // Constructor with null list falls back to empty list
        var ts3 = new TagSet(null);
        ts3.Tags.Should().NotBeNull();
        ts3.Tags.Should().BeEmpty();

        // Setting Tags to a new list
        var newTags = new List<Tag> { new Tag("env", "prod") };
        ts2.Tags = newTags;
        ts2.Tags.Should().BeSameAs(newTags);
    }

    [TestMethod]
    public void Tag_Constructors_And_Validation()
    {
        // Parameterless constructor
        var t1 = new Tag();
        t1.Key.Should().BeEmpty();
        t1.Value.Should().BeNull();

        // Parameterized constructor
        var t2 = new Tag("project", "sharps3");
        t2.Key.Should().Be("project");
        t2.Value.Should().Be("sharps3");

        // Constructor with null value
        var t3 = new Tag("project", null);
        t3.Key.Should().Be("project");
        t3.Value.Should().BeNull();

        // Property setters
        t2.Key = "newKey";
        t2.Key.Should().Be("newKey");
        t2.Value = "newVal";
        t2.Value.Should().Be("newVal");

        // Key validation in setter
        Action actSetNull = () => t2.Key = null!;
        actSetNull.Should().Throw<ArgumentNullException>();

        Action actSetEmpty = () => t2.Key = "";
        actSetEmpty.Should().Throw<ArgumentNullException>();

        // Key validation in constructor
        Action actCtorNull = () => new Tag(null!, "val");
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new Tag("", "val");
        actCtorEmpty.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void RestoreRequest_Days_Validation_And_Branches()
    {
        var rr = new RestoreRequest();
        rr.Days.Should().BeNull();

        // Valid positive days
        rr.Days = 5;
        rr.Days.Should().Be(5);

        // Null days
        rr.Days = null;
        rr.Days.Should().BeNull();

        // Days < 1 throws ArgumentOutOfRangeException
        Action actZero = () => rr.Days = 0;
        actZero.Should().Throw<ArgumentOutOfRangeException>();

        Action actNegative = () => rr.Days = -1;
        actNegative.Should().Throw<ArgumentOutOfRangeException>();

        // EffectiveTier: when Tier is set vs when GlacierJobParameters is used
        rr.Tier = RestoreTierEnum.Bulk;
        rr.EffectiveTier.Should().Be(RestoreTierEnum.Bulk);

        rr.Tier = null;
        rr.GlacierJobParameters = new GlacierJobParameters { Tier = RestoreTierEnum.Expedited };
        rr.EffectiveTier.Should().Be(RestoreTierEnum.Expedited);

        rr.GlacierJobParameters = null;
        rr.EffectiveTier.Should().BeNull();

        // HasUnsupportedRestoreSelectFields branches
        rr.HasUnsupportedRestoreSelectFields.Should().BeFalse();
        rr.AdditionalElements = Array.Empty<System.Xml.XmlElement>();
        rr.HasUnsupportedRestoreSelectFields.Should().BeFalse();

        var doc = new System.Xml.XmlDocument();
        var normalElem = doc.CreateElement("OtherElement");
        rr.AdditionalElements = new[] { normalElem };
        rr.HasUnsupportedRestoreSelectFields.Should().BeFalse();

        var selectElem = doc.CreateElement("SelectParameters");
        rr.AdditionalElements = new[] { selectElem };
        rr.HasUnsupportedRestoreSelectFields.Should().BeTrue();

        var outputElem = doc.CreateElement("OutputLocation");
        rr.AdditionalElements = new[] { outputElem };
        rr.HasUnsupportedRestoreSelectFields.Should().BeTrue();

        var typeElem = doc.CreateElement("Type");
        rr.AdditionalElements = new[] { typeElem };
        rr.HasUnsupportedRestoreSelectFields.Should().BeTrue();
    }

    [TestMethod]
    public void Object_Constructors_And_Validation()
    {
        // Parameterless constructor
        var obj1 = new SharpS3.Models.Object();
        obj1.Key.Should().BeNull();
        obj1.VersionId.Should().BeNull();

        // Parameterized constructor with key and versionId
        var obj2 = new SharpS3.Models.Object("file.txt", "v1");
        obj2.Key.Should().Be("file.txt");
        obj2.VersionId.Should().Be("v1");

        // Parameterized constructor with key only
        var obj3 = new SharpS3.Models.Object("file.txt");
        obj3.Key.Should().Be("file.txt");
        obj3.VersionId.Should().BeNull();

        // Key validation in constructor
        Action actCtorNull = () => new SharpS3.Models.Object(null!);
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new SharpS3.Models.Object("");
        actCtorEmpty.Should().Throw<ArgumentNullException>();

        // Property setters
        obj1.Key = "my-key";
        obj1.VersionId = "v2";
        obj1.Key.Should().Be("my-key");
        obj1.VersionId.Should().Be("v2");
    }

    [TestMethod]
    public void LoggingEnabled_Constructors_And_Validation()
    {
        // Parameterless constructor
        var le1 = new LoggingEnabled();
        le1.TargetBucket.Should().BeNull();
        le1.TargetPrefix.Should().BeNull();
        le1.TargetGrants.Should().NotBeNull();
        le1.TargetGrants.Grants.Should().BeEmpty();

        // Parameterized constructor with targetBucket only
        var le2 = new LoggingEnabled("target-bucket");
        le2.TargetBucket.Should().Be("target-bucket");
        le2.TargetPrefix.Should().BeNull();
        le2.TargetGrants.Should().NotBeNull();

        // Parameterized constructor with null grants
        var le3 = new LoggingEnabled("target-bucket", "prefix/", null);
        le3.TargetBucket.Should().Be("target-bucket");
        le3.TargetPrefix.Should().Be("prefix/");
        le3.TargetGrants.Should().NotBeNull();

        // Parameterized constructor with non-null grants
        var grant = new Grant(new Grantee("gid", "disp", "uri", "CanonicalUser", "email"), PermissionEnum.Read);
        var targetGrants = new TargetGrants(new List<Grant> { grant });
        var le4 = new LoggingEnabled("target-bucket", "prefix/", targetGrants);
        le4.TargetGrants.Should().BeSameAs(targetGrants);

        // Validation in constructor
        Action actCtorNull = () => new LoggingEnabled(null!);
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new LoggingEnabled("");
        actCtorEmpty.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void LocationConstraint_Constructors_And_DefaultHandling()
    {
        // Parameterless constructor defaults to us-east-1
        var lc1 = new LocationConstraint();
        lc1.Text.Should().Be("us-east-1");

        // Parameterized constructor with region
        var lc2 = new LocationConstraint("us-west-2");
        lc2.Text.Should().Be("us-west-2");

        // Parameterized constructor with null falls back to empty string
        var lc3 = new LocationConstraint(null);
        lc3.Text.Should().BeEmpty();

        // Property setter with null falls back to empty string
        lc2.Text = null!;
        lc2.Text.Should().BeEmpty();

        // Property setter with region
        lc2.Text = "eu-west-1";
        lc2.Text.Should().Be("eu-west-1");
    }

    [TestMethod]
    public void LegalHold_Constructors_Validation_And_Properties()
    {
        // Parameterless constructor defaults Status to OFF
        var lh1 = new LegalHold();
        lh1.Status.Should().Be("OFF");

        // Parameterized constructor with valid values
        var lh2 = new LegalHold("ON");
        lh2.Status.Should().Be("ON");

        var lh3 = new LegalHold("OFF");
        lh3.Status.Should().Be("OFF");

        // Parameterized constructor with null or empty
        var lh4 = new LegalHold(null!);
        lh4.Status.Should().BeNull();

        var lh5 = new LegalHold(string.Empty);
        lh5.Status.Should().BeEmpty();

        // Parameterized constructor with invalid value throws ArgumentException
        Action actCtorInvalid = () => new LegalHold("INVALID");
        actCtorInvalid.Should().Throw<ArgumentException>().WithMessage("*Unknown Status 'INVALID'*");

        // Status setter with valid values
        lh1.Status = "ON";
        lh1.Status.Should().Be("ON");

        lh1.Status = "OFF";
        lh1.Status.Should().Be("OFF");

        // Status setter with null and empty string (covers line 29 / null & empty branches)
        lh1.Status = null!;
        lh1.Status.Should().BeNull();

        lh1.Status = string.Empty;
        lh1.Status.Should().BeEmpty();

        // Status setter with invalid value throws ArgumentException
        Action actSetInvalid = () => lh1.Status = "UNKNOWN";
        actSetInvalid.Should().Throw<ArgumentException>().WithMessage("*Unknown Status 'UNKNOWN'*");
    }

    [TestMethod]
    public void ErrorDocument_Constructors_And_Validation()
    {
        // Parameterless constructor
        var doc1 = new ErrorDocument();
        doc1.Key.Should().BeNull();

        // Parameterized constructor with valid key
        var doc2 = new ErrorDocument("error.html");
        doc2.Key.Should().Be("error.html");

        // Property setter
        doc2.Key = "custom-error.html";
        doc2.Key.Should().Be("custom-error.html");

        // Parameterized constructor with null or empty throws ArgumentNullException (covers line 43)
        Action actCtorNull = () => new ErrorDocument(null!);
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new ErrorDocument(string.Empty);
        actCtorEmpty.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void Error_ActualObjectSize_Validation_And_Branches()
    {
        var err = new Error();
        err.ActualObjectSize.Should().BeNull();
        err.ShouldSerializeActualObjectSize().Should().BeFalse();

        // Valid non-negative values (0 and positive)
        err.ActualObjectSize = 0;
        err.ActualObjectSize.Should().Be(0);
        err.ShouldSerializeActualObjectSize().Should().BeTrue();

        err.ActualObjectSize = 1048576;
        err.ActualObjectSize.Should().Be(1048576);
        err.ShouldSerializeActualObjectSize().Should().BeTrue();

        // Setting back to null (covers line 281 null branch)
        err.ActualObjectSize = null;
        err.ActualObjectSize.Should().BeNull();
        err.ShouldSerializeActualObjectSize().Should().BeFalse();

        // Negative values throw ArgumentOutOfRangeException
        Action actNegative1 = () => err.ActualObjectSize = -1;
        actNegative1.Should().Throw<ArgumentOutOfRangeException>();

        Action actNegativeLarge = () => err.ActualObjectSize = -1048576;
        actNegativeLarge.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void DeleteMultiple_Constructors_And_Validation()
    {
        // Parameterless constructor
        var dm1 = new DeleteMultiple();
        dm1.Quiet.Should().BeFalse();
        dm1.Objects.Should().NotBeNull();
        dm1.Objects.Should().BeEmpty();

        // Parameterized constructor with valid parameters
        var objects = new List<SharpS3.Models.Object>
        {
            new SharpS3.Models.Object("key1"),
            new SharpS3.Models.Object("key2", "v2")
        };
        var dm2 = new DeleteMultiple(true, objects);
        dm2.Quiet.Should().BeTrue();
        dm2.Objects.Should().BeSameAs(objects);

        // Parameterized constructor with null objects throws ArgumentNullException (covers line 51)
        Action actCtorNull = () => new DeleteMultiple(false, null!);
        actCtorNull.Should().Throw<ArgumentNullException>();

        // Property setters
        dm1.Quiet = true;
        dm1.Quiet.Should().BeTrue();
        dm1.Objects = objects;
        dm1.Objects.Should().BeSameAs(objects);
    }

    [TestMethod]
    public void Deleted_Constructors_And_Validation()
    {
        // Parameterless constructor
        var d1 = new Deleted();
        d1.Key.Should().BeNull();
        d1.VersionId.Should().BeNull();
        d1.DeleteMarker.Should().BeNull();
        d1.DeleteMarkerVersionId.Should().BeNull();

        // Parameterized constructor with all parameters
        var d2 = new Deleted("file.txt", "v123", true, "dm-v123");
        d2.Key.Should().Be("file.txt");
        d2.VersionId.Should().Be("v123");
        d2.DeleteMarker.Should().BeTrue();
        d2.DeleteMarkerVersionId.Should().Be("dm-v123");

        // Parameterized constructor with key only (defaults for optional params)
        var d3 = new Deleted("file.txt");
        d3.Key.Should().Be("file.txt");
        d3.VersionId.Should().BeNull();
        d3.DeleteMarker.Should().BeNull();
        d3.DeleteMarkerVersionId.Should().BeNull();

        // Parameterized constructor with null or empty key throws ArgumentNullException (covers line 67)
        Action actCtorNull = () => new Deleted(null!);
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new Deleted(string.Empty);
        actCtorEmpty.Should().Throw<ArgumentNullException>();

        // Property setters and helper methods
        d1.Key = "new-key";
        d1.VersionId = "v2";
        d1.DeleteMarker = false;
        d1.DeleteMarkerVersionId = "dm2";

        d1.Key.Should().Be("new-key");
        d1.VersionId.Should().Be("v2");
        d1.DeleteMarker.Should().BeFalse();
        d1.DeleteMarkerVersionId.Should().Be("dm2");

        d1.ShouldSerializeVersionId().Should().BeTrue();
        d1.ShouldSerializeDeleteMarkerVersionId().Should().BeTrue();

        d1.VersionId = null!;
        d1.DeleteMarkerVersionId = null!;
        d1.ShouldSerializeVersionId().Should().BeFalse();
        d1.ShouldSerializeDeleteMarkerVersionId().Should().BeFalse();
    }

    [TestMethod]
    public void CompleteMultipartUpload_Constructors_And_Properties()
    {
        // Parameterless constructor
        var cmu1 = new CompleteMultipartUpload();
        cmu1.Parts.Should().NotBeNull();
        cmu1.Parts.Should().BeEmpty();

        // Parameterized constructor with list
        var parts = new List<Part>
        {
            new Part { PartNumber = 1, ETag = "\"etag1\"" },
            new Part { PartNumber = 2, ETag = "\"etag2\"" }
        };
        var cmu2 = new CompleteMultipartUpload(parts);
        cmu2.Parts.Should().BeSameAs(parts);
        cmu2.Parts.Should().HaveCount(2);

        // Parameterized constructor with null defaults to empty list
        var cmu3 = new CompleteMultipartUpload(null);
        cmu3.Parts.Should().NotBeNull();
        cmu3.Parts.Should().BeEmpty();

        // Setting Parts property to a new list
        cmu2.Parts = parts;
        cmu2.Parts.Should().BeSameAs(parts);
    }

    [TestMethod]
    public void Bucket_Constructors_And_Validation()
    {
        // Parameterless constructor
        var b1 = new Bucket();
        b1.Name.Should().BeNull();
        b1.CreationDate.Kind.Should().Be(DateTimeKind.Utc);

        // Parameterized constructor with valid parameters
        var now = DateTime.UtcNow;
        var b2 = new Bucket("my-bucket", now);
        b2.Name.Should().Be("my-bucket");
        b2.CreationDate.Should().Be(now);

        // Parameterized constructor with null or empty name throws ArgumentNullException (covers line 56)
        Action actCtorNull = () => new Bucket(null!, now);
        actCtorNull.Should().Throw<ArgumentNullException>();

        Action actCtorEmpty = () => new Bucket(string.Empty, now);
        actCtorEmpty.Should().Throw<ArgumentNullException>();

        // Property setters
        b1.Name = "custom-bucket";
        b1.Name.Should().Be("custom-bucket");

        var localTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        b1.CreationDate = localTime;
        b1.CreationDate.Kind.Should().Be(DateTimeKind.Utc);
    }
}
