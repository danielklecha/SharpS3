using System;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Generators;

namespace SharpS3.Generators.Tests.Unit;

public partial class XmlSerializerSourceGeneratorTests
{
    private static Compilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        return CSharpCompilation.Create(
            "DirectTestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    [TestMethod]
    public void S3XmlEncodingHelperIsListing_ShouldTestAllBranches()
    {
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("Key").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("Prefix").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("Delimiter").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("Marker").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("NextMarker").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("StartAfter").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("KeyMarker").Should().BeTrue();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("NextKeyMarker").Should().BeTrue();

        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("Other").Should().BeFalse();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("").Should().BeFalse();
        XmlSerializerSourceGenerator.S3XmlEncodingHelperIsListing("key").Should().BeFalse();
    }

    [TestMethod]
    public void HasAttribute_ShouldHandleNullAndMemberAttributes()
    {
        string source = @"
using System;
using System.Xml.Serialization;

[XmlRoot]
public class HasClassAttr
{
    public string Name { get; set; } = """";
}

public class HasMemberAttr
{
    [XmlElement]
    public string Val { get; set; } = """";
}

public class NoAttrClass
{
    public string Text { get; set; } = """";
}
";
        var compilation = CreateCompilation(source);
        var xmlRootSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlRootAttribute");
        var xmlElementSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlElementAttribute");

        var classAttrType = compilation.GetTypeByMetadataName("HasClassAttr")!;
        var memberAttrType = compilation.GetTypeByMetadataName("HasMemberAttr")!;
        var noAttrType = compilation.GetTypeByMetadataName("NoAttrClass")!;

        // Null attribute symbol guard
        XmlSerializerSourceGenerator.HasAttribute(classAttrType, null).Should().BeFalse();

        // Class-level attribute
        XmlSerializerSourceGenerator.HasAttribute(classAttrType, xmlRootSymbol).Should().BeTrue();

        // Member-level attribute
        XmlSerializerSourceGenerator.HasAttribute(memberAttrType, xmlElementSymbol).Should().BeTrue();

        // Neither class nor member has attribute
        XmlSerializerSourceGenerator.HasAttribute(noAttrType, xmlRootSymbol).Should().BeFalse();
        XmlSerializerSourceGenerator.HasAttribute(noAttrType, xmlElementSymbol).Should().BeFalse();
    }

    [TestMethod]
    public void GetXmlEnumName_ShouldHandleAllVariants()
    {
        string source = @"
using System.Xml.Serialization;

public enum TestEnum
{
    [XmlEnum(""CtorVal"")]
    WithCtor,

    [XmlEnum(Name = ""NamedVal"")]
    WithNamed,

    [XmlEnum]
    WithEmptyAttr,

    WithoutAttr
}
";
        var compilation = CreateCompilation(source);
        var enumType = compilation.GetTypeByMetadataName("TestEnum")!;
        var members = enumType.GetMembers().OfType<IFieldSymbol>().ToDictionary(m => m.Name);

        XmlSerializerSourceGenerator.GetXmlEnumName(members["WithCtor"]).Should().Be("CtorVal");
        XmlSerializerSourceGenerator.GetXmlEnumName(members["WithNamed"]).Should().Be("NamedVal");
        XmlSerializerSourceGenerator.GetXmlEnumName(members["WithEmptyAttr"]).Should().Be("WithEmptyAttr");
        XmlSerializerSourceGenerator.GetXmlEnumName(members["WithoutAttr"]).Should().Be("WithoutAttr");
    }

    [TestMethod]
    public void GetInheritanceDepth_ShouldCalculateCorrectLevels()
    {
        string source = @"
public class BaseLevel0 { }
public class Level1 : BaseLevel0 { }
public class Level2 : Level1 { }
";
        var compilation = CreateCompilation(source);
        var level0 = compilation.GetTypeByMetadataName("BaseLevel0")!;
        var level1 = compilation.GetTypeByMetadataName("Level1")!;
        var level2 = compilation.GetTypeByMetadataName("Level2")!;

        XmlSerializerSourceGenerator.GetInheritanceDepth(level0).Should().Be(0);
        XmlSerializerSourceGenerator.GetInheritanceDepth(level1).Should().Be(1);
        XmlSerializerSourceGenerator.GetInheritanceDepth(level2).Should().Be(2);
    }

    [TestMethod]
    public void GetInheritanceDepth_ShouldHandleInterfaceOrNullBaseType()
    {
        string source = "public interface ITestInterface { }";
        var compilation = CreateCompilation(source);
        var iface = compilation.GetTypeByMetadataName("ITestInterface")!;
        XmlSerializerSourceGenerator.GetInheritanceDepth(iface).Should().Be(0);
    }
}
