param([string]$ProjectDir)
$ErrorActionPreference = 'Stop'

$distSrc   = Join-Path $ProjectDir 'Output\dist'
$modelsSrc = Join-Path $ProjectDir '..\assets\models'
$dbSrc     = Join-Path $ProjectDir '..\assets\nodes.db'
$zipDest   = Join-Path $ProjectDir 'payload.zip'
$tmp       = Join-Path $env:TEMP 'DynCopilotPayload'

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null

if (Test-Path $distSrc)   { Copy-Item $distSrc   -Destination $tmp -Recurse }
if (Test-Path $modelsSrc) { Copy-Item $modelsSrc -Destination $tmp -Recurse }
if (Test-Path $dbSrc)     { Copy-Item $dbSrc     -Destination $tmp }

$items = Get-ChildItem $tmp
if ($items.Count -gt 0) {
    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zipDest -Force
    Write-Host "payload.zip created at $zipDest"
} else {
    Write-Warning "No payload items found - payload.zip not created"
}

Remove-Item $tmp -Recurse -Force
