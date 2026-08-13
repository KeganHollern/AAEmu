param(
    [string]$Configuration = "Content/content-studio.json",
    [string]$Url = "http://127.0.0.1:5188"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$configurationPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Configuration))
if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "Content Studio configuration not found: $configurationPath"
}

$env:AAEMU_CONTENT_CONFIG = $configurationPath
Write-Host "AAEmu Content Studio: $Url"
Write-Host "Configuration: $configurationPath"
dotnet run --project (Join-Path $repositoryRoot "Tools/AAEmu.ContentStudio.Designer/AAEmu.ContentStudio.Designer.csproj") --urls $Url
