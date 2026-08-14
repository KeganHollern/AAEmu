[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$PackPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AAPackerDllPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ReplacementFilePath,

    [Parameter(Mandatory = $true)]
    [string]$ModuleVirtualPath,

    [Parameter(Mandatory = $true)]
    [string]$PatchName,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedOriginalModuleSha256,

    [ValidateSet('Validate', 'Apply', 'Restore')]
    [string]$Action = 'Validate',

    [string]$BackupDirectory,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SafePatchName {
    param([Parameter(Mandatory = $true)][string]$Name)
    return $Name -replace '[^A-Za-z0-9._-]', '_'
}

function Get-StreamBytes {
    param([Parameter(Mandatory = $true)][System.IO.Stream]$Stream)

    $result = [byte[]]::new($Stream.Length)
    $stream.Position = 0
    $totalRead = 0
    while ($totalRead -lt $result.Length) {
        $read = $Stream.Read($result, $totalRead, $result.Length - $totalRead)
        if ($read -eq 0) {
            throw 'Unexpected end of stream.'
        }
        $totalRead += $read
    }
    return $result
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes))
}

function Copy-FileRange {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][long]$Offset,
        [Parameter(Mandatory = $true)][long]$Length,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $source = [System.IO.File]::Open($SourcePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    $destination = [System.IO.File]::Open($DestinationPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    try {
        $source.Position = $Offset
        $remaining = $Length
        $buffer = [byte[]]::new(1MB)
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min($buffer.Length, $remaining)
            $read = $source.Read($buffer, 0, $requested)
            if ($read -eq 0) {
                throw "Unexpected end of file while backing up $Length bytes at offset $Offset."
            }
            $destination.Write($buffer, 0, $read)
            $remaining -= $read
        }
        $destination.Flush($true)
    }
    finally {
        $destination.Dispose()
        $source.Dispose()
    }
}

function Write-Metadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Metadata
    )

    $temporaryPath = "$Path.tmp"
    $utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($temporaryPath, ($Metadata | ConvertTo-Json -Depth 5), $utf8WithoutBom)
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Restore-StructuralBackup {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPackPath,
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)][string]$HeaderBackupPath,
        [Parameter(Mandatory = $true)][string]$TailBackupPath,
        [Parameter(Mandatory = $true)][string]$ModuleStorageBackupPath
    )

    $headerBytes = [System.IO.File]::ReadAllBytes($HeaderBackupPath)
    if ((Get-Sha256Hex -Bytes $headerBytes) -ne [string]$Metadata.headerSha256) {
        throw 'The structural backup header hash is invalid.'
    }
    if ((Get-FileHash -LiteralPath $TailBackupPath -Algorithm SHA256).Hash -ne [string]$Metadata.tailSha256) {
        throw 'The structural backup file-table hash is invalid.'
    }
    if ((Get-FileHash -LiteralPath $ModuleStorageBackupPath -Algorithm SHA256).Hash -ne [string]$Metadata.moduleStorageSha256) {
        throw 'The archived module-storage backup hash is invalid.'
    }

    $moduleStorageBytes = [System.IO.File]::ReadAllBytes($ModuleStorageBackupPath)
    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::Open
    $options.Access = [System.IO.FileAccess]::ReadWrite
    $options.Share = [System.IO.FileShare]::None
    $options.BufferSize = 1MB
    $options.Options = [System.IO.FileOptions]::WriteThrough
    $packStream = [System.IO.FileStream]::new($TargetPackPath, $options)
    $tailStream = [System.IO.File]::OpenRead($TailBackupPath)
    try {
        $packStream.Position = [long]$Metadata.moduleOffset
        $packStream.Write($moduleStorageBytes, 0, $moduleStorageBytes.Length)
        $packStream.Position = [long]$Metadata.firstFileInfoOffset
        $tailStream.CopyTo($packStream, 1MB)
        $packStream.SetLength([long]$Metadata.originalPackLength)
        $packStream.Position = 0
        $packStream.Write($headerBytes, 0, $headerBytes.Length)
        $packStream.Flush($true)
    }
    finally {
        $tailStream.Dispose()
        $packStream.Dispose()
    }
}

