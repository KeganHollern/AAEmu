[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$BinaryPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ManifestPath,

    [ValidateSet('Validate', 'Apply', 'Restore')]
    [string]$Action = 'Validate',

    [string]$BackupDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-HexString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$FieldName
    )

    $normalized = $Value -replace '\s', ''
    if ($normalized.Length -eq 0 -or ($normalized.Length % 2) -ne 0 -or $normalized -notmatch '^[0-9A-Fa-f]+$') {
        throw "Manifest field '$FieldName' must contain a non-empty, even-length hexadecimal byte string."
    }

    $bytes = [byte[]]::new($normalized.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($normalized.Substring($index * 2, 2), 16)
    }
    return $bytes
}

function Test-BytesEqual {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Left,

        [Parameter(Mandatory = $true)]
        [byte[]]$Right
    )

    if ($Left.Length -ne $Right.Length) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Length; $index++) {
        if ($Left[$index] -ne $Right[$index]) {
            return $false
        }
    }
    return $true
}

function Read-ExactBytes {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory = $true)]
        [long]$Offset,

        [Parameter(Mandatory = $true)]
        [int]$Length
    )

    $buffer = [byte[]]::new($Length)
    $Stream.Position = $Offset
    $totalRead = 0
    while ($totalRead -lt $Length) {
        $read = $Stream.Read($buffer, $totalRead, $Length - $totalRead)
        if ($read -eq 0) {
            throw "Unexpected end of file while reading $Length bytes at offset $Offset."
        }
        $totalRead += $read
    }
    return $buffer
}

function Get-BinaryPatchState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object[]]$Patches,

        [Parameter(Mandatory = $true)]
        [string]$OriginalSha256,

        [Parameter(Mandatory = $true)]
        [string]$PatchedSha256
    )

    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $originalMatches = 0
    $patchedMatches = 0
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        foreach ($patch in $Patches) {
            $actual = Read-ExactBytes -Stream $stream -Offset $patch.Offset -Length $patch.ExpectedBytes.Length
            if (Test-BytesEqual -Left $actual -Right $patch.ExpectedBytes) {
                $originalMatches++
            }
            elseif (Test-BytesEqual -Left $actual -Right $patch.ReplacementBytes) {
                $patchedMatches++
            }
        }
    }
    finally {
        $stream.Dispose()
    }

    $state = if ($originalMatches -eq $Patches.Count -and $hash -eq $OriginalSha256) {
        'Original'
    }
    elseif ($patchedMatches -eq $Patches.Count -and $hash -eq $PatchedSha256) {
        'Patched'
    }
    else {
        'Unexpected'
    }

    return [pscustomobject]@{
        State = $state
        Sha256 = $hash
        OriginalPatchCount = $originalMatches
        PatchedPatchCount = $patchedMatches
    }
}

$resolvedBinaryPath = (Resolve-Path -LiteralPath $BinaryPath).Path
$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schema version '$($manifest.schemaVersion)'."
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.name)) {
    throw 'The manifest must define a name.'
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.expectedBinaryName)) {
    throw 'The manifest must define expectedBinaryName.'
}
if ((Split-Path -Leaf $resolvedBinaryPath) -ne [string]$manifest.expectedBinaryName) {
    throw "Expected binary '$($manifest.expectedBinaryName)', found '$(Split-Path -Leaf $resolvedBinaryPath)'."
}

$binaryInfo = Get-Item -LiteralPath $resolvedBinaryPath
if ($binaryInfo.Length -ne [long]$manifest.expectedLength) {
    throw "Binary length mismatch. Expected $($manifest.expectedLength), found $($binaryInfo.Length)."
}

$preparedPatches = @(
    foreach ($patch in $manifest.patches) {
        $expectedBytes = ConvertFrom-HexString -Value ([string]$patch.expectedHex) -FieldName "$($patch.name).expectedHex"
        $replacementBytes = ConvertFrom-HexString -Value ([string]$patch.replacementHex) -FieldName "$($patch.name).replacementHex"
        if ($expectedBytes.Length -ne $replacementBytes.Length) {
            throw "Patch '$($patch.name)' changes byte length. Binary patches must remain the same size."
        }

        $offset = [long]$patch.offset
        if ($offset -lt 0 -or ($offset + $expectedBytes.Length) -gt $binaryInfo.Length) {
            throw "Patch '$($patch.name)' is outside the binary."
        }

        [pscustomobject]@{
            Name = [string]$patch.name
            Offset = $offset
            ExpectedBytes = $expectedBytes
            ReplacementBytes = $replacementBytes
        }
    }
)

