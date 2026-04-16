<#
.SYNOPSIS
    Installs DynamoCopilot for an end-user from a release zip.

.DESCRIPTION
    Does NOT require the .NET SDK — only the release zip from CI/CD.

    Steps:
    1. Extracts DLLs from the zip into %AppData%\DynamoCopilot\<tfm>\.
    2. Creates settings.json with defaults (preserves existing file).
    3. Detects all installed Dynamo versions on this machine.
    4. Writes a ViewExtensionDefinition XML with the correct absolute path
       and copies it into each Dynamo viewExtensions folder found.

    TFM rules:
        Revit 2023, 2024            -> net48
        Revit 2025 and above        -> net8.0-windows
        Dynamo Sandbox 2.x          -> net48
        Dynamo Sandbox 3.x          -> net8.0-windows

.PARAMETER ZipPath
    Path to DynamoCopilot-release.zip.
    If omitted, looks in the same folder as install.ps1.

.USAGE
    .\install.ps1
    .\install.ps1 -ZipPath "C:\Downloads\DynamoCopilot-release.zip"
#>

param(
    [string]$ZipPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Locate zip ───────────────────────────────────────────────────────────────

if ([string]::IsNullOrEmpty($ZipPath)) {
    $ZipPath = Join-Path $PSScriptRoot "DynamoCopilot-release.zip"
}

if (-not (Test-Path $ZipPath)) {
    Write-Error "Release zip not found: $ZipPath`nPlace DynamoCopilot-release.zip next to install.ps1 and re-run."
    exit 1
}

$AppData  = [Environment]::GetFolderPath("ApplicationData")
$DestBase = Join-Path $AppData "DynamoCopilot"

# ── Extract DLLs ─────────────────────────────────────────────────────────────
# Expected zip layout:
#   net48\              <- net48 DLLs
#   net8.0-windows\     <- net8 DLLs

Write-Host "Extracting..." -ForegroundColor Cyan
$ExtractDir = Join-Path $env:TEMP "DynamoCopilotInstall_$(Get-Random)"
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force

foreach ($Tfm in @("net48", "net8.0-windows")) {
    $Src = Join-Path $ExtractDir $Tfm
    if (-not (Test-Path $Src)) { continue }

    $Dest = Join-Path $DestBase $Tfm
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Copy-Item "$Src\*" $Dest -Recurse -Force
    Write-Host "  DLLs ($Tfm) -> $Dest" -ForegroundColor Green
}

Remove-Item $ExtractDir -Recurse -Force

# ── Create / update settings.json ────────────────────────────────────────────
# Always run after install to ensure useLocalServer is false and all fields exist.
# Preserves user-customised values (e.g. maxHistoryMessages) when the file exists.

$SettingsFile = Join-Path $DestBase "settings.json"
New-Item -ItemType Directory -Path $DestBase -Force | Out-Null

if (Test-Path $SettingsFile) {
    # Merge: read existing, enforce required fields, write back
    try   { $s = Get-Content $SettingsFile -Raw | ConvertFrom-Json }
    catch { $s = [PSCustomObject]@{} }

    # Always force these — installer must point at production
    $s | Add-Member -Force -MemberType NoteProperty -Name useLocalServer  -Value $false
    $s | Add-Member -Force -MemberType NoteProperty -Name serverUrl       -Value "https://radiant-determination-production.up.railway.app"

    # Add missing fields without overwriting user values
    if (-not $s.PSObject.Properties['maxHistoryMessages']) {
        $s | Add-Member -MemberType NoteProperty -Name maxHistoryMessages -Value 40
    }
    if (-not $s.PSObject.Properties['localServerUrl']) {
        $s | Add-Member -MemberType NoteProperty -Name localServerUrl -Value "http://localhost:8080"
    }

    $s | ConvertTo-Json -Depth 4 | Set-Content -Path $SettingsFile -Encoding UTF8
    Write-Host "  settings.json updated: $SettingsFile" -ForegroundColor Green
}
else {
    $DefaultSettings = [ordered]@{
        serverUrl          = "https://radiant-determination-production.up.railway.app"
        maxHistoryMessages = 40
        useLocalServer     = $false
        localServerUrl     = "http://localhost:8080"
    } | ConvertTo-Json -Depth 4
    Set-Content -Path $SettingsFile -Value $DefaultSettings -Encoding UTF8
    Write-Host "  settings.json created: $SettingsFile" -ForegroundColor Green
}

# ── Dynamo path → required TFM ───────────────────────────────────────────────

$DynamoInstalls = [ordered]@{
    # Revit-hosted Dynamo
    "$env:ProgramFiles\Autodesk\Revit 2026\AddIns\DynamoForRevit\viewExtensions" = "net8.0-windows"
    "$env:ProgramFiles\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions" = "net8.0-windows"
    "$env:ProgramFiles\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2022\AddIns\DynamoForRevit\viewExtensions" = "net48"
    # Dynamo Sandbox
    "$env:ProgramFiles\Dynamo\Dynamo Core\3\viewExtensions"                       = "net8.0-windows"
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.19\viewExtensions"                    = "net48"
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.18\viewExtensions"                    = "net48"
}

# ── Write XML into each Dynamo install found ──────────────────────────────────

$Installed = 0

foreach ($Entry in $DynamoInstalls.GetEnumerator()) {
    $DynPath = $Entry.Key
    $Tfm     = $Entry.Value

    if (-not (Test-Path $DynPath)) { continue }

    # Build XML inline (no template file dependency for end-user installs)
    $DllPath    = Join-Path $DestBase "$Tfm\DynamoCopilot.Extension.dll"
    $XmlContent = @"
<?xml version="1.0" encoding="utf-8"?>
<ViewExtensionDefinition>
  <AssemblyPath>$DllPath</AssemblyPath>
  <TypeName>DynamoCopilot.Extension.DynamoCopilotViewExtension</TypeName>
</ViewExtensionDefinition>
"@

    $XmlDest = Join-Path $DynPath "DynamoCopilot_ViewExtensionDefinition.xml"
    Set-Content -Path $XmlDest -Value $XmlContent -Encoding UTF8
    Write-Host "  Registered -> $DynPath" -ForegroundColor Green
    $Installed++
}

if ($Installed -eq 0) {
    Write-Warning "No Dynamo installation found at the default paths."
    Write-Host "DLLs are at: $DestBase" -ForegroundColor Yellow
    Write-Host "Manually create a ViewExtensionDefinition.xml in your Dynamo viewExtensions folder pointing to:" -ForegroundColor Yellow
    Write-Host "  $DestBase\<tfm>\DynamoCopilot.Extension.dll" -ForegroundColor Yellow
} else {
    Write-Host "`nInstallation complete ($Installed Dynamo install(s) registered)." -ForegroundColor Green
    Write-Host "Start or restart Dynamo to use DynamoCopilot." -ForegroundColor Green
}
