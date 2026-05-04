# DynamoCopilot — Project Guide for Claude

This file is read by Claude Code at the start of every session. Keep it updated.

---

## What This Project Is

The project ships **two Dynamo sidebar extensions** under a shared "BimEra" menu tab, both compiled into a single DLL (`DynamoCopilot.Extension.dll`):

1. **Dynamo Co-pilot** (`ExtensionConstants.CopilotId = "Copilot"`) — AI chat for generating and fixing Dynamo Python (Revit API) code
2. **Suggest Nodes** (`ExtensionConstants.SuggestNodesId = "SuggestNodes"`) — local vector search over 78,000+ indexed Dynamo package nodes

The **server** (`src/DynamoCopilot.Server/`) is the cloud backend that:
1. Authenticates users (email + password, JWT tokens)
2. Enforces a daily **request limit** per user (default 200/day); token usage is tracked but **never capped** — BYOK means the user's own API key is charged
3. Proxies chat requests to Google Gemini (swappable to other providers via `ILlmService`)
4. Manages user accounts and per-extension licences
5. Serves a **Razor Pages admin dashboard** at `/Dashboard` for managing users, licences, and usage analytics

The extension and server are **developed separately**. The server is built and tested with Postman first.

---

## Solution Structure

```
src/
├── DynamoCopilot.Core/          Shared models + LLM service interfaces (used by Extension)
├── DynamoCopilot.Extension/     Dynamo WPF add-in (the UI inside Dynamo)
├── DynamoCopilot.GraphInterop/  Reflection wrappers around Dynamo internals
├── DynamoCopilot.NodeIndexer/   CLI tool — builds nodes.db from package zips/folders
└── DynamoCopilot.Server/        Cloud backend API
```

---

## Extension — Two-Extension Architecture

### Overview

Both extensions live in the same `DynamoCopilot.Extension.dll`. Dynamo discovers them through two separate XML manifests placed in its `viewExtensions\` folder:

| Manifest | TypeName | Purpose |
|----------|----------|---------|
| `DynamoCopilot_ViewExtensionDefinition.xml` | `DynamoCopilotViewExtension` | Python chat panel |
| `SuggestNodes_ViewExtensionDefinition.xml` | `SuggestNodesViewExtension` | Node search panel |

### File structure (Extension project)

```
DynamoCopilot.Extension/
├── DynamoCopilotViewExtension.cs          IViewExtension — Copilot chat
├── SuggestNodesViewExtension.cs           IViewExtension — Suggest Nodes
├── DynamoCopilot_ViewExtensionDefinition.xml
├── SuggestNodes_ViewExtensionDefinition.xml
│
├── ViewModels/
│   ├── CopilotPanelViewModel.cs           Chat + auth + AI settings + user info
│   ├── SuggestNodesPanelViewModel.cs      Node search + auth + user info
│   ├── SettingsPanelViewModel.cs          AI provider config (Copilot only)
│   ├── NodeSuggestionCardViewModel.cs     Per-card state for node results
│   ├── SpecCardViewModel.cs               Spec-first feature card
│   └── ChatMessageViewModel.cs            Per-message display state
│
├── Views/
│   ├── CopilotPanelView.xaml              Chat UI (no node suggest tab)
│   └── SuggestNodesPanelView.xaml         Search input + cards + user icon
│
└── Services/
    ├── CopilotLogger.cs
    ├── PackageStateService.cs             Used by Suggest Nodes only
    └── DynamoPackageDownloader.cs         Used by Suggest Nodes only
