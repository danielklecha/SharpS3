using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Generators;

namespace SharpS3.Generators.Tests.Unit;

public partial class XmlSerializerSourceGeneratorTests
{
    

    [TestMethod]
    public void Pipeline_ShouldHandleTypeFiltering_Exclusions_And_Constructors()
    {
        string source = @"
using System.Xml.Serialization;

namespace SharpS3.Models
{
    // Excluded by name
    [XmlRoot] public class FileSystemStorageBackend { }
    [XmlRoot] public class S3ObjectResponse { }
    [XmlRoot] public class S3Request { }

    // Enums in SharpS3.Models are automatically included
    public enum AutoModelEnum { ItemA, ItemB }

    // Abstract / Static / Special types
    [XmlRoot] public abstract class AbstractClass { }
    public static class StaticClass { }

    // No default constructor
    [XmlRoot]
    public class NoDefaultCtorClass
    {
        public NoDefaultCtorClass(int x) { }
    }

    // Internal default constructor
    [XmlRoot]
    public class InternalCtorClass
    {
        internal InternalCtorClass() { }
    }

    // Has [XmlType]
    [XmlType]
    public class XmlTypeClass
    {
        public string Text { get; set; } = """";
    }

    // Non-class types
    public struct MyStruct { }
    public interface IMyInterface { }
    public record struct MyRecordStruct(int X);
}

namespace OtherNamespace
{
    using System.Xml.Serialization;

    // Has [XmlEnum] on enum type
    [XmlEnum]
    public enum DecoratedEnum { X, Y }

    // No [XmlEnum] in non-SharpS3 namespace -> skipped
    public enum UnusedEnum { A, B }
}

public enum GlobalEnum { One, Two }
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        var enumHelper = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs")).ToString();

        // XmlTypeClass is included
        serializer.Should().Contain("XmlTypeClass");
        // Excluded types are not generated
        serializer.Should().NotContain("Write_FileSystemStorageBackend");
        serializer.Should().NotContain("Write_S3ObjectResponse");
        serializer.Should().NotContain("Write_S3Request");
        serializer.Should().NotContain("Write_AbstractClass");
        serializer.Should().NotContain("Write_NoDefaultCtorClass");
        serializer.Should().NotContain("Write_InternalCtorClass");

        // Enums
        enumHelper.Should().Contain("AutoModelEnum");
        enumHelper.Should().Contain("DecoratedEnum");
        enumHelper.Should().NotContain("UnusedEnum");
    }

    [TestMethod]
    public void Pipeline_ShouldHandleXmlInclude_And_QueueDiscovery()
    {
        string source = @"
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace TestQueue
{
    public enum QueueEnum { First, Second }

    public class ValidIncluded
    {
        public string Name { get; set; } = """";
    }

    public abstract class AbstractIncluded { }
    public class NoCtorIncluded { public NoCtorIncluded(int a) { } }

    public class QueueNestedClass
    {
        public int Val { get; set; }
    }

    public class CollectionItem
    {
        public string Title { get; set; } = "";
    }

    public class CustomEnumerable : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator() => Enumerable.Empty<string>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [XmlRoot]
    [XmlInclude(typeof(ValidIncluded))]
    [XmlInclude(typeof(AbstractIncluded))]
    [XmlInclude(typeof(int))]
    [XmlInclude(typeof(NoCtorIncluded))]
    public class QueueRootModel
    {
        [XmlIgnore]
        public string IgnoredProp { get; set; } = "";

        public QueueNestedClass? Nested { get; set; }

        public List<CollectionItem> Items { get; set; } = new();

        public IEnumerable<CollectionItem> DirectEnumerable { get; set; } = new List<CollectionItem>();

        public CustomEnumerable CustomColl { get; set; } = new();

        public NoCtorIncluded? NoCtorProp { get; set; }

        public List<string> StringList { get; set; } = new();

        public List<AbstractIncluded> AbstractItems { get; set; } = new();

        public List<NoCtorIncluded> NoCtorItems { get; set; } = new();

        public QueueEnum EnumProp { get; set; }

        public QueueEnum? NullableEnumProp { get; set; }
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        var enumHelper = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs")).ToString();

        serializer.Should().Contain("Write_ValidIncluded");
        serializer.Should().Contain("Write_QueueNestedClass");
        serializer.Should().Contain("Write_CollectionItem");
        serializer.Should().NotContain("IgnoredProp");

        enumHelper.Should().Contain("QueueEnum");
    }

    [TestMethod]
    public void SpecialSerializers_LocationConstraint_ShouldGenerateCorrectly()
    {
        string source = @"
using System.Xml.Serialization;

namespace SharpS3.Models
{
    [XmlRoot(""LocationConstraint"")]
    public class LocationConstraint
    {
        public string Text { get; set; } = """";
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        serializer.Should().Contain("Write_LocationConstraint(XmlWriter writer, global::SharpS3.Models.LocationConstraint obj");
        serializer.Should().Contain("Read_LocationConstraint(XmlReader reader)");
        serializer.Should().Contain("writer.WriteString(obj.Text);");
        serializer.Should().Contain("result.Text = reader.ReadElementContentAsString();");
    }

    [TestMethod]
    public void SpecialSerializers_ListVersionsResult_ShouldGenerateCorrectly()
    {
        string source = @"
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SharpS3.Models
{
    public class ObjectVersion
    {
        public string Key { get; set; } = """";
    }

    public class DeleteMarker
    {
        public string Key { get; set; } = """";
    }

    public class CommonPrefixes
    {
        public string Prefix { get; set; } = """";
    }

    [XmlRoot(""ListVersionsResult"")]
    public class ListVersionsResult
    {
        public string? Name { get; set; }
        public string? Prefix { get; set; }
        public string? KeyMarker { get; set; }
        public string? VersionIdMarker { get; set; }
        public string? NextKeyMarker { get; set; }
        public string? NextVersionIdMarker { get; set; }
        public int MaxKeys { get; set; }
        public string? Delimiter { get; set; }
        public string? EncodingType { get; set; }
        public bool IsTruncated { get; set; }

        public List<object> Entries { get; set; } = new();
        public List<ObjectVersion> Versions { get; set; } = new();
        public List<DeleteMarker> DeleteMarkers { get; set; } = new();
        public List<CommonPrefixes> CommonPrefixes { get; set; } = new();

        public bool ShouldSerializeNextKeyMarker() => true;
        public bool ShouldSerializeNextVersionIdMarker() => true;
        public bool ShouldSerializeDelimiter() => true;
        public bool ShouldSerializeEncodingType() => true;
        public bool ShouldSerializeVersions() => true;
        public bool ShouldSerializeDeleteMarkers() => true;
        public bool ShouldSerializeCommonPrefixes() => true;
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        serializer.Should().Contain("Write_ListVersionsResult");
        serializer.Should().Contain("Read_ListVersionsResult");
        serializer.Should().Contain("Write_ObjectVersion(writer, ov, urlEncodeListingValues, \"Version\", string.Empty)");
        serializer.Should().Contain("Write_DeleteMarker(writer, dm, urlEncodeListingValues, \"DeleteMarker\", string.Empty)");
        serializer.Should().Contain("case \"Version\":");
        serializer.Should().Contain("case \"DeleteMarker\":");
        serializer.Should().Contain("case \"CommonPrefixes\":");
    }

    [TestMethod]
    public void SpecialSerializers_DeleteResult_ShouldGenerateCorrectly()
    {
        string source = @"
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SharpS3.Models
{
    public enum ErrorCode { NoSuchKey, InternalError }

    public class Deleted
    {
        public string Key { get; set; } = """";
    }

    public class Error
    {
        public string? Key { get; set; }
        public string? VersionId { get; set; }
        public ErrorCode Code { get; set; }
        public string? Message { get; set; }
    }

    [XmlRoot(""DeleteResult"")]
    public class DeleteResult
    {
        public List<Deleted> DeletedObjects { get; set; } = new();
        public List<Error> Errors { get; set; } = new();

        public bool ShouldSerializeDeletedObjects() => true;
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        serializer.Should().Contain("Write_DeleteResult");
        serializer.Should().Contain("Read_DeleteResult");
        serializer.Should().Contain("Write_Deleted(writer, item, urlEncodeListingValues, \"Deleted\", string.Empty)");
        serializer.Should().Contain("writer.WriteStartElement(\"Error\");");
        serializer.Should().Contain("case \"Deleted\":");
        serializer.Should().Contain("case \"Error\":");
        serializer.Should().Contain("S3XmlEnumHelper.ParseErrorCode");
    }

    [TestMethod]
    public void SpecialSerializers_FileMetadata_And_FileSystemStorageBackend_ShouldGenerateCorrectly()
    {
        string source = @"
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SharpS3.Storage
{
    public class Tagging { }
    public class LegalHold { }
    public class Retention { }

    public class FileSystemStorageBackend
    {
        public class MetadataEntry
        {
            public string? Key { get; set; }
            public string? Value { get; set; }
        }

        [XmlRoot(""FileMetadata"")]
        public class FileMetadata
        {
            public string? ContentType { get; set; }
            public string? ETag { get; set; }
            public DateTime LastModified { get; set; }
            public FileSystemStorageBackend.MetadataEntry[]? UserMetadataEntries { get; set; }
            public Tagging? Tags { get; set; }
            public LegalHold? LegalHold { get; set; }
            public Retention? Retention { get; set; }
        }
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        serializer.Should().Contain("Write_FileSystemStorageBackend_FileMetadata");
        serializer.Should().Contain("Read_FileSystemStorageBackend_FileMetadata");
        serializer.Should().Contain("writer.WriteStartElement(\"UserMetadata\");");
        serializer.Should().Contain("case \"UserMetadata\":");
        serializer.Should().Contain("case \"Tagging\":");
        serializer.Should().Contain("case \"LegalHold\":");
        serializer.Should().Contain("case \"Retention\":");
    }

    [TestMethod]
    public void SpecialSerializers_Grantee_Polymorphism_ShouldGenerateCorrectly()
    {
        string source = @"
using System.Xml.Serialization;

namespace SharpS3.Models
{
    public class CanonicalUser : Grantee { }
    public class Group : Grantee { }
    public class AmazonCustomerByEmail : Grantee { }

    [XmlRoot(""Grantee"")]
    public class Grantee
    {
        public string GranteeType { get; set; } = ""CanonicalUser"";
        public string? ID { get; set; }
        public string? DisplayName { get; set; }
        public string? URI { get; set; }
        public string? EmailAddress { get; set; }

        public bool ShouldSerializeID() => true;
        public bool ShouldSerializeDisplayName() => true;
        public bool ShouldSerializeURI() => true;
        public bool ShouldSerializeEmailAddress() => true;
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        serializer.Should().Contain("Write_Grantee");
        serializer.Should().Contain("Read_Grantee");
        serializer.Should().Contain("writer.WriteAttributeString(\"xsi\", \"type\"");
        serializer.Should().Contain("CanonicalUser");
        serializer.Should().Contain("Group");
        serializer.Should().Contain("AmazonCustomerByEmail");
    }

    [TestMethod]
    public void GeneralSerializer_AllPropertyTypes_Attributes_And_ShouldSerialize()
    {
        string source = @"
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace SharpS3.Models
{
    public enum StatusEnum
    {
        [XmlEnum(""ACTIVE"")]
        Active,
        [XmlEnum(Name = ""INACTIVE"")]
        Inactive,
        [XmlEnum]
        Pending,
        None
    }

    public class ChildItem
    {
        public string Name { get; set; } = """";
    }

    public class BaseClass
    {
        public bool ShouldSerializeConditionalProp() => true;
    }

    [XmlRoot(""CustomRoot"", Namespace = ""http://example.com/s3"")]
    public class ComprehensiveModel : BaseClass
    {
        [XmlElement(""ConditionalProp"")]
        public string? ConditionalProp { get; set; }
        // String with listing tag (tested for UrlEncode)
        [XmlElement(""Key"")]
        public string? Key { get; set; }

        [XmlElement(""Prefix"")]
        public string? Prefix { get; set; }

        [XmlElement(ElementName = ""NamedString"")]
        public string? NamedString { get; set; }

        // DateTime & Nullable DateTime
        public DateTime CreatedAt { get; set; }
        public DateTime? ModifiedAt { get; set; }

        // Boolean & Nullable Boolean
        public bool IsEnabled { get; set; }
        public bool? IsFlagged { get; set; }

        // Int32 & Nullable Int32
        public int Count { get; set; }
        public int? OptionalCount { get; set; }

        // Int64 & Nullable Int64
        public long Size { get; set; }
        public long? OptionalSize { get; set; }

        // Enum & Nullable Enum
        public StatusEnum Status { get; set; }
        public StatusEnum? OptionalStatus { get; set; }

        // Collections
        public List<string> StringList { get; set; } = new();
        public List<ChildItem> ChildList { get; set; } = new();

        // Nested Complex Class
        public ChildItem? SingleChild { get; set; }
    }

    // Special namespace root models
    [XmlRoot] public class BucketLoggingStatus { }
    [XmlRoot] public class BucketMetadata { }

    // Nested class in container
    public class OuterContainer
    {
        [XmlRoot]
        public class InnerNested
        {
            public string Val { get; set; } = """";
        }
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();

        // All property writes & reads
        serializer.Should().Contain("S3XmlEncodingHelper.UrlEncode(obj.Key)");
        serializer.Should().Contain("S3XmlEncodingHelper.UrlEncode(obj.Prefix)");
        serializer.Should().Contain("writer.WriteElementString(\"NamedString\", obj.NamedString ?? string.Empty)");
        serializer.Should().Contain("S3XmlEncodingHelper.FormatDateTime(obj.CreatedAt)");
        serializer.Should().Contain("obj.ModifiedAt.HasValue");
        serializer.Should().Contain("obj.IsEnabled ? \"true\" : \"false\"");
        serializer.Should().Contain("obj.IsFlagged.HasValue");
        serializer.Should().Contain("XmlConvert.ToString(obj.Count)");
        serializer.Should().Contain("obj.OptionalCount.HasValue");
        serializer.Should().Contain("XmlConvert.ToString(obj.Size)");
        serializer.Should().Contain("obj.OptionalSize.HasValue");
        serializer.Should().Contain("S3XmlEnumHelper.Format(obj.Status)");
        serializer.Should().Contain("obj.OptionalStatus.HasValue");
        serializer.Should().Contain("Write_ChildItem(writer, item, urlEncodeListingValues");
        serializer.Should().Contain("Write_ChildItem(writer, obj.SingleChild, urlEncodeListingValues");

        // ShouldSerialize on base class
        serializer.Should().Contain("if (obj.ShouldSerializeConditionalProp())");

        // Special namespaces
        serializer.Should().Contain("http://doc.s3.amazonaws.com/2006-03-01"); // BucketLoggingStatus

        // Nested class
        serializer.Should().Contain("Write_OuterContainer_InnerNested");
        serializer.Should().Contain("Read_OuterContainer_InnerNested");
    }

    [TestMethod]
    public void CentralRegistryDispatch_InheritanceSorting_And_DispatchMethods()
    {
        string source = @"
using System.Xml.Serialization;

namespace TestInheritance
{
    [XmlRoot] public class RootTier { }
    [XmlRoot] public class MiddleTier : RootTier { }
    [XmlRoot] public class LeafTier : MiddleTier { }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();

        // Central dispatch methods
        serializer.Should().Contain("public static void Serialize<T>(XmlWriter writer, T obj");
        serializer.Should().Contain("public static void SerializeObject(XmlWriter writer, object obj");
        serializer.Should().Contain("public static T Deserialize<T>(XmlReader reader)");
        serializer.Should().Contain("public static object Deserialize(XmlReader reader, Type type)");
        serializer.Should().Contain("public static bool IsSupported(Type type)");

        // Inheritance ordering: LeafTier (depth 2) before MiddleTier (depth 1) before RootTier (depth 0)
        int leafPos = serializer.IndexOf("LeafTier val: Write_LeafTier");
        int middlePos = serializer.IndexOf("MiddleTier val: Write_MiddleTier");
        int rootPos = serializer.IndexOf("RootTier val: Write_RootTier");

        leafPos.Should().BeLessThan(middlePos);
        middlePos.Should().BeLessThan(rootPos);
    }

    [TestMethod]
    public void SyntaxErrorOrMalformedCode_ShouldNotCrashGenerator()
    {
        string source = "public class { }";
        var (_, generatedTrees, _) = RunGenerator(source, includeSharpS3Reference: false);
        generatedTrees.Should().NotBeNull();
    }

    [TestMethod]
    public void AdvancedBranches_Coverage_AllEdgeCases()
    {
        string source = @"
using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace CustomAttributes
{
    [AttributeUsage(AttributeTargets.Field)]
    public class XmlEnumAttribute : Attribute
    {
        public int Other { get; set; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public class XmlIncludeAttribute : Attribute
    {
        public XmlIncludeAttribute() { }
        public XmlIncludeAttribute(string text) { }
    }
}

namespace SharpS3.Models
{
    using CustomAttributes;

    // Enum with custom XmlEnum having non-Name named argument
    public enum EnumWithOtherAttribute
    {
        [CustomAttributes.XmlEnum(Other = 999)]
        ItemOther,
        NormalItem
    }

    // Empty enum for default(T) fallback branch
    public enum EmptyEnum { }

    public class ContainerClass
    {
        public class NestedClass
        {
            public string Val { get; set; } = """";
        }
    }

    public class ReusedItem
    {
        public string Data { get; set; } = """";
    }

    [XmlRoot]
    [CustomAttributes.XmlInclude]
    [CustomAttributes.XmlInclude(""not_a_type"")]
    public class ModelWithComplexBranches
    {
        // Property with XmlElement that has named argument other than ElementName
        [System.Xml.Serialization.XmlElement(Namespace = ""urn:custom:ns"")]
        public string PropWithNamespaceOnly { get; set; } = """";

        // Filtered out properties: static, private, read-only non-collection, private setter, indexer
        public static string StaticProp { get; set; } = "";
        private string PrivateProp { get; set; } = "";
        public int ReadOnlyInt => 42;
        public int PrivateSetInt { get; private set; } = 42;
        public int[] PrivateSetArray { get; private set; } = Array.Empty<int>();
        public byte[]? RawData { get; set; }
        public string this[int index] { get => ""; set { } }

        // Read-only collection (included)
        public List<string> ReadOnlyList { get; } = new();

        // ContainingType in collection item and single property
        public List<ContainerClass.NestedClass> NestedCollection { get; set; } = new();
        public ContainerClass.NestedClass? SingleNested { get; set; }

        // Duplicate complex property to hit already-added branch in queue
        public ReusedItem? ItemA { get; set; }
        public ReusedItem? ItemB { get; set; }

        // Enum with other attr and empty enum
        public EnumWithOtherAttribute OtherEnumProp { get; set; }
        public EmptyEnum? EmptyEnumProp { get; set; }
    }

    // Class with [XmlType] and NO [XmlRoot]
    [System.Xml.Serialization.XmlType]
    public class ClassWithXmlTypeOnly
    {
        public string Text { get; set; } = """";
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var serializer = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs")).ToString();
        var enumHelper = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs")).ToString();

        serializer.Should().Contain("Write_ContainerClass_NestedClass");
        serializer.Should().Contain("Write_ClassWithXmlTypeOnly");
        serializer.Should().NotContain("StaticProp");
        serializer.Should().NotContain("PrivateProp");
        serializer.Should().NotContain("ReadOnlyInt");
        serializer.Should().NotContain("PrivateSetInt");

        enumHelper.Should().Contain("ItemOther");
        enumHelper.Should().Contain("default(global::SharpS3.Models.EmptyEnum)");
    }

    [TestMethod]
    public void Generator_ShouldHandleMetadataEnumWithNonConstantFields()
    {
        string source = @"
using System;
using System.Xml.Serialization;

namespace SharpS3.Models
{
    [XmlRoot]
    public class ClassWithMetadataEnum
    {
        public DayOfWeek Day { get; set; }
    }
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);
        diagnostics.Should().BeEmpty();

        var enumHelper = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs")).ToString();
        enumHelper.Should().Contain("Format(global::System.DayOfWeek value)");
        enumHelper.Should().Contain("ParseDayOfWeek(string text)");
        enumHelper.Should().Contain("global::System.DayOfWeek.Monday => \"Monday\"");
        enumHelper.Should().NotContain("value__");
    }

    [TestMethod]
    public void Generator_ShouldHandleEnumWithInvalidOrNullConstantValue()
    {
        string source = @"
namespace SharpS3.Models
{
    public enum BrokenEnum
    {
        ValidMember = 1,
        InvalidMember = ""not an int""
    }
}
";
        var (_, generatedTrees, _) = RunGenerator(source, includeSharpS3Reference: false);
        var enumHelper = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs")).ToString();
        enumHelper.Should().Contain("ValidMember => \"ValidMember\"");
        enumHelper.Should().NotContain("InvalidMember");
    }
}
