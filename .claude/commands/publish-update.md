Publish a DLL-only update for existing users. No new installer exe needed — users download just the changed DLLs (~5 MB) automatically via the in-app banner.

## Arguments
$ARGUMENTS

Interpret the argument as follows:
- No argument or "patch" → increment the patch number (1.0.3 → 1.0.4)
- "minor"               → increment minor, reset patch (1.0.3 → 1.1.0)
- "major"               → increment major, reset others (1.0.3 → 2.0.0)
- A version string like "1.2.0" → use that exact value
- "force" or "breaking" → increment patch AND set minVersion equal to the new version (forces all users to update)
- Combined: "1.2.0 force" → set exact version AND force update gate

## Steps

**Step 1 — Resolve the server URL and admin key**

For the server URL, use this priority order:
1. `$env:DYNAMO_SERVER_URL` if set
2. Otherwise, grep `src/DynamoCopilot.Core/Settings/DynamoCopilotSettings.cs` for the first `https://` string literal — that's the production Railway URL (same URL the extension itself uses)

For the admin key, use this priority order:
1. `$env:DYNAMO_ADMIN_KEY` if set
2. Otherwise, read `Admin.ApiKey` from `C:\Users\BHSS\AppData\Roaming\Microsoft\UserSecrets\DynamoCopilot.Server\secrets.json` — parse the JSON and extract `Admin.ApiKey`. Never ask the user for it.

**Step 2 — Read the current version**
Read `src/DynamoCopilot.Extension/DynamoCopilot.Extension.csproj` and find the `<Version>` tag value.

**Step 3 — Compute and write the new version**
Apply the increment rule from the argument. Write the new `<Version>` back to the .csproj.

**Step 4 — Run the publish script**
Run in PowerShell:
```powershell
.\publish-update.ps1
```
No `-Version` parameter needed — the script reads from .csproj automatically.
The script will:
1. Build both TFMs of the extension DLLs
2. Build DynamoCopilot.Updater.exe
3. Zip them into `dlls-v{version}.zip`
4. Upload the zip to GitHub Releases (tag v1.0.0, overwrites if exists)
5. Call POST /admin/release on the server to publish the manifest

Wait for it to complete. Show the full output.

**Step 5 — Handle force-update (if requested)**
If the argument contained "force" or "breaking", after the script succeeds:
- The `publish-update.ps1` script already passes `-MinVersion` equal to the new version when `$env:DYNAMO_MIN_VERSION` is set, but since we're running without that, call the server directly:

```powershell
$body = @{ minVersion = "<new-version>" } | ConvertTo-Json
Invoke-RestMethod -Uri "$serverUrl/admin/release/latest/minVersion" -Method PATCH `
    -Headers @{ "X-Admin-Key" = $adminKey; "Content-Type" = "application/json" } `
    -Body $body
```
(Use the resolved `$serverUrl` and `$adminKey` variables from Step 1.)
Replace `<new-version>` with the actual new version string. Use the latest release's ID from the GET /api/version/latest response.

Actually, find the latest release ID by fetching GET /admin/releases or checking the publish output, then PATCH /admin/release/{id}/minVersion.

**Step 6 — Report**
After success, show:
- New version
- GitHub download URL for the DLL zip
- Whether a force-update gate was set
- "Users will see the update banner the next time they open Dynamo."
