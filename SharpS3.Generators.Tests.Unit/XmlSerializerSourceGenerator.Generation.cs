using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpS3.Generators;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace SharpS3.Generators.Tests.Unit;

[TestClass]
public partial class XmlSerializerSourceGeneratorTests
{
    private static (Compilation OutputCompilation, IEnumerable<SyntaxTree> GeneratedTrees, IEnumerable<Diagnostic> Diagnostics) RunGenerator(
        string source,
        bool includeSharpS3Reference = true)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location) && (includeSharpS3Reference || a.FullName?.StartsWith("SharpS3,") != true))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        if (includeSharpS3Reference)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(SharpS3.ISharpS3Client).Assembly.Location));
        }

        references.Add(MetadataReference.CreateFromFile(typeof(XmlRootAttribute).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new XmlSerializerSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();
        return (outputCompilation, runResult.GeneratedTrees, diagnostics);
    }

    [TestMethod]
    public void Generator_ShouldGenerate_EncodingHelper_And_EnumHelper_And_Serializer()
    {
        string source = @"
namespace TestModels;
using System.Xml.Serialization;

[XmlRoot(ElementName = ""Sample"")]
public class Sample
{
    public string Name { get; set; } = """";
}
";
        var (_, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: false);

        diagnostics.Should().BeEmpty();
        var treeList = generatedTrees.ToList();
        treeList.Should().HaveCountGreaterThanOrEqualTo(3);

        treeList.Should().Contain(t => t.FilePath.EndsWith("S3XmlEncodingHelper.g.cs"));
        treeList.Should().Contain(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs"));
        treeList.Should().Contain(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs"));
    }

    [TestMethod]
    public void Generator_ShouldGenerate_ReadAndWrite_For_SampleModel()
    {
        string source = @"
namespace TestModels;
using System.Xml.Serialization;

[XmlRoot(ElementName = ""Sample"")]
public class Sample
{
    [XmlElement(ElementName = ""Name"")]
    public string Name { get; set; } = """";

    [XmlElement(ElementName = ""Count"")]
    public int Count { get; set; } = 0;
}
";
        var (_, generatedTrees, _) = RunGenerator(source, includeSharpS3Reference: false);
        var serializerTree = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs"));
        string text = serializerTree.ToString();

        text.Should().Contain("Write_Sample(XmlWriter writer, global::TestModels.Sample obj");
        text.Should().Contain("Read_Sample(XmlReader reader)");
        text.Should().Contain("writer.WriteElementString(\"Name\", obj.Name");
        text.Should().Contain("result.Name = reader.ReadElementContentAsString();");
        text.Should().Contain("result.Count = XmlConvert.ToInt32(reader.ReadElementContentAsString());");
    }

    [TestMethod]
    public void Generator_ShouldGenerate_Serializer_For_CustomModel()
    {
        string source = @"
namespace TestApp;
using System.Xml.Serialization;

[XmlRoot(ElementName = ""CustomBucket"")]
public class CustomBucket
{
    [XmlElement(ElementName = ""Name"")]
    public string Name { get; set; } = """";

    [XmlElement(ElementName = ""CreationDate"")]
    public System.DateTime CreationDate { get; set; }
}
";
        var (outputCompilation, generatedTrees, diagnostics) = RunGenerator(source, includeSharpS3Reference: true);

        diagnostics.Should().BeEmpty();
        var serializerTree = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlGeneratedSerializer.g.cs"));
        string text = serializerTree.ToString();

        text.Should().Contain("Write_CustomBucket");
        text.Should().Contain("Read_CustomBucket");
        text.Should().Contain("CustomBucket");

        var compileDiagnostics = outputCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        compileDiagnostics.Should().BeEmpty();
    }

    [TestMethod]
    public void Generator_EnumHelper_ShouldHandle_XmlEnum_Attributes()
    {
        string source = @"
namespace TestModels;
using System.Xml.Serialization;

public enum TestTier
{
    [XmlEnum(Name = ""StandardTier"")]
    Standard,
    [XmlEnum(Name = ""BulkTier"")]
    Bulk
}

[XmlRoot(ElementName = ""Item"")]
public class Item
{
    public TestTier Tier { get; set; }
}
";
        var (_, generatedTrees, _) = RunGenerator(source, includeSharpS3Reference: false);
        var enumTree = generatedTrees.First(t => t.FilePath.EndsWith("S3XmlEnumHelper.g.cs"));
        string text = enumTree.ToString();

        text.Should().Contain("StandardTier");
        text.Should().Contain("BulkTier");
        text.Should().Contain("Format(global::TestModels.TestTier value)");
        text.Should().Contain("ParseTestTier(string text)");
    }
}
