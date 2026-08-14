[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ManifestPath,

    [ValidateSet('Validate', 'Apply', 'Restore')]
    [string]$Action = 'Validate',

    [string]$BackupDirectory,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-StrictAsciiBytes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,

        [Parameter(Mandatory = $true)]
        [string]$FieldName
    )

    $encoding = [System.Text.Encoding]::GetEncoding(
        20127,
        [System.Text.EncoderFallback]::ExceptionFallback,
        [System.Text.DecoderFallback]::ExceptionFallback
    )
    try {
        return $encoding.GetBytes($Value)
    }
    catch {
        throw "Manifest field '$FieldName' must contain ASCII text only."
    }
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

function Get-BackupPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$PatchName
    )

    $safeName = $PatchName -replace '[^A-Za-z0-9._-]', '_'
    return Join-Path $Directory "$safeName.backup.json"
}

$resolvedPackPath = (Resolve-Path -LiteralPath $PackPath).Path
$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported manifest schema version '$($manifest.schemaVersion)'."
}
if ([string]::IsNullOrWhiteSpace([string]$manifest.name)) {
    throw 'The manifest must define a name.'
}
if (-not $manifest.expectedPackLength) {
    throw 'The manifest must define expectedPackLength.'
}
if (-not $manifest.patches -or $manifest.patches.Count -eq 0) {
    throw 'The manifest must contain at least one patch.'
}

$packInfo = Get-Item -LiteralPath $resolvedPackPath
$expectedPackLength = [long]$manifest.expectedPackLength
if ($packInfo.Length -lt $expectedPackLength) {
    throw "Pack is shorter than the target build. Expected at least $expectedPackLength bytes, found $($packInfo.Length)."
}

$preparedPatches = foreach ($patch in $manifest.patches) {
    $expectedBytes = ConvertTo-StrictAsciiBytes -Value ([string]$patch.expectedAscii) -FieldName "$($patch.name).expectedAscii"
    $replacementBytes = ConvertTo-StrictAsciiBytes -Value ([string]$patch.replacementAscii) -FieldName "$($patch.name).replacementAscii"
    if ($expectedBytes.Length -eq 0) {
        throw "Patch '$($patch.name)' cannot be empty."
    }
    if ($expectedBytes.Length -ne $replacementBytes.Length) {
        throw "Patch '$($patch.name)' changes byte length. Direct game_pak patches must remain the same size."
    }

    $offset = [long]$patch.absoluteOffset
    if ($offset -lt 0 -or ($offset + $expectedBytes.Length) -gt $packInfo.Length) {
        throw "Patch '$($patch.name)' is outside the pack."
    }

    [pscustomobject]@{
        Name = [string]$patch.name
        VirtualPath = [string]$patch.virtualPath
        Offset = $offset
        ExpectedBytes = $expectedBytes
        ReplacementBytes = $replacementBytes
    }
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
    $BackupDirectory = Join-Path $packInfo.DirectoryName '.aaemu-client-backups'
}
$backupPath = Get-BackupPath -Directory $BackupDirectory -PatchName ([string]$manifest.name)

