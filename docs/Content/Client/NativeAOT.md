# Native AOT & Trimming

SharpS3 is designed from the ground up for modern .NET Native Ahead-of-Time (Native AOT) compilation and aggressive IL trimming in .NET 8, 9, and 10.

---

## 1. Zero Runtime Reflection

Traditional XML serializers in .NET (`System.Xml.Serialization.XmlSerializer`) rely on runtime reflection and runtime code generation (`System.Reflection.Emit`), which are incompatible with Native AOT and fail when compiled ahead of time.

SharpS3 completely eliminates runtime reflection through compile-time Roslyn source generators (`SharpS3.Generators`). All XML serialization and deserialization methods are generated at build time into strongly-typed C# code.

---

## 2. Enabling Native AOT in Your Project

To publish a self-contained Native AOT application using SharpS3, enable `<PublishAot>` in your `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SharpS3" Version="*" />
  </ItemGroup>

</Project>
```

Publish using the `dotnet publish` command:

```bash
dotnet publish -c Release -r win-x64
```

### Benefits:
* **Instant Startup:** Zero JIT compilation latency.
* **Minimal Memory Footprint:** Lean memory usage suitable for microVMs and edge appliances.
* **Single Executable:** Ships as an autonomous binary with no external .NET runtime dependency.
