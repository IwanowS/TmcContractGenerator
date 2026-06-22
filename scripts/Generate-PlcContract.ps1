[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Config
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\TmcContractGenerator\TmcContractGenerator.csproj'
$configPath = [System.IO.Path]::GetFullPath($Config)

if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "TMC contract config was not found: $configPath"
}

& dotnet run --project $project --configuration Release -- --config $configPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
