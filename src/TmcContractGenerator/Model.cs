using System.Text.Json.Serialization;

namespace TmcContractGenerator;

public sealed class GeneratorConfig
{
    [JsonPropertyName("tmc")]
    public string Tmc { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("roots")]
    public string[] Roots { get; set; } = Array.Empty<string>();

    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("generateDto")]
    public bool GenerateDto { get; set; } = true;

    [JsonPropertyName("generateManifest")]
    public bool GenerateManifest { get; set; } = true;

    [JsonPropertyName("generateWrappers")]
    public bool GenerateWrappers { get; set; } = true;

    [JsonPropertyName("generateSubscriptions")]
    public bool GenerateSubscriptions { get; set; }

    [JsonPropertyName("unknownTypeIsError")]
    public bool UnknownTypeIsError { get; set; }
}

public sealed class GenerationResult
{
    public int FoundSymbols { get; init; }
    public int GeneratedSymbols { get; init; }
    public string[] Roots { get; init; } = Array.Empty<string>();
    public string[] Warnings { get; init; } = Array.Empty<string>();
    public string[] Files { get; init; } = Array.Empty<string>();
}

public sealed class GeneratorException : Exception
{
    public GeneratorException(string message) : base(message) { }
    public GeneratorException(string message, Exception inner) : base(message, inner) { }
}

internal enum PlcTypeKind { Primitive, Enum, Struct, Array, String, Namespace, Pointer, Unknown }

internal sealed class PlcArrayDimension
{
    public int LowerBound { get; init; }
    public int UpperBound { get; init; }
}

internal sealed class PlcField
{
    public string Name { get; init; } = string.Empty;
    public TypeReference Type { get; init; } = new();
    public int? BitSize { get; init; }
    public int? BitOffset { get; init; }
    public string? Comment { get; init; }
    public List<PlcArrayDimension> Dimensions { get; init; } = new();
}

internal sealed class PlcEnumValue
{
    public string Name { get; init; } = string.Empty;
    public long Value { get; init; }
}

internal sealed class PlcType
{
    public string Name { get; init; } = string.Empty;
    public string QualifiedName { get; init; } = string.Empty;
    public string? Guid { get; init; }
    public int? BitSize { get; init; }
    public TypeReference BaseType { get; init; } = new();
    public List<PlcField> Fields { get; init; } = new();
    public List<PlcEnumValue> EnumValues { get; init; } = new();
    public bool IsSyntheticNamespace { get; init; }
}

internal sealed class TypeReference
{
    public string Name { get; init; } = string.Empty;
    public string? Namespace { get; init; }
    public string? Guid { get; init; }
    public int PointerLevel { get; init; }
    public string QualifiedName => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
}

internal sealed class PlcSymbol
{
    public string Path { get; init; } = string.Empty;
    public TypeReference Type { get; init; } = new();
    public int? BitSize { get; init; }
    public string? Comment { get; init; }
    public List<PlcArrayDimension> Dimensions { get; init; } = new();
}

internal sealed class ContractItem
{
    public string Path { get; init; } = string.Empty;
    public string PlcTypeName { get; init; } = string.Empty;
    public string CSharpTypeName { get; init; } = string.Empty;
    public PlcTypeKind Kind { get; init; }
    public int? Size { get; init; }
    public string? Comment { get; init; }
    public bool BinaryLayoutReliable { get; init; }
    public List<PlcArrayDimension> Dimensions { get; init; } = new();
    public TypeReference Type { get; init; } = new();
}
