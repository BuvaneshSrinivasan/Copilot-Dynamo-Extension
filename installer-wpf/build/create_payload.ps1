param([string]$ProjectDir)
$ErrorActionPreference = 'Stop'

$distSrc   = Join-Path $ProjectDir 'Output\dist'
$modelsSrc = Join-Path $ProjectDir '..\assets\models'
$dbSrc     = Join-Path $ProjectDir '..\assets\nodes.db'
$zipDest   = Join-Path $ProjectDir 'payload.zip'
$tmp       = Join-Path $env:TEMP 'DynCopilotPayload'

if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null

# Only bundle the extension DLLs — nodes.db and ONNX models are downloaded at install time
if (Test-Path $distSrc) { Copy-Item $distSrc -Destination $tmp -Recurse }

# Obfuscate our DLLs in the staging folder before zipping
$obfuscateScript = Join-Path $ProjectDir 'build\obfuscate.ps1'
$mappingDir      = Join-Path $ProjectDir 'obfuscation-mappings'
if (-not (Test-Path $mappingDir)) { New-Item -ItemType Directory $mappingDir | Out-Null }
& $obfuscateScript -DistDir (Join-Path $tmp 'dist') -MappingOutputDir $mappingDir
if ($LASTEXITCODE -ne 0) { Write-Error "Obfuscation failed"; exit 1 }

$items = @(Get-ChildItem $tmp)
if ($items.Count -gt 0) {
    Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $zipDest -Force
    Write-Host "payload.zip created at $zipDest"
} else {
    Write-Warning "No payload items found - payload.zip not created"
}

Remove-Item $tmp -Recurse -Force
