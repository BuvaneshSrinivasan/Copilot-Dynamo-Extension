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
    .\build-local.ps1 -UseLocalServer          # build and point the extension at localhost:8080
#>

param(
    [ValidateSet("net48", "net8.0-windows", "both")]
    [string]$TargetFramework = "both",

    [switch]$UseLocalServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot    = if ($PSScriptRoot) { $PSScriptRoot } else { $PSCommandPath | Split-Path -Parent }
if (-not $RepoRoot) { $RepoRoot = (Get-Location).Path }
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
    $Found = 0
    $Placed = 0
    foreach ($Entry in $DynamoInstalls.GetEnumerator()) {
        if ($Entry.Value -ne $Tfm)          { continue }   # wrong TFM for this Dynamo
        if (-not (Test-Path $Entry.Key))    { continue }   # Dynamo not installed here

        $Found++
        $XmlDest = Join-Path $Entry.Key "DynamoCopilot_ViewExtensionDefinition.xml"
        try {
            Copy-Item $RefXml $XmlDest -Force
            Write-Host "    XML   -> $XmlDest" -ForegroundColor Green
            $Placed++
        }
        catch [System.UnauthorizedAccessException] {
            Write-Warning "Permission denied writing to $XmlDest. Run PowerShell as Administrator or copy the XML manually."
        }
        catch {
            Write-Warning ("Failed to write {0}: {1}" -f $XmlDest, $_)
        }
    }

    if ($Found -eq 0) {
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
        serverUrl           = "https://radiant-determination-production.up.railway.app"
        maxHistoryMessages  = 40
        useLocalServer      = $false
        localServerUrl      = "http://localhost:8080"
    } | ConvertTo-Json -Depth 4

    Set-Content -Path $SettingsFile -Value $DefaultSettings -Encoding UTF8
    Write-Host "`n    settings.json created: $SettingsFile" -ForegroundColor Green
    Write-Host "    Edit 'serverUrl' there if you need to point to a different server." -ForegroundColor DarkGray
}

function Set-LocalServerMode {
    param(
        [bool]$Enabled,
        [string]$Url = "http://localhost:8080"
    )

    $SettingsFile = Join-Path $DestBase "settings.json"
    if (-not (Test-Path $SettingsFile)) { return }

    $settings = Get-Content $SettingsFile -Raw | ConvertFrom-Json

    if (-not $settings.PSObject.Properties['useLocalServer']) {
        Add-Member -InputObject $settings -MemberType NoteProperty -Name useLocalServer -Value $Enabled
    }
    else {
        $settings.useLocalServer = $Enabled
    }

    if (-not $settings.PSObject.Properties['localServerUrl']) {
        Add-Member -InputObject $settings -MemberType NoteProperty -Name localServerUrl -Value $Url
    }
    else {
        $settings.localServerUrl = $Url
    }

    $settings | ConvertTo-Json -Depth 4 | Set-Content -Path $SettingsFile -Encoding UTF8

    Write-Host "    useLocalServer = $Enabled" -ForegroundColor DarkGray
    if ($Enabled) { Write-Host "    localServerUrl = $Url" -ForegroundColor DarkGray }
}

# ── Copy shared AI assets (models + node DB) ─────────────────────────────────

function Copy-Assets {
    # ONNX model + vocab
    $ModelsSrc = Join-Path $RepoRoot "assets\models"
    $ModelsDst = Join-Path $DestBase "models"
    if (Test-Path $ModelsSrc) {
        New-Item -ItemType Directory -Path $ModelsDst -Force | Out-Null
        Copy-Item "$ModelsSrc\*" $ModelsDst -Recurse -Force
        Write-Host "`n    Models -> $ModelsDst" -ForegroundColor Green
        "AFTER models copy" | Out-File "$env:TEMP\build-local-debug.txt" -Append
    }
    else {
        Write-Warning "assets\models not found — skipping ONNX model copy."
        "models not found" | Out-File "$env:TEMP\build-local-debug.txt" -Append
    }

    try { "AFTER if/else block" | Out-File "$env:TEMP\build-local-debug.txt" -Append } catch { "ERROR at AFTER if/else: $_" | Out-File "$env:TEMP\build-local-debug.txt" -Append }
    # Pre-built node vector DB
    $DbSrc = Join-Path $RepoRoot "assets\nodes.db"
    "nodes.db check: $DbSrc exists=$(Test-Path $DbSrc)" | Out-File "$env:TEMP\build-local-debug.txt" -Append
    Write-Host "`n    Looking for nodes.db at: $DbSrc" -ForegroundColor DarkGray
    if (Test-Path $DbSrc) {
        [System.IO.File]::Copy($DbSrc, (Join-Path $DestBase "nodes.db"), $true)
        Write-Host "    nodes.db -> $DestBase\nodes.db" -ForegroundColor Green
    }
    else {
        Write-Warning "assets\nodes.db not found at '$DbSrc' — node suggestions will use keyword search only."
        Write-Warning "Run the NodeIndexer (Mode 2) first to generate assets\nodes.db."
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

Ensure-Settings
Set-LocalServerMode -Enabled ([bool]$UseLocalServer)
Copy-Assets

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net48") {
    Build-And-Deploy "net48"
}

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net8.0-windows") {
    Build-And-Deploy "net8.0-windows"
}

Write-Host "`n==> Done. Restart Dynamo to pick up the changes." -ForegroundColor Green
