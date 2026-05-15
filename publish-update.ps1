<#
.SYNOPSIS
    Builds and publishes a DLL-only update for existing users.
    No new installer exe is needed — users download just the changed DLLs (~5 MB).

.DESCRIPTION
    1. Publishes both TFMs of the Extension project to a staging folder.
    2. Builds DynamoCopilot.Updater.exe (net48) and adds it to the staging root.
    3. Zips the staging folder → dlls-v{Version}.zip.
    4. Uploads the zip to the GitHub release as a release asset.
    5. Calls POST /admin/release on your server to publish the version manifest.

    When users open Dynamo, the extension checks GET /api/version/latest, sees a
    newer version, shows a banner, downloads the zip, and stages it for next launch.

.PARAMETER Version
    New version string (e.g. "1.0.4"). Must be a valid System.Version string.

.PARAMETER Notes
    Release notes shown in the banner (optional, 1–2 sentences).

.PARAMETER MinVersion
    Minimum required version. Users below this see a hard blocker. Default "1.0.0".
    Set this equal to $Version only when the server API changed in a breaking way.

.PARAMETER AdminKey
    Your Admin:ApiKey value. Reads from env var DYNAMO_ADMIN_KEY if not supplied.

.PARAMETER ServerUrl
    Your Railway server URL. If not supplied, reads from DynamoCopilotSettings.cs
    (the same URL the extension itself uses — single source of truth).

.PARAMETER GitHubRepo
    GitHub repo in "owner/name" format. Default: BuvaneshSrinivasan/Copilot-Dynamo-Extension.

.PARAMETER GitHubTag
    GitHub release tag to upload the asset to. Default: v1.0.0 (the release that
    already hosts nodes.db and model.onnx — just add the DLL zip alongside them).

.PARAMETER DbVersion
    Optional nodes.db date string (e.g. "2026-05-15"). Include when you also upload
    a new nodes.db to GitHub and want the extension to offer a DB update notification.

.PARAMETER DbUrl
    GitHub download URL for the new nodes.db (only needed with -DbVersion).

.PARAMETER DbSizeBytes
    Byte size of the new nodes.db (only needed with -DbVersion).

.USAGE
    # Most releases — DLLs only:
    .\publish-update.ps1 -Version "1.0.4" -Notes "Fixed node insertion bug"

    # Breaking server API change — force all users to update:
    .\publish-update.ps1 -Version "1.0.5" -Notes "New auth flow" -MinVersion "1.0.5"

    # DLLs + new nodes.db:
    .\publish-update.ps1 -Version "1.0.4" -Notes "..." -DbVersion "2026-05-15" `
        -DbUrl "https://github.com/.../releases/download/v1.0.0/nodes.db" `
        -DbSizeBytes 195035136
#>

param(
    [string]$Version    = "",   # Leave blank to read from the Extension .csproj <Version> tag
    [string]$Notes        = "",
    [string]$MinVersion   = "1.0.0",
    [string]$AdminKey     = $env:DYNAMO_ADMIN_KEY,
    [string]$ServerUrl    = "",   # Leave blank to read from DynamoCopilotSettings.cs
    [string]$GitHubRepo   = "BuvaneshSrinivasan/Copilot-Dynamo-Extension",
    [string]$GitHubTag    = "v1.0.0",
    [string]$DbVersion    = "",
    [string]$DbUrl        = "",
    [long]  $DbSizeBytes  = 0
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

# ── Resolve server URL from DynamoCopilotSettings.cs if not supplied ──────────
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    # Try env var first, then fall back to the hardcoded default in Settings.cs
    if (-not [string]::IsNullOrWhiteSpace($env:DYNAMO_SERVER_URL)) {
        $ServerUrl = $env:DYNAMO_SERVER_URL
    } else {
        $settingsPath = Join-Path $PSScriptRoot "src\DynamoCopilot.Core\Settings\DynamoCopilotSettings.cs"
        $match = Select-String -Path $settingsPath -Pattern '"(https://[^"]+)"' | Select-Object -First 1
        if ($match) {
            $ServerUrl = $match.Matches[0].Groups[1].Value
            Write-Host "Server URL from DynamoCopilotSettings.cs: $ServerUrl" -ForegroundColor DarkGray
        } else {
            throw "Could not resolve ServerUrl. Pass -ServerUrl or set DYNAMO_SERVER_URL env var."
        }
    }
}

# ── Validate inputs ────────────────────────────────────────────────────────────

if ([string]::IsNullOrWhiteSpace($AdminKey)) {
    throw "AdminKey is required. Pass -AdminKey or set the DYNAMO_ADMIN_KEY env var."
}
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    throw "ServerUrl is required. Pass -ServerUrl or set the DYNAMO_SERVER_URL env var."
}

$ServerUrl = $ServerUrl.TrimEnd('/')
Write-Host "Publishing update v$Version → $ServerUrl" -ForegroundColor Cyan

# ── Paths ──────────────────────────────────────────────────────────────────────

