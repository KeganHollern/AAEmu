[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$LuacPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$OriginalModulePath,

    [string]$PythonPath = 'python',

    [string]$OutputPath = (Join-Path $PSScriptRoot 'Compiled\windowed_fullscreen_screen_option.alb')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedOriginalSha256 = 'EAE4CF81493E82D48B4B6350B48D4FF89C66A175EBA64D2BEC96409934904A25'
$sourcePath = Join-Path $PSScriptRoot 'Sources\windowed_fullscreen_screen_mode.lua'
$cameraSourcePath = Join-Path $PSScriptRoot 'Sources\camera_controls_screen.lua'
$chunkToolPath = Join-Path $PSScriptRoot 'lua51_chunk.py'
$resolvedOriginalPath = (Resolve-Path -LiteralPath $OriginalModulePath).Path
$actualOriginalSha256 = (Get-FileHash -LiteralPath $resolvedOriginalPath -Algorithm SHA256).Hash
if ($actualOriginalSha256 -ne $expectedOriginalSha256) {
    throw "screen_option.alb does not match ArcheAge r208022. Expected $expectedOriginalSha256, found $actualOriginalSha256."
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    [void](New-Item -ItemType Directory -Path $outputDirectory)
}

$temporaryDonorPath = Join-Path ([IO.Path]::GetTempPath()) ("aaemu-windowed-fullscreen-{0}.luac" -f [Guid]::NewGuid().ToString('N'))
$temporaryCameraDonorPath = Join-Path ([IO.Path]::GetTempPath()) ("aaemu-camera-controls-{0}.luac" -f [Guid]::NewGuid().ToString('N'))
$temporaryScreenModulePath = Join-Path ([IO.Path]::GetTempPath()) ("aaemu-screen-option-{0}.alb" -f [Guid]::NewGuid().ToString('N'))
try {
    & $LuacPath -s -o $temporaryDonorPath $sourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "Lua 5.1 compilation failed with exit code $LASTEXITCODE."
    }

    & $PythonPath $chunkToolPath patch-screen-mode `
        $resolvedOriginalPath `
        $temporaryDonorPath `
        $temporaryScreenModulePath
    if ($LASTEXITCODE -ne 0) {
        throw "Lua prototype transplant failed with exit code $LASTEXITCODE."
    }

    & $LuacPath -s -o $temporaryCameraDonorPath $cameraSourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "Camera-control Lua 5.1 compilation failed with exit code $LASTEXITCODE."
    }

    & $PythonPath $chunkToolPath patch-camera-controls `
        $temporaryScreenModulePath `
        $temporaryCameraDonorPath `
        $OutputPath
    if ($LASTEXITCODE -ne 0) {
        throw "Camera-control prototype transplant failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDonorPath) {
        Remove-Item -LiteralPath $temporaryDonorPath
    }
    if (Test-Path -LiteralPath $temporaryCameraDonorPath) {
        Remove-Item -LiteralPath $temporaryCameraDonorPath
    }
    if (Test-Path -LiteralPath $temporaryScreenModulePath) {
        Remove-Item -LiteralPath $temporaryScreenModulePath
    }
}

$compiled = Get-Item -LiteralPath $OutputPath
Write-Host "Built $($compiled.FullName)"
Write-Host "SHA-256: $((Get-FileHash -LiteralPath $compiled.FullName -Algorithm SHA256).Hash)"
