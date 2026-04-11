<#
.SYNOPSIS
    Installs DynamoCopilot for a user from a release zip.

.DESCRIPTION
    Run this script as the end-user to install the extension.
    It does NOT require the .NET SDK — only the release zip produced by CI/CD.

    Steps:
    1. Extracts DLLs from the zip into %AppData%\DynamoCopilot\<tfm>\.
    2. Detects installed Dynamo versions (Revit + Dynamo Sandbox).
    3. Writes ViewExtensionDefinition XML with the correct absolute path.
    4. Copies the XML into each detected viewExtensions folder.

.PARAMETER ZipPath
    Path to the DynamoCopilot-release.zip file.
    If omitted, the script looks for it in the same folder as install.ps1.

.USAGE
    # From a folder containing install.ps1 and DynamoCopilot-release.zip:
    .\install.ps1

    # Or specify the zip explicitly:
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
    Write-Error "Release zip not found: $ZipPath`nDownload DynamoCopilot-release.zip and place it next to install.ps1."
    exit 1
}

$AppData  = [Environment]::GetFolderPath("ApplicationData")
$DestBase = Join-Path $AppData "DynamoCopilot"

# ── Extract zip ──────────────────────────────────────────────────────────────

Write-Host "Extracting release zip..." -ForegroundColor Cyan
$ExtractDir = Join-Path $env:TEMP "DynamoCopilotInstall_$(Get-Random)"
Expand-Archive -Path $ZipPath -DestinationPath $ExtractDir -Force

# Expected zip layout:
#   net48\               ← net48 DLLs
#   net8.0-windows\      ← net8 DLLs

foreach ($Tfm in @("net48","net8.0-windows")) {
    $Src = Join-Path $ExtractDir $Tfm
    if (-not (Test-Path $Src)) { continue }

    $Dest = Join-Path $DestBase $Tfm
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Copy-Item "$Src\*" $Dest -Recurse -Force
    Write-Host "  Installed $Tfm -> $Dest" -ForegroundColor Green
}

Remove-Item $ExtractDir -Recurse -Force

# ── Dynamo detection ─────────────────────────────────────────────────────────

# Key: path to viewExtensions folder   Value: TFM that folder needs
$DynamoCandidates = [ordered]@{
    # Dynamo for Revit (net48)
    "$env:ProgramFiles\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions" = "net48"
    # Dynamo Sandbox 3.x (net8)
    "$env:ProgramFiles\Dynamo\Dynamo Core\3\viewExtensions"                       = "net8.0-windows"
    # Dynamo Sandbox 2.x (net48)
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.19\viewExtensions"                    = "net48"
}

$Installed = 0

foreach ($Entry in $DynamoCandidates.GetEnumerator()) {
    $DynPath = $Entry.Key
    $Tfm     = $Entry.Value

    if (-not (Test-Path $DynPath)) { continue }

    # Read the correct template and substitute the real AppData path
    $TemplateName = if ($Tfm -eq "net48") { "net48" } else { "net8" }
    $TemplateFile = Join-Path $PSScriptRoot "DynamoCopilot_ViewExtensionDefinition.$TemplateName.xml.template"

    # If templates aren't bundled with the installer zip, generate the XML directly
    if (Test-Path $TemplateFile) {
        $XmlContent = (Get-Content $TemplateFile -Raw) -replace '\{\{APPDATA\}\}', $AppData
    } else {
        $DllPath    = Join-Path $DestBase "$Tfm\DynamoCopilot.Extension.dll"
        $XmlContent = @"
<?xml version="1.0" encoding="utf-8"?>
<ViewExtensionDefinition>
  <AssemblyPath>$DllPath</AssemblyPath>
  <TypeName>DynamoCopilot.Extension.DynamoCopilotViewExtension</TypeName>
</ViewExtensionDefinition>
"@
    }

    $XmlDest = Join-Path $DynPath "DynamoCopilot_ViewExtensionDefinition.xml"
    Set-Content -Path $XmlDest -Value $XmlContent -Encoding UTF8
    Write-Host "  Registered in Dynamo: $DynPath" -ForegroundColor Green
    $Installed++
}

if ($Installed -eq 0) {
    Write-Warning "No Dynamo installation found at the expected paths.`nManually copy DynamoCopilot_ViewExtensionDefinition.xml to your Dynamo viewExtensions folder."
    Write-Host "DLLs are at: $DestBase" -ForegroundColor Yellow
} else {
    Write-Host "`nInstallation complete. Start or restart Dynamo to use DynamoCopilot." -ForegroundColor Green
}
