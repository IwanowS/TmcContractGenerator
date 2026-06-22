using System.Globalization;
using System.Xml.Linq;

namespace TmcContractGenerator;

internal sealed class TmcModel
{
    public List<PlcSymbol> Symbols { get; } = new();
    public Dictionary<string, PlcType> TypesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PlcType> TypesByGuid { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PlcType? Resolve(TypeReference reference)
    {
        if (!string.IsNullOrEmpty(reference.Guid) && TypesByGuid.TryGetValue(reference.Guid, out var byGuid))
            return byGuid;
        TypesByName.TryGetValue(reference.QualifiedName, out var qualified);
        if (qualified != null) return qualified;
        TypesByName.TryGetValue(reference.Name, out var simple);
        return simple;
    }
}

internal static class TmcParser
{
    public static TmcModel Parse(string path)
    {
        XDocument document;
        try { document = XDocument.Load(path, LoadOptions.PreserveWhitespace); }
        catch (Exception ex) { throw new GeneratorException("Cannot read TMC XML '" + path + "'.", ex); }

        var model = new TmcModel();
        foreach (var element in document.Descendants().Where(x => x.Name.LocalName == "DataType"))
        {
            var nameElement = Child(element, "Name");
            if (nameElement == null || string.IsNullOrWhiteSpace(nameElement.Value)) continue;
            var ns = Attr(nameElement, "Namespace");
            var name = nameElement.Value.Trim();
            var qualified = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
            var type = new PlcType
            {
                Name = name,
                QualifiedName = qualified,
                Guid = Attr(nameElement, "GUID"),
                BitSize = Int(Child(element, "BitSize")),
                BaseType = Reference(Child(element, "BaseType"))
            };
            foreach (var field in element.Elements().Where(x => x.Name.LocalName == "SubItem"))
                type.Fields.Add(ParseField(field));
            foreach (var enumElement in element.Elements().Where(x => x.Name.LocalName == "EnumInfo"))
            {
                var text = Child(enumElement, "Text")?.Value;
                var value = Long(Child(enumElement, "Enum"));
                if (!string.IsNullOrWhiteSpace(text) && value.HasValue)
                    type.EnumValues.Add(new PlcEnumValue { Name = text.Trim(), Value = value.Value });
            }
            model.TypesByName[qualified] = type;
            if (!model.TypesByName.ContainsKey(name)) model.TypesByName[name] = type;
            if (!string.IsNullOrEmpty(type.Guid)) model.TypesByGuid[type.Guid] = type;
        }

        foreach (var element in document.Descendants().Where(x => x.Name.LocalName == "Symbol"))
        {
            var name = Child(element, "Name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) continue;
            model.Symbols.Add(new PlcSymbol
            {
                Path = name.Trim(),
                Type = Reference(Child(element, "BaseType")),
                BitSize = Int(Child(element, "BitSize")),
                Comment = Child(element, "Comment")?.Value.Trim(),
                Dimensions = Dimensions(element)
            });
        }
        return model;
    }

    private static PlcField ParseField(XElement element) => new()
    {
        Name = Child(element, "Name")?.Value.Trim() ?? string.Empty,
        Type = Reference(Child(element, "Type")),
        BitSize = Int(Child(element, "BitSize")),
        BitOffset = Int(Child(element, "BitOffs")),
        Comment = Child(element, "Comment")?.Value.Trim(),
        Dimensions = Dimensions(element)
    };

    private static List<PlcArrayDimension> Dimensions(XElement parent)
    {
        var result = new List<PlcArrayDimension>();
        foreach (var array in parent.Elements().Where(x => x.Name.LocalName == "ArrayInfo"))
        {
            var lower = Int(Child(array, "LBound")) ?? 0;
            var elements = Int(Child(array, "Elements")) ?? 0;
            result.Add(new PlcArrayDimension { LowerBound = lower, UpperBound = lower + elements - 1 });
        }
        return result;
    }

    private static TypeReference Reference(XElement? element) => element == null ? new TypeReference() : new TypeReference
    {
        Name = element.Value.Trim(),
        Namespace = Attr(element, "Namespace"),
        Guid = Attr(element, "GUID"),
        PointerLevel = int.TryParse(Attr(element, "PointerTo"), out var p) ? p : 0
    };

    private static XElement? Child(XElement parent, string name) => parent.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static string? Attr(XElement element, string name) => element.Attributes().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
    private static int? Int(XElement? element) => int.TryParse(element?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static long? Long(XElement? element) => long.TryParse(element?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
