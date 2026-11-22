using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SharpS3.Generators;

/// <summary>
/// Roslyn source generator that generates optimized XML serializers and deserializers for SharpS3 model classes.
/// </summary>
[Generator]
public class XmlSerializerSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => s is BaseTypeDeclarationSyntax,
                transform: static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol((BaseTypeDeclarationSyntax)ctx.Node) as INamedTypeSymbol)
            .Where(static s => s != null)
            .Select(static (s, _) => s!);

        var compilationAndTypes = context.CompilationProvider.Combine(typeDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndTypes, static (spc, source) =>
        {
            Execute(source.Left, source.Right, spc);
        });
    }

    internal static void Execute(Compilation compilation, ImmutableArray<INamedTypeSymbol> types, SourceProductionContext context)
    {
        var xmlRootAttrSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlRootAttribute");
        var xmlElementAttrSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlElementAttribute");
        var xmlEnumAttrSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlEnumAttribute");
        var xmlTypeAttrSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlTypeAttribute");
        var xmlIncludeAttrSymbol = compilation.GetTypeByMetadataName("System.Xml.Serialization.XmlIncludeAttribute");

        var modelsToGenerate = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var enumsToGenerate = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var type in types)
        {
            if (type.TypeKind == TypeKind.Enum)
            {
                var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
                if (ns == "SharpS3.Models" || HasAttribute(type, xmlEnumAttrSymbol))
                {
                    enumsToGenerate.Add(type);
                }
            }
            else if (type.TypeKind == TypeKind.Class && !type.IsAbstract && !type.IsStatic && type.SpecialType == SpecialType.None)
            {
                if (type.Name == "FileSystemStorageBackend" || type.Name == "S3ObjectResponse" || type.Name == "S3Request")
                    continue;

                bool hasXmlAttr = HasAttribute(type, xmlRootAttrSymbol) || HasAttribute(type, xmlTypeAttrSymbol);
                bool hasDefaultCtor = type.Constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

                if (hasXmlAttr && hasDefaultCtor)
                {
                    modelsToGenerate.Add(type);
                }
            }
        }

        // Also discover any referenced model types (e.g. child classes, XmlInclude)
        var queue = new Queue<INamedTypeSymbol>(modelsToGenerate);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Check XmlInclude attributes on current class
            foreach (var attr in current.GetAttributes().Where(a => a.AttributeClass!.Name == "XmlIncludeAttribute"))
            {
                if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is INamedTypeSymbol incType)
                {
                    if (incType.TypeKind == TypeKind.Class && !incType.IsAbstract && incType.SpecialType == SpecialType.None
                        && incType.Constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                        && modelsToGenerate.Add(incType))
                    {
                        queue.Enqueue(incType);
                    }
                }
            }

            foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (member.GetAttributes().Any(a => a.AttributeClass!.Name == "XmlIgnoreAttribute")) continue;

                var propType = member.Type;
                if (propType is INamedTypeSymbol namedProp)
                {
                    if (namedProp.IsGenericType && (namedProp.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T
                        || namedProp.AllInterfaces.Any(i => i.IsGenericType && i.ConstructedFrom.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)))
                    {
                        var itemType = namedProp.TypeArguments.FirstOrDefault() as INamedTypeSymbol;
                        if (itemType != null && itemType.TypeKind == TypeKind.Class && !itemType.IsAbstract && itemType.SpecialType == SpecialType.None
                            && itemType.Constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                            && modelsToGenerate.Add(itemType))
                        {
                            queue.Enqueue(itemType);
                        }
                    }
                    else if (namedProp.TypeKind == TypeKind.Class && !namedProp.IsAbstract && namedProp.SpecialType == SpecialType.None
                        && namedProp.Constructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public)
                        && modelsToGenerate.Add(namedProp))
                    {
                        queue.Enqueue(namedProp);
                    }
                    else if (namedProp.TypeKind == TypeKind.Enum)
                    {
                        enumsToGenerate.Add(namedProp);
                    }
                    else if (namedProp.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && namedProp.TypeArguments.Length > 0 && namedProp.TypeArguments[0].TypeKind == TypeKind.Enum)
                    {
                        enumsToGenerate.Add((INamedTypeSymbol)namedProp.TypeArguments[0]);
                    }
                }
            }
        }

        // Generate Helpers
        GenerateHelpers(context);

        // Generate Enum Helpers
        GenerateEnumParsers(context, enumsToGenerate);

        // Generate Model Serializers
        GenerateModelSerializers(context, modelsToGenerate, compilation);
    }

    internal static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol? attrSymbol)
    {
        if (attrSymbol == null) return false;
        return type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrSymbol))
            || type.GetMembers().Any(m => m.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attrSymbol)));
    }

    private static void GenerateHelpers(SourceProductionContext context)
    {
        var code = @"// <auto-generated />
#nullable enable
#pragma warning disable
namespace SharpS3.Serialization;

using System;
using System.Globalization;
using System.Text;
using System.Xml;

[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
[global::System.CodeDom.Compiler.GeneratedCode(""SharpS3.Generators"", ""1.0.0"")]
internal static class S3XmlEncodingHelper
{
    private static readonly System.Collections.Generic.HashSet<string> _UrlEncodedElements = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    {
        ""Key"",
        ""Prefix"",
        ""Delimiter"",
        ""Marker"",
        ""NextMarker"",
        ""StartAfter"",
        ""KeyMarker"",
        ""NextKeyMarker""
    };

    public static bool IsListingElement(string elementName) => _UrlEncodedElements.Contains(elementName);

    public static string UrlEncode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        var sb = new StringBuilder(value.Length * 2);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c == '-' || c == '_' || c == '.' || c == '*' || c == '/')
            {
                sb.Append(c);
            }
            else if (c == ' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString(""X2"", CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }

    public static string FormatDateTime(DateTime dt)
    {
        DateTime utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
        return utc.ToString(""yyyy-MM-dd'T'HH:mm:ss.fff'Z'"", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseDateTime(string text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            return parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
        }
        return DateTime.UtcNow;
    }
}
";
        context.AddSource("S3XmlEncodingHelper.g.cs", SourceText.From(code, Encoding.UTF8));
    }

    private static void GenerateEnumParsers(SourceProductionContext context, HashSet<INamedTypeSymbol> enums)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"// <auto-generated />
#nullable enable
#pragma warning disable
namespace SharpS3.Serialization;

using System;
using SharpS3.Models;

[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
[global::System.CodeDom.Compiler.GeneratedCode(""SharpS3.Generators"", ""1.0.0"")]
internal static class S3XmlEnumHelper
{");

        foreach (var enumType in enums.OrderBy(e => e.ToDisplayString()))
        {
            var typeName = enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var simpleName = enumType.Name;
            var members = enumType.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(m => m.ConstantValue != null)
                .ToList();

            sb.AppendLine($"    public static string Format({typeName} value) => value switch");
            sb.AppendLine("    {");
            foreach (var member in members)
            {
                string xmlName = GetXmlEnumName(member);

                sb.AppendLine($"        {typeName}.{member.Name} => \"{xmlName}\",");
            }
            sb.AppendLine($"        _ => value.ToString()");
            sb.AppendLine("    };");
            sb.AppendLine();

            sb.AppendLine($"    public static {typeName} Parse{simpleName}(string text) => text switch");
            sb.AppendLine("    {");
            foreach (var member in members)
            {
                string xmlName = GetXmlEnumName(member);

                sb.AppendLine($"        string s when string.Equals(s, \"{xmlName}\", StringComparison.OrdinalIgnoreCase) => {typeName}.{member.Name},");
                if (xmlName != member.Name)
                {
                    sb.AppendLine($"        string s when string.Equals(s, \"{member.Name}\", StringComparison.OrdinalIgnoreCase) => {typeName}.{member.Name},");
                }
            }
            var defaultMember = members.FirstOrDefault();
            var defaultExpr = defaultMember != null ? $"{typeName}.{defaultMember.Name}" : $"default({typeName})";
            sb.AppendLine($"        _ => {defaultExpr}");
            sb.AppendLine("    };");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        context.AddSource("S3XmlEnumHelper.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    internal static string GetXmlEnumName(IFieldSymbol member)
    {
        var xmlEnumAttr = member.GetAttributes().FirstOrDefault(a => a.AttributeClass!.Name == "XmlEnumAttribute");
        if (xmlEnumAttr != null)
        {
            if (xmlEnumAttr.ConstructorArguments.Length > 0 && xmlEnumAttr.ConstructorArguments[0].Value is string ctorVal)
            {
                return ctorVal;
            }
            foreach (var named in xmlEnumAttr.NamedArguments)
            {
                if (named.Key == "Name" && named.Value.Value is string namedVal)
                {
                    return namedVal;
                }
            }
        }
        return member.Name;
    }

    private static void GenerateModelSerializers(SourceProductionContext context, HashSet<INamedTypeSymbol> models, Compilation compilation)
    {
        var serializerSb = new StringBuilder();
        serializerSb.AppendLine(@"// <auto-generated />
#nullable enable
#pragma warning disable
namespace SharpS3.Serialization;

using System;
using System.IO;
using System.Text;
using System.Xml;
using SharpS3.Models;
using SharpS3.Storage;

[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
[global::System.CodeDom.Compiler.GeneratedCode(""SharpS3.Generators"", ""1.0.0"")]
internal static partial class S3XmlGeneratedSerializer
{");

        // First generate individual Read/Write methods for each model
        foreach (var model in models.OrderBy(m => m.ToDisplayString()))
        {
            GenerateModelMethods(serializerSb, model);
        }

        // Now generate central Serialize<T> / Deserialize<T> dispatch methods
        GenerateRegistryDispatch(serializerSb, models);

        serializerSb.AppendLine("}");
        context.AddSource("S3XmlGeneratedSerializer.g.cs", SourceText.From(serializerSb.ToString(), Encoding.UTF8));
    }

    private static void GenerateModelMethods(StringBuilder sb, INamedTypeSymbol model)
    {
        var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var methodName = model.Name;
        if (model.ContainingType != null)
        {
            methodName = model.ContainingType.Name + "_" + model.Name;
        }

        var xmlRootAttr = model.GetAttributes().FirstOrDefault(a => a.AttributeClass!.Name == "XmlRootAttribute");
        string defaultRootName = model.Name;
        string defaultNamespace = "http://s3.amazonaws.com/doc/2006-03-01/";

        if (model.Name == "Error") defaultNamespace = "";
        else if (model.Name == "BucketLoggingStatus") defaultNamespace = "http://doc.s3.amazonaws.com/2006-03-01";
        else if (model.Name == "BucketMetadata" || model.Name == "FileMetadata") defaultNamespace = "";

        if (xmlRootAttr != null)
        {
            foreach (var named in xmlRootAttr.NamedArguments)
            {
                if (named.Key == "ElementName" && named.Value.Value is string elemName) defaultRootName = elemName;
                if (named.Key == "Namespace" && named.Value.Value is string ns) defaultNamespace = ns;
            }
            if (xmlRootAttr.ConstructorArguments.Length > 0 && xmlRootAttr.ConstructorArguments[0].Value is string consElemName)
            {
                defaultRootName = consElemName;
            }
        }

        // Special class handlers
        if (model.Name == "LocationConstraint")
        {
            GenerateLocationConstraintMethods(sb, typeName, methodName, defaultRootName, defaultNamespace);
            return;
        }
        if (model.Name == "ListVersionsResult")
        {
            GenerateListVersionsResultMethods(sb, typeName, methodName, defaultRootName, defaultNamespace);
            return;
        }
        if (model.Name == "DeleteResult")
        {
            GenerateDeleteResultMethods(sb, typeName, methodName, defaultRootName, defaultNamespace);
            return;
        }
        if (model.Name == "FileMetadata")
        {
            GenerateFileMetadataMethods(sb, typeName, methodName, defaultRootName, defaultNamespace);
            return;
        }
        if (model.Name == "Grantee")
        {
            GenerateGranteeMethods(sb, typeName, methodName, defaultRootName, defaultNamespace);
            return;
        }

        // General class serializer
        var properties = model.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p.DeclaredAccessibility == Accessibility.Public && !p.IsStatic && !p.IsIndexer)
            .Where(p => !p.GetAttributes().Any(a => a.AttributeClass!.Name == "XmlIgnoreAttribute"))
            .Where(p => (p.SetMethod != null && p.SetMethod.DeclaredAccessibility == Accessibility.Public) ||
                        (p.Type is INamedTypeSymbol n && (n.Name == "List" || n.AllInterfaces.Any(i => i.Name == "IEnumerable"))))
            .ToList();

        // Check if there are ShouldSerialize methods on the class or base types
        var methods = new List<IMethodSymbol>();
        var curr = model;
        while (curr!.SpecialType != SpecialType.System_Object)
        {
            methods.AddRange(curr.GetMembers().OfType<IMethodSymbol>());
            curr = curr.BaseType;
        }

        // Write method
        sb.AppendLine($"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string tag = elementName ?? \"{defaultRootName}\";");
        sb.AppendLine($"        string ns = elementNamespace ?? \"{defaultNamespace}\";");
        sb.AppendLine("        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);");
        sb.AppendLine("        else writer.WriteStartElement(tag, ns);");

        foreach (var prop in properties)
        {
            GeneratePropertyWrite(sb, model, prop, methods);
        }

        sb.AppendLine("        writer.WriteEndElement();");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Read method
        sb.AppendLine($"    public static {typeName} Read_{methodName}(XmlReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var result = new {typeName}();");
        sb.AppendLine("        if (reader.IsEmptyElement) { reader.Read(); return result; }");
        sb.AppendLine("        reader.ReadStartElement();");
        sb.AppendLine("        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (reader.LocalName)");
        sb.AppendLine("                {");

        foreach (var prop in properties)
        {
            GeneratePropertyRead(sb, model, prop);
        }

        sb.AppendLine("                    default:");
        sb.AppendLine("                        reader.Skip();");
        sb.AppendLine("                        break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GeneratePropertyWrite(StringBuilder sb, INamedTypeSymbol model, IPropertySymbol prop, List<IMethodSymbol> methods)
    {
        var propName = prop.Name;
        var xmlElementAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass!.Name == "XmlElementAttribute");
        string tag = propName;
        if (xmlElementAttr != null)
        {
            foreach (var named in xmlElementAttr.NamedArguments)
            {
                if (named.Key == "ElementName" && named.Value.Value is string elemName) tag = elemName;
            }
            if (xmlElementAttr.ConstructorArguments.Length > 0 && xmlElementAttr.ConstructorArguments[0].Value is string consElemName)
            {
                tag = consElemName;
            }
        }

        var shouldSerializeMethod = methods.FirstOrDefault(m => m.Name == "ShouldSerialize" + propName && m.Parameters.Length == 0);
        string conditionPrefix = "";
        string conditionSuffix = "";
        if (shouldSerializeMethod != null)
        {
            conditionPrefix = $"if (obj.ShouldSerialize{propName}()) {{ ";
            conditionSuffix = " }";
        }

        var propType = prop.Type;

        // String property
        if (propType.SpecialType == SpecialType.System_String)
        {
            if (shouldSerializeMethod == null)
            {
                conditionPrefix = $"if (obj.{propName} != null) {{ ";
                conditionSuffix = " }";
            }
            sb.Append("        " + conditionPrefix);
            if (S3XmlEncodingHelperIsListing(tag))
            {
                sb.Append($"string val_{propName} = (urlEncodeListingValues && obj.{propName} != null) ? S3XmlEncodingHelper.UrlEncode(obj.{propName}) : (obj.{propName} ?? string.Empty); ");
                sb.Append($"writer.WriteElementString(\"{tag}\", val_{propName});");
            }
            else
            {
                sb.Append($"writer.WriteElementString(\"{tag}\", obj.{propName} ?? string.Empty);");
            }
            sb.AppendLine(conditionSuffix);
        }
        // DateTime property
        else if (propType.SpecialType == SpecialType.System_DateTime)
        {
            sb.AppendLine($"        {conditionPrefix}writer.WriteElementString(\"{tag}\", S3XmlEncodingHelper.FormatDateTime(obj.{propName}));{conditionSuffix}");
        }
        else if (propType is INamedTypeSymbol nNullable && nNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && nNullable.TypeArguments[0].SpecialType == SpecialType.System_DateTime)
        {
            sb.AppendLine($"        if (obj.{propName}.HasValue) {{ writer.WriteElementString(\"{tag}\", S3XmlEncodingHelper.FormatDateTime(obj.{propName}.Value)); }}");
        }
        // Boolean property
        else if (propType.SpecialType == SpecialType.System_Boolean)
        {
            sb.AppendLine($"        {conditionPrefix}writer.WriteElementString(\"{tag}\", obj.{propName} ? \"true\" : \"false\");{conditionSuffix}");
        }
        else if (propType is INamedTypeSymbol bNullable && bNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && bNullable.TypeArguments[0].SpecialType == SpecialType.System_Boolean)
        {
            sb.AppendLine($"        if (obj.{propName}.HasValue) {{ writer.WriteElementString(\"{tag}\", obj.{propName}.Value ? \"true\" : \"false\"); }}");
        }
        // Int32 property
        else if (propType.SpecialType == SpecialType.System_Int32)
        {
            sb.AppendLine($"        {conditionPrefix}writer.WriteElementString(\"{tag}\", XmlConvert.ToString(obj.{propName}));{conditionSuffix}");
        }
        else if (propType is INamedTypeSymbol iNullable && iNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && iNullable.TypeArguments[0].SpecialType == SpecialType.System_Int32)
        {
            sb.AppendLine($"        if (obj.{propName}.HasValue) {{ writer.WriteElementString(\"{tag}\", XmlConvert.ToString(obj.{propName}.Value)); }}");
        }
        // Int64 property
        else if (propType.SpecialType == SpecialType.System_Int64)
        {
            sb.AppendLine($"        {conditionPrefix}writer.WriteElementString(\"{tag}\", XmlConvert.ToString(obj.{propName}));{conditionSuffix}");
        }
        else if (propType is INamedTypeSymbol lNullable && lNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && lNullable.TypeArguments[0].SpecialType == SpecialType.System_Int64)
        {
            sb.AppendLine($"        if (obj.{propName}.HasValue) {{ writer.WriteElementString(\"{tag}\", XmlConvert.ToString(obj.{propName}.Value)); }}");
        }
        // Enum property
        else if (propType.TypeKind == TypeKind.Enum)
        {
            sb.AppendLine($"        {conditionPrefix}writer.WriteElementString(\"{tag}\", S3XmlEnumHelper.Format(obj.{propName}));{conditionSuffix}");
        }
        else if (propType is INamedTypeSymbol eNullable && eNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && eNullable.TypeArguments[0].TypeKind == TypeKind.Enum)
        {
            sb.AppendLine($"        if (obj.{propName}.HasValue) {{ writer.WriteElementString(\"{tag}\", S3XmlEnumHelper.Format(obj.{propName}.Value)); }}");
        }
        // Collection / List<T> property
        else if (propType is INamedTypeSymbol collType && (collType.Name == "List" || collType.AllInterfaces.Any(i => i.Name == "IEnumerable")))
        {
            var itemType = collType.TypeArguments.FirstOrDefault();
            if (itemType != null)
            {
                var itemMethodName = itemType.Name;
                if (itemType.ContainingType != null) itemMethodName = itemType.ContainingType.Name + "_" + itemType.Name;

                sb.AppendLine($"        if (obj.{propName} != null)");
                sb.AppendLine("        {");
                sb.AppendLine($"            foreach (var item in obj.{propName})");
                sb.AppendLine("            {");
                if (itemType.SpecialType == SpecialType.System_String)
                {
                    sb.AppendLine($"                if (item != null) writer.WriteElementString(\"{tag}\", item);");
                }
                else
                {
                    sb.AppendLine($"                if (item != null) Write_{itemMethodName}(writer, item, urlEncodeListingValues, \"{tag}\", string.Empty);");
                }
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }
        }
        // Nested Complex object property
        else if (propType.TypeKind == TypeKind.Class)
        {
            var nestedMethodName = propType.Name;
            if (propType.ContainingType != null) nestedMethodName = propType.ContainingType.Name + "_" + propType.Name;

            sb.AppendLine($"        if (obj.{propName} != null)");
            sb.AppendLine("        {");
            sb.AppendLine($"            Write_{nestedMethodName}(writer, obj.{propName}, urlEncodeListingValues, \"{tag}\", string.Empty);");
            sb.AppendLine("        }");
        }
    }

    private static void GeneratePropertyRead(StringBuilder sb, INamedTypeSymbol model, IPropertySymbol prop)
    {
        var propName = prop.Name;
        var xmlElementAttr = prop.GetAttributes().FirstOrDefault(a => a.AttributeClass!.Name == "XmlElementAttribute");
        string tag = propName;
        if (xmlElementAttr != null)
        {
            foreach (var named in xmlElementAttr.NamedArguments)
            {
                if (named.Key == "ElementName" && named.Value.Value is string elemName) tag = elemName;
            }
            if (xmlElementAttr.ConstructorArguments.Length > 0 && xmlElementAttr.ConstructorArguments[0].Value is string consElemName)
            {
                tag = consElemName;
            }
        }

        sb.AppendLine($"                    case \"{tag}\":");
        var propType = prop.Type;

        if (propType.SpecialType == SpecialType.System_String)
        {
            sb.AppendLine($"                        result.{propName} = reader.ReadElementContentAsString();");
        }
        else if (propType.SpecialType == SpecialType.System_DateTime)
        {
            sb.AppendLine($"                        result.{propName} = S3XmlEncodingHelper.ParseDateTime(reader.ReadElementContentAsString());");
        }
        else if (propType is INamedTypeSymbol nNullable && nNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && nNullable.TypeArguments[0].SpecialType == SpecialType.System_DateTime)
        {
            sb.AppendLine($"                        result.{propName} = S3XmlEncodingHelper.ParseDateTime(reader.ReadElementContentAsString());");
        }
        else if (propType.SpecialType == SpecialType.System_Boolean)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToBoolean(reader.ReadElementContentAsString().ToLowerInvariant());");
        }
        else if (propType is INamedTypeSymbol bNullable && bNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && bNullable.TypeArguments[0].SpecialType == SpecialType.System_Boolean)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToBoolean(reader.ReadElementContentAsString().ToLowerInvariant());");
        }
        else if (propType.SpecialType == SpecialType.System_Int32)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToInt32(reader.ReadElementContentAsString());");
        }
        else if (propType is INamedTypeSymbol iNullable && iNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && iNullable.TypeArguments[0].SpecialType == SpecialType.System_Int32)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToInt32(reader.ReadElementContentAsString());");
        }
        else if (propType.SpecialType == SpecialType.System_Int64)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToInt64(reader.ReadElementContentAsString());");
        }
        else if (propType is INamedTypeSymbol lNullable && lNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && lNullable.TypeArguments[0].SpecialType == SpecialType.System_Int64)
        {
            sb.AppendLine($"                        result.{propName} = XmlConvert.ToInt64(reader.ReadElementContentAsString());");
        }
        else if (propType.TypeKind == TypeKind.Enum)
        {
            sb.AppendLine($"                        result.{propName} = S3XmlEnumHelper.Parse{propType.Name}(reader.ReadElementContentAsString());");
        }
        else if (propType is INamedTypeSymbol eNullable && eNullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T && eNullable.TypeArguments[0].TypeKind == TypeKind.Enum)
        {
            sb.AppendLine($"                        result.{propName} = S3XmlEnumHelper.Parse{eNullable.TypeArguments[0].Name}(reader.ReadElementContentAsString());");
        }
        else if (propType is INamedTypeSymbol collType && (collType.Name == "List" || collType.AllInterfaces.Any(i => i.Name == "IEnumerable")))
        {
            var itemType = collType.TypeArguments.FirstOrDefault();
            if (itemType != null)
            {
                var itemMethodName = itemType.Name;
                if (itemType.ContainingType != null) itemMethodName = itemType.ContainingType.Name + "_" + itemType.Name;

                if (itemType.SpecialType == SpecialType.System_String)
                {
                    sb.AppendLine($"                        result.{propName}.Add(reader.ReadElementContentAsString());");
                }
                else
                {
                    sb.AppendLine($"                        result.{propName}.Add(Read_{itemMethodName}(reader));");
                }
            }
        }
        else if (propType.TypeKind == TypeKind.Class)
        {
            var nestedMethodName = propType.Name;
            if (propType.ContainingType != null) nestedMethodName = propType.ContainingType.Name + "_" + propType.Name;
            sb.AppendLine($"                        result.{propName} = Read_{nestedMethodName}(reader);");
        }
        sb.AppendLine("                        break;");
    }

    internal static bool S3XmlEncodingHelperIsListing(string tag)
    {
        return tag == "Key" || tag == "Prefix" || tag == "Delimiter" || tag == "Marker"
            || tag == "NextMarker" || tag == "StartAfter" || tag == "KeyMarker" || tag == "NextKeyMarker";
    }

    private static void GenerateLocationConstraintMethods(StringBuilder sb, string typeName, string methodName, string defaultRootName, string defaultNamespace)
    {
        sb.AppendLine($@"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)
    {{
        string tag = elementName ?? ""{defaultRootName}"";
        string ns = elementNamespace ?? ""{defaultNamespace}"";
        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);
        else writer.WriteStartElement(tag, ns);
        if (!string.IsNullOrEmpty(obj.Text)) writer.WriteString(obj.Text);
        writer.WriteEndElement();
    }}

    public static {typeName} Read_{methodName}(XmlReader reader)
    {{
        var result = new {typeName}();
        result.Text = reader.ReadElementContentAsString();
        return result;
    }}
");
    }

    private static void GenerateListVersionsResultMethods(StringBuilder sb, string typeName, string methodName, string defaultRootName, string defaultNamespace)
    {
        sb.AppendLine($"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string tag = elementName ?? \"{defaultRootName}\";");
        sb.AppendLine($"        string ns = elementNamespace ?? \"{defaultNamespace}\";");
        sb.AppendLine("        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);");
        sb.AppendLine("        else writer.WriteStartElement(tag, ns);");
        sb.AppendLine("        if (obj.Name != null) writer.WriteElementString(\"Name\", obj.Name);");
        sb.AppendLine("        if (obj.Prefix != null) writer.WriteElementString(\"Prefix\", urlEncodeListingValues ? S3XmlEncodingHelper.UrlEncode(obj.Prefix) : obj.Prefix);");
        sb.AppendLine("        if (obj.KeyMarker != null) writer.WriteElementString(\"KeyMarker\", urlEncodeListingValues ? S3XmlEncodingHelper.UrlEncode(obj.KeyMarker) : obj.KeyMarker);");
        sb.AppendLine("        if (obj.VersionIdMarker != null) writer.WriteElementString(\"VersionIdMarker\", obj.VersionIdMarker);");
        sb.AppendLine("        if (obj.ShouldSerializeNextKeyMarker()) writer.WriteElementString(\"NextKeyMarker\", urlEncodeListingValues ? S3XmlEncodingHelper.UrlEncode(obj.NextKeyMarker) : obj.NextKeyMarker);");
        sb.AppendLine("        if (obj.ShouldSerializeNextVersionIdMarker()) writer.WriteElementString(\"NextVersionIdMarker\", obj.NextVersionIdMarker);");
        sb.AppendLine("        writer.WriteElementString(\"MaxKeys\", XmlConvert.ToString(obj.MaxKeys));");
        sb.AppendLine("        if (obj.ShouldSerializeDelimiter()) writer.WriteElementString(\"Delimiter\", urlEncodeListingValues ? S3XmlEncodingHelper.UrlEncode(obj.Delimiter) : obj.Delimiter);");
        sb.AppendLine("        if (obj.ShouldSerializeEncodingType()) writer.WriteElementString(\"EncodingType\", obj.EncodingType);");
        sb.AppendLine("        writer.WriteElementString(\"IsTruncated\", obj.IsTruncated ? \"true\" : \"false\");");
        sb.AppendLine("        if (obj.Entries != null && obj.Entries.Count > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var item in obj.Entries)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (item is ObjectVersion ov) Write_ObjectVersion(writer, ov, urlEncodeListingValues, \"Version\", string.Empty);");
        sb.AppendLine("                else if (item is DeleteMarker dm) Write_DeleteMarker(writer, dm, urlEncodeListingValues, \"DeleteMarker\", string.Empty);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        else");
        sb.AppendLine("        {");
        sb.AppendLine("            if (obj.ShouldSerializeVersions())");
        sb.AppendLine("            {");
        sb.AppendLine("                foreach (var ov in obj.Versions) Write_ObjectVersion(writer, ov, urlEncodeListingValues, \"Version\", string.Empty);");
        sb.AppendLine("            }");
        sb.AppendLine("            if (obj.ShouldSerializeDeleteMarkers())");
        sb.AppendLine("            {");
        sb.AppendLine("                foreach (var dm in obj.DeleteMarkers) Write_DeleteMarker(writer, dm, urlEncodeListingValues, \"DeleteMarker\", string.Empty);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (obj.ShouldSerializeCommonPrefixes())");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var cp in obj.CommonPrefixes) Write_CommonPrefixes(writer, cp, urlEncodeListingValues, \"CommonPrefixes\", string.Empty);");
        sb.AppendLine("        }");
        sb.AppendLine("        writer.WriteEndElement();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public static {typeName} Read_{methodName}(XmlReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var result = new {typeName}();");
        sb.AppendLine("        if (reader.IsEmptyElement) { reader.Read(); return result; }");
        sb.AppendLine("        reader.ReadStartElement();");
        sb.AppendLine("        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (reader.LocalName)");
        sb.AppendLine("                {");
        sb.AppendLine("                    case \"Name\": result.Name = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"Prefix\": result.Prefix = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"KeyMarker\": result.KeyMarker = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"VersionIdMarker\": result.VersionIdMarker = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"NextKeyMarker\": result.NextKeyMarker = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"NextVersionIdMarker\": result.NextVersionIdMarker = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"MaxKeys\": result.MaxKeys = XmlConvert.ToInt32(reader.ReadElementContentAsString()); break;");
        sb.AppendLine("                    case \"Delimiter\": result.Delimiter = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"EncodingType\": result.EncodingType = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"IsTruncated\": result.IsTruncated = XmlConvert.ToBoolean(reader.ReadElementContentAsString().ToLowerInvariant()); break;");
        sb.AppendLine("                    case \"Version\":");
        sb.AppendLine("                        var ov = Read_ObjectVersion(reader);");
        sb.AppendLine("                        result.Entries.Add(ov);");
        sb.AppendLine("                        result.Versions.Add(ov);");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    case \"DeleteMarker\":");
        sb.AppendLine("                        var dm = Read_DeleteMarker(reader);");
        sb.AppendLine("                        result.Entries.Add(dm);");
        sb.AppendLine("                        result.DeleteMarkers.Add(dm);");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    case \"CommonPrefixes\":");
        sb.AppendLine("                        result.CommonPrefixes.Add(Read_CommonPrefixes(reader));");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    default:");
        sb.AppendLine("                        reader.Skip();");
        sb.AppendLine("                        break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateDeleteResultMethods(StringBuilder sb, string typeName, string methodName, string defaultRootName, string defaultNamespace)
    {
        sb.AppendLine($"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string tag = elementName ?? \"{defaultRootName}\";");
        sb.AppendLine($"        string ns = elementNamespace ?? \"{defaultNamespace}\";");
        sb.AppendLine("        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);");
        sb.AppendLine("        else writer.WriteStartElement(tag, ns);");
        sb.AppendLine("        if (obj.ShouldSerializeDeletedObjects())");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var item in obj.DeletedObjects)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (item != null) Write_Deleted(writer, item, urlEncodeListingValues, \"Deleted\", string.Empty);");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (obj.Errors != null)");
        sb.AppendLine("        {");
        sb.AppendLine("            foreach (var err in obj.Errors)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (err != null)");
        sb.AppendLine("                {");
        sb.AppendLine("                    writer.WriteStartElement(\"Error\");");
        sb.AppendLine("                    if (err.Key != null) writer.WriteElementString(\"Key\", err.Key);");
        sb.AppendLine("                    if (err.VersionId != null) writer.WriteElementString(\"VersionId\", err.VersionId);");
        sb.AppendLine("                    writer.WriteElementString(\"Code\", S3XmlEnumHelper.Format(err.Code));");
        sb.AppendLine("                    if (err.Message != null) writer.WriteElementString(\"Message\", err.Message);");
        sb.AppendLine("                    writer.WriteEndElement();");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        writer.WriteEndElement();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public static {typeName} Read_{methodName}(XmlReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var result = new {typeName}();");
        sb.AppendLine("        if (reader.IsEmptyElement) { reader.Read(); return result; }");
        sb.AppendLine("        reader.ReadStartElement();");
        sb.AppendLine("        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (reader.LocalName)");
        sb.AppendLine("                {");
        sb.AppendLine("                    case \"Deleted\":");
        sb.AppendLine("                        result.DeletedObjects.Add(Read_Deleted(reader));");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    case \"Error\":");
        sb.AppendLine("                        var err = new Error();");
        sb.AppendLine("                        if (!reader.IsEmptyElement)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            reader.ReadStartElement();");
        sb.AppendLine("                            while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("                                {");
        sb.AppendLine("                                    switch (reader.LocalName)");
        sb.AppendLine("                                    {");
        sb.AppendLine("                                        case \"Key\": err.Key = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                                        case \"VersionId\": err.VersionId = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                                        case \"Code\": err.Code = S3XmlEnumHelper.ParseErrorCode(reader.ReadElementContentAsString()); break;");
        sb.AppendLine("                                        case \"Message\": err.Message = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                                        default: reader.Skip(); break;");
        sb.AppendLine("                                    }");
        sb.AppendLine("                                }");
        sb.AppendLine("                                else reader.Read();");
        sb.AppendLine("                            }");
        sb.AppendLine("                            if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else reader.Read();");
        sb.AppendLine("                        result.Errors.Add(err);");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    default:");
        sb.AppendLine("                        reader.Skip();");
        sb.AppendLine("                        break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else");
        sb.AppendLine("            {");
        sb.AppendLine("                reader.Read();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateFileMetadataMethods(StringBuilder sb, string typeName, string methodName, string defaultRootName, string defaultNamespace)
    {
        sb.AppendLine($"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string tag = elementName ?? \"{defaultRootName}\";");
        sb.AppendLine($"        string ns = elementNamespace ?? \"{defaultNamespace}\";");
        sb.AppendLine("        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);");
        sb.AppendLine("        else writer.WriteStartElement(tag, ns);");
        sb.AppendLine("        if (obj.ContentType != null) writer.WriteElementString(\"ContentType\", obj.ContentType);");
        sb.AppendLine("        if (obj.ETag != null) writer.WriteElementString(\"ETag\", obj.ETag);");
        sb.AppendLine("        writer.WriteElementString(\"LastModified\", S3XmlEncodingHelper.FormatDateTime(obj.LastModified));");
        sb.AppendLine("        if (obj.UserMetadataEntries != null && obj.UserMetadataEntries.Length > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            writer.WriteStartElement(\"UserMetadata\");");
        sb.AppendLine("            foreach (var entry in obj.UserMetadataEntries)");
        sb.AppendLine("            {");
        sb.AppendLine("                if (entry != null)");
        sb.AppendLine("                {");
        sb.AppendLine("                    writer.WriteStartElement(\"Entry\");");
        sb.AppendLine("                    if (entry.Key != null) writer.WriteAttributeString(\"Key\", entry.Key);");
        sb.AppendLine("                    if (entry.Value != null) writer.WriteString(entry.Value);");
        sb.AppendLine("                    writer.WriteEndElement();");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            writer.WriteEndElement();");
        sb.AppendLine("        }");
        sb.AppendLine("        if (obj.Tags != null) Write_Tagging(writer, obj.Tags, urlEncodeListingValues, \"Tagging\", string.Empty);");
        sb.AppendLine("        if (obj.LegalHold != null) Write_LegalHold(writer, obj.LegalHold, urlEncodeListingValues, \"LegalHold\", string.Empty);");
        sb.AppendLine("        if (obj.Retention != null) Write_Retention(writer, obj.Retention, urlEncodeListingValues, \"Retention\", string.Empty);");
        sb.AppendLine("        if (obj.Acl != null) Write_AccessControlPolicy(writer, obj.Acl, urlEncodeListingValues, \"AccessControlPolicy\", string.Empty);");
        sb.AppendLine("        writer.WriteEndElement();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public static {typeName} Read_{methodName}(XmlReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var result = new {typeName}();");
        sb.AppendLine("        if (reader.IsEmptyElement) { reader.Read(); return result; }");
        sb.AppendLine("        reader.ReadStartElement();");
        sb.AppendLine("        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (reader.LocalName)");
        sb.AppendLine("                {");
        sb.AppendLine("                    case \"ContentType\": result.ContentType = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"ETag\": result.ETag = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"LastModified\": result.LastModified = S3XmlEncodingHelper.ParseDateTime(reader.ReadElementContentAsString()); break;");
        sb.AppendLine("                    case \"UserMetadata\":");
        sb.AppendLine("                        var entries = new System.Collections.Generic.List<FileSystemStorageBackend.MetadataEntry>();");
        sb.AppendLine("                        if (!reader.IsEmptyElement)");
        sb.AppendLine("                        {");
        sb.AppendLine("                            reader.ReadStartElement();");
        sb.AppendLine("                            while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("                            {");
        sb.AppendLine("                                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == \"Entry\")");
        sb.AppendLine("                                {");
        sb.AppendLine("                                    var e = new FileSystemStorageBackend.MetadataEntry();");
        sb.AppendLine("                                    if (reader.HasAttributes && reader.MoveToAttribute(\"Key\"))");
        sb.AppendLine("                                    {");
        sb.AppendLine("                                        e.Key = reader.Value;");
        sb.AppendLine("                                        reader.MoveToElement();");
        sb.AppendLine("                                    }");
        sb.AppendLine("                                    e.Value = reader.ReadElementContentAsString();");
        sb.AppendLine("                                    entries.Add(e);");
        sb.AppendLine("                                }");
        sb.AppendLine("                                else reader.Read();");
        sb.AppendLine("                            }");
        sb.AppendLine("                            if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("                        }");
        sb.AppendLine("                        else reader.Read();");
        sb.AppendLine("                        result.UserMetadataEntries = entries.ToArray();");
        sb.AppendLine("                        break;");
        sb.AppendLine("                    case \"Tagging\": result.Tags = Read_Tagging(reader); break;");
        sb.AppendLine("                    case \"LegalHold\": result.LegalHold = Read_LegalHold(reader); break;");
        sb.AppendLine("                    case \"Retention\": result.Retention = Read_Retention(reader); break;");
        sb.AppendLine("                    case \"AccessControlPolicy\": result.Acl = Read_AccessControlPolicy(reader); break;");
        sb.AppendLine("                    default: reader.Skip(); break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else reader.Read();");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateGranteeMethods(StringBuilder sb, string typeName, string methodName, string defaultRootName, string defaultNamespace)
    {
        sb.AppendLine($"    public static void Write_{methodName}(XmlWriter writer, {typeName} obj, bool urlEncodeListingValues, string? elementName = null, string? elementNamespace = null)");
        sb.AppendLine("    {");
        sb.AppendLine($"        string tag = elementName ?? \"{defaultRootName}\";");
        sb.AppendLine($"        string ns = elementNamespace ?? \"{defaultNamespace}\";");
        sb.AppendLine("        if (string.IsNullOrEmpty(ns)) writer.WriteStartElement(tag);");
        sb.AppendLine("        else writer.WriteStartElement(tag, ns);");
        sb.AppendLine("        string gType = obj is CanonicalUser ? \"CanonicalUser\" : (obj is Group ? \"Group\" : (obj is AmazonCustomerByEmail ? \"AmazonCustomerByEmail\" : obj.GranteeType));");
        sb.AppendLine("        writer.WriteAttributeString(\"xsi\", \"type\", \"http://www.w3.org/2001/XMLSchema-instance\", gType);");
        sb.AppendLine("        if (obj.ShouldSerializeID()) writer.WriteElementString(\"ID\", obj.ID);");
        sb.AppendLine("        if (obj.ShouldSerializeDisplayName()) writer.WriteElementString(\"DisplayName\", obj.DisplayName);");
        sb.AppendLine("        if (obj.ShouldSerializeURI()) writer.WriteElementString(\"URI\", obj.URI);");
        sb.AppendLine("        if (obj.ShouldSerializeEmailAddress()) writer.WriteElementString(\"EmailAddress\", obj.EmailAddress);");
        sb.AppendLine("        writer.WriteEndElement();");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine($"    public static {typeName} Read_{methodName}(XmlReader reader)");
        sb.AppendLine("    {");
        sb.AppendLine("        string gType = \"CanonicalUser\";");
        sb.AppendLine("        if (reader.HasAttributes)");
        sb.AppendLine("        {");
        sb.AppendLine("            while (reader.MoveToNextAttribute())");
        sb.AppendLine("            {");
        sb.AppendLine("                if (reader.LocalName == \"type\" && (reader.NamespaceURI == \"http://www.w3.org/2001/XMLSchema-instance\" || string.IsNullOrEmpty(reader.NamespaceURI)))");
        sb.AppendLine("                {");
        sb.AppendLine("                    gType = reader.Value;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            reader.MoveToElement();");
        sb.AppendLine("        }");
        sb.AppendLine("        Grantee result = gType == \"Group\" ? new Group() : (gType == \"AmazonCustomerByEmail\" ? new AmazonCustomerByEmail() : new CanonicalUser());");
        sb.AppendLine("        if (reader.IsEmptyElement) { reader.Read(); return result; }");
        sb.AppendLine("        reader.ReadStartElement();");
        sb.AppendLine("        while (reader.NodeType != XmlNodeType.EndElement && !reader.EOF)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (reader.NodeType == XmlNodeType.Element)");
        sb.AppendLine("            {");
        sb.AppendLine("                switch (reader.LocalName)");
        sb.AppendLine("                {");
        sb.AppendLine("                    case \"ID\": result.ID = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"DisplayName\": result.DisplayName = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"URI\": result.URI = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    case \"EmailAddress\": result.EmailAddress = reader.ReadElementContentAsString(); break;");
        sb.AppendLine("                    default: reader.Skip(); break;");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("            else reader.Read();");
        sb.AppendLine("        }");
        sb.AppendLine("        if (reader.NodeType == XmlNodeType.EndElement) reader.ReadEndElement();");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    internal static int GetInheritanceDepth(INamedTypeSymbol symbol)
    {
        int depth = 0;
        var current = symbol.BaseType;
        while (current != null && current.SpecialType != SpecialType.System_Object)
        {
            depth++;
            current = current.BaseType;
        }
        return depth;
    }

    private static void GenerateRegistryDispatch(StringBuilder sb, HashSet<INamedTypeSymbol> models)
    {
        var orderedModels = models
            .OrderByDescending(m => GetInheritanceDepth(m))
            .ThenBy(m => m.ToDisplayString())
            .ToList();

        // Generic Serialize<T>
        sb.AppendLine(@"    public static void Serialize<T>(XmlWriter writer, T obj, bool urlEncodeListingValues = false) where T : class
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));");

        foreach (var model in orderedModels)
        {
            var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var methodName = model.Name;
            if (model.ContainingType != null) methodName = model.ContainingType.Name + "_" + model.Name;

            sb.AppendLine($"        if (typeof(T) == typeof({typeName})) {{ Write_{methodName}(writer, ({typeName})(object)obj, urlEncodeListingValues); return; }}");
        }

        sb.AppendLine(@"        SerializeObject(writer, obj, urlEncodeListingValues);
    }");
        sb.AppendLine();

        // Non-generic Serialize
        sb.AppendLine(@"    public static void SerializeObject(XmlWriter writer, object obj, bool urlEncodeListingValues = false)
    {
        if (obj == null) throw new ArgumentNullException(nameof(obj));
        switch (obj)
        {");

        foreach (var model in orderedModels)
        {
            var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var methodName = model.Name;
            if (model.ContainingType != null) methodName = model.ContainingType.Name + "_" + model.Name;

            sb.AppendLine($"            case {typeName} val: Write_{methodName}(writer, val, urlEncodeListingValues); return;");
        }

        sb.AppendLine(@"            default:
                throw new NotSupportedException($""Type {obj.GetType().FullName} is not supported by generated S3 XML serializer."");
        }
    }");
        sb.AppendLine();

        // Generic Deserialize<T>
        sb.AppendLine(@"    public static T Deserialize<T>(XmlReader reader) where T : class
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        reader.MoveToContent();");

        foreach (var model in orderedModels)
        {
            var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var methodName = model.Name;
            if (model.ContainingType != null) methodName = model.ContainingType.Name + "_" + model.Name;

            sb.AppendLine($"        if (typeof(T) == typeof({typeName})) return (T)(object)Read_{methodName}(reader);");
        }

        sb.AppendLine(@"        return (T)Deserialize(reader, typeof(T));
    }");
        sb.AppendLine();

        // Non-generic Deserialize
        sb.AppendLine(@"    public static object Deserialize(XmlReader reader, Type type)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (type == null) throw new ArgumentNullException(nameof(type));
        reader.MoveToContent();");

        foreach (var model in orderedModels)
        {
            var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var methodName = model.Name;
            if (model.ContainingType != null) methodName = model.ContainingType.Name + "_" + model.Name;

            sb.AppendLine($"        if (type == typeof({typeName})) return Read_{methodName}(reader);");
        }

        sb.AppendLine(@"        throw new NotSupportedException($""Type {type.FullName} is not supported by generated S3 XML serializer."");
    }");
        sb.AppendLine();

        // IsSupported
        sb.AppendLine(@"    public static bool IsSupported(Type type)
    {");
        foreach (var model in orderedModels)
        {
            var typeName = model.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.AppendLine($"        if (type == typeof({typeName})) return true;");
        }
        sb.AppendLine(@"        return false;
    }");
    }
}
