<#
.SYNOPSIS
    Builds DynamoCopilot-Setup.exe (custom WPF installer).

.DESCRIPTION
    1. Publishes both TFMs (net48 and net8.0-windows) to a staging dist\ folder.
    2. Builds the WPF installer project (installer-wpf\).
    3. Copies dist\ into the installer output folder.
    4. Final deliverable: installer-wpf\Output\DynamoCopilot-Setup.exe
                          installer-wpf\Output\dist\

    Requires:
      - .NET 8 SDK

.PARAMETER Version
    Version string baked into the installer assembly (default: "1.0.0").

.USAGE
    .\build-installer.ps1
    .\build-installer.ps1 -Version "1.2.0"
#>

param(
    [string]$Version = "1.0.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot      = $PSScriptRoot
$ExtProj       = Join-Path $RepoRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
$InstallerProj = Join-Path $RepoRoot "installer-wpf\DynamoCopilot.Installer.csproj"
$StagingDist   = Join-Path $RepoRoot "installer-wpf\staging-dist"
$OutputDir     = Join-Path $RepoRoot "installer-wpf\Output"

# ── Clean staging ─────────────────────────────────────────────────────────────

foreach ($Dir in @($StagingDist, $OutputDir)) {
    if (Test-Path $Dir) {
        Remove-Item $Dir -Recurse -Force
        Write-Host "Cleaned: $Dir" -ForegroundColor DarkGray
    }
}

# ── Publish extension DLLs (both TFMs) ───────────────────────────────────────

$RequiredDlls = @(
    "DynamoCopilot.Extension.dll"
    "DynamoCopilot.Core.dll"
    "DynamoCopilot.GraphInterop.dll"
)

foreach ($Tfm in @("net48", "net8.0-windows")) {
    Write-Host "`n==> Publishing extension ($Tfm) ..." -ForegroundColor Cyan

    $Out = Join-Path $StagingDist $Tfm

    & dotnet publish $ExtProj `
        --configuration Release `
        --framework      $Tfm `
        --output         $Out `
        --no-self-contained

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Tfm" }

    foreach ($Dll in $RequiredDlls) {
        if (-not (Test-Path (Join-Path $Out $Dll))) {
            throw "Expected DLL not found after publish: $Tfm\$Dll"
        }
    }

    Write-Host "    OK: $Out" -ForegroundColor Green
}

# ── Build WPF installer ───────────────────────────────────────────────────────

Write-Host "`n==> Building WPF installer (v$Version) ..." -ForegroundColor Cyan

& dotnet publish $InstallerProj `
    --configuration Release `
    --framework     net8.0-windows `
    --output        $OutputDir `
    --no-self-contained `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version

if ($LASTEXITCODE -ne 0) { throw "WPF installer build failed" }

Write-Host "    Published installer to: $OutputDir" -ForegroundColor Green

# ── Copy dist into the installer output folder ────────────────────────────────

Write-Host "`n==> Copying dist into output ..." -ForegroundColor Cyan

$OutputDist = Join-Path $OutputDir "dist"
Copy-Item $StagingDist $OutputDist -Recurse -Force

# Clean staging (no longer needed)
Remove-Item $StagingDist -Recurse -Force

Write-Host "    dist\ ready at: $OutputDist" -ForegroundColor Green

# ── Summary ───────────────────────────────────────────────────────────────────

$ExePath = Join-Path $OutputDir "DynamoCopilot-Setup.exe"
if (-not (Test-Path $ExePath)) {
    throw "Installer exe not found: $ExePath"
}

$SizeMb = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)

Write-Host ""
Write-Host "==> Done! Installer ready ($SizeMb MB)" -ForegroundColor Green
Write-Host "    Exe:  $ExePath"                     -ForegroundColor Green
Write-Host "    Dist: $OutputDist"                  -ForegroundColor Green
Write-Host ""
Write-Host "    Distribute the entire Output\ folder (exe + dist\)." -ForegroundColor DarkGray