function Get-PatchState {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPackPath,
        [Parameter(Mandatory = $true)][string]$VirtualPath,
        [Parameter(Mandatory = $true)][string]$OriginalHash,
        [Parameter(Mandatory = $true)][string]$ReplacementHash
    )

    $pak = [AAPacker.AAPak]::new($TargetPackPath, $true, $false)
    try {
        if (-not $pak.Header.IsValid) {
            throw "AAPacker rejected the archive: $($pak.LastError)"
        }
        $matches = @($pak.Files | Where-Object { $_.Name -eq $VirtualPath })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one '$VirtualPath' entry, found $($matches.Count)."
        }
        $moduleStream = $pak.ExportFileAsStream($matches[0])
        try {
            $moduleHash = Get-Sha256Hex -Bytes (Get-StreamBytes -Stream $moduleStream)
        }
        finally {
            $moduleStream.Dispose()
        }

        $state = if ($moduleHash -eq $OriginalHash) {
            'Original'
        }
        elseif ($moduleHash -eq $ReplacementHash) {
            'Patched'
        }
        else {
            'Unexpected'
        }

        return [pscustomobject]@{
            State = $state
            PackLength = (Get-Item -LiteralPath $TargetPackPath).Length
            FirstFileInfoOffset = [long]$pak.Header.FirstFileInfoOffset
            HeaderLength = [int]$pak.Header.RawData.Length
            ModuleHash = $moduleHash
            ModuleOffset = [long]$matches[0].Offset
            ModuleSize = [long]$matches[0].Size
            ModulePaddingSize = [long]$matches[0].PaddingSize
        }
    }
    finally {
        $pak.ClosePak()
    }
}

$resolvedPackPath = (Resolve-Path -LiteralPath $PackPath).Path
$resolvedDllPath = (Resolve-Path -LiteralPath $AAPackerDllPath).Path
$resolvedReplacementPath = (Resolve-Path -LiteralPath $ReplacementFilePath).Path
$replacementBytes = [System.IO.File]::ReadAllBytes($resolvedReplacementPath)
$replacementHash = Get-Sha256Hex -Bytes $replacementBytes
$originalHash = $ExpectedOriginalModuleSha256.ToUpperInvariant()
$packInfo = Get-Item -LiteralPath $resolvedPackPath

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $BackupDirectory = Join-Path $packInfo.DirectoryName '.aaemu-client-backups'
}
$backupStem = Join-Path $BackupDirectory (Get-SafePatchName -Name $PatchName)
$metadataPath = "$backupStem.structural-backup.json"
$headerBackupPath = "$backupStem.header.bin"
$tailBackupPath = "$backupStem.file-table.bin"
$moduleStorageBackupPath = "$backupStem.module-storage.bin"

Add-Type -Path $resolvedDllPath

if ($Action -eq 'Restore') {
    foreach ($requiredPath in @($metadataPath, $headerBackupPath, $tailBackupPath, $moduleStorageBackupPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Structural backup is incomplete; missing $requiredPath"
        }
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ([string]$metadata.patchName -ne $PatchName -or
        [string]$metadata.moduleVirtualPath -ne $ModuleVirtualPath -or
        [string]$metadata.replacementModuleSha256 -ne $replacementHash) {
        throw 'The structural backup does not match this patch or replacement module.'
    }
    $currentLength = (Get-Item -LiteralPath $resolvedPackPath).Length
    if (-not $Force -and [long]$metadata.patchedPackLength -ne $currentLength) {
        throw 'The archive length changed after this patch. Restore later structural patches first, or use -Force only if discarding them is intentional.'
    }
    if (Get-Process -Name archeage -ErrorAction SilentlyContinue) {
        throw 'Close ArcheAge before restoring game_pak.'
    }
    if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Restore structural patch '$PatchName'")) {
        return
    }
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata $metadata -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath -ModuleStorageBackupPath $moduleStorageBackupPath
    $restoredState = Get-PatchState -TargetPackPath $resolvedPackPath -VirtualPath $ModuleVirtualPath -OriginalHash $originalHash -ReplacementHash $replacementHash
    if ($restoredState.State -ne 'Original' -or $restoredState.PackLength -ne [long]$metadata.originalPackLength) {
        throw 'Structural restore verification failed.'
    }
    Write-Host "Restored '$PatchName' and verified the original module and file table."
    return
}

$state = Get-PatchState -TargetPackPath $resolvedPackPath -VirtualPath $ModuleVirtualPath -OriginalHash $originalHash -ReplacementHash $replacementHash
$state | Format-List State, PackLength, FirstFileInfoOffset, ModuleHash, ModuleOffset, ModuleSize, ModulePaddingSize
if ($state.State -eq 'Unexpected') {
    throw "'$ModuleVirtualPath' does not match the expected original or replacement module."
}
if ($Action -eq 'Validate') {
    Write-Host "Validated '$PatchName'."
    return
}
if ($state.State -eq 'Patched') {
    Write-Host "'$PatchName' is already applied."
    return
}
if (Get-Process -Name archeage -ErrorAction SilentlyContinue) {
    throw 'Close ArcheAge before patching game_pak.'
}
if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Replace '$ModuleVirtualPath' for patch '$PatchName'")) {
    return
}