if ($Action -eq 'Restore') {
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        throw "Backup not found: $backupPath"
    }

    $backup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    if ([string]$backup.patchName -ne [string]$manifest.name) {
        throw 'Backup patch name does not match this manifest.'
    }
    if ([long]$backup.expectedPackLength -gt $packInfo.Length) {
        throw 'The game_pak is shorter than the build recorded in this backup.'
    }

    $restoreItems = foreach ($change in $backup.changes) {
        [pscustomobject]@{
            Name = [string]$change.name
            Offset = [long]$change.offset
            OriginalBytes = [Convert]::FromBase64String([string]$change.originalBase64)
            ReplacementBytes = [Convert]::FromBase64String([string]$change.replacementBase64)
        }
    }
    if ($restoreItems.Count -ne $orderedPatches.Count) {
        throw 'Backup change count does not match this manifest.'
    }
    foreach ($item in $restoreItems) {
        $matchingPatch = @($orderedPatches | Where-Object { $_.Offset -eq $item.Offset })
        if ($matchingPatch.Count -ne 1 -or
            -not (Test-BytesEqual -Left $item.OriginalBytes -Right $matchingPatch[0].ExpectedBytes) -or
            -not (Test-BytesEqual -Left $item.ReplacementBytes -Right $matchingPatch[0].ReplacementBytes)) {
            throw "Backup change '$($item.Name)' does not match this manifest."
        }
    }

    $checkStream = [System.IO.File]::Open($resolvedPackPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $restoreStates = foreach ($item in $restoreItems) {
            $actual = Read-ExactBytes -Stream $checkStream -Offset $item.Offset -Length $item.OriginalBytes.Length
            $state = if (Test-BytesEqual -Left $actual -Right $item.OriginalBytes) {
                'Original'
            }
            elseif (Test-BytesEqual -Left $actual -Right $item.ReplacementBytes) {
                'Patched'
            }
            else {
                'Unexpected'
            }
            [pscustomobject]@{ Name = $item.Name; Offset = $item.Offset; State = $state }
        }
    }
    finally {
        $checkStream.Dispose()
    }

    $restoreStates | Format-Table -AutoSize
    if (($restoreStates.State -contains 'Unexpected') -and -not $Force) {
        throw 'At least one patch location contains unexpected bytes. Use -Force only if you intentionally want to restore over another modification.'
    }
    if (-not ($restoreStates.State -contains 'Patched') -and -not ($restoreStates.State -contains 'Unexpected')) {
        Write-Host 'The original bytes are already present; nothing to restore.'
        return
    }
    if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Restore patch '$($manifest.name)' from $backupPath")) {
        return
    }
    if ($packInfo.Name -eq 'game_pak' -and (Get-Process -Name archeage -ErrorAction SilentlyContinue)) {
        throw 'Close ArcheAge before restoring game_pak.'
    }

    $streamOptions = [System.IO.FileStreamOptions]::new()
    $streamOptions.Mode = [System.IO.FileMode]::Open
    $streamOptions.Access = [System.IO.FileAccess]::ReadWrite
    $streamOptions.Share = [System.IO.FileShare]::None
    $streamOptions.BufferSize = 4096
    $streamOptions.Options = [System.IO.FileOptions]::WriteThrough
    $stream = [System.IO.FileStream]::new($resolvedPackPath, $streamOptions)
    try {
        foreach ($item in $restoreItems) {
            $stream.Position = $item.Offset
            $stream.Write($item.OriginalBytes, 0, $item.OriginalBytes.Length)
        }
        $stream.Flush($true)
        foreach ($item in $restoreItems) {
            $actual = Read-ExactBytes -Stream $stream -Offset $item.Offset -Length $item.OriginalBytes.Length
            if (-not (Test-BytesEqual -Left $actual -Right $item.OriginalBytes)) {
                throw "Restore verification failed for '$($item.Name)'."
            }
        }
    }
    finally {
        $stream.Dispose()
    }

    Write-Host "Restored '$($manifest.name)' and verified every changed byte."
    return
}

$readStream = [System.IO.File]::Open($resolvedPackPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
try {
    $states = foreach ($patch in $orderedPatches) {
        $actual = Read-ExactBytes -Stream $readStream -Offset $patch.Offset -Length $patch.ExpectedBytes.Length
        $state = if (Test-BytesEqual -Left $actual -Right $patch.ExpectedBytes) {
            'Original'
        }
        elseif (Test-BytesEqual -Left $actual -Right $patch.ReplacementBytes) {
            'Patched'
        }
        else {
            'Unexpected'
        }
        [pscustomobject]@{
            Name = $patch.Name
            VirtualPath = $patch.VirtualPath
            Offset = $patch.Offset
            State = $state
            ActualHex = [Convert]::ToHexString($actual)
        }
    }
}
finally {
    $readStream.Dispose()
}

$states | Format-Table -AutoSize
if ($states.State -contains 'Unexpected') {
    throw 'At least one patch location contains unexpected bytes. The pack may be a different build or already modified.'
}
if (($states.State -contains 'Original') -and ($states.State -contains 'Patched')) {
    throw 'The pack is only partially patched. Restore from a known backup before continuing.'
}
if ($Action -eq 'Validate') {
    Write-Host "Validated '$($manifest.name)' against $resolvedPackPath."
    return
}
if (-not ($states.State -contains 'Original')) {
    Write-Host 'The patch is already applied; nothing to do.'
    return
}
if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Apply patch '$($manifest.name)' and create backup $backupPath")) {
    return
}
if ($packInfo.Name -eq 'game_pak' -and (Get-Process -Name archeage -ErrorAction SilentlyContinue)) {
    throw 'Close ArcheAge before patching game_pak.'
}

