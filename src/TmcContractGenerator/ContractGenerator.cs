using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TmcContractGenerator;

public static class ContractGenerator
{
    public static GenerationResult Generate(string configPath)
    {
        if (!File.Exists(configPath)) throw new GeneratorException("Config not found: " + configPath);
        GeneratorConfig config;
        try
        {
            config = JsonSerializer.Deserialize<GeneratorConfig>(File.ReadAllText(configPath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            }) ?? throw new GeneratorException("Config is empty.");
        }
        catch (JsonException ex) { throw new GeneratorException("Invalid generator config: " + configPath, ex); }
        if (string.IsNullOrWhiteSpace(config.Tmc) || string.IsNullOrWhiteSpace(config.Namespace) || string.IsNullOrWhiteSpace(config.Output))
            throw new GeneratorException("Config must define tmc, namespace and output.");
        if (config.GenerateWrappers && (!config.GenerateDto || !config.GenerateManifest))
            throw new GeneratorException("generateWrappers requires generateDto and generateManifest.");

        var configDirectory = Path.GetDirectoryName(configPath)!;
        var tmcPath = Resolve(configDirectory, config.Tmc);
        var outputPath = Resolve(configDirectory, config.Output);
        if (!File.Exists(tmcPath)) throw new GeneratorException("TMC not found: " + tmcPath);
        Directory.CreateDirectory(outputPath);
        EnsureOutputOwnership(outputPath, Path.GetFileName(tmcPath));
        var model = TmcParser.Parse(tmcPath);
        var roots = SelectRoots(model, config.Roots ?? Array.Empty<string>());
        var emitter = new CSharpEmitter(model, config, Path.GetFileName(tmcPath));
        var output = emitter.Emit(roots);
        if (config.UnknownTypeIsError && emitter.Warnings.Any(x => x.StartsWith("Unknown PLC type", StringComparison.Ordinal)))
            throw new GeneratorException(string.Join(Environment.NewLine, emitter.Warnings));

        var files = new List<string>();
        foreach (var file in output)
        {
            var path = Path.Combine(outputPath, file.Key);
            WriteIfChanged(path, file.Value);
            files.Add(path);
        }
        CleanOwnedFiles(outputPath, files, Path.GetFileName(tmcPath));
        return new GenerationResult
        {
            FoundSymbols = model.Symbols.Count,
            GeneratedSymbols = emitter.Items.Count,
            Roots = roots.Select(x => x.Path).ToArray(),
            Warnings = emitter.Warnings.ToArray(),
            Files = files.ToArray()
        };
    }

    private static List<PlcSymbol> SelectRoots(TmcModel model, string[] configured)
    {
        var symbols = model.Symbols;
        if (configured.Length > 0)
        {
            var selected = new List<PlcSymbol>();
            foreach (var root in configured)
            {
                var exact = symbols.FirstOrDefault(x => string.Equals(x.Path, root, StringComparison.Ordinal));
                if (exact != null)
                {
                    selected.Add(exact);
                    continue;
                }

                var descendants = symbols.Where(x => x.Path.StartsWith(root + ".", StringComparison.Ordinal)).ToList();
                if (descendants.Count == 0)
                    throw new GeneratorException("Configured root symbol or namespace not found: " + root);
                selected.Add(CreateNamespaceRoot(model, root, descendants));
            }
            return selected;
        }
        return symbols.Where(symbol => !symbols.Any(other => !ReferenceEquals(symbol, other) && symbol.Path.StartsWith(other.Path + ".", StringComparison.Ordinal)))
            .OrderBy(x => x.Path, StringComparer.Ordinal).ToList();
    }

    private static PlcSymbol CreateNamespaceRoot(TmcModel model, string path, List<PlcSymbol> descendants)
    {
        var type = CreateNamespaceType(model, path, descendants);
        return new PlcSymbol
        {
            Path = path,
            Type = new TypeReference { Name = type.QualifiedName },
            BitSize = null,
            Comment = "Synthetic node generated from TwinCAT symbol namespace."
        };
    }

    private static PlcType CreateNamespaceType(TmcModel model, string path, List<PlcSymbol> descendants)
    {
        var typeName = "__TmcNamespace_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).Substring(0, 12);
        if (model.TypesByName.TryGetValue(typeName, out var existing)) return existing;

        var type = new PlcType { Name = typeName, QualifiedName = typeName, IsSyntheticNamespace = true };
        model.TypesByName[typeName] = type;
        var prefixLength = path.Length + 1;
        foreach (var group in descendants.GroupBy(x => FirstPathSegment(x.Path.Substring(prefixLength)), StringComparer.Ordinal)
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var childPath = path + "." + group.Key;
            var exact = group.FirstOrDefault(x => string.Equals(x.Path, childPath, StringComparison.Ordinal));
            if (exact != null)
            {
                type.Fields.Add(new PlcField
                {
                    Name = group.Key,
                    Type = exact.Type,
                    BitSize = exact.BitSize,
                    Comment = exact.Comment,
                    Dimensions = exact.Dimensions
                });
                continue;
            }

            var nested = CreateNamespaceType(model, childPath, group.ToList());
            type.Fields.Add(new PlcField
            {
                Name = group.Key,
                Type = new TypeReference { Name = nested.QualifiedName },
                Comment = "Synthetic node generated from TwinCAT symbol namespace."
            });
        }
        return type;
    }

    private static string FirstPathSegment(string relativePath)
    {
        var separator = relativePath.IndexOf('.');
        return separator < 0 ? relativePath : relativePath.Substring(0, separator);
    }

    private static string Resolve(string directory, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));

    private static void WriteIfChanged(string path, string content)
    {
        content = content.Replace("\r\n", "\n");
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    private static void CleanOwnedFiles(string output, IEnumerable<string> expected, string tmcName)
    {
        var expectedSet = new HashSet<string>(expected.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(output, "*.g.cs"))
        {
            if (expectedSet.Contains(Path.GetFullPath(path))) continue;
            string header;
            using (var reader = new StreamReader(path))
                header = (reader.ReadLine() ?? "") + "\n" + (reader.ReadLine() ?? "");
            if (header.Contains("<auto-generated", StringComparison.Ordinal) && header.Contains("Generated from: " + tmcName, StringComparison.Ordinal))
                File.Delete(path);
        }
    }

    private static void EnsureOutputOwnership(string output, string tmcName)
    {
        foreach (var path in Directory.EnumerateFiles(output, "*.g.cs"))
        {
            using var reader = new StreamReader(path);
            var first = reader.ReadLine() ?? string.Empty;
            var second = reader.ReadLine() ?? string.Empty;
            if (first.Contains("<auto-generated", StringComparison.Ordinal) && second.StartsWith("// Generated from: ", StringComparison.Ordinal)
                && !string.Equals(second, "// Generated from: " + tmcName, StringComparison.Ordinal))
                throw new GeneratorException("Output directory is already owned by another TMC contract: " + output);
        }
    }

    internal static string Sha256(IEnumerable<ContractItem> items)
    {
        var canonical = string.Join("\n", items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .Select(x => string.Join("|", x.Path, x.PlcTypeName, x.CSharpTypeName, x.Size?.ToString() ?? "", x.Kind.ToString().ToLowerInvariant())));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
