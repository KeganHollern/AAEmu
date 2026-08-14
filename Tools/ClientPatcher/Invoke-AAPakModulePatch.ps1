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
    [string]$CompiledModulePath,

    [Parameter(Mandatory = $true)]
    [string]$ModuleVirtualPath,

    [Parameter(Mandatory = $true)]
    [string]$TocVirtualPath,

    [Parameter(Mandatory = $true)]
    [string]$TocModuleLine,

    [Parameter(Mandatory = $true)]
    [string]$TocInsertAfter,

    [Parameter(Mandatory = $true)]
    [string]$PatchName,

    [Parameter(Mandatory = $true)]
    [long]$ExpectedOriginalPackLength,

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
    $Stream.Position = 0
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

function Restore-StructuralBackup {
    param(
        [Parameter(Mandatory = $true)][string]$TargetPackPath,
        [Parameter(Mandatory = $true)]$Metadata,
        [Parameter(Mandatory = $true)][string]$HeaderBackupPath,
        [Parameter(Mandatory = $true)][string]$TailBackupPath
    )

    $headerBytes = [System.IO.File]::ReadAllBytes($HeaderBackupPath)
    if ((Get-Sha256Hex -Bytes $headerBytes) -ne [string]$Metadata.headerSha256) {
        throw 'The structural backup header hash is invalid.'
    }
    if ((Get-FileHash -LiteralPath $TailBackupPath -Algorithm SHA256).Hash -ne [string]$Metadata.tailSha256) {
        throw 'The structural backup file-table hash is invalid.'
    }

    $options = [System.IO.FileStreamOptions]::new()
    $options.Mode = [System.IO.FileMode]::Open
    $options.Access = [System.IO.FileAccess]::ReadWrite
    $options.Share = [System.IO.FileShare]::None
    $options.BufferSize = 1MB
    $options.Options = [System.IO.FileOptions]::WriteThrough
    $packStream = [System.IO.FileStream]::new($TargetPackPath, $options)
    $tailStream = [System.IO.File]::OpenRead($TailBackupPath)
    try {
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
        [Parameter(Mandatory = $true)][string]$DllPath,
        [Parameter(Mandatory = $true)][string]$ExpectedModuleHash
    )

    Add-Type -Path $DllPath
    $pak = [AAPacker.AAPak]::new($TargetPackPath, $true, $false)
    try {
        if (-not $pak.Header.IsValid) {
            throw "AAPacker rejected the archive: $($pak.LastError)"
        }

        $tocMatches = @($pak.Files | Where-Object { $_.Name -eq $TocVirtualPath })
        if ($tocMatches.Count -ne 1) {
            throw "Expected exactly one '$TocVirtualPath' entry, found $($tocMatches.Count)."
        }
        $tocStream = $pak.ExportFileAsStream($tocMatches[0])
        try {
            $tocText = [Text.Encoding]::UTF8.GetString((Get-StreamBytes -Stream $tocStream))
        }
        finally {
            $tocStream.Dispose()
        }

        $moduleMatches = @($pak.Files | Where-Object { $_.Name -eq $ModuleVirtualPath })
        $tocHasModule = [regex]::IsMatch($tocText, "(?m)^$([regex]::Escape($TocModuleLine))`r?$")
        $moduleHash = $null
        if ($moduleMatches.Count -eq 1) {
            $moduleStream = $pak.ExportFileAsStream($moduleMatches[0])
            try {
                $moduleHash = Get-Sha256Hex -Bytes (Get-StreamBytes -Stream $moduleStream)
            }
            finally {
                $moduleStream.Dispose()
            }
        }

        $state = if ($moduleMatches.Count -eq 0 -and -not $tocHasModule) {
            'Original'
        }
        elseif ($moduleMatches.Count -eq 1 -and $tocHasModule -and $moduleHash -eq $ExpectedModuleHash) {
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
            TocText = $tocText
            TocHasModule = $tocHasModule
            ModuleCount = $moduleMatches.Count
            ModuleHash = $moduleHash
        }
    }
    finally {
        $pak.ClosePak()
    }
}

$resolvedPackPath = (Resolve-Path -LiteralPath $PackPath).Path
$resolvedDllPath = (Resolve-Path -LiteralPath $AAPackerDllPath).Path
$resolvedModulePath = (Resolve-Path -LiteralPath $CompiledModulePath).Path
$moduleBytes = [System.IO.File]::ReadAllBytes($resolvedModulePath)
$moduleHash = Get-Sha256Hex -Bytes $moduleBytes
$packInfo = Get-Item -LiteralPath $resolvedPackPath

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    $BackupDirectory = Join-Path $packInfo.DirectoryName '.aaemu-client-backups'
}
$safePatchName = Get-SafePatchName -Name $PatchName
$backupStem = Join-Path $BackupDirectory $safePatchName
$metadataPath = "$backupStem.structural-backup.json"
$headerBackupPath = "$backupStem.header.bin"
$tailBackupPath = "$backupStem.file-table.bin"