$backupChanges = foreach ($patch in $orderedPatches) {
    [ordered]@{
        name = $patch.Name
        offset = $patch.Offset
        originalBase64 = [Convert]::ToBase64String($patch.ExpectedBytes)
        replacementBase64 = [Convert]::ToBase64String($patch.ReplacementBytes)
    }
}
$backupDocument = [ordered]@{
    schemaVersion = 1
    patchName = [string]$manifest.name
    packPath = $resolvedPackPath
    expectedPackLength = $packInfo.Length
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    changes = @($backupChanges)
}

if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    [void](New-Item -ItemType Directory -Path $BackupDirectory)
}
if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
    $temporaryBackupPath = "$backupPath.tmp"
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($temporaryBackupPath, ($backupDocument | ConvertTo-Json -Depth 6), $utf8WithoutBom)
    Move-Item -LiteralPath $temporaryBackupPath -Destination $backupPath
}
else {
    $existingBackup = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    if ([string]$existingBackup.patchName -ne [string]$manifest.name -or [long]$existingBackup.expectedPackLength -ne $packInfo.Length) {
        throw "Existing backup does not match this patch: $backupPath"
    }
    $existingChanges = @($existingBackup.changes)
    if ($existingChanges.Count -ne $backupChanges.Count) {
        throw "Existing backup does not match this patch: $backupPath"
    }
    for ($index = 0; $index -lt $backupChanges.Count; $index++) {
        $expectedChange = $backupChanges[$index]
        $existingChange = $existingChanges[$index]
        if ([long]$existingChange.offset -ne [long]$expectedChange.offset -or
            [string]$existingChange.originalBase64 -ne [string]$expectedChange.originalBase64 -or
            [string]$existingChange.replacementBase64 -ne [string]$expectedChange.replacementBase64) {
            throw "Existing backup does not match this patch: $backupPath"
        }
    }
}

$writeStreamOptions = [System.IO.FileStreamOptions]::new()
$writeStreamOptions.Mode = [System.IO.FileMode]::Open
$writeStreamOptions.Access = [System.IO.FileAccess]::ReadWrite
$writeStreamOptions.Share = [System.IO.FileShare]::None
$writeStreamOptions.BufferSize = 4096
$writeStreamOptions.Options = [System.IO.FileOptions]::WriteThrough
$writeStream = [System.IO.FileStream]::new($resolvedPackPath, $writeStreamOptions)
$writtenPatches = [System.Collections.Generic.List[object]]::new()
try {
    foreach ($patch in $orderedPatches) {
        $writeStream.Position = $patch.Offset
        $writeStream.Write($patch.ReplacementBytes, 0, $patch.ReplacementBytes.Length)
        $writtenPatches.Add($patch)
    }
    $writeStream.Flush($true)

    foreach ($patch in $orderedPatches) {
        $actual = Read-ExactBytes -Stream $writeStream -Offset $patch.Offset -Length $patch.ReplacementBytes.Length
        if (-not (Test-BytesEqual -Left $actual -Right $patch.ReplacementBytes)) {
            throw "Apply verification failed for '$($patch.Name)'."
        }
    }
}
catch {
    foreach ($patch in $writtenPatches) {
        $writeStream.Position = $patch.Offset
        $writeStream.Write($patch.ExpectedBytes, 0, $patch.ExpectedBytes.Length)
    }
    $writeStream.Flush($true)
    throw
}
finally {
    $writeStream.Dispose()
}

Write-Host "Applied '$($manifest.name)' and verified every changed byte."
Write-Host "Backup: $backupPath"
