# DynamoCopilot – Deploy Script
# Builds the extension and copies output to all detected Dynamo viewExtensions folders.
#
# Usage:
#   .\install\deploy.ps1                   # Deploy to all detected Dynamo installations
#   .\install\deploy.ps1 -Configuration Release
#   .\install\deploy.ps1 -DryRun           # Print target paths without copying

param(
    [ValidateSet("Debug","Release")]
    [string]$Configuration = "Release",

    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Locate solution root ──────────────────────────────────────────────────────
$scriptDir  = Split-Path $MyInvocation.MyCommand.Path -Parent
$solutionDir = Split-Path $scriptDir -Parent

# ── Build ─────────────────────────────────────────────────────────────────────
Write-Host "Building DynamoCopilot ($Configuration)..." -ForegroundColor Cyan
Push-Location $solutionDir
try {
    dotnet build DynamoCopilot.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
} finally {
    Pop-Location
}

# ── Determine which TFM to deploy based on Dynamo version ─────────────────────
# Dynamo 2.x (Revit 2022-2023) uses .NET 4.8  → deploy net48 output
# Dynamo 3.x (Revit 2024-2025) uses .NET 8    → deploy net8.0-windows output

$tfmMap = @{
    "2.x" = "net48"
    "3.x" = "net8.0-windows"
}

# ── Discover Dynamo viewExtensions folders ────────────────────────────────────
#
# Common install paths for Dynamo (inside Revit):
#   Revit 2022: C:\Program Files\Autodesk\Revit 2022\AddIns\DynamoForRevit\
#   Revit 2023: C:\Program Files\Autodesk\Revit 2023\AddIns\DynamoForRevit\
#   Revit 2024: C:\Program Files\Autodesk\Revit 2024\AddIns\DynamoForRevit\
#   Revit 2025: C:\Program Files\Autodesk\Revit 2025\AddIns\DynamoForRevit\
# Each contains a "viewExtensions" subfolder.

$revitYears = 2022, 2023, 2024, 2025
$baseRevit   = "C:\Program Files\Autodesk"
$targets     = [System.Collections.Generic.List[hashtable]]::new()

foreach ($year in $revitYears) {
    $dynPath = Join-Path $baseRevit "Revit $year\AddIns\DynamoForRevit"
    if (Test-Path $dynPath) {
        $viewExtDir = Join-Path $dynPath "viewExtensions"
        $tfm = if ($year -ge 2024) { "net8.0-windows" } else { "net48" }
        $targets.Add(@{ ViewExtDir = $viewExtDir; TFM = $tfm; Year = $year })
        Write-Host "Found Revit $year → $viewExtDir  [TFM: $tfm]"
    }
}

if ($targets.Count -eq 0) {
    Write-Warning "No Dynamo/Revit installations found under '$baseRevit'."
    Write-Warning "Manually copy build output to your Dynamo viewExtensions folder."
    exit 1
}

# ── Copy files ────────────────────────────────────────────────────────────────
$extensionBin = Join-Path $solutionDir "src\DynamoCopilot.Extension\bin\$Configuration"

foreach ($t in $targets) {
    $src = Join-Path $extensionBin "$($t.TFM)"
    $dst = $t.ViewExtDir

    if (-not (Test-Path $src)) {
        Write-Warning "Build output not found: $src  (skipping Revit $($t.Year))"
        continue
    }

    Write-Host ""
    Write-Host "Deploying to Revit $($t.Year) [$($t.TFM)]..." -ForegroundColor Cyan
    Write-Host "  From: $src"
    Write-Host "  To:   $dst"

    if (-not $DryRun) {
        New-Item -ItemType Directory -Path $dst -Force | Out-Null

        # ── Remove previous deployment ─────────────────────────────────────────
        $oldFiles = @(
            "DynamoCopilot.Extension.dll"
            "DynamoCopilot.Core.dll"
            "DynamoCopilot.GraphInterop.dll"
            "DynamoCopilot_ViewExtensionDefinition.xml"
        )
        foreach ($f in $oldFiles) {
            $oldFile = Join-Path $dst $f
            if (Test-Path $oldFile) {
                Remove-Item $oldFile -Force
                Write-Host "  Removed: $f" -ForegroundColor DarkGray
            }
        }
        # Remove leftover web folder from previous WebView2-based build
        $oldWeb = Join-Path $dst "web"
        if (Test-Path $oldWeb) {
            Remove-Item $oldWeb -Recurse -Force
            Write-Host "  Removed: web\" -ForegroundColor DarkGray
        }
        # Remove old WebView2 loader DLLs if present
        Get-ChildItem $dst -Filter "*.dll" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^(Microsoft\.Web\.WebView2|WebView2Loader)" } |
            ForEach-Object {
                Remove-Item $_.FullName -Force
                Write-Host "  Removed: $($_.Name)" -ForegroundColor DarkGray
            }

        # ── Copy new build output ──────────────────────────────────────────────
        $filesToCopy = @(
            "DynamoCopilot.Extension.dll"
            "DynamoCopilot.Core.dll"
            "DynamoCopilot.GraphInterop.dll"
            "DynamoCopilot_ViewExtensionDefinition.xml"
        )

        foreach ($f in $filesToCopy) {
            $srcFile = Join-Path $src $f
            if (Test-Path $srcFile) {
                Copy-Item $srcFile $dst -Force
                Write-Host "  Copied: $f"
            } else {
                Write-Warning "  Missing: $f"
            }
        }
    } else {
        Write-Host "  [DRY RUN] Would remove old files and copy new output to $dst"
    }
}

Write-Host ""
Write-Host "Deployment complete." -ForegroundColor Green
Write-Host "Restart Revit/Dynamo to load the updated extension." -ForegroundColor Yellow
