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
    private readonly HashSet<string> _unsupportedPointers = new(StringComparer.OrdinalIgnoreCase);

    public CSharpEmitter(TmcModel model, GeneratorConfig config, string tmcName)
    {
        _model = model;
        _config = config;
        _tmcName = tmcName;
        _rootClass = "PlcRoot_" + Identifier(Path.GetFileNameWithoutExtension(tmcName));
    }

    public List<string> Warnings { get; } = new();
    public List<ContractItem> Items { get; } = new();

    public Dictionary<string, string> Emit(List<PlcSymbol> roots)
    {
        foreach (var root in roots)
            Flatten(root.Path, root.Type, root.BitSize, root.Comment, root.Dimensions, new HashSet<string>());

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
            || (kind == PlcTypeKind.Struct && type != null && CanGenerateRawDto(type, new HashSet<string>()));

        if (kind == PlcTypeKind.Pointer && !IsSupportedPointerTarget(reference, type))
            UnsupportedPointer(reference);

        Items.Add(new ContractItem
        {
            Path = path,
            PlcTypeName = PlcName(reference),
            CSharpTypeName = CsLogicalType(reference),
            Kind = kind,
            Size = bits.HasValue && bits.Value % 8 == 0 ? bits.Value / 8 : null,
            Comment = comment,
            BinaryLayoutReliable = reliable,
            Dimensions = dimensions,
            Type = reference
        });

        if ((kind != PlcTypeKind.Struct && kind != PlcTypeKind.Namespace && kind != PlcTypeKind.Pointer)
            || type == null
            || type.Fields.Count == 0
            || !stack.Add(type.QualifiedName))
            return;

        var childPrefix = kind == PlcTypeKind.Pointer ? path + "^." : path + ".";
        foreach (var field in type.Fields)
            Flatten(childPrefix + field.Name, field.Type, field.BitSize, field.Comment, field.Dimensions, stack);
        stack.Remove(type.QualifiedName);
    }

    private string EmitWrappers(List<PlcSymbol> roots)
    {
        var writer = Header();
        using (writer.Block("namespace " + _config.Namespace))
        {
            EmitSymbolHelpers(writer);

            var reachable = ReachableTypes(roots.Select(x => x.Type));
            foreach (var type in reachable.Where(x => x.Fields.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
                EmitNode(writer, type, isPointerNode: false);

            var pointerTypes = ReachablePointerStructTypes(roots.Select(x => x.Type));
            foreach (var type in pointerTypes.OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
                EmitNode(writer, type, isPointerNode: true);

            using (writer.Block("public sealed partial class " + _rootClass))
            {
                writer.Line("private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;");
                writer.Line("private readonly ETS.TwinCAT.Ads.PlcConnection _connection;");
                writer.Line();
                using (writer.Block("public " + _rootClass + "(ETS.TwinCAT.Interfaces.IVariablesProvider variables)"))
                {
                    writer.Line("if (variables == null) throw new System.ArgumentNullException(\"variables\");");
                    writer.Line("_variables = variables;");
                    writer.Line("Initialize();");
                }
                writer.Line();
                using (writer.Block("public " + _rootClass + "(ETS.TwinCAT.Ads.PlcConnection connection)"))
                {
                    writer.Line("if (connection == null) throw new System.ArgumentNullException(\"connection\");");
                    writer.Line("_connection = connection;");
                    writer.Line("_variables = connection.VariablesProvider;");
                    writer.Line("Initialize();");
                }
                writer.Line();
                using (writer.Block("private void Initialize()"))
                {
                    var rootNames = UniqueMemberNames(roots.Select(x => x.Path.Split('.').Last()).ToList());
                    for (var i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        writer.Line(rootNames[i] + " = new " + WrapperType(root.Type, root.Dimensions) + "(_variables, _connection, \""
                            + Escape(SymbolAccessPath(root.Path, root.Type)) + "\"" + DimensionsArgument(root.Dimensions) + ");");
                    }
                }
                writer.Line();

                var names = UniqueMemberNames(roots.Select(x => x.Path.Split('.').Last()).ToList());
                for (var i = 0; i < roots.Count; i++)
                {
                    writer.Line("public " + WrapperType(roots[i].Type, roots[i].Dimensions) + " " + names[i] + " { get; private set; }");
                    writer.Line();
                }

                using (writer.Block("public static ETS.PlcVariables.Contracts.PlcContractManifest Contract"))
                {
                    writer.Line("get { return " + _rootClass + "Manifest.Value; }");
                }
            }
        }
        return writer.ToString();
    }

    private void EmitSymbolHelpers(CodeWriter writer)
    {
        using (writer.Block("public class PlcSymbol<T>"))
        {
            writer.Line("protected readonly ETS.TwinCAT.Interfaces.IVariablesProvider Variables;");
            writer.Line("protected readonly ETS.TwinCAT.Ads.PlcConnection Connection;");
            writer.Line("public PlcSymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path) : this(variables, null, path) { }");
            writer.Line("internal PlcSymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, ETS.TwinCAT.Ads.PlcConnection connection, string path)");
            writer.Line("{ if (variables == null) throw new System.ArgumentNullException(\"variables\"); Variables = variables; Connection = connection; Path = path; }");
            writer.Line("public string Path { get; private set; }");
            writer.Line("public T Read() { return Variables.ReadValue<T>(Path); }");
            writer.Line("public void Write(T value) { Variables.WriteValue<T>(Path, value); }");
            writer.Line("public override string ToString() { return Path; }");
        }
        writer.Line();

        if (_config.GenerateSubscriptions)
        {
            using (writer.Block("public sealed class PlcSubscribableSymbol<T> : PlcSymbol<T>, ETS.PlcVariables.IPlcSubscribableSymbol<T>"))
            {
                writer.Line("public PlcSubscribableSymbol(ETS.TwinCAT.Ads.PlcConnection connection, string path)");
                writer.Line("    : base(connection == null ? throw new System.ArgumentNullException(\"connection\") : connection.VariablesProvider, connection, path) { }");
                writer.Line("internal PlcSubscribableSymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, ETS.TwinCAT.Ads.PlcConnection connection, string path)");
                writer.Line("    : base(variables, connection, path) { }");
                writer.Line("public ETS.PlcVariables.PlcSubscription<T> Subscribe(System.EventHandler<ETS.PlcVariables.PlcVariableValueChangedEventArgs> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings = null)");
                writer.Line("{ return RequiredConnection().Subscribe<T>(Path, handler, settings); }");
                writer.Line("public ETS.PlcVariables.PlcSubscription<T> Subscribe(System.Action<T> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings = null)");
                writer.Line("{ return RequiredConnection().Subscribe<T>(Path, handler, settings); }");
                writer.Line("System.IDisposable ETS.PlcVariables.IPlcSubscribableSymbol<T>.Subscribe(System.EventHandler<ETS.PlcVariables.PlcVariableValueChangedEventArgs> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings)");
                writer.Line("{ return Subscribe(handler, settings); }");
                using (writer.Block("private ETS.TwinCAT.Ads.PlcConnection RequiredConnection()"))
                {
                    writer.Line("if (Connection == null) throw new System.InvalidOperationException(\"Subscribe requires a root constructed with PlcConnection.\");");
                    writer.Line("return Connection;");
                }
            }
            writer.Line();
        }

        using (writer.Block("public sealed class PlcArraySymbol<T>"))
        {
            writer.Line("private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;");
            writer.Line("private readonly ETS.TwinCAT.Ads.PlcConnection _connection;");
            writer.Line("private readonly int[] _lowerBounds;");
            writer.Line("private readonly int[] _upperBounds;");
            writer.Line("public PlcArraySymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path, int[] lowerBounds, int[] upperBounds)");
            writer.Line("    : this(variables, null, path, lowerBounds, upperBounds) { }");
            writer.Line("internal PlcArraySymbol(ETS.TwinCAT.Interfaces.IVariablesProvider variables, ETS.TwinCAT.Ads.PlcConnection connection, string path, int[] lowerBounds, int[] upperBounds)");
            writer.Line("{ _variables = variables; _connection = connection; Path = path; _lowerBounds = lowerBounds; _upperBounds = upperBounds; }");
            writer.Line("public string Path { get; private set; }");
            writer.Line("public int[] LowerBounds { get { return (int[])_lowerBounds.Clone(); } }");
            writer.Line("public int[] UpperBounds { get { return (int[])_upperBounds.Clone(); } }");
            using (writer.Block("public PlcSymbol<T> At(params int[] indices)"))
            {
                writer.Line("if (indices == null || indices.Length != _lowerBounds.Length) throw new System.ArgumentException(\"Wrong array rank.\", \"indices\");");
                writer.Line("for (int i = 0; i < indices.Length; i++) if (indices[i] < _lowerBounds[i] || indices[i] > _upperBounds[i]) throw new System.ArgumentOutOfRangeException(\"indices\");");
                writer.Line("return new PlcSymbol<T>(_variables, _connection, Path + \"[\" + string.Join(\",\", indices) + \"]\");");
            }
            writer.Line("public T ReadElement(params int[] indices) { return At(indices).Read(); }");
            writer.Line("public void WriteElement(T value, params int[] indices) { At(indices).Write(value); }");
        }
        writer.Line();
    }

    private void EmitNode(CodeWriter writer, PlcType type, bool isPointerNode)
    {
        var nodeName = TypeIdentifier(type) + (isPointerNode ? "PointerNode" : "Node");
        var rawDtoName = TypeIdentifier(type) + "RawDto";
        var canRaw = !isPointerNode && CanGenerateRawDto(type, new HashSet<string>());
        var bindable = _config.GenerateSubscriptions && canRaw;

        using (writer.Block("public sealed class " + nodeName + (bindable ? " : ETS.PlcVariables.IPlcSubscribableSymbol<" + rawDtoName + ">" : string.Empty)))
        {
            writer.Line("private readonly ETS.TwinCAT.Interfaces.IVariablesProvider _variables;");
            writer.Line("private readonly ETS.TwinCAT.Ads.PlcConnection _connection;");
            writer.Line("private readonly string _path;");
            writer.Line();

            using (writer.Block("public " + nodeName + "(ETS.TwinCAT.Interfaces.IVariablesProvider variables, string path)"))
            {
                writer.Line("if (variables == null) throw new System.ArgumentNullException(\"variables\");");
                writer.Line("_variables = variables;");
                writer.Line("_path = path;");
                writer.Line("Initialize();");
            }
            writer.Line();
            using (writer.Block("public " + nodeName + "(ETS.TwinCAT.Ads.PlcConnection connection, string path)"))
            {
                writer.Line("if (connection == null) throw new System.ArgumentNullException(\"connection\");");
                writer.Line("_connection = connection;");
                writer.Line("_variables = connection.VariablesProvider;");
                writer.Line("_path = path;");
                writer.Line("Initialize();");
            }
            writer.Line();
            using (writer.Block("internal " + nodeName + "(ETS.TwinCAT.Interfaces.IVariablesProvider variables, ETS.TwinCAT.Ads.PlcConnection connection, string path)"))
            {
                writer.Line("_variables = variables;");
                writer.Line("_connection = connection;");
                writer.Line("_path = path;");
                writer.Line("Initialize();");
            }
            writer.Line();

            using (writer.Block("private void Initialize()"))
            {
                var names = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
                for (var i = 0; i < type.Fields.Count; i++)
                {
                    var field = type.Fields[i];
                    writer.Line(names[i] + " = new " + WrapperType(field.Type, field.Dimensions) + "(_variables, _connection, _path + \""
                        + Escape(FieldAccessSuffix(field)) + "\"" + DimensionsArgument(field.Dimensions) + ");");
                }
            }
            writer.Line();
            writer.Line("public string Path { get { return _path; } }");
            writer.Line();

            if (canRaw)
            {
                writer.Line("public " + rawDtoName + " ReadRaw() { return _variables.ReadValue<" + rawDtoName + ">(_path); }");
                writer.Line("public void WriteRaw(" + rawDtoName + " value) { _variables.WriteValue<" + rawDtoName + ">(_path, value); }");
                if (_config.GenerateSubscriptions)
                {
                    writer.Line(rawDtoName + " ETS.PlcVariables.IPlcSubscribableSymbol<" + rawDtoName + ">.Read()");
                    writer.Line("{ return ReadRaw(); }");
                    writer.Line("void ETS.PlcVariables.IPlcSubscribableSymbol<" + rawDtoName + ">.Write(" + rawDtoName + " value)");
                    writer.Line("{ WriteRaw(value); }");
                    writer.Line("public ETS.PlcVariables.PlcSubscription<" + rawDtoName + "> Subscribe(System.EventHandler<ETS.PlcVariables.PlcVariableValueChangedEventArgs> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings = null)");
                    writer.Line("{ return RequiredConnection().Subscribe<" + rawDtoName + ">(_path, handler, settings); }");
                    writer.Line("public ETS.PlcVariables.PlcSubscription<" + rawDtoName + "> Subscribe(System.Action<" + rawDtoName + "> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings = null)");
                    writer.Line("{ return RequiredConnection().Subscribe<" + rawDtoName + ">(_path, handler, settings); }");
                    writer.Line("System.IDisposable ETS.PlcVariables.IPlcSubscribableSymbol<" + rawDtoName + ">.Subscribe(System.EventHandler<ETS.PlcVariables.PlcVariableValueChangedEventArgs> handler, ETS.TwinCAT.Ads.AdsVariableSettings settings)");
                    writer.Line("{ return Subscribe(handler, settings); }");
                    using (writer.Block("private ETS.TwinCAT.Ads.PlcConnection RequiredConnection()"))
                    {
                        writer.Line("if (_connection == null) throw new System.InvalidOperationException(\"Subscribe requires a root constructed with PlcConnection.\");");
                        writer.Line("return _connection;");
                    }
                }
                writer.Line();
            }

            var propertyNames = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
            for (var i = 0; i < type.Fields.Count; i++)
                writer.Line("public " + WrapperType(type.Fields[i].Type, type.Fields[i].Dimensions) + " " + propertyNames[i] + " { get; private set; }");
        }
        writer.Line();
    }

    private string EmitDtos()
    {
        var writer = Header();
        using (writer.Block("namespace " + _config.Namespace))
        {
            var referenced = ReachableTypes(Items.Select(x => x.Type));
            foreach (var type in referenced.Where(x => x.EnumValues.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
            {
                using (writer.Block("public enum " + TypeIdentifier(type) + " : " + EnumBase(type)))
                {
                    var names = UniqueNames(type.EnumValues.Select(x => x.Name).ToList());
                    for (var i = 0; i < type.EnumValues.Count; i++)
                        writer.Line(names[i] + " = " + type.EnumValues[i].Value.ToString(CultureInfo.InvariantCulture) + (i + 1 == type.EnumValues.Count ? string.Empty : ","));
                }
                writer.Line();
            }

            foreach (var type in referenced.Where(x => x.Fields.Count > 0).OrderBy(x => x.QualifiedName, StringComparer.Ordinal))
            {
                EmitLogicalDto(writer, type);
                if (CanGenerateRawDto(type, new HashSet<string>()))
                    EmitRawDto(writer, type);
            }
        }
        return writer.ToString();
    }

    private void EmitLogicalDto(CodeWriter writer, PlcType type)
    {
        using (writer.Block("public sealed class " + TypeIdentifier(type) + "Dto"))
        {
            var names = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
            for (var i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                writer.Line("public " + CsLogicalTypeForLogical(field.Type, field.Dimensions) + " " + names[i] + " { get; set; }");
                writer.Line();
            }
        }
        writer.Line();
    }

    private void EmitRawDto(CodeWriter writer, PlcType type)
    {
        writer.Line("[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = " + ((type.BitSize ?? 0) / 8) + ")]");
        using (writer.Block("public struct " + TypeIdentifier(type) + "RawDto"))
        {
            var names = UniqueMemberNames(type.Fields.Select(x => x.Name).ToList());
            for (var i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                writer.Line("[System.Runtime.InteropServices.FieldOffset(" + ((field.BitOffset ?? 0) / 8) + ")]");
                if (string.Equals(field.Type.Name, "BOOL", StringComparison.OrdinalIgnoreCase))
                    writer.Line("[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.I1)]");
                writer.Line("public " + CsRawType(field.Type) + " " + names[i] + ";");
                writer.Line();
            }
        }
        writer.Line();
    }

    private string EmitManifest()
    {
        var hash = ContractGenerator.Sha256(Items);
        var writer = Header();
        using (writer.Block("namespace " + _config.Namespace))
        {
            using (writer.Block("internal static class " + _rootClass + "Manifest"))
            {
                writer.Line("internal static readonly ETS.PlcVariables.Contracts.PlcContractManifest Value = new ETS.PlcVariables.Contracts.PlcContractManifest");
                writer.Line("{");
                writer.Line("    TmcName = \"" + Escape(_tmcName) + "\",");
                writer.Line("    ContractHash = \"" + hash + "\",");
                writer.Line("    Symbols = new ETS.PlcVariables.Contracts.PlcContractSymbol[]");
                writer.Line("    {");
                foreach (var item in Items)
                {
                    writer.Line("        new ETS.PlcVariables.Contracts.PlcContractSymbol { Path = \"" + Escape(item.Path)
                        + "\", PlcTypeName = \"" + Escape(item.PlcTypeName)
                        + "\", CSharpTypeName = \"" + Escape(item.CSharpTypeName)
                        + "\", Size = " + (item.Size?.ToString(CultureInfo.InvariantCulture) ?? "null")
                        + ", Kind = \"" + item.Kind.ToString().ToLowerInvariant()
                        + "\", Comment = " + (item.Comment == null ? "null" : "\"" + Escape(item.Comment) + "\"")
                        + ", BinaryLayoutReliable = " + (item.BinaryLayoutReliable ? "true" : "false")
                        + ", Dimensions = new ETS.PlcVariables.Contracts.PlcArrayDimension[] { " + DimensionsInitializer(item.Dimensions) + " } },");
                }
                writer.Line("    }");
                writer.Line("};");
            }
        }
        return writer.ToString();
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

    private List<PlcType> ReachablePointerStructTypes(IEnumerable<TypeReference> references)
    {
        var result = new Dictionary<string, PlcType>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(TypeReference reference)
        {
            var type = _model.Resolve(reference);
            if (reference.PointerLevel > 0 && type?.Fields.Count > 0)
                result.TryAdd(type.QualifiedName, type);
            if (type == null || !visited.Add(type.QualifiedName)) return;
            foreach (var field in type.Fields) Visit(field.Type);
        }
        foreach (var reference in references) Visit(reference);
        return result.Values.ToList();
    }

    private bool CanGenerateRawDto(PlcType type, HashSet<string> stack)
    {
        if (_layout.TryGetValue(type.QualifiedName, out var cached)) return cached;
        if (type.IsSyntheticNamespace || !stack.Add(type.QualifiedName) || !type.BitSize.HasValue || type.BitSize.Value % 8 != 0 || type.Fields.Count == 0)
            return _layout[type.QualifiedName] = false;

        foreach (var field in type.Fields)
        {
            if (field.Type.PointerLevel > 0 || field.Dimensions.Count > 0 || !field.BitOffset.HasValue || field.BitOffset.Value % 8 != 0 || !field.BitSize.HasValue || field.BitSize.Value % 8 != 0)
                return _layout[type.QualifiedName] = false;
            var nested = _model.Resolve(field.Type);
            if (nested != null && nested.EnumValues.Count == 0 && !CanGenerateRawDto(nested, stack))
                return _layout[type.QualifiedName] = false;
            if (nested == null && (!PrimitiveTypes.ContainsKey(BaseName(field.Type.Name)) || IsString(field.Type.Name)))
                return _layout[type.QualifiedName] = false;
        }

        stack.Remove(type.QualifiedName);
        return _layout[type.QualifiedName] = true;
    }

    private PlcTypeKind Kind(TypeReference reference, PlcType? type, List<PlcArrayDimension> dimensions)
    {
        if (reference.PointerLevel > 0 && dimensions.Count == 0) return PlcTypeKind.Pointer;
        if (dimensions.Count > 0) return PlcTypeKind.Array;
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
        if (_unknown.Add(reference.QualifiedName))
            Warnings.Add("Unknown PLC type '" + reference.QualifiedName + "'. Symbols use object wrappers and have no binary layout.");
        return PlcTypeKind.Unknown;
    }

    private void UnsupportedPointer(TypeReference reference)
    {
        if (_unsupportedPointers.Add(reference.QualifiedName))
            Warnings.Add("Unsupported pointer target PLC type '" + reference.QualifiedName + "'. Pointer symbols use object wrappers and have no binary layout.");
    }

    private bool IsSupportedPointerTarget(TypeReference reference, PlcType? type)
    {
        if (type?.Fields.Count > 0 || type?.EnumValues.Count > 0) return true;
        return IsString(reference.Name) || PrimitiveTypes.ContainsKey(BaseName(reference.Name));
    }

    private string WrapperType(TypeReference reference, List<PlcArrayDimension> dimensions)
    {
        var type = _model.Resolve(reference);
        if (reference.PointerLevel > 0 && dimensions.Count == 0)
        {
            if (type?.Fields.Count > 0) return TypeIdentifier(type) + "PointerNode";
            var pointerKind = Kind(new TypeReference { Name = reference.Name, Namespace = reference.Namespace, Guid = reference.Guid }, type, dimensions);
            if (_config.GenerateSubscriptions && (pointerKind == PlcTypeKind.Primitive || pointerKind == PlcTypeKind.Enum))
                return "PlcSubscribableSymbol<" + CsLogicalType(reference) + ">";
            return "PlcSymbol<" + CsLogicalType(reference) + ">";
        }
        if (dimensions.Count > 0) return "PlcArraySymbol<" + CsLogicalType(reference) + ">";
        if (type?.Fields.Count > 0) return TypeIdentifier(type) + "Node";
        var kind = Kind(reference, type, dimensions);
        if (_config.GenerateSubscriptions && (kind == PlcTypeKind.Primitive || kind == PlcTypeKind.Enum))
            return "PlcSubscribableSymbol<" + CsLogicalType(reference) + ">";
        return "PlcSymbol<" + CsLogicalType(reference) + ">";
    }

    private string CsLogicalType(TypeReference reference)
    {
        if (IsString(reference.Name)) return "string";
        if (PrimitiveTypes.TryGetValue(BaseName(reference.Name), out var primitive)) return primitive;
        var type = _model.Resolve(reference);
        if (type?.EnumValues.Count > 0) return TypeIdentifier(type);
        if (type?.Fields.Count > 0) return TypeIdentifier(type) + "Dto";
        if (type != null && !string.IsNullOrEmpty(type.BaseType.Name)) return CsLogicalType(type.BaseType);
        return "object";
    }

    private string CsRawType(TypeReference reference)
    {
        if (PrimitiveTypes.TryGetValue(BaseName(reference.Name), out var primitive)) return primitive;
        var type = _model.Resolve(reference);
        if (type?.EnumValues.Count > 0) return TypeIdentifier(type);
        if (type?.Fields.Count > 0) return TypeIdentifier(type) + "RawDto";
        if (type != null && !string.IsNullOrEmpty(type.BaseType.Name)) return CsRawType(type.BaseType);
        return "object";
    }

    private string CsLogicalTypeForLogical(TypeReference reference, List<PlcArrayDimension> dimensions)
    {
        return CsLogicalType(reference) + (dimensions.Count == 0 ? string.Empty : "[]");
    }

    private string PlcName(TypeReference reference) => string.IsNullOrEmpty(reference.QualifiedName) ? "UNKNOWN" : reference.QualifiedName;
    private static bool IsString(string name) => BaseName(name).StartsWith("STRING", StringComparison.OrdinalIgnoreCase) || BaseName(name).StartsWith("WSTRING", StringComparison.OrdinalIgnoreCase);
    private static string BaseName(string name) => Regex.Replace((name ?? string.Empty).Trim(), "\\s*\\(.*\\)$", string.Empty).ToUpperInvariant();
    private string EnumBase(PlcType type) => PrimitiveTypes.TryGetValue(BaseName(type.BaseType.Name), out var value) && value != "bool" ? value : "int";

    private string TypeIdentifier(PlcType type)
    {
        var duplicate = _model.TypesByName.Values.Distinct().Count(x => string.Equals(x.Name, type.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        return Identifier(duplicate ? type.QualifiedName.Replace('.', '_') : type.Name);
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

    private CodeWriter Header()
    {
        var writer = new CodeWriter();
        writer.Line("// <auto-generated />");
        writer.Line("// Generated from: " + _tmcName);
        writer.Line("// Do not edit manually.");
        writer.Line();
        return writer;
    }

    private static string SymbolAccessPath(string path, TypeReference reference)
    {
        return reference.PointerLevel > 0 ? path + "^" : path;
    }

    private static string FieldAccessSuffix(PlcField field)
    {
        return "." + field.Name + (field.Type.PointerLevel > 0 && field.Dimensions.Count == 0 ? "^" : string.Empty);
    }

    private static string DimensionsArgument(List<PlcArrayDimension> dimensions)
    {
        if (dimensions.Count == 0) return string.Empty;
        return ", new int[] { " + string.Join(", ", dimensions.Select(x => x.LowerBound)) + " }, new int[] { "
            + string.Join(", ", dimensions.Select(x => x.UpperBound)) + " }";
    }

    private static string DimensionsInitializer(List<PlcArrayDimension> dimensions)
    {
        return string.Concat(dimensions.Select(dim => "new ETS.PlcVariables.Contracts.PlcArrayDimension { LowerBound = "
            + dim.LowerBound + ", UpperBound = " + dim.UpperBound + " }, "));
    }

    private static string Pascal(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
    private static string ShortHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))).Substring(0, 8);
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");

    private static readonly HashSet<string> Keywords = new(new[]
    {
        "class", "struct", "enum", "event", "namespace", "public", "private", "internal", "void",
        "string", "object", "base", "this", "new", "return", "params", "ref", "out", "in",
        "is", "as", "operator", "implicit", "explicit", "interface", "delegate", "readonly",
        "sealed", "partial", "static", "bool", "byte", "short", "int", "long", "float", "double"
    }, StringComparer.Ordinal);
}
