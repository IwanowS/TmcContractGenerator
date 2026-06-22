using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TmcContractGenerator;

internal sealed class CSharpEmitter
{
    private static readonly Dictionary<string, string> PrimitiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = "bool", ["BYTE"] = "byte", ["USINT"] = "byte", ["SINT"] = "sbyte",
        ["WORD"] = "ushort", ["UINT"] = "ushort", ["INT"] = "short", ["DWORD"] = "uint",
        ["UDINT"] = "uint", ["DINT"] = "int", ["LWORD"] = "ulong", ["ULINT"] = "ulong",
        ["LINT"] = "long", ["REAL"] = "float", ["LREAL"] = "double", ["TIME"] = "uint"
    };
    private readonly TmcModel _model;
    private readonly GeneratorConfig _config;
    private readonly string _tmcName;
    private readonly string _rootClass;
    private readonly Dictionary<string, bool> _layout = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unknown = new(StringComparer.OrdinalIgnoreCase);

    public CSharpEmitter(TmcModel model, GeneratorConfig config, string tmcName)
    {
        _model = model;
        _config = config;
        _tmcName = tmcName;
        _rootClass = "Plc" + Identifier(Path.GetFileNameWithoutExtension(tmcName));
    }

    public List<string> Warnings { get; } = new();
    public List<ContractItem> Items { get; } = new();

    public Dictionary<string, string> Emit(List<PlcSymbol> roots)
    {
        foreach (var root in roots) Flatten(root.Path, root.Type, root.BitSize, root.Comment, root.Dimensions, new HashSet<string>());
        Items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_config.GenerateWrappers) files[_rootClass + ".g.cs"] = EmitWrappers(roots);
        if (_config.GenerateDto) files[_rootClass + ".Dto.g.cs"] = EmitDtos();
        if (_config.GenerateManifest) files[_rootClass + ".Manifest.g.cs"] = EmitManifest();
        return files;
    }

    private void Flatten(string path, TypeReference reference, int? bits, string? comment, List<PlcArrayDimension> dimensions, HashSet<string> stack)
    {
        var type = _model.Resolve(reference);
        var kind = Kind(reference, type, dimensions);
        var reliable = kind == PlcTypeKind.Primitive || kind == PlcTypeKind.Enum
            || (kind == PlcTypeKind.Struct && type != null && IsReliable(type, new HashSet<string>()));
        Items.Add(new ContractItem
        {
            Path = path, PlcTypeName = PlcName(reference), CSharpTypeName = CsType(reference), Kind = kind,
            Size = bits.HasValue && bits.Value % 8 == 0 ? bits.Value / 8 : null, Comment = comment,
            BinaryLayoutReliable = reliable, Dimensions = dimensions, Type = reference
        });
        if ((kind != PlcTypeKind.Struct && kind != PlcTypeKind.Namespace) || type == null || !stack.Add(type.QualifiedName)) return;
        foreach (var field in type.Fields)
            Flatten(path + "." + field.Name, field.Type, field.BitSize, field.Comment, field.Dimensions, stack);
        stack.Remove(type.QualifiedName);
    }

    private string EmitWrappers(List<PlcSymbol> roots)
    {
        var sb = Header();
        sb.Append("namespace ").Append(_config.Namespace).Append("\n{\n");
        EmitSymbolHelpers(sb);
        var reachable = ReachableTypes(roots.Select(x => x.Type));
        foreach (var type in reachable.Where(x => x.Fields.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal)) EmitNode(sb, type);

        sb.Append("    public sealed partial class ").Append(_rootClass).Append("\n    {\n")
          .Append("        private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;\n\n")
          .Append("        public ").Append(_rootClass).Append("(ETS.TwinCAT.Interfaces.IVariablesProvider variables)\n        {\n")
          .Append("            if (variables == null) throw new System.ArgumentNullException(\"variables\");\n            _variables = variables;\n");
        var rootNames = UniqueMemberNames(roots.Select(x => x.Path.Split('.').Last()).ToList());
        for (var i = 0; i < roots.Count; i++)
        {
            var root = roots[i];
            sb.Append("            ").Append(rootNames[i]).Append(" = new ").Append(WrapperType(root.Type, root.Dimensions))
              .Append("(_variables, \"").Append(Escape(root.Path)).Append('"');
            AppendDimensions(sb, root.Dimensions);
            sb.Append(");\n");
        }
        sb.Append("        }\n\n");
        for (var i = 0; i < roots.Count; i++)
            sb.Append("        public ").Append(WrapperType(roots[i].Type, roots[i].Dimensions)).Append(' ').Append(rootNames[i]).Append(" { get; private set; }\n\n");
        sb.Append("        public static ETS.PlcVariables.Contracts.PlcContractManifest Contract\n        {\n")
          .Append("            get { return ").Append(_rootClass).Append("Manifest.Value; }\n        }\n")
          .Append("    }\n}\n");
        return sb.ToString();
    }

    private void EmitSymbolHelpers(StringBuilder sb)
    {
        sb.Append("    public sealed class PlcSymbol<T>\n    {\n")
          .Append("        private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;\n")
          .Append("        public PlcSymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path) { _variables = variables; Path = path; }\n")
          .Append("        public string Path { get; private set; }\n")
          .Append("        public T Read() { return _variables.ReadValue<T>(Path); }\n")
          .Append("        public void Write(T value) { _variables.WriteValue<T>(Path, value); }\n")
          .Append("        public override string ToString() { return Path; }\n    }\n\n")
          .Append("    public sealed class PlcArraySymbol<T>\n    {\n")
          .Append("        private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;\n")
          .Append("        private readonly int[] _lowerBounds;\n        private readonly int[] _upperBounds;\n")
          .Append("        public PlcArraySymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path, int[] lowerBounds, int[] upperBounds)\n")
          .Append("        { _variables = variables; Path = path; _lowerBounds = lowerBounds; _upperBounds = upperBounds; }\n")
          .Append("        public string Path { get; private set; }\n")
          .Append("        public int[] LowerBounds { get { return (int[])_lowerBounds.Clone(); } }\n")
          .Append("        public int[] UpperBounds { get { return (int[])_upperBounds.Clone(); } }\n")
          .Append("        public PlcSymbol<T> At(params int[] indices)\n        {\n")
          .Append("            if (indices == null || indices.Length != _lowerBounds.Length) throw new System.ArgumentException(\"Wrong array rank.\", \"indices\");\n")
          .Append("            for (int i = 0; i < indices.Length; i++) if (indices[i] < _lowerBounds[i] || indices[i] > _upperBounds[i]) throw new System.ArgumentOutOfRangeException(\"indices\");\n")
          .Append("            return new PlcSymbol<T>(_variables, Path + \"[\" + string.Join(\",\", indices) + \"]\");\n        }\n")
          .Append("        public T ReadElement(params int[] indices) { return At(indices).Read(); }\n")
          .Append("        public void WriteElement(T value, params int[] indices) { At(indices).Write(value); }\n")
          .Append("    }\n\n");
    }

    private void EmitNode(StringBuilder sb, PlcType type)
    {
        var nodeName = TypeIdentifier(type) + "Node";
        var dtoName = TypeIdentifier(type) + "Dto";
        sb.Append("    public sealed class ").Append(nodeName).Append("\n    {\n")
          .Append("        private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;\n        private readonly string _path;\n\n")
          .Append("        public ").Append(nodeName).Append("(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path)\n        {\n")
          .Append("            _variables = variables;\n            _path = path;\n");
        var names = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
        for (var i = 0; i < type.Fields.Count; i++)
        {
            var field = type.Fields[i];
            sb.Append("            ").Append(names[i]).Append(" = new ").Append(WrapperType(field.Type, field.Dimensions))
              .Append("(_variables, _path + \".").Append(Escape(field.Name)).Append('"');
            AppendDimensions(sb, field.Dimensions);
            sb.Append(");\n");
        }
        sb.Append("        }\n\n        public string Path { get { return _path; } }\n\n");
        if (IsReliable(type, new HashSet<string>()))
            sb.Append("        public ").Append(dtoName).Append(" Read() { return _variables.ReadValue<").Append(dtoName).Append(">(_path); }\n")
              .Append("        public void Write(").Append(dtoName).Append(" value) { _variables.WriteValue<").Append(dtoName).Append(">(_path, value); }\n\n");
        for (var i = 0; i < type.Fields.Count; i++)
            sb.Append("        public ").Append(WrapperType(type.Fields[i].Type, type.Fields[i].Dimensions)).Append(' ').Append(names[i]).Append(" { get; private set; }\n");
        sb.Append("    }\n\n");
    }

    private string EmitDtos()
    {
        var sb = Header();
        sb.Append("namespace ").Append(_config.Namespace).Append("\n{\n");
        var referenced = ReachableTypes(Items.Select(x => x.Type));
        foreach (var type in referenced.Where(x => x.EnumValues.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
        {
            sb.Append("    public enum ").Append(TypeIdentifier(type)).Append(" : ").Append(EnumBase(type)).Append("\n    {\n");
            var names = UniqueNames(type.EnumValues.Select(x => x.Name).ToList());
            for (var i = 0; i < type.EnumValues.Count; i++)
                sb.Append("        ").Append(names[i]).Append(" = ").Append(type.EnumValues[i].Value.ToString(CultureInfo.InvariantCulture)).Append(i + 1 == type.EnumValues.Count ? "\n" : ",\n");
            sb.Append("    }\n\n");
        }
        foreach (var type in referenced.Where(x => x.Fields.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
        {
            var reliable = IsReliable(type, new HashSet<string>());
            if (reliable)
                sb.Append("    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = ")
                  .Append((type.BitSize ?? 0) / 8).Append(")]\n    public struct ");
            else sb.Append("    public sealed class ");
            sb.Append(TypeIdentifier(type)).Append("Dto\n    {\n");
            var names = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
            for (var i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                if (reliable)
                {
                    sb.Append("        [System.Runtime.InteropServices.FieldOffset(").Append((field.BitOffset ?? 0) / 8).Append(")]\n");
                    if (string.Equals(field.Type.Name, "BOOL", StringComparison.OrdinalIgnoreCase))
                        sb.Append("        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]\n");
                    sb.Append("        public ").Append(CsType(field.Type)).Append(' ').Append(names[i]).Append(";\n\n");
                }
                else sb.Append("        public ").Append(CsTypeForLogical(field.Type, field.Dimensions)).Append(' ').Append(names[i]).Append(" { get; set; }\n\n");
            }
            sb.Append("    }\n\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private string EmitManifest()
    {
        var hash = ContractGenerator.Sha256(Items);
        var sb = Header();
        sb.Append("namespace ").Append(_config.Namespace).Append("\n{\n")
          .Append("    internal static class ").Append(_rootClass).Append("Manifest\n    {\n")
          .Append("        internal static readonly ETS.PlcVariables.Contracts.PlcContractManifest Value = new ETS.PlcVariables.Contracts.PlcContractManifest\n        {\n")
          .Append("            TmcName = \"").Append(Escape(_tmcName)).Append("\",\n")
          .Append("            ContractHash = \"").Append(hash).Append("\",\n")
          .Append("            Symbols = new ETS.PlcVariables.Contracts.PlcContractSymbol[]\n            {\n");
        foreach (var item in Items)
        {
            sb.Append("                new ETS.PlcVariables.Contracts.PlcContractSymbol { Path = \"").Append(Escape(item.Path))
              .Append("\", PlcTypeName = \"").Append(Escape(item.PlcTypeName)).Append("\", CSharpTypeName = \"")
              .Append(Escape(item.CSharpTypeName)).Append("\", Size = ").Append(item.Size?.ToString(CultureInfo.InvariantCulture) ?? "null")
              .Append(", Kind = \"").Append(item.Kind.ToString().ToLowerInvariant()).Append("\", Comment = ")
              .Append(item.Comment == null ? "null" : "\"" + Escape(item.Comment) + "\"")
              .Append(", BinaryLayoutReliable = ").Append(item.BinaryLayoutReliable ? "true" : "false")
              .Append(", Dimensions = new ETS.PlcVariables.Contracts.PlcArrayDimension[] { ");
            foreach (var dim in item.Dimensions)
                sb.Append("new ETS.PlcVariables.Contracts.PlcArrayDimension { LowerBound = ").Append(dim.LowerBound).Append(", UpperBound = ").Append(dim.UpperBound).Append(" }, ");
            sb.Append("} },\n");
        }
        sb.Append("            }\n        };\n    }\n}\n");
        return sb.ToString();
    }

    private List<PlcType> ReachableTypes(IEnumerable<TypeReference> references)
    {
        var result = new Dictionary<string, PlcType>(StringComparer.OrdinalIgnoreCase);
        void Visit(TypeReference reference)
        {
            var type = _model.Resolve(reference);
            if (type == null || !result.TryAdd(type.QualifiedName, type)) return;
            if (!string.IsNullOrEmpty(type.BaseType.Name)) Visit(type.BaseType);
            foreach (var field in type.Fields) Visit(field.Type);
        }
        foreach (var reference in references) Visit(reference);
        return result.Values.ToList();
    }

    private bool IsReliable(PlcType type, HashSet<string> stack)
    {
        if (_layout.TryGetValue(type.QualifiedName, out var cached)) return cached;
        if (!stack.Add(type.QualifiedName) || !type.BitSize.HasValue || type.BitSize.Value % 8 != 0 || type.Fields.Count == 0)
            return _layout[type.QualifiedName] = false;
        foreach (var field in type.Fields)
        {
            if (field.Type.PointerLevel > 0 || field.Dimensions.Count > 0 || !field.BitOffset.HasValue || field.BitOffset.Value % 8 != 0 || !field.BitSize.HasValue || field.BitSize.Value % 8 != 0)
                return _layout[type.QualifiedName] = false;
            var nested = _model.Resolve(field.Type);
            if (nested != null && nested.EnumValues.Count == 0 && !IsReliable(nested, stack)) return _layout[type.QualifiedName] = false;
            if (nested == null && (!PrimitiveTypes.ContainsKey(BaseName(field.Type.Name)) || IsString(field.Type.Name))) return _layout[type.QualifiedName] = false;
        }
        stack.Remove(type.QualifiedName);
        return _layout[type.QualifiedName] = true;
    }

    private PlcTypeKind Kind(TypeReference reference, PlcType? type, List<PlcArrayDimension> dimensions)
    {
        if (dimensions.Count > 0) return PlcTypeKind.Array;
        if (reference.PointerLevel > 0) return Unknown(reference);
        if (type?.IsSyntheticNamespace == true) return PlcTypeKind.Namespace;
        if (IsString(reference.Name) || (type != null && IsString(type.BaseType.Name))) return PlcTypeKind.String;
        if (type?.EnumValues.Count > 0) return PlcTypeKind.Enum;
        if (type?.Fields.Count > 0) return PlcTypeKind.Struct;
        if (PrimitiveTypes.ContainsKey(BaseName(reference.Name))) return PlcTypeKind.Primitive;
        if (type != null && !string.IsNullOrEmpty(type.BaseType.Name)) return Kind(type.BaseType, _model.Resolve(type.BaseType), dimensions);
        return Unknown(reference);
    }

    private PlcTypeKind Unknown(TypeReference reference)
    {
        if (_unknown.Add(reference.QualifiedName)) Warnings.Add("Unknown PLC type '" + reference.QualifiedName + "'. Symbols use object wrappers and have no binary layout.");
        return PlcTypeKind.Unknown;
    }

    private string WrapperType(TypeReference reference, List<PlcArrayDimension> dimensions)
    {
        var type = _model.Resolve(reference);
        if (dimensions.Count > 0) return "PlcArraySymbol<" + CsType(reference) + ">";
        if (type?.Fields.Count > 0) return TypeIdentifier(type) + "Node";
        return "PlcSymbol<" + CsType(reference) + ">";
    }

    private string CsType(TypeReference reference)
    {
        if (reference.PointerLevel > 0) return "object";
        if (IsString(reference.Name)) return "string";
        if (PrimitiveTypes.TryGetValue(BaseName(reference.Name), out var primitive)) return primitive;
        var type = _model.Resolve(reference);
        if (type?.EnumValues.Count > 0) return TypeIdentifier(type);
        if (type?.Fields.Count > 0) return TypeIdentifier(type) + "Dto";
        if (type != null && !string.IsNullOrEmpty(type.BaseType.Name)) return CsType(type.BaseType);
        return "object";
    }

    private string CsTypeForLogical(TypeReference reference, List<PlcArrayDimension> dimensions) => CsType(reference) + (dimensions.Count == 0 ? string.Empty : "[]");
    private string PlcName(TypeReference reference) => string.IsNullOrEmpty(reference.QualifiedName) ? "UNKNOWN" : reference.QualifiedName;
    private static bool IsString(string name) => BaseName(name).StartsWith("STRING", StringComparison.OrdinalIgnoreCase) || BaseName(name).StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase);
    private static string BaseName(string name) => Regex.Replace(name.Trim(), "\\s*\\(.*\\)$", string.Empty).ToUpperInvariant();
    private string EnumBase(PlcType type) => PrimitiveTypes.TryGetValue(BaseName(type.BaseType.Name), out var value) && value != "bool" ? value : "int";

    private string TypeIdentifier(PlcType type)
    {
        var duplicate = _model.TypesByName.Values.Distinct().Count(x => string.Equals(x.Name, type.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        return Identifier((duplicate ? type.QualifiedName.Replace('.', '_') : type.Name));
    }

    private static List<string> UniqueNames(List<string> source)
    {
        return UniqueNames(source, Identifier);
    }

    private static List<string> UniqueMemberNames(List<string> source)
    {
        return UniqueNames(source, value => Regex.IsMatch(value, "^[A-Z][A-Z0-9]{0,3}$") ? value : Identifier(value));
    }

    private static List<string> UniqueNames(List<string> source, Func<string, string> normalize)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in source)
        {
            var value = normalize(raw);
            if (!used.Add(value))
            {
                value += "_" + ShortHash(raw);
                while (!used.Add(value)) value += "X";
            }
            result.Add(value);
        }
        return result;
    }

    internal static string Identifier(string value)
    {
        var chunks = Regex.Matches(value ?? string.Empty, "[A-Za-z0-9]+").Select(x => x.Value);
        var words = chunks.SelectMany(x => Regex.Matches(x, "[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[0-9]+|[A-Z]").Select(m => m.Value));
        var joined = string.Concat(words.Select(Pascal));
        if (string.IsNullOrEmpty(joined)) joined = "Unnamed";
        if (char.IsDigit(joined[0])) joined = "_" + joined;
        if (Keywords.Contains(joined)) joined = "@" + joined;
        return joined;
    }

    private static string Pascal(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    private static string ShortHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 8);
    private static readonly HashSet<string> Keywords = new(new[] { "class", "struct", "enum", "event", "namespace", "public", "private", "internal", "void", "string", "object", "base", "this", "new", "return", "params", "ref", "out", "in", "is", "as", "operator", "implicit", "explicit", "interface", "delegate", "readonly", "sealed", "partial", "static", "bool", "byte", "short", "int", "long", "float", "double" }, StringComparer.Ordinal);

    private StringBuilder HeaderInstance()
    {
        var sb = new StringBuilder();
        sb.Append("// <auto-generated />\n// Generated from: ").Append(_tmcName).Append("\n// Do not edit manually.\n\n");
        return sb;
    }

    private StringBuilder Header()
    {
        return HeaderInstance();
    }

    private static void AppendDimensions(StringBuilder sb, List<PlcArrayDimension> dimensions)
    {
        if (dimensions.Count == 0) return;
        sb.Append(", new int[] { ").Append(string.Join(", ", dimensions.Select(x => x.LowerBound))).Append(" }, new int[] { ")
          .Append(string.Join(", ", dimensions.Select(x => x.UpperBound))).Append(" }");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
}
