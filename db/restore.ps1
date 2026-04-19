# db/restore.ps1
# On a new PC: applies EF migrations then restores seed data.
# Run from repo root: .\db\restore.ps1

param(
    [string]$Host     = "localhost",
    [string]$Port     = "5433",
    [string]$Username = "postgres",
    [string]$Database = "dynamocopilot_dev",
    [string]$Seed     = "$PSScriptRoot\seed.sql"
)

$env:PGPASSWORD = Read-Host "Enter postgres password" -AsSecureString | `
    ForEach-Object { [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) }

$psql = "psql"

if (-not (Get-Command $psql -ErrorAction SilentlyContinue)) {
    $candidates = @(
        "C:\Program Files\PostgreSQL\17\bin\psql.exe",
        "C:\Program Files\PostgreSQL\16\bin\psql.exe",
        "C:\Program Files\PostgreSQL\15\bin\psql.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $psql = $c; break }
    }
}

# Step 1 — apply EF Core migrations (creates all tables)
Write-Host "Step 1: Applying EF Core migrations..."
Push-Location "$PSScriptRoot\..\src\DynamoCopilot.Server"
dotnet ef database update
if ($LASTEXITCODE -ne 0) {
    Write-Host "Migration failed. Make sure dotnet-ef is installed: dotnet tool install -g dotnet-ef" -ForegroundColor Red
    Pop-Location; exit 1
}
Pop-Location

# Step 2 — load seed data
if (-not (Test-Path $Seed)) {
    Write-Host "No seed.sql found at $Seed — skipping data restore." -ForegroundColor Yellow
    Write-Host "Schema is ready. Run the app and start using it, or pull seed.sql from GitHub."
    exit 0
}

Write-Host "Step 2: Restoring seed data from $Seed ..."
& $psql `
    --host=$Host `
    --port=$Port `
    --username=$Username `
    --dbname=$Database `
    --file=$Seed

if ($LASTEXITCODE -eq 0) {
    Write-Host "All done. Database is ready." -ForegroundColor Green
} else {
    Write-Host "psql restore failed (exit $LASTEXITCODE)." -ForegroundColor Red
}