$RepoRoot    = $PSScriptRoot
$ExtProj     = Join-Path $RepoRoot "src\DynamoCopilot.Extension\DynamoCopilot.Extension.csproj"
$UpdaterProj = Join-Path $RepoRoot "installer-wpf\DynamoCopilot.Updater\DynamoCopilot.Updater.csproj"
$StagingDir  = Join-Path $RepoRoot "update-staging"
$ZipName     = "dlls-v$Version.zip"
$ZipPath     = Join-Path $RepoRoot $ZipName

foreach ($Dir in @($StagingDir)) {
    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
}
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

# ── Publish extension DLLs (both TFMs) ────────────────────────────────────────

$RequiredDlls = @(
    "DynamoCopilot.Extension.dll"
    "DynamoCopilot.Core.dll"
    "DynamoCopilot.GraphInterop.dll"
)

foreach ($Tfm in @("net48", "net8.0-windows")) {
    Write-Host "`n==> Publishing Extension ($Tfm) ..." -ForegroundColor Cyan

    $Out = Join-Path $StagingDir $Tfm

    & dotnet publish $ExtProj `
        --configuration Release `
        --framework      $Tfm `
        --output         $Out `
        --no-self-contained `
        -p:Version=$Version `
        -p:AssemblyVersion=$Version

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Tfm" }

    foreach ($Dll in $RequiredDlls) {
        if (-not (Test-Path (Join-Path $Out $Dll))) {
            throw "Expected DLL missing after publish: $Tfm\$Dll"
        }
    }

    # Prune non-Windows ONNX runtimes to keep the zip small
    $RuntimesDir = Join-Path $Out "runtimes"
    if (Test-Path $RuntimesDir) {
        Get-ChildItem $RuntimesDir -Directory |
            Where-Object { $_.Name -notlike "win*" } |
            ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
    }

    Write-Host "    OK: $Out" -ForegroundColor Green
}

# ── Build Updater (net48) ──────────────────────────────────────────────────────

Write-Host "`n==> Building DynamoCopilot.Updater (net48) ..." -ForegroundColor Cyan

$UpdaterOut = Join-Path $RepoRoot "updater-out"

& dotnet publish $UpdaterProj `
    --configuration Release `
    --framework     net48 `
    --output        $UpdaterOut `
    -p:Version=$Version `
    -p:AssemblyVersion=$Version

if ($LASTEXITCODE -ne 0) { throw "Updater build failed" }

# Place Updater.exe at the staging root so it lands in %AppData%\DynamoCopilot\
Copy-Item (Join-Path $UpdaterOut "DynamoCopilot.Updater.exe") `
          (Join-Path $StagingDir "DynamoCopilot.Updater.exe") -Force

Remove-Item $UpdaterOut -Recurse -Force
Write-Host "    Updater.exe added to staging root." -ForegroundColor Green

# ── Zip the staging folder ─────────────────────────────────────────────────────

Write-Host "`n==> Creating $ZipName ..." -ForegroundColor Cyan

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($StagingDir, $ZipPath)

$ZipSizeMb    = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
$ZipSizeBytes = (Get-Item $ZipPath).Length
Write-Host "    $ZipName ready ($ZipSizeMb MB)" -ForegroundColor Green

Remove-Item $StagingDir -Recurse -Force

# ── Upload to GitHub Releases ──────────────────────────────────────────────────

Write-Host "`n==> Uploading $ZipName to GitHub release $GitHubTag ..." -ForegroundColor Cyan

gh release upload $GitHubTag $ZipPath `
    --repo  $GitHubRepo `
    --clobber

if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }

$DownloadUrl = "https://github.com/$GitHubRepo/releases/download/$GitHubTag/$ZipName"
Write-Host "    Uploaded: $DownloadUrl" -ForegroundColor Green

Remove-Item $ZipPath -Force

# ── Publish release manifest to server ────────────────────────────────────────

Write-Host "`n==> Publishing manifest to $ServerUrl/admin/release ..." -ForegroundColor Cyan

$body = @{
    version       = $Version
    minVersion    = $MinVersion
    releaseNotes  = $Notes
    dllsUrl       = $DownloadUrl
    dllsSizeBytes = $ZipSizeBytes
}

if ($DbVersion -and $DbUrl -and $DbSizeBytes -gt 0) {
    $body["dbVersion"]   = $DbVersion
    $body["dbUrl"]       = $DbUrl
    $body["dbSizeBytes"] = $DbSizeBytes
    Write-Host "    Including nodes.db update (dbVersion=$DbVersion)" -ForegroundColor DarkGray
}

$bodyJson = $body | ConvertTo-Json -Compress

$response = Invoke-RestMethod `
    -Uri     "$ServerUrl/admin/release" `
    -Method  POST `
    -Headers @{ "X-Admin-Key" = $AdminKey; "Content-Type" = "application/json" } `
    -Body    $bodyJson

Write-Host "    Server response: $($response | ConvertTo-Json -Compress)" -ForegroundColor Green

# ── Summary ────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "==> Done! v$Version is live." -ForegroundColor Green
Write-Host "    DLL zip:    $DownloadUrl" -ForegroundColor Green
Write-Host "    Size:       $ZipSizeMb MB" -ForegroundColor Green
Write-Host "    MinVersion: $MinVersion" -ForegroundColor Green
Write-Host ""
Write-Host "Users will see the update banner the next time they open Dynamo." -ForegroundColor DarkGray