if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    [void](New-Item -ItemType Directory -Path $BackupDirectory)
}
$backupPaths = @($metadataPath, $headerBackupPath, $tailBackupPath, $moduleStorageBackupPath)
$existingBackupCount = @($backupPaths | Where-Object { Test-Path -LiteralPath $_ }).Count
if ($existingBackupCount -ne 0 -and $existingBackupCount -ne $backupPaths.Count) {
    throw "A partial structural backup already uses '$backupStem'."
}
if ($existingBackupCount -eq 0) {
    $tailLength = $state.PackLength - $state.FirstFileInfoOffset
    $moduleStorageLength = $state.ModuleSize + $state.ModulePaddingSize
    Copy-FileRange -SourcePath $resolvedPackPath -Offset 0 -Length $state.HeaderLength -DestinationPath $headerBackupPath
    Copy-FileRange -SourcePath $resolvedPackPath -Offset $state.FirstFileInfoOffset -Length $tailLength -DestinationPath $tailBackupPath
    Copy-FileRange -SourcePath $resolvedPackPath -Offset $state.ModuleOffset -Length $moduleStorageLength -DestinationPath $moduleStorageBackupPath
    $metadata = [ordered]@{
        schemaVersion = 1
        patchName = $PatchName
        packPath = $resolvedPackPath
        originalPackLength = $state.PackLength
        patchedPackLength = 0
        firstFileInfoOffset = $state.FirstFileInfoOffset
        headerLength = $state.HeaderLength
        headerSha256 = (Get-FileHash -LiteralPath $headerBackupPath -Algorithm SHA256).Hash
        tailLength = $tailLength
        tailSha256 = (Get-FileHash -LiteralPath $tailBackupPath -Algorithm SHA256).Hash
        moduleOffset = $state.ModuleOffset
        moduleStorageLength = $moduleStorageLength
        moduleStorageSha256 = (Get-FileHash -LiteralPath $moduleStorageBackupPath -Algorithm SHA256).Hash
        moduleVirtualPath = $ModuleVirtualPath
        originalModuleSha256 = $originalHash
        replacementModuleSha256 = $replacementHash
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-Metadata -Path $metadataPath -Metadata $metadata
}
else {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ([string]$metadata.patchName -ne $PatchName -or
        [string]$metadata.originalModuleSha256 -ne $originalHash -or
        [string]$metadata.replacementModuleSha256 -ne $replacementHash) {
        throw "Existing structural backup does not match '$PatchName'."
    }
}

try {
    $pak = [AAPacker.AAPak]::new($resolvedPackPath, $false, $false)
    try {
        if (-not $pak.Header.IsValid) {
            throw "AAPacker rejected the archive: $($pak.LastError)"
        }
        $matches = @($pak.Files | Where-Object { $_.Name -eq $ModuleVirtualPath })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one '$ModuleVirtualPath' entry, found $($matches.Count)."
        }
        $moduleEntry = $matches[0]
        $replacementStream = [System.IO.MemoryStream]::new($replacementBytes, $false)
        try {
            if (-not $pak.ReplaceFile([ref]$moduleEntry, $replacementStream, [DateTime]::UtcNow)) {
                throw "AAPacker could not replace '$ModuleVirtualPath': $($pak.LastError)"
            }
        }
        finally {
            $replacementStream.Dispose()
        }
        if (-not $pak.SaveHeader()) {
            throw "AAPacker could not save the updated file table: $($pak.LastError)"
        }
    }
    finally {
        $pak.ClosePak()
    }
}
catch {
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata $metadata -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath -ModuleStorageBackupPath $moduleStorageBackupPath
    throw
}

$appliedState = Get-PatchState -TargetPackPath $resolvedPackPath -VirtualPath $ModuleVirtualPath -OriginalHash $originalHash -ReplacementHash $replacementHash
if ($appliedState.State -ne 'Patched') {
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata $metadata -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath -ModuleStorageBackupPath $moduleStorageBackupPath
    throw 'Apply verification failed; the original file table was restored.'
}

$metadata.patchedPackLength = $appliedState.PackLength
Write-Metadata -Path $metadataPath -Metadata $metadata
Write-Host "Applied '$PatchName' and verified '$ModuleVirtualPath'."
Write-Host "Structural backup: $metadataPath"
