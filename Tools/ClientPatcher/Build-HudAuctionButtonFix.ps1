[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$OriginalModulePath,

    [string]$PythonPath = 'python',

    [string]$OutputPath = (Join-Path $PSScriptRoot 'Compiled\hud_auction_button.alb')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedOriginalSha256 = '99677CFFF60DAE8509E1359AC0ABADC8402D987F42AB53248AC2AF46401C2DEE'
$chunkToolPath = Join-Path $PSScriptRoot 'lua51_chunk.py'
$resolvedOriginalPath = (Resolve-Path -LiteralPath $OriginalModulePath).Path
$actualOriginalSha256 = (Get-FileHash -LiteralPath $resolvedOriginalPath -Algorithm SHA256).Hash
if ($actualOriginalSha256 -ne $expectedOriginalSha256) {
    throw "right_button_set.alb does not match ArcheAge r208022. Expected $expectedOriginalSha256, found $actualOriginalSha256."
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    [void](New-Item -ItemType Directory -Path $outputDirectory)
}

& $PythonPath $chunkToolPath patch-hud-auction-button $resolvedOriginalPath $OutputPath
if ($LASTEXITCODE -ne 0) {
    throw "HUD auction-button patch build failed with exit code $LASTEXITCODE."
}

$compiled = Get-Item -LiteralPath $OutputPath
Write-Host "Built $($compiled.FullName)"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $compiled.FullName -Algorithm SHA256).Hash)"