```

### BimEra menu — shared tab, two items

Both extensions call `FindOrCreateBimEraMenu(dynamoMenu.Items, "BimEra")` in their `Loaded()` method. Whichever loads first creates the "BimEra" `MenuItem`; the second finds and reuses it. Result: one "BimEra" top-level menu with two sub-items, load-order independent.

**Do not** let either extension call `loadedParams.dynamoMenu.Items.Add(new MenuItem { Header = "BimEra" })` directly — that creates duplicate top-level entries.

### Panel open/close state

Both extensions track `_panelOpen` via WPF `Loaded`/`Unloaded` events on the view, not in `OnTogglePanel`:

```csharp
_view.Loaded   += (_, __) => _panelOpen = true;
_view.Unloaded += (_, __) => _panelOpen = false;
```

`OnTogglePanel` only calls `AddToExtensionsSideBar` / `CloseExtensioninInSideBar`. This correctly handles the case where the user closes the panel via Dynamo's own X button (not the menu item), which previously left `_panelOpen` stale.

The menu item header never changes — it always shows the extension name.

### Shared authentication

Both extensions use separate `AuthService` instances pointing to the same `tokens.json` on disk (`%AppData%\DynamoCopilot\tokens.json`). In-memory login state is kept in sync via **static events** on `AuthService`:

```csharp
public static event Action<string>? GlobalLoggedIn;   // fired after tokens saved
public static event Action?         GlobalLoggedOut;  // fired after tokens deleted
```

**Login sync flow:**
1. User logs in via either panel → `AuthService` saves tokens, fires `GlobalLoggedIn`
2. The *other* VM's `OnGlobalLoggedIn` handler calls `OnAuthSuccess()` if it isn't already logged in

**Logout sync flow:**
1. User clicks Sign Out in VM-A → VM-A calls `ClearAuthState()` first (sets `IsLoggedIn = false`)
2. Then calls `_authService.Logout()` → fires `GlobalLoggedOut`
3. VM-A's handler guard `if (!IsLoggedIn) return` skips (already false) — no double clear
4. VM-B's handler guard passes → calls `DispatchToUi(ClearAuthState)` → VM-B's UI clears

Both VMs subscribe in their constructor and **unsubscribe in `Shutdown()`** to prevent memory leaks.

---

## Extension — Architecture & Key Design Decisions

### Copilot vs Suggest Nodes — feature boundary

- **Copilot** (`CopilotPanelViewModel`) — chat only: streaming LLM responses, Python code extraction, Insert/Fix-Error, spec-first flow, AI settings, user info panel
- **Suggest Nodes** (`SuggestNodesPanelViewModel`) — node search only: ONNX vector search via `LocalNodeSearchService`, node cards with Download/Insert, user icon (top-right) reveals user info flyout

**Node suggestion cards do NOT appear in the Copilot chat.** If the AI mentions a node name in prose, it stays as text — no interactive cards. All node card functionality is isolated to the Suggest Nodes extension.

### Per-extension licensing

Licences are stored in the `UserLicenses` table (one row per user per extension). Each extension has a fixed string identifier defined in `ExtensionConstants` (Core project) and `AppConstants` (Server project) — both files must stay in sync.

**Extension IDs** (`src/DynamoCopilot.Core/ExtensionConstants.cs`):
```csharp
public const string CopilotId      = "Copilot";
public const string SuggestNodesId = "SuggestNodes";
public const string SupportEmail   = "info@bimera.com";
```

**Server constants** (`src/DynamoCopilot.Server/AppConstants.cs`):
```csharp
public static class Extensions
{
    public const string Copilot      = "Copilot";
    public const string SuggestNodes = "SuggestNodes";
}
```

**When adding a new extension:** add its ID to both files and add a `LicenseFilter.Require(AppConstants.Extensions.NewId)` to its endpoint.

**Licence check flow (extension side):**
1. `OnAuthSuccess()` — calls `_authService.GetGrantedExtensions()` which decodes the `ext` JWT claims synchronously. Sets `IsLicenceActive` immediately (no network call).
2. `RefreshUserInfoAsync()` — hits `/api/me`, finds the extension-specific `UserLicenseInfo` row in the `Licenses[]` array, updates `IsLicenceActive` and `LicenseEndDate` with server-authoritative values.
3. The XAML shows a "Sorry, you don't have a licence…" banner when `IsLicenceActive = false`, and hides the chat input / search input. The user info panel shows the expiry date for that extension's licence only.

**Licence check flow (server side):**
- `LicenseFilter.Require(extensionId)` is an endpoint filter attached to each protected route. It reads `httpContext.User.FindAll("ext")` and returns `403 { error: "no_license" }` if the extension ID is absent from the JWT.
- `User.IsActive` remains a global account kill switch checked by `RateLimitMiddleware`.

**Granting a licence (Postman workflow):**
```
POST /admin/grant
X-Admin-Key: your-key
{ "email": "user@example.com", "extension": "Copilot", "months": 12 }
```
The user must log out and back in (or wait for token refresh) to receive the updated `ext` claim.

### Package State (`PackageStateService`)

`IsInstalled(packageName)` checks **only the currently running Dynamo version's packages folder**, not all versions. A package downloaded in Revit 2025 is not considered installed when running in Revit 2024.

- `_installedCurrentVersion` — packages found under `CurrentVersionPackagesDir` (version-scoped, gates the Download/Insert buttons)
- `_installed` — all packages across every version (used only for path resolution via `GetPackageFolderPath`)
- `_paths` — prefers the current-version path when a package exists in multiple versions

**Do not revert to checking `_installed` for `IsInstalled`** — it caused the Download button to be disabled in the wrong Dynamo version.

---

### Node Insertion (`GraphNodeInserter`)

All insertion goes through `InsertNode(model, nodeName, packageName, nodeType, packageFolderPath, x, y, log)`.

The `log` parameter is `Action<string>?` — pass `CopilotLogger.Log` from the Extension call site. `GraphInterop` has no reference to `Extension` so it cannot call `CopilotLogger` directly.

**ZeroTouch insertion flow:**

1. `ResolveCreationName` scans loaded assemblies from the package's `bin/` folder and returns `type.Name + "." + method.Name` — **simple class name, no namespace**.
   - This must match `FunctionDescriptor.QualifiedName = ClassName + "." + UserFriendlyName` which is the dictionary key in `LibraryServices` (Dynamo source: `FunctionDescriptor.cs:417`).
   - Using `type.FullName` (with namespace) adds extra segments and breaks `LibraryServices.CanbeResolvedTo` which requires the search term to have ≤ segments than the key (`LibraryServices.cs:511`).

2. `TryResolveMangledName` queries `DynamoModel.LibraryServices` (NonPublic property) to promote from `ClassName.Method` to the exact `MangledName = ClassName.Method@T1,T2` needed for overloaded nodes.
   - Uses `GetFunctionDescriptor(string)` first, then `GetAllFunctionDescriptors` for overloads (`FunctionGroup.cs:71`).

3. `ExecuteCreateNode` fires `DynamoModel.CreateNodeCommand` via reflection. Node creation success is confirmed by finding the new GUID in `workspace.Nodes`.

**DYF insertion flow:**
- Finds `.dyf` file by simple node name, parses GUID from XML, calls `CustomNodeManager.AddUninitializedCustomNode`, then `CreateNodeCommand` with the GUID string.

**Critical**: `CanInsert` is gated on `IsInstalled` (disk presence), NOT on whether the node actually exists in Dynamo's runtime `LibraryServices`. If a node name in our index doesn't exist in the installed package version, Insert will fail with Dynamo's own "Could not create node" exception.

---

### Node Index (`nodes.db`)

- Location: `%AppData%\DynamoCopilot\nodes.db`
- Hosted on GitHub Releases `v1.0.0` as a release asset — the installer downloads it from there.
- Built by `DynamoCopilot.NodeIndexer` CLI tool.
- **~186 MB**, 38,188 nodes from 2,878 packages.

**Rebuilding nodes.db:**
```
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- \
  --packages "C:\Users\BHSS\Downloads\Mass Search\Unpacked" \
  --sqlite   "%AppData%\DynamoCopilot\nodes.db" \
  --model    "%AppData%\DynamoCopilot\models\model.onnx" \
  --vocab    "%AppData%\DynamoCopilot\models\vocab.txt"
