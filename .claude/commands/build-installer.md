Build a new DynamoCopilot-Setup.exe installer with an auto-incremented version.

## Arguments
$ARGUMENTS

Interpret the argument as follows:
- No argument or "patch" → increment the patch number (1.0.3 → 1.0.4)
- "minor"               → increment minor, reset patch (1.0.3 → 1.1.0)
- "major"               → increment major, reset others (1.0.3 → 2.0.0)
- A version string like "1.2.0" → use that exact value, no auto-increment

## Steps

**Step 1 — Read the current version**
Read `src/DynamoCopilot.Extension/DynamoCopilot.Extension.csproj` and find the `<Version>` tag value (e.g. `1.0.3`).

**Step 2 — Compute the new version**
Apply the increment rule from the argument above. Split on `.` → [major, minor, patch], increment the right part, rejoin.

**Step 3 — Write the new version to the .csproj**
Edit `src/DynamoCopilot.Extension/DynamoCopilot.Extension.csproj`:
- Set `<Version>` to the new version string
- Set `<AssemblyVersion>` to the new version string (it should already be `$(Version)` so it self-updates)

**Step 4 — Build the installer**
Run in PowerShell:
```powershell
.\build-installer.ps1
```
No `-Version` parameter needed — the script reads it from the .csproj automatically.
Wait for it to complete. Show the full output so the user can see progress.

**Step 5 — Report**
After success, report:
- New version number
- Output exe: `installer-wpf\Output\DynamoCopilot-Setup.exe`
- File size of the exe
- Reminder: upload this exe to GitHub Releases and share with users

If the build fails, show the error output and do not report success.
