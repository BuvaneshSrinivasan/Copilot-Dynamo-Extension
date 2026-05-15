Update the Dynamo node database (nodes.db) incrementally, upload it to GitHub, and update the server manifest so users see an "Update database" button in the extension.

## Arguments
$ARGUMENTS

- No argument     → incremental update (only new/changed packages since last build)
- "full"          → full rebuild from scratch (all 2,448 packages, takes 2-4 hours)
- "stats"         → just show current DB stats (node count, last built date), do NOT update

## Prerequisites
These must be present on disk. If any are missing, tell the user and stop:
- `%APPDATA%\DynamoCopilot\nodes.db`
- `%APPDATA%\DynamoCopilot\models\model.onnx`
- `%APPDATA%\DynamoCopilot\models\vocab.txt`

For Step 4 you need the admin key and server URL. Resolve them as follows:
- **Server URL**: use `$env:DYNAMO_SERVER_URL` if set; otherwise grep `src/DynamoCopilot.Core/Settings/DynamoCopilotSettings.cs` for the first `https://` string literal (the hardcoded Railway URL the extension uses)
- **Admin key**: use `$env:DYNAMO_ADMIN_KEY`; if not set, ask the user before continuing

## Steps

**Step 0 — Stats only (if argument is "stats")**
Run:
```powershell
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- `
  --stats `
  --sqlite "$env:APPDATA\DynamoCopilot\nodes.db"
```
Show the output and stop. Do not proceed to Step 1.

**Step 1 — Run the NodeIndexer**

For incremental update (default):
```powershell
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- `
  --update `
  --sqlite        "$env:APPDATA\DynamoCopilot\nodes.db" `
  --model         "$env:APPDATA\DynamoCopilot\models\model.onnx" `
  --vocab         "$env:APPDATA\DynamoCopilot\models\vocab.txt" `
  --keep-packages "$env:APPDATA\DynamoCopilot\packages"
```

For full rebuild (argument is "full"):
```powershell
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- `
  --update --full `
  --sqlite        "$env:APPDATA\DynamoCopilot\nodes.db" `
  --model         "$env:APPDATA\DynamoCopilot\models\model.onnx" `
  --vocab         "$env:APPDATA\DynamoCopilot\models\vocab.txt" `
  --keep-packages "$env:APPDATA\DynamoCopilot\packages"
```

This may take a long time. Show progress output as it runs. When complete, run `--stats` to get the final node count and the new `last_built_at` timestamp.

**Step 2 — Get file info**
After the indexer finishes, get:
- File size of `%APPDATA%\DynamoCopilot\nodes.db` in bytes
- The `last_built_at` value from the stats output (format: ISO date, e.g. "2026-06-01")
Use the date portion only as `dbVersion` (YYYY-MM-DD).

**Step 3 — Upload to GitHub Releases**
```powershell
gh release upload v1.0.0 "$env:APPDATA\DynamoCopilot\nodes.db" `
  --repo BuvaneshSrinivasan/Copilot-Dynamo-Extension `
  --clobber
```
The download URL will be:
`https://github.com/BuvaneshSrinivasan/Copilot-Dynamo-Extension/releases/download/v1.0.0/nodes.db`

**Step 4 — Update the server manifest**
PATCH the nodes.db info onto the latest release:
```powershell
$body = @{
    dbVersion   = "<dbVersion from Step 2>"
    dbUrl       = "https://github.com/BuvaneshSrinivasan/Copilot-Dynamo-Extension/releases/download/v1.0.0/nodes.db"
    dbSizeBytes = <file size from Step 2>
} | ConvertTo-Json -Compress

Invoke-RestMethod `
  -Uri     "$env:DYNAMO_SERVER_URL/admin/release/latest/db" `
  -Method  PATCH `
  -Headers @{ "X-Admin-Key" = $env:DYNAMO_ADMIN_KEY; "Content-Type" = "application/json" } `
  -Body    $body
```

**Step 5 — Report**
Show:
- DB version (last_built_at date)
- File size in MB
- GitHub URL
- Node count (from stats)
- "Users with an outdated database will see an 'Update database' button in the Suggest Nodes panel."

If any step fails, show the error and stop. Do not mark as success if upload or server update failed.
