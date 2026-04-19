# Database Sync

Schema is version-controlled via EF Core migrations (in `src/DynamoCopilot.Server/Data/Migrations/`).  
Data is synced by committing `seed.sql` to this folder.

---

## Current PC — export your data

```powershell
.\db\dump.ps1
```

Then commit and push:

```bash
git add db/seed.sql
git commit -m "Update seed data"
git push
```

---

## New PC — set up from scratch

**Prerequisites:**
- PostgreSQL installed and running
- .NET 8 SDK
- `dotnet-ef` tool: `dotnet tool install -g dotnet-ef`

**Steps:**

1. Pull the repo
2. Create the database in pgAdmin (name: `dynamocopilot_dev`, port `5433`)
3. Add your connection string to User Secrets:
   ```bash
   cd src/DynamoCopilot.Server
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Host=localhost;Port=5433;Username=postgres;Password=YOUR_PASSWORD;Database=dynamocopilot_dev"
   ```
4. Run the restore script:
   ```powershell
   .\db\restore.ps1
   ```

This applies all migrations and loads seed data in one step.

---

## Default connection settings

| Setting  | Value               |
|----------|---------------------|
| Host     | localhost           |
| Port     | 5433                |
| Username | postgres            |
| Database | dynamocopilot_dev   |

Override any of these by passing parameters:

```powershell
.\db\dump.ps1 -Port 5432 -Database my_other_db
.\db\restore.ps1 -Port 5432 -Database my_other_db
```

---

## What's excluded from seed.sql

- `__EFMigrationsHistory` (recreated by migrations)
- Refresh tokens (ephemeral — users just log in again)