```

After rebuilding, upload to the GitHub release:
```
gh release upload v1.0.0 assets/nodes.db --repo BuvaneshSrinivasan/Copilot-Dynamo-Extension --clobber
```

**`node_libraries` filter (critical correctness rule):**

`PackageExtractor` only indexes XML doc files whose base filename matches an assembly listed in `pkg.json`'s `node_libraries` field. This mirrors Dynamo's own `Package.IsNodeLibrary` logic (`Package.cs:459`):
- `node_libraries` absent → index all DLLs (legacy packages)
- `node_libraries` present → only index DLLs listed there

**Do not remove this filter.** Without it, packages that bundle third-party DLLs (e.g. Summerisle bundles LunchBox.dll) produce ghost node suggestions — nodes that exist in a bundled DLL but are never exposed by that package in Dynamo.

---

### Logging

`CopilotLogger.Log(string)` appends to `%AppData%\DynamoCopilot\log`. It is in `DynamoCopilot.Extension` — not accessible from `GraphInterop` or `Core`. Pass it as `Action<string>` when crossing project boundaries.

---

## Server — Build Phases

| Phase | Status | What It Adds |
|-------|--------|-------------|
| 1 | ✅ Complete | Gemini streaming endpoint, no auth |
| 2 | ✅ Complete | PostgreSQL + Users table (EF Core) |
| 3 | ✅ Complete | Email/password auth, JWT access + refresh tokens |
| 4 | ✅ Complete | Rate limiting middleware (requests/day; tokens tracked, not capped) |
| 5 | ✅ Complete | Admin API endpoints + per-extension `UserLicenses` table |
| 6 | ✅ Complete | Razor Pages admin dashboard (`/Dashboard`) + `UsageLogs` history table |
| 7 | ⏳ Pending | Railway deployment (Dockerfile, env vars) |

---

## Server — File Structure

```
src/DynamoCopilot.Server/
├── Program.cs                    Entry point: services + middleware + routes
├── AppConstants.cs               Extension ID strings (must match ExtensionConstants in Core)
├── appsettings.json              Default config (NO secrets here)
├── appsettings.Development.json  Local dev overrides (never commit API keys)
├── Dockerfile                    Phase 7
│
├── Models/
│   ├── ChatRequest.cs            ChatRequest + ChatMessage records (DTOs)
│   ├── User.cs                   EF Core entity — Users table
│   ├── UserLicense.cs            EF Core entity — UserLicenses table (per-extension)
│   ├── RefreshToken.cs           EF Core entity — RefreshTokens table
│   ├── DynamoNode.cs             EF Core entity — DynamoNodes table
│   ├── UsageLog.cs               EF Core entity — UsageLogs table (one row per user per day)
│   └── AuthRequests.cs           Login/register/refresh request DTOs
│
├── Services/
│   ├── ILlmService.cs            Interface: any AI provider must implement this
│   ├── GeminiService.cs          Google Gemini implementation
│   ├── TokenService.cs           JWT generation + refresh token handling
│   ├── UsageTracker.cs           Scoped mailbox — GeminiService writes tokens, middleware reads
│   ├── EmbeddingService.cs       Gemini text embedding for node search
│   ├── NodeSearchService.cs      Vector + keyword hybrid search
│   └── NodeRerankService.cs      Gemini re-ranking of search results
│
├── Endpoints/
│   ├── ChatEndpoints.cs          POST /api/chat/stream  (requires Copilot licence)
│   ├── NodeEndpoints.cs          POST /api/nodes/suggest (requires SuggestNodes licence)
│   ├── AuthEndpoints.cs          POST /auth/register, /auth/login, /auth/refresh
│   ├── UserEndpoints.cs          GET /api/me
│   └── AdminEndpoints.cs         GET /admin/users, POST /admin/grant, POST /admin/revoke, …
│
├── Filters/
│   └── LicenseFilter.cs          Endpoint filter — checks JWT "ext" claim per extension
│
├── Data/
│   ├── AppDbContext.cs           EF Core DbContext
│   └── Migrations/               Generated by `dotnet ef migrations add`
│
├── Middleware/
│   └── RateLimitMiddleware.cs    Checks IsActive + daily request limit; tracks tokens (no cap)
│
└── Pages/                        Razor Pages admin dashboard
    ├── _ViewImports.cshtml        Tag helpers + namespace
    └── Dashboard/
        ├── _ViewStart.cshtml      Sets _Layout for all dashboard pages
        ├── _Layout.cshtml         Dark sidebar layout (Bootstrap 5.3 + Chart.js via CDN)
        ├── DashboardPageModel.cs  Base class with [Authorize(AuthenticationSchemes="AdminCookie")]
        ├── Login.cshtml(.cs)      Admin key login form → issues 8-hour session cookie
        ├── Logout.cshtml(.cs)     GET /Dashboard/Logout → signs out → redirect to Login
        ├── Index.cshtml(.cs)      Dashboard: stat cards + registrations chart + top users
        ├── Users.cshtml(.cs)      User list with email search + status filter
        └── UserDetail.cshtml(.cs) Per-user: licences, daily/monthly usage chart, request limit, notes