if ($preparedPatches.Count -eq 0) {
    throw 'The manifest must contain at least one patch.'
}

$orderedPatches = @($preparedPatches | Sort-Object Offset)
for ($index = 1; $index -lt $orderedPatches.Count; $index++) {
    $previous = $orderedPatches[$index - 1]
    $current = $orderedPatches[$index]
    if ($current.Offset -lt ($previous.Offset + $previous.ExpectedBytes.Length)) {
        throw "Patches '$($previous.Name)' and '$($current.Name)' overlap."
    }
}

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $BackupDirectory = Join-Path $binaryInfo.DirectoryName '.aaemu-client-backups'
}
$safeName = ([string]$manifest.name) -replace '[^A-Za-z0-9._-]', '_'
$backupPath = Join-Path $BackupDirectory "$safeName.original.bin"
$originalSha256 = ([string]$manifest.originalSha256).ToUpperInvariant()
$patchedSha256 = ([string]$manifest.patchedSha256).ToUpperInvariant()

$state = Get-BinaryPatchState -Path $resolvedBinaryPath -Patches $orderedPatches -OriginalSha256 $originalSha256 -PatchedSha256 $patchedSha256
$state | Format-List

if ($state.State -eq 'Unexpected') {
    throw "The binary does not match the supported original or patched build. SHA256: $($state.Sha256)"
}
if ($Action -eq 'Validate') {
    Write-Host "Validated '$($manifest.name)' as $($state.State)."
    return
}
if ((Get-Process -Name archeage -ErrorAction SilentlyContinue)) {
    throw "Close ArcheAge before modifying '$($binaryInfo.Name)'."
}

if ($Action -eq 'Restore') {
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        throw "Backup not found: $backupPath"
    }
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $originalSha256) {
        throw 'The backup hash does not match the supported original binary.'
    }
    if (-not $PSCmdlet.ShouldProcess($resolvedBinaryPath, "Restore '$($manifest.name)'")) {
        return
    }

    [System.IO.File]::Copy($backupPath, $resolvedBinaryPath, $true)
    $restoredState = Get-BinaryPatchState -Path $resolvedBinaryPath -Patches $orderedPatches -OriginalSha256 $originalSha256 -PatchedSha256 $patchedSha256
    if ($restoredState.State -ne 'Original') {
        throw 'Restore verification failed.'
    }
    Write-Host "Restored '$($manifest.name)' and verified the original binary."
    return
}

if ($state.State -eq 'Patched') {
    Write-Host "'$($manifest.name)' is already applied."
    return
}
if (-not $PSCmdlet.ShouldProcess($resolvedBinaryPath, "Apply '$($manifest.name)'")) {
    return
}

if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    [void](New-Item -ItemType Directory -Path $BackupDirectory)
}
if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
    if ((Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -ne $originalSha256) {
        throw "An incompatible backup already exists: $backupPath"
    }
}
else {
    [System.IO.File]::Copy($resolvedBinaryPath, $backupPath, $false)
}

try {
    $stream = [System.IO.File]::Open($resolvedBinaryPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        foreach ($patch in $orderedPatches) {
            $stream.Position = $patch.Offset
            $stream.Write($patch.ReplacementBytes, 0, $patch.ReplacementBytes.Length)
        }
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }

    $appliedState = Get-BinaryPatchState -Path $resolvedBinaryPath -Patches $orderedPatches -OriginalSha256 $originalSha256 -PatchedSha256 $patchedSha256
    if ($appliedState.State -ne 'Patched') {
        throw 'Apply verification failed.'
    }
}
catch {
    [System.IO.File]::Copy($backupPath, $resolvedBinaryPath, $true)
    throw
}

Write-Host "Applied '$($manifest.name)' and verified the patched binary."
Write-Host "Backup: $backupPath"
