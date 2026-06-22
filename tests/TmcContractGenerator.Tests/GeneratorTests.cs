using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace TmcContractGenerator.Tests;

[TestClass]
public sealed class GeneratorTests
{
    private string _directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "TmcContractGeneratorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Simple.tmc"), Path.Combine(_directory, "MachinePlc.tmc"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, true);

    [TestMethod]
    public void GeneratesRootWrappersDtosManifestAndBounds()
    {
        var config = WriteConfig(new[] { "GVL_HMI.HMI" });
        var result = ContractGenerator.Generate(config);
        Assert.AreEqual(2, result.FoundSymbols);
        Assert.AreEqual("GVL_HMI.HMI", result.Roots.Single());
        var wrappers = Read("Generated/PlcRoot_MachinePlc.g.cs");
        var dto = Read("Generated/PlcRoot_MachinePlc.Dto.g.cs");
        var manifest = Read("Generated/PlcRoot_MachinePlc.Manifest.g.cs");
        StringAssert.Contains(wrappers, "public sealed partial class PlcRoot_MachinePlc");
        StringAssert.Contains(wrappers, "public PlcRoot_MachinePlc(ETS.TwinCAT.Ads.PlcConnection connection)");
        StringAssert.Contains(wrappers, "public PlcSubscribableSymbol<bool> IsReady");
        StringAssert.Contains(wrappers, "public PlcSymbol<string> Title");
        StringAssert.Contains(wrappers, "public PlcArraySymbol<string> Names");
        StringAssert.Contains(wrappers, "new int[] { 1, -1 }, new int[] { 2, 0 }");
        StringAssert.Contains(dto, "LayoutKind.Explicit");
        StringAssert.Contains(dto, "public sealed class StHmiDto");
        StringAssert.Contains(dto, "Class = 1");
        StringAssert.Contains(manifest, "BinaryLayoutReliable = false");

        StringAssert.Contains(wrappers, "PlcSubscription<StStateDto> Subscribe");
        Assert.IsFalse(wrappers.Contains("PlcSubscription<StHmiDto>"));
    }

    [TestMethod]
    public void EmptyRootsSelectAllTopLevelSymbols()
    {
        var result = ContractGenerator.Generate(WriteConfig(Array.Empty<string>()));
        CollectionAssert.AreEquivalent(new[] { "GVL_HMI.HMI", "GVL_HMI.Counter" }, result.Roots);
    }

    [TestMethod]
    public void NamespaceRootCreatesSyntheticParentNode()
    {
        var result = ContractGenerator.Generate(WriteConfig(new[] { "GVL_HMI" }));
        Assert.AreEqual("GVL_HMI", result.Roots.Single());
        var wrappers = Read("Generated/PlcRoot_MachinePlc.g.cs");
        StringAssert.Contains(wrappers, "public TmcNamespace");
        StringAssert.Contains(wrappers, " GvlHmi { get; private set; }");
        StringAssert.Contains(wrappers, " HMI { get; private set; }");
        var manifest = Read("Generated/PlcRoot_MachinePlc.Manifest.g.cs");
        StringAssert.Contains(manifest, "Path = \"GVL_HMI\"");
        StringAssert.Contains(manifest, "Kind = \"namespace\"");
        StringAssert.Contains(manifest, "Path = \"GVL_HMI.HMI.State.is-ready\"");
        StringAssert.Contains(manifest, "Path = \"GVL_HMI.Counter\"");
    }

    [TestMethod]
    public void GenerationIsByteStableAndDoesNotTouchUnchangedFiles()
    {
        var config = WriteConfig(new[] { "GVL_HMI.HMI" });
        var first = ContractGenerator.Generate(config);
        var bytes = first.Files.ToDictionary(x => x, File.ReadAllBytes);
        var times = first.Files.ToDictionary(x => x, File.GetLastWriteTimeUtc);
        Thread.Sleep(1100);
        var second = ContractGenerator.Generate(config);
        foreach (var file in second.Files)
        {
            CollectionAssert.AreEqual(bytes[file], File.ReadAllBytes(file));
            Assert.AreEqual(times[file], File.GetLastWriteTimeUtc(file));
        }
    }

    [TestMethod]
    public void MissingRootFails()
    {
        var exception = Assert.ThrowsException<GeneratorException>(() => ContractGenerator.Generate(WriteConfig(new[] { "Missing.Root" })));
        StringAssert.Contains(exception.Message, "Configured root symbol or namespace not found");
    }

    [TestMethod]
    public void UnknownPrimitiveCanBePromotedToError()
    {
        var tmc = Path.Combine(_directory, "MachinePlc.tmc");
        File.WriteAllText(tmc, File.ReadAllText(tmc).Replace("<t:Type>REAL</t:Type>", "<t:Type>MYSTERY</t:Type>"));
        var configPath = WriteConfig(new[] { "GVL_HMI.HMI" });
        var config = JsonSerializer.Deserialize<GeneratorConfig>(File.ReadAllText(configPath))!;
        config.UnknownTypeIsError = true;
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        StringAssert.Contains(Assert.ThrowsException<GeneratorException>(() => ContractGenerator.Generate(configPath)).Message, "Unknown PLC type");
    }

    [TestMethod]
    public void OutputDirectoryCannotBeSharedByDifferentTmcFiles()
    {
        ContractGenerator.Generate(WriteConfig(new[] { "GVL_HMI.HMI" }));
        File.Copy(Path.Combine(_directory, "MachinePlc.tmc"), Path.Combine(_directory, "Other.tmc"));
        var config = new GeneratorConfig
        {
            Tmc = "Other.tmc", Namespace = "Other.Generated", Roots = new[] { "GVL_HMI.HMI" }, Output = "Generated",
            GenerateDto = true, GenerateManifest = true, GenerateWrappers = true
        };
        var path = Path.Combine(_directory, "other.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config));
        StringAssert.Contains(Assert.ThrowsException<GeneratorException>(() => ContractGenerator.Generate(path)).Message, "already owned");
    }

    [TestMethod]
    public void RemovesObsoleteOwnedGeneratedFiles()
    {
        var output = Path.Combine(_directory, "Generated");
        Directory.CreateDirectory(output);
        var obsolete = Path.Combine(output, "PlcOldName.g.cs");
        File.WriteAllText(obsolete, "// <auto-generated />\n// Generated from: MachinePlc.tmc\n// Do not edit manually.\n");
        ContractGenerator.Generate(WriteConfig(new[] { "GVL_HMI.HMI" }));
        Assert.IsFalse(File.Exists(obsolete));
        Assert.IsTrue(File.Exists(Path.Combine(output, "PlcRoot_MachinePlc.g.cs")));
    }

    [TestMethod]
    public void SubscriptionApiIsOptIn()
    {
        var configPath = WriteConfig(new[] { "GVL_HMI.HMI" });
        var config = JsonSerializer.Deserialize<GeneratorConfig>(File.ReadAllText(configPath))!;
        config.GenerateSubscriptions = false;
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        ContractGenerator.Generate(configPath);
        var wrappers = Read("Generated/PlcRoot_MachinePlc.g.cs");
        Assert.IsFalse(wrappers.Contains("PlcSubscription"));
        Assert.IsFalse(wrappers.Contains("PlcSubscribableSymbol"));
    }

    private string WriteConfig(string[] roots)
    {
        var path = Path.Combine(_directory, "contract.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new GeneratorConfig
        {
            Tmc = "MachinePlc.tmc", Namespace = "Example.Generated", Roots = roots, Output = "Generated",
            GenerateDto = true, GenerateManifest = true, GenerateWrappers = true, GenerateSubscriptions = true
        }));
        return path;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_directory, relative.Replace('/', Path.DirectorySeparatorChar)));

}