if ($Action -eq 'Restore') {
    foreach ($requiredPath in @($metadataPath, $headerBackupPath, $tailBackupPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Structural backup is incomplete; missing $requiredPath"
        }
    }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    if ([string]$metadata.patchName -ne $PatchName -or [string]$metadata.moduleSha256 -ne $moduleHash) {
        throw 'The structural backup does not match this patch or compiled module.'
    }
    if ((Get-Process -Name archeage -ErrorAction SilentlyContinue)) {
        throw 'Close ArcheAge before restoring game_pak.'
    }
    if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Restore structural patch '$PatchName'")) {
        return
    }
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata $metadata -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath
    $restoredState = Get-PatchState -TargetPackPath $resolvedPackPath -DllPath $resolvedDllPath -ExpectedModuleHash $moduleHash
    if ($restoredState.State -ne 'Original' -or $restoredState.PackLength -ne [long]$metadata.originalPackLength) {
        throw 'Structural restore verification failed.'
    }
    Write-Host "Restored '$PatchName' and verified the original file table."
    return
}

$state = Get-PatchState -TargetPackPath $resolvedPackPath -DllPath $resolvedDllPath -ExpectedModuleHash $moduleHash
$state | Select-Object State, PackLength, TocHasModule, ModuleCount, ModuleHash | Format-List

if ($state.State -eq 'Unexpected') {
    throw 'The archive contains a partial or unexpected version of this module patch.'
}
if ($Action -eq 'Validate') {
    if ($state.State -eq 'Original' -and $state.PackLength -ne $ExpectedOriginalPackLength) {
        throw "Original pack length mismatch. Expected $ExpectedOriginalPackLength, found $($state.PackLength)."
    }
    Write-Host "Validated '$PatchName'."
    return
}
if ($state.State -eq 'Patched') {
    Write-Host "'$PatchName' is already applied."
    return
}
if ($state.PackLength -ne $ExpectedOriginalPackLength) {
    throw "Original pack length mismatch. Expected $ExpectedOriginalPackLength, found $($state.PackLength)."
}
if ((Get-Process -Name archeage -ErrorAction SilentlyContinue)) {
    throw 'Close ArcheAge before patching game_pak.'
}
if (-not [regex]::IsMatch($state.TocText, "(?m)^$([regex]::Escape($TocInsertAfter))`r?$")) {
    throw "The TOC does not contain the expected insertion point '$TocInsertAfter'."
}
if (-not $PSCmdlet.ShouldProcess($resolvedPackPath, "Add '$ModuleVirtualPath' and update '$TocVirtualPath'")) {
    return
}

if (-not (Test-Path -LiteralPath $BackupDirectory)) {
    [void](New-Item -ItemType Directory -Path $BackupDirectory)
}
if ((Test-Path -LiteralPath $metadataPath) -or (Test-Path -LiteralPath $headerBackupPath) -or (Test-Path -LiteralPath $tailBackupPath)) {
    throw "A partial or existing structural backup already uses '$backupStem'."
}

$tailLength = $state.PackLength - $state.FirstFileInfoOffset
Copy-FileRange -SourcePath $resolvedPackPath -Offset 0 -Length $state.HeaderLength -DestinationPath $headerBackupPath
Copy-FileRange -SourcePath $resolvedPackPath -Offset $state.FirstFileInfoOffset -Length $tailLength -DestinationPath $tailBackupPath
$metadata = [ordered]@{
    schemaVersion = 1
    patchName = $PatchName
    packPath = $resolvedPackPath
    originalPackLength = $state.PackLength
    firstFileInfoOffset = $state.FirstFileInfoOffset
    headerLength = $state.HeaderLength
    headerSha256 = (Get-FileHash -LiteralPath $headerBackupPath -Algorithm SHA256).Hash
    tailLength = $tailLength
    tailSha256 = (Get-FileHash -LiteralPath $tailBackupPath -Algorithm SHA256).Hash
    moduleVirtualPath = $ModuleVirtualPath
    moduleSha256 = $moduleHash
    tocVirtualPath = $TocVirtualPath
    originalTocBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($state.TocText))
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
}
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 5), $utf8WithoutBom)

$insertMatch = [regex]::Match($state.TocText, "(?m)^$([regex]::Escape($TocInsertAfter))`r?$")
$patchedTocText = $state.TocText.Insert($insertMatch.Index + $insertMatch.Length, "`n$TocModuleLine")
$patchedTocBytes = [Text.Encoding]::UTF8.GetBytes($patchedTocText)

try {
    Add-Type -Path $resolvedDllPath
    $pak = [AAPacker.AAPak]::new($resolvedPackPath, $false, $false)
    try {
        if (-not $pak.AddFileFromFile($resolvedModulePath, $ModuleVirtualPath, $true)) {
            throw "AAPacker could not add '$ModuleVirtualPath': $($pak.LastError)"
        }
        $tocEntry = @($pak.Files | Where-Object { $_.Name -eq $TocVirtualPath })[0]
        $tocStream = [System.IO.MemoryStream]::new($patchedTocBytes, $false)
        try {
            if (-not $pak.ReplaceFile([ref]$tocEntry, $tocStream, [DateTime]::UtcNow)) {
                throw "AAPacker could not replace '$TocVirtualPath': $($pak.LastError)"
            }
        }
        finally {
            $tocStream.Dispose()
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
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata ([pscustomobject]$metadata) -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath
    throw
}

$appliedState = Get-PatchState -TargetPackPath $resolvedPackPath -DllPath $resolvedDllPath -ExpectedModuleHash $moduleHash
if ($appliedState.State -ne 'Patched') {
    Restore-StructuralBackup -TargetPackPath $resolvedPackPath -Metadata ([pscustomobject]$metadata) -HeaderBackupPath $headerBackupPath -TailBackupPath $tailBackupPath
    throw 'Apply verification failed; the original file table was restored.'
}

Write-Host "Applied '$PatchName' and verified the added module and TOC entry."
Write-Host "Structural backup: $metadataPath"