```

---

## Server — API Reference

### Auth endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | /health | None | Health check |
| POST | /auth/register | None | Create account (no licence granted on register) |
| POST | /auth/login | None | Returns access token (1hr) + refresh token (7 days) |
| POST | /auth/refresh | Refresh token | New access + refresh tokens (token rotation) |
| GET | /api/me | Bearer JWT | User profile + per-extension licence list |

### Chat / node endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | /api/chat/stream | Bearer + `ext=Copilot` | Stream AI response as SSE |
| POST | /api/nodes/suggest | Bearer + `ext=SuggestNodes` | Vector search + Gemini re-rank |

### Admin endpoints (X-Admin-Key header required)

These remain available for Postman/scripting. The dashboard at `/Dashboard` is the primary UI for day-to-day management.

| Method | Path | Description |
|--------|------|-------------|
| GET | /admin/users | All users with their licences and usage |
| POST | /admin/grant | `{ email, extension, months }` — grant or extend a licence |
| POST | /admin/revoke | `{ email, extension }` — revoke a licence (sets IsActive=false) |
| POST | /admin/users/{id}/activate | Re-enable a deactivated account |
| POST | /admin/users/{id}/deactivate | Global account kill switch |
| POST | /admin/users/{id}/reset-usage | Reset daily counters |
| PATCH | /admin/users/{id}/limits | Override per-user rate limits |

### Admin Dashboard (Razor Pages)

Accessed at `https://your-server/Dashboard/Login`. Protected by an 8-hour session cookie — enter the `Admin:ApiKey` value to sign in.

