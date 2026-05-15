<#
.SYNOPSIS
    Builds DynamoCopilot-Setup.exe (bootstrapper + WPF installer).

.DESCRIPTION
    1. Publishes both TFMs (net48 and net8.0-windows) to a staging dist\ folder.
    2. Builds the WPF installer (net8.0-windows) — the "inner" installer.
    3. Appends the DLL payload zip to the inner installer exe.
    4. Embeds the inner installer into the net48 Bootstrapper project.
    5. Builds the Bootstrapper → final DynamoCopilot-Setup.exe.

    The bootstrapper (net48) always runs on Windows 10/11. It checks for the
    .NET 8 Desktop Runtime, downloads and installs it if missing, then extracts
    and launches the inner WPF installer.

    Requires:
      - .NET 8 SDK

.PARAMETER Version
    Version string baked into both installer assemblies (default: "1.0.0").

.USAGE
    .\build-installer.ps1
    .\build-installer.ps1 -Version "1.2.0"
#>

param(
    [string]$Version = ""   # Leave blank to read from the Extension .csproj <Version> tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resolve version from .csproj if not supplied ──────────────────────────────
if ([string]::IsNullOrWhiteSpace($Version)) {
    $csprojPath = Join-Path $PSScriptRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
    $xml = [xml](Get-Content $csprojPath)
    $Version = ($xml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($Version)) { throw "No <Version> tag found in $csprojPath" }
    Write-Host "Version from .csproj: $Version" -ForegroundColor DarkGray
}

$RepoRoot             = $PSScriptRoot
$ExtProj              = Join-Path $RepoRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
$UpdaterProj          = Join-Path $RepoRoot "installer-wpf\DynamoCopilot.Updater\DynamoCopilot.Updater.csproj"
$InstallerProj        = Join-Path $RepoRoot "installer-wpf\DynamoCopilot.Installer.csproj"
$BootstrapperProj     = Join-Path $RepoRoot "installer-wpf\Bootstrapper\Bootstrapper.csproj"
$StagingDist          = Join-Path $RepoRoot "installer-wpf\staging-dist"
$OutputDir            = Join-Path $RepoRoot "installer-wpf\Output"          # WPF installer + final exe
$BootstrapperOut      = Join-Path $RepoRoot "installer-wpf\BootstrapperOut" # temp: bootstrapper publish

# ── Clean staging ─────────────────────────────────────────────────────────────

foreach ($Dir in @($StagingDist, $OutputDir, $BootstrapperOut)) {
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

    # Prune non-Windows ONNX runtime folders — dotnet publish for net8 pulls in
    # native libs for every platform (iOS, macOS, Android, Linux…). Only win-* is needed.
    $RuntimesDir = Join-Path $Out "runtimes"
    if (Test-Path $RuntimesDir) {
        Get-ChildItem $RuntimesDir -Directory |
            Where-Object { $_.Name -notlike "win*" } |
            ForEach-Object {
                Remove-Item $_.FullName -Recurse -Force
                Write-Host "    Pruned: runtimes\$($_.Name)" -ForegroundColor DarkGray
            }
    }
}

# ── Build Updater (net48 helper — applied after Dynamo closes) ───────────────

Write-Host "`n==> Building DynamoCopilot.Updater (net48) ..." -ForegroundColor Cyan

$UpdaterOut = Join-Path $RepoRoot "installer-wpf\UpdaterOut"

& dotnet publish $UpdaterProj `
    --configuration Release `
    --framework     net48 `
    --output        $UpdaterOut `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version

if ($LASTEXITCODE -ne 0) { throw "Updater build failed" }

# Place Updater.exe at the root of staging-dist so it lands in
# %AppData%\DynamoCopilot\DynamoCopilot.Updater.exe (not inside a TFM folder).
Copy-Item (Join-Path $UpdaterOut "DynamoCopilot.Updater.exe") `
          (Join-Path $StagingDist "DynamoCopilot.Updater.exe") -Force

Remove-Item $UpdaterOut -Recurse -Force

Write-Host "    Updater.exe placed in staging-dist root." -ForegroundColor Green

# ── Build WPF installer (inner, net8.0-windows) ──────────────────────────────

Write-Host "`n==> Building WPF installer - inner (v$Version) ..." -ForegroundColor Cyan

& dotnet publish $InstallerProj `
    --configuration Release `
    --framework     net8.0-windows `
    --output        $OutputDir `
    --no-self-contained `
    -p:PublishSingleFile=true `
    -p:Version=$Version

if ($LASTEXITCODE -ne 0) { throw "WPF installer build failed" }

Write-Host "    Published inner installer to: $OutputDir" -ForegroundColor Green

# ── Copy dist into the output folder ─────────────────────────────────────────

Write-Host "`n==> Copying dist into output ..." -ForegroundColor Cyan

$OutputDist = Join-Path $OutputDir "dist"
Copy-Item $StagingDist $OutputDist -Recurse -Force

Remove-Item $StagingDist -Recurse -Force

Write-Host "    dist\ ready at: $OutputDist" -ForegroundColor Green

# ── Append DLL payload zip to the inner installer exe ────────────────────────
# create_payload.ps1 expects $ProjectDir\Output\dist — pass installer-wpf\ as before.

Write-Host "`n==> Embedding payload into inner installer ..." -ForegroundColor Cyan

$InstallerDir  = Join-Path $RepoRoot "installer-wpf"
$PayloadScript = Join-Path $InstallerDir "build\create_payload.ps1"
$AppendScript  = Join-Path $InstallerDir "build\append_payload.ps1"
$PayloadZip    = Join-Path $InstallerDir "payload.zip"
$InnerExePath  = Join-Path $OutputDir "DynamoCopilot-Setup.exe"

if (-not (Test-Path $InnerExePath))   { throw "Inner installer exe not found: $InnerExePath" }
if (-not (Test-Path $PayloadScript))  { throw "create_payload.ps1 not found: $PayloadScript" }
if (-not (Test-Path $AppendScript))   { throw "append_payload.ps1 not found: $AppendScript" }

& $PayloadScript -ProjectDir $InstallerDir
if ($LASTEXITCODE -ne 0) { throw "create_payload.ps1 failed" }

& $AppendScript -ExePath $InnerExePath -ZipPath $PayloadZip
if ($LASTEXITCODE -ne 0) { throw "append_payload.ps1 failed" }

if (Test-Path $PayloadZip) { Remove-Item $PayloadZip -Force }

Write-Host "    Inner installer with payload ready: $InnerExePath" -ForegroundColor Green

# ── Wrap in net48 Bootstrapper ────────────────────────────────────────────────
# The bootstrapper is a net48 WinForms exe — always runs on Windows 10/11 without
# any pre-installed runtime. It checks for .NET 8, downloads it if missing,
# then extracts and launches the inner WPF installer (embedded as a resource).

Write-Host "`n==> Building net48 Bootstrapper (embeds inner installer) ..." -ForegroundColor Cyan

$BootstrapperDir   = Join-Path $RepoRoot "installer-wpf\Bootstrapper"
$BootstrapperInner = Join-Path $BootstrapperDir "inner-installer.exe"

Copy-Item $InnerExePath $BootstrapperInner -Force
Write-Host "    Copied inner installer → Bootstrapper\inner-installer.exe" -ForegroundColor DarkGray

& dotnet publish $BootstrapperProj `
    --configuration Release `
    --framework     net48 `
    --output        $BootstrapperOut `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version

if ($LASTEXITCODE -ne 0) { throw "Bootstrapper build failed" }

# Remove the embedded resource from the source tree (it's a build artifact).
Remove-Item $BootstrapperInner -Force

# Replace the inner exe in Output\ with the final bootstrapper exe.
Copy-Item (Join-Path $BootstrapperOut "DynamoCopilot-Setup.exe") $InnerExePath -Force
Remove-Item $BootstrapperOut -Recurse -Force

Write-Host "    Bootstrapper wrapped successfully." -ForegroundColor Green

# ── Summary ───────────────────────────────────────────────────────────────────

$FinalExe = Join-Path $OutputDir "DynamoCopilot-Setup.exe"
$SizeMb   = [math]::Round((Get-Item $FinalExe).Length / 1MB, 1)

Write-Host ""
Write-Host "==> Done! Installer ready ($SizeMb MB)" -ForegroundColor Green
Write-Host "    Exe: $FinalExe"                      -ForegroundColor Green
Write-Host "    Bootstrapper auto-installs .NET 8 Desktop Runtime if missing." -ForegroundColor DarkGray
Write-Host "    DLLs and runtimes are embedded; nodes.db downloaded at install time." -ForegroundColor DarkGray
