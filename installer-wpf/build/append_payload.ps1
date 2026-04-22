param([string]$ExePath, [string]$ZipPath)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { Write-Error "Exe not found: $ExePath"; exit 1 }
if (-not (Test-Path $ZipPath)) { Write-Error "Zip not found: $ZipPath"; exit 1 }

# Record exe size before appending — this becomes the zip start offset at runtime.
# The 8-byte trailer lets InstallerEngine create a SubStream over exactly the zip region,
# so ZipArchive never sees the exe bytes and reads offsets correctly.
$exeSize = (Get-Item $ExePath).Length

$writer = [IO.File]::Open($ExePath, 'Append')
$reader = [IO.File]::OpenRead($ZipPath)
$reader.CopyTo($writer)
$reader.Close()
# Append original exe size as little-endian int64 (8 bytes)
$sizeBytes = [BitConverter]::GetBytes([long]$exeSize)
$writer.Write($sizeBytes, 0, 8)
$writer.Close()

$sizeMB = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host "payload.zip appended to $ExePath ($sizeMB MB total)"
