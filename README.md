# TmcContractGenerator

Deterministic C# PLC/HMI contract generation from a TwinCAT `.tmc` file.

Each consuming SDK-style project owns its TMC, JSON config, and generated files:

```xml
<ItemGroup>
  <TmcContract Include="Contracts\MachinePlc.json" />
</ItemGroup>
<Import Project="..\..\tools\TmcContractGenerator\build\TmcContractGenerator.targets" />
```

Generated output must be under `$(TmcContractGeneratedRoot)`, which defaults to the project's `Generated` directory. Use a distinct output directory and namespace for each config.

Run manually with Windows PowerShell:

```powershell
.\scripts\Generate-PlcContract.ps1 -Config C:\path\contract.json
```
