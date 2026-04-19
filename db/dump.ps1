# db/dump.ps1
# Dumps data-only from the local dev database to db/seed.sql
# Run from repo root: .\db\dump.ps1

param(
    [string]$Host     = "localhost",
    [string]$Port     = "5433",
    [string]$Username = "postgres",
    [string]$Database = "dynamocopilot_dev",
    [string]$Output   = "$PSScriptRoot\seed.sql"
)

$env:PGPASSWORD = Read-Host "Enter postgres password" -AsSecureString | `
    ForEach-Object { [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($_)) }

$pgDump = "pg_dump"

# Try common pgAdmin install paths if pg_dump isn't on PATH
if (-not (Get-Command $pgDump -ErrorAction SilentlyContinue)) {
    $candidates = @(
        "C:\Program Files\PostgreSQL\17\bin\pg_dump.exe",
        "C:\Program Files\PostgreSQL\16\bin\pg_dump.exe",
        "C:\Program Files\PostgreSQL\15\bin\pg_dump.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $pgDump = $c; break }
    }
}

Write-Host "Dumping data from $Database to $Output ..."

& $pgDump `
    --host=$Host `
    --port=$Port `
    --username=$Username `
    --dbname=$Database `
    --data-only `
    --inserts `
    --exclude-table="__EFMigrationsHistory" `
    --file=$Output

if ($LASTEXITCODE -eq 0) {
    Write-Host "Done. Commit db\seed.sql and push to GitHub."
} else {
    Write-Host "pg_dump failed (exit $LASTEXITCODE). Check connection settings." -ForegroundColor Red
}