| Page | URL | What it shows |
|------|-----|--------------|
| Login | `/Dashboard/Login` | Admin key form |
| Dashboard | `/Dashboard` | Stat cards (users, licences, tokens today), registrations chart, top users |
| Users | `/Dashboard/Users` | Searchable/filterable user table with licence badges and usage bars |
| User Detail | `/Dashboard/UserDetail?id=…` | 4 usage cards (today + month), 30-day dual-axis chart, grant/revoke licences, request limit override, notes |

### POST /api/chat/stream — Request / Response

```json
{ "messages": [{ "role": "user", "content": "Write hello world in Python for Dynamo" }] }
```
```
data: {"type":"token","value":"Sure"}
data: {"type":"done"}
data: {"type":"error","message":"..."}   ← on failure
```

### JWT payload

```json
{
  "sub":   "<user-guid>",
  "email": "user@example.com",
  "jti":   "<unique-token-id>",
  "ext":   ["Copilot", "SuggestNodes"],   ← one entry per active licence
  "exp":   1234567890
}
```

`ext` is populated at login and refresh from the `UserLicenses` table. A user with no licences gets an empty `ext` array — they can log in but all extension endpoints return 403.

### GET /api/me — Response

```json
{
  "email": "user@example.com",
  "dailyTokenCount": 1200,
  "isActive": true,
  "licenses": [
    { "extension": "Copilot", "isActive": true, "endDate": "2027-01-01T00:00:00Z", "expired": false }
  ]
}
```

---

## Database Schema

