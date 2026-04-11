<#
.SYNOPSIS
    Builds DynamoCopilot and installs it for local Dynamo testing.

.DESCRIPTION
    1. Builds the Extension project for both net48 and net8.0-windows.
    2. Copies DLLs + dependencies to %AppData%\DynamoCopilot\<tfm>\.
    3. Writes a ViewExtensionDefinition XML with the correct absolute path.
    4. Copies the XML to any Dynamo 2.x / 3.x viewExtensions folders found.

.USAGE
    cd <repo root>
    .\build-local.ps1

    To build only one target framework:
    .\build-local.ps1 -TargetFramework net48
    .\build-local.ps1 -TargetFramework net8.0-windows
#>

param(
    [ValidateSet("net48","net8.0-windows","both")]
    [string]$TargetFramework = "both"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot
$ServerProj = Join-Path $RepoRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
$AppData    = [Environment]::GetFolderPath("ApplicationData")
$DestBase   = Join-Path $AppData "DynamoCopilot"

# ── Dynamo viewExtensions folder candidates ──────────────────────────────────
# Add or remove paths here if Dynamo is installed in a non-default location.

$DynamoPaths = @(
    # Dynamo for Revit 2024 / 2025 (net48)
    "$env:ProgramFiles\Autodesk\Revit 2025\AddIns\DynamoForRevit\viewExtensions",
    "$env:ProgramFiles\Autodesk\Revit 2024\AddIns\DynamoForRevit\viewExtensions",
    "$env:ProgramFiles\Autodesk\Revit 2023\AddIns\DynamoForRevit\viewExtensions",
    # Dynamo Sandbox (standalone, net8)
    "$env:ProgramFiles\Dynamo\Dynamo Core\3\viewExtensions",
    "$env:ProgramFiles\Dynamo\Dynamo Core\2.19\viewExtensions"
)

# ── Helper: build + copy one target framework ────────────────────────────────

function Build-And-Copy {
    param([string]$Tfm)

    Write-Host "`n==> Building $Tfm ..." -ForegroundColor Cyan

    $PublishDir = Join-Path $env:TEMP "DynamoCopilotPublish\$Tfm"
    if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }

    & dotnet publish $ServerProj `
        --configuration Release `
        --framework $Tfm `
        --output $PublishDir `
        --no-self-contained `
        | Write-Host

    if ($LASTEXITCODE -ne 0) { throw "Build failed for $Tfm" }

    # Copy to AppData destination
    $Dest = Join-Path $DestBase $Tfm
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Copy-Item "$PublishDir\*" $Dest -Recurse -Force
    Write-Host "    DLLs copied to: $Dest" -ForegroundColor Green

    # Write ViewExtensionDefinition XML
    $Template = Join-Path $RepoRoot "DynamoCopilot_ViewExtensionDefinition.$( $Tfm -replace '\.', '' | ForEach-Object { if ($_ -eq 'net48') {'net48'} else {'net8'} } ).xml.template"
    # Simpler: pick template by tfm
    if ($Tfm -eq "net48") {
        $Template = Join-Path $RepoRoot "DynamoCopilot_ViewExtensionDefinition.net48.xml.template"
    } else {
        $Template = Join-Path $RepoRoot "DynamoCopilot_ViewExtensionDefinition.net8.xml.template"
    }

    $XmlContent = (Get-Content $Template -Raw) -replace '\{\{APPDATA\}\}', $AppData
    $XmlFile    = Join-Path $DestBase "DynamoCopilot_ViewExtensionDefinition_$Tfm.xml"
    Set-Content -Path $XmlFile -Value $XmlContent -Encoding UTF8
    Write-Host "    XML written:     $XmlFile" -ForegroundColor Green

    # Copy XML to any installed Dynamo viewExtensions folders
    $IsNet8   = $Tfm.StartsWith("net8")
    foreach ($DynPath in $DynamoPaths) {
        $PathIsNet8 = $DynPath -match "Dynamo Core\\3"
        if ($PathIsNet8 -ne $IsNet8) { continue }   # match TFM to Dynamo generation
        if (-not (Test-Path $DynPath)) { continue }

        $XmlDest = Join-Path $DynPath "DynamoCopilot_ViewExtensionDefinition.xml"
        Copy-Item $XmlFile $XmlDest -Force
        Write-Host "    XML installed -> $XmlDest" -ForegroundColor Green
    }
}

# ── Main ─────────────────────────────────────────────────────────────────────

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net48") {
    Build-And-Copy "net48"
}

if ($TargetFramework -eq "both" -or $TargetFramework -eq "net8.0-windows") {
    Build-And-Copy "net8.0-windows"
}

Write-Host "`n==> Done. Restart Dynamo to pick up the updated extension." -ForegroundColor Green
