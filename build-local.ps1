<#
.SYNOPSIS
    Builds DynamoCopilot and deploys it locally for Dynamo testing.

.DESCRIPTION
    1. Builds the Extension project for the required target framework(s).
    2. Copies DLLs to %AppData%\DynamoCopilot\<tfm>\.
    3. Creates %AppData%\DynamoCopilot\settings.json (if not already present)
       so you can edit the server URL before launching Dynamo.
    4. Writes a ViewExtensionDefinition XML and copies it into every Dynamo
       viewExtensions folder found on this machine.

    TFM rules (matches Dynamo's .NET version per Revit year):
        Revit 2023, 2024            -> net48
        Revit 2025 and above        -> net8.0-windows
        Dynamo Sandbox 2.x          -> net48
        Dynamo Sandbox 3.x          -> net8.0-windows

.USAGE
    cd <repo root>
    .\build-local.ps1                          # builds both TFMs
    .\build-local.ps1 -TargetFramework net48
    .\build-local.ps1 -TargetFramework net8.0-windows
#>

param(
    [ValidateSet("net48", "net8.0-windows", "both")]
    [string]$TargetFramework = "both"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = $PSScriptRoot
$ExtProj     = Join-Path $RepoRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
$AppData     = [Environment]::GetFolderPath("ApplicationData")
$DestBase    = Join-Path $AppData "DynamoCopilot"

# ── Dynamo path → required TFM ───────────────────────────────────────────────
# Key  : viewExtensions folder path
# Value: TFM the DLLs in that folder must target
#
# Rule: Revit < 2025 ships with Dynamo built against .NET Framework 4.8 (net48).
#       Revit 2025+ ships with Dynamo built against .NET 8 (net8.0-windows).
#       Add new Revit years here as they release.

$DynamoInstalls = [ordered]@{
    # ── Revit-hosted Dynamo ───────────────────────────────────────────────────
    "$env:ProgramFiles\Autodesk\Revit 2026\AddIns\DynamoForRevit\viewExtensions" = "net8.0-windows"
    "$env:ProgramFiles\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions" = "net8.0-windows"
    "$env:ProgramFiles\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions" = "net48"
    "$env:ProgramFiles\Autodesk\Revit 2022\AddIns\DynamoForRevit\viewExtensions" = "net48"
    # ── Dynamo Sandbox ────────────────────────────────────────────────────────
    "$env:ProgramFiles\Dynamo\Dynamo Core\3\viewExtensions"                       = "net8.0-windows"
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.19\viewExtensions"                    = "net48"
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.18\viewExtensions"                    = "net48"
}

# ── Build helper ─────────────────────────────────────────────────────────────

function Build-And-Deploy {
    param([string]$Tfm)

    Write-Host "`n==> Building $Tfm ..." -ForegroundColor Cyan

    $PublishDir = Join-Path $env:TEMP "DynamoCopilotBuild\$Tfm"
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    & dotnet publish $ExtProj `
        --configuration Release `
        --framework $Tfm `
        --output $PublishDir `
        --no-self-contained

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Tfm" }

    # Copy DLLs to %AppData%\DynamoCopilot\<tfm>\
    $Dest = Join-Path $DestBase $Tfm
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Copy-Item "$PublishDir\*" $Dest -Recurse -Force
    Write-Host "    DLLs  -> $Dest" -ForegroundColor Green

    # Build the XML content (expand {{APPDATA}} to the real path)
    $TemplateName = if ($Tfm -eq "net48") { "net48" } else { "net8" }
    $Template     = Join-Path $RepoRoot "DynamoCopilot_ViewExtensionDefinition.$TemplateName.xml.template"
    $XmlContent   = (Get-Content $Template -Raw) -replace '\{\{APPDATA\}\}', $AppData

    # Save a copy in %AppData%\DynamoCopilot\ for reference / manual installs
    $RefXml = Join-Path $DestBase "DynamoCopilot_ViewExtensionDefinition_$Tfm.xml"
    Set-Content -Path $RefXml -Value $XmlContent -Encoding UTF8

    # Copy XML into every matching Dynamo install found on this machine
    $Placed = 0
    foreach ($Entry in $DynamoInstalls.GetEnumerator()) {
        if ($Entry.Value -ne $Tfm)          { continue }   # wrong TFM for this Dynamo
        if (-not (Test-Path $Entry.Key))    { continue }   # Dynamo not installed here

        $XmlDest = Join-Path $Entry.Key "DynamoCopilot_ViewExtensionDefinition.xml"
        Copy-Item $RefXml $XmlDest -Force
        Write-Host "    XML   -> $XmlDest" -ForegroundColor Green
        $Placed++
    }

    if ($Placed -eq 0) {
        Write-Warning "No $Tfm Dynamo install found at the default paths."
        Write-Host    "    Manually copy this XML into your Dynamo viewExtensions folder:" -ForegroundColor Yellow
        Write-Host    "    $RefXml" -ForegroundColor Yellow
    }
}

# ── Ensure settings.json exists (create with defaults if missing) ─────────────
# The file normally gets created the first time the extension loads inside Dynamo.
# We create it here so you can review / edit the server URL before launching.

function Ensure-Settings {
    $SettingsFile = Join-Path $DestBase "settings.json"
    if (Test-Path $SettingsFile) {
        Write-Host "`n    settings.json already exists: $SettingsFile" -ForegroundColor DarkGray
        return
    }

    New-Item -ItemType Directory -Path $DestBase -Force | Out-Null
    $DefaultSettings = @{
        serverUrl           = "https://copilot-dynamo-extension-production.up.railway.app"
        maxHistoryMessages  = 40
    } | ConvertTo-Json -Depth 2

    Set-Content -Path $SettingsFile -Value $DefaultSettings -Encoding UTF8
    Write-Host "`n    settings.json created: $SettingsFile" -ForegroundColor Green
    Write-Host "    Edit 'serverUrl' there if you need to point to a different server." -ForegroundColor DarkGray
}

# ── Main ─────────────────────────────────────────────────────────────────────

Ensure-Settings

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net48") {
    Build-And-Deploy "net48"
}

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net8.0-windows") {
    Build-And-Deploy "net8.0-windows"
}

Write-Host "`n==> Done. Restart Dynamo to pick up the changes." -ForegroundColor Green