### Users table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| Email | string | Unique |
| PasswordHash | string | BCrypt hash |
| IsActive | bool | Global account kill switch (default true) |
| DailyRequestCount | int | Today's request count — resets lazily on first request of a new day |
| DailyTokenCount | int | Today's token count — resets lazily; tracked but never capped (BYOK) |
| LastResetDate | date | Nullable — date the daily counters were last zeroed |
| RequestLimit | int? | Nullable — per-user override; falls back to `RateLimit:DailyRequestLimit` (default 1000) |
| TokenLimit | int? | Kept in schema but **not enforced** — token cap was removed (BYOK) |
| Notes | string? | Admin notes |
| CreatedAt | datetime | |

### UserLicenses table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK → Users (cascade delete) |
| Extension | string | `"Copilot"` or `"SuggestNodes"` (max 64 chars) |
| IsActive | bool | Per-extension kill switch |
| StartDate | datetime | |
| EndDate | datetime? | Null = never expires |
| CreatedAt | datetime | |

Unique index on `(UserId, Extension)` — one row per user per extension.

### UsageLogs table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK → Users (cascade delete) |
| Date | DateOnly | The calendar day this row covers |
| RequestCount | int | Total requests made on that day |
| TokenCount | int | Total tokens used on that day |

Unique index on `(UserId, Date)` — one row per user per day. Written by `RateLimitMiddleware` just before it resets the daily counters (lazy reset on first request of a new day). This gives the dashboard permanent historical data for daily and monthly analytics. Today's live counters are always read from `User.DailyRequestCount` / `User.DailyTokenCount` directly.

### RefreshTokens table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK → Users (cascade delete) |
| TokenHash | string | SHA-256 hash of the raw token |
| ExpiresAt | datetime | |
| CreatedAt | datetime | |

---

## Running Locally

**Prerequisites:** .NET 8 SDK, Gemini API key (free at https://aistudio.google.com/apikey)

```bash
# Add secrets via User Secrets (never put keys in appsettings files)
cd src/DynamoCopilot.Server
dotnet user-secrets set "Gemini:ApiKey"  "YOUR_KEY_HERE"
dotnet user-secrets set "Jwt:Secret"     "your-32-char-secret"
dotnet user-secrets set "Admin:ApiKey"   "your-admin-key"

# Run
dotnet run
# → http://localhost:8080
```

**Migrations (after model changes):**
```bash
cd src/DynamoCopilot.Server
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

**Admin dashboard:**
```
http://localhost:8080/Dashboard/Login
```
Enter your `Admin:ApiKey` value. The dashboard covers all day-to-day operations (grant/revoke licences, view usage, manage accounts).

**Postman — grant a licence (alternative to dashboard):**
```
POST /admin/grant
X-Admin-Key: your-admin-key
{ "email": "user@example.com", "extension": "Copilot", "months": 12 }
```

---

## Configuration Reference

**Railway env var naming:** Railway runs on Linux, which allows `:` in environment variable names. Set variables exactly as shown (e.g. `Gemini:ApiKey`, `Jwt:Secret`) — ASP.NET Core reads them directly. The `__` double-underscore convention is only needed on Windows.

| Key | Description | Default |
|-----|-------------|---------|
| `Gemini:ApiKey` | Google Gemini API key | **Required** |
| `Gemini:Model` | Gemini model name | `gemini-2.5-flash` |
| `Gemini:SystemPrompt` | Override built-in Dynamo prompt | Built-in |
| `Jwt:Secret` | HMAC-SHA256 signing key (≥32 chars) | **Required** |
| `Jwt:Issuer` | JWT issuer | `DynamoCopilot` |
| `Jwt:Audience` | JWT audience | `DynamoCopilot` |
| `Jwt:AccessTokenExpiryMinutes` | Access token lifetime | `60` |
| `Admin:ApiKey` | Secret for X-Admin-Key header **and** dashboard login | **Required** |
| `RateLimit:DailyRequestLimit` | Default daily request cap per user (token limit removed — BYOK) | `1000` |
| `PORT` | HTTP port (set by Railway automatically) | `8080` |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string (local dev) | — |
| `DATABASE_URL` | PostgreSQL URI (set by Railway automatically) | — |

---

## Key Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Auth | Email + password | OAuth requires browser redirects (awkward in desktop app) + Google app verification for production |
| Registration | Open + no licence on register | Anyone can create an account; admin grants licence manually after payment |
| Licensing | `UserLicenses` junction table | Per-extension expiry dates; scales to many extensions without schema changes |
| Licence grant workflow | Postman → `POST /admin/grant` by email | No payment system yet — manual Excel tracking; email avoids GUID lookup |
| Licence check — server | `LicenseFilter` endpoint filter reads JWT `ext` claims | Runs before handler, rejects 403 if extension absent; no DB call per request |
| Licence check — extension | JWT decoded client-side in `GetGrantedExtensions()` | Instant at login, no extra network call; `/api/me` confirms on panel open |
| No-licence UX | Panel visible but content replaced with banner | User can see the tool exists (upsell) but can't use it |
| AI Provider | Gemini 2.5 Flash | Cost-effective pre-revenue; model is a config value, swap without code changes |
| Rate limiting | Requests/day only (default 1000); tokens tracked but never capped | BYOK — user's own API key is charged for tokens, so a server-side token cap makes no sense |
| Token history | `UsageLogs` table (one row per user per day) | Written by middleware before lazy reset; enables daily/monthly analytics in dashboard without storing per-request logs |
| Admin dashboard | Razor Pages at `/Dashboard`, protected by session cookie | Single deployment (embedded in the server); direct DbContext access avoids a separate API layer; Bootstrap + Chart.js via CDN means no build step |
| Dashboard auth | Admin key login form → 8-hour cookie (`dc_admin`) | Reuses existing `Admin:ApiKey` secret; GET-based logout for simplicity (CSRF on logout is acceptable for an internal tool) |
| Hosting | Railway | Native PostgreSQL addon, reads PORT + DATABASE_URL automatically |
| Two extensions, one DLL | Single DLL, two `IViewExtension` classes | Dynamo requires one XML manifest per extension; single DLL avoids duplicating shared services |
| Cross-extension auth sync | Static events on `AuthService` | Both extensions share the same AppDomain; static events are the correct in-process signal — no IPC needed |
| Panel state tracking | WPF `Loaded`/`Unloaded` events | Correctly detects user closing the panel via Dynamo's own X button, not just our menu item |

---

## Installer Build

The installer is a self-contained WPF exe (`installer-wpf/`) that bundles the extension DLLs as an embedded zip payload.

### Build command
```powershell
.\build-installer.ps1 -Version "1.0.3"
# Output: installer-wpf\Output\DynamoCopilot-Setup.exe
```

### Build pipeline (in order)
1. `dotnet publish` Extension → `installer-wpf\staging-dist\net48\` and `net8.0-windows\`
2. `dotnet publish` installer WPF exe → `installer-wpf\Output\`
3. Copies staging dist → `installer-wpf\Output\dist\`
4. **Obfuscates** the 3 DLLs in a temp staging copy (`obfuscate.ps1`)
5. Zips the obfuscated staging copy → `payload.zip`
6. Appends zip to the exe (`append_payload.ps1`)

### ViewExtension XML manifests

Two manifests are required in each Dynamo `viewExtensions\` folder:

| File | Template |
|------|----------|
| `DynamoCopilot_ViewExtensionDefinition.xml` | `DynamoCopilot_ViewExtensionDefinition.net8.xml.template` |
| `SuggestNodes_ViewExtensionDefinition.xml` | `SuggestNodes_ViewExtensionDefinition.net8.xml.template` |

(`net48` variants exist for Revit 2024 and below.)

**`build-local.ps1`** generates both XMLs from their templates (replacing `{{APPDATA}}`) and copies both to every detected Dynamo `viewExtensions\` folder. Run as Administrator because the target folders are under `Program Files`.

**`InstallerEngine.cs` `RegisterDynamo()`** writes both XMLs when the installer registers a Dynamo install.

The `SuggestNodes_ViewExtensionDefinition.xml` file is also declared as `<Content CopyToOutputDirectory="PreserveNewest">` in the `.csproj` so it lands in the build output alongside the DLL.

### Obfuscation one-time setup
```powershell
dotnet tool restore   # installs Obfuscar from .config/dotnet-tools.json
```
Tool is pinned in `.config/dotnet-tools.json` (Obfuscar 2.2.50).

### What is and isn't obfuscated
- **Obfuscated:** private/internal members, fields, local variables, string literals (system prompts, server URL) — via `KeepPublicApi=true`
- **Preserved:** public class/method names (required for Dynamo ViewExtension loading and WPF BAML deserialization)
- **Stripped:** all `.pdb` files from the shipped DLLs

### Mapping files
`installer-wpf/obfuscation-mappings/Mapping_*.xml` maps obfuscated names back to originals — needed to decode crash stack traces. These are gitignored; keep them private.

### Key obfuscation constraints
- `SkipType` and `SkipNamespace` rules inside `<Module>` are silently ignored by Obfuscar 2.2.50 — only `KeepPublicApi` reliably controls what gets renamed
- `DynamoCopilotViewExtension` must keep its name — Dynamo reads it from `DynamoCopilot_ViewExtensionDefinition.xml`
- `SuggestNodesViewExtension` must keep its name — Dynamo reads it from `SuggestNodes_ViewExtensionDefinition.xml`
- All WPF view/viewmodel type names must be preserved — BAML embeds them as strings

---

## Obfuscation Compatibility Rules

### NEVER use anonymous types with `JsonSerializer`
Anonymous types (`new { email, password }`) generate compiler-produced generic types. Obfuscar renames their constructor parameter names to `null`. `System.Text.Json` inspects those parameter names when building its type cache — even for serialization — and throws:

> `The deserialization constructor for type 'A.a\`2[...]' contains parameters with null names.`

**Always use `Dictionary<string, T>` instead:**

```csharp
// WRONG — breaks after obfuscation
JsonSerializer.Serialize(new { email, password });

// CORRECT
JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
```

This applies in: `AuthService`, `ServerLlmService`, `NodeSuggestService`, and all LLM provider services (`GeminiLlmService`, `OpenAiLlmService`, `OllamaLlmService`, `ClaudeLlmService`). All have already been converted.

The JSON wire format is byte-for-byte identical — dictionaries serialize the same way as anonymous types, so no server API changes are needed.

---

## Adding a New AI Provider

1. Create `Services/AnthropicService.cs` (or any name) implementing `ILlmService`
2. In `Program.cs`, change one line:
   ```csharp
   builder.Services.AddScoped<ILlmService, AnthropicService>();
   ```
3. Done — `ChatEndpoints.cs` doesn't change at all.

---

## Adding a New Extension

1. Add the extension ID to `ExtensionConstants.cs` (Core) and `AppConstants.cs` (Server)
2. Create the server endpoint, apply `.AddEndpointFilter(LicenseFilter.Require(AppConstants.Extensions.NewId))`
3. In the VM: set `IsLicenceActive = _authService.GetGrantedExtensions().Contains(ExtensionConstants.NewId)` in `OnAuthSuccess()`
4. In `RefreshUserInfoAsync()`: `var lic = info.GetLicense(ExtensionConstants.NewId)`
5. In the XAML: bind content rows to `IsLicenceActive`, add the no-licence banner (same pattern as Copilot/SuggestNodes)

---

## Learning Topics by Phase

| Phase | Concept to learn | Where |
|-------|-----------------|-------|
| 1 | Minimal APIs, IAsyncEnumerable, SSE streaming | Done ✅ |
| 2 | EF Core migrations with PostgreSQL | Done ✅ |
| 3 | JWT Bearer authentication + refresh token rotation | Done ✅ |
| 4 | Writing custom middleware | Done ✅ |
| 5 | Endpoint filters, per-resource authorization | Done ✅ |
| 6 | Razor Pages, cookie authentication, Chart.js | Done ✅ |
| 7 | Docker basics | Docker "Getting Started" guide |
