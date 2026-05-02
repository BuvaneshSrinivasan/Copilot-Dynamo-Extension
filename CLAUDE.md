# DynamoCopilot — Project Guide for Claude

This file is read by Claude Code at the start of every session. Keep it updated.

---

## What This Project Is

The project ships **two Dynamo sidebar extensions** under a shared "BimEra" menu tab, both compiled into a single DLL (`DynamoCopilot.Extension.dll`):

1. **Dynamo Co-pilot** — AI chat for generating and fixing Dynamo Python (Revit API) code
2. **Suggest Nodes** — local vector search over 78,000+ indexed Dynamo package nodes

The **server** (`src/DynamoCopilot.Server/`) is the cloud backend that:
1. Authenticates users (email + password, JWT tokens)
2. Enforces rate limits (daily requests + daily tokens per user)
3. Proxies chat requests to Google Gemini (swappable to other providers via `ILlmService`)
4. Manages user accounts and licenses

The extension and server are **developed separately**. The server is built and tested with Postman first. The extension is connected to the server only after all server phases are complete.

---

## Solution Structure

```
src/
├── DynamoCopilot.Core/          Shared models + LLM service interfaces (used by Extension)
├── DynamoCopilot.Extension/     Dynamo WPF add-in (the UI inside Dynamo)
├── DynamoCopilot.GraphInterop/  Reflection wrappers around Dynamo internals
├── DynamoCopilot.NodeIndexer/   CLI tool — builds nodes.db from package zips/folders
└── DynamoCopilot.Server/        Cloud backend API ← active development
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
| 2 | 🔜 Next | PostgreSQL + Users table (EF Core) |
| 3 | ⏳ Pending | Email/password auth, JWT access + refresh tokens |
| 4 | ⏳ Pending | Rate limiting middleware (requests/day + tokens/day) |
| 5 | ⏳ Pending | Admin endpoints (manage users, view usage) |
| 6 | ⏳ Pending | Railway deployment (Dockerfile, env vars) |

---

## Server — File Structure

```
src/DynamoCopilot.Server/
├── Program.cs                    Entry point: services + middleware + routes
├── appsettings.json              Default config (NO secrets here)
├── appsettings.Development.json  Local dev overrides (never commit API keys)
├── Dockerfile                    Phase 6
│
├── Models/
│   └── ChatRequest.cs            ChatRequest + ChatMessage records (DTOs)
│
├── Services/
│   ├── ILlmService.cs            Interface: any AI provider must implement this
│   ├── GeminiService.cs          Google Gemini implementation
│   ├── TokenService.cs           Phase 3: JWT generation + refresh token handling
│   └── RateLimitService.cs       Phase 4: daily limit checks + reset logic
│
├── Endpoints/
│   ├── ChatEndpoints.cs          POST /api/chat/stream
│   ├── AuthEndpoints.cs          Phase 3: POST /auth/register, /auth/login, /auth/refresh
│   └── AdminEndpoints.cs         Phase 5: GET /admin/users, PATCH /admin/users/{id}/...
│
├── Data/                         Phase 2+
│   ├── AppDbContext.cs           EF Core DbContext
│   └── Migrations/               Generated by `dotnet ef migrations add`
│
└── Middleware/                   Phase 4
    └── RateLimitMiddleware.cs    Checks IsActive + daily request/token counts
```

---

## Server — API Reference

### Phase 1 Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | /health | None | Health check |
| POST | /api/chat/stream | None (Phase 3 adds JWT) | Stream AI response as SSE |

### POST /api/chat/stream — Request

```json
{
  "messages": [
    { "role": "user", "content": "Write a Python function to sum a list" },
    { "role": "assistant", "content": "Here is the code..." },
    { "role": "user", "content": "Can you add error handling?" }
  ]
}
```

### POST /api/chat/stream — Response (SSE stream)

```
data: {"type":"token","value":"Sure"}
data: {"type":"token","value":", here is"}
data: {"type":"token","value":" the updated code..."}
data: {"type":"done"}
```

Error event (if Gemini API fails):
```
data: {"type":"error","message":"Gemini API error 429: quota exceeded"}
```

### Phase 3+ Endpoints (planned)

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| POST | /auth/register | None | Create account (auto-activated) |
| POST | /auth/login | None | Returns access token (1hr) + refresh token (7 days) |
| POST | /auth/refresh | Refresh token | New access token |
| GET | /admin/users | Admin key | List all users + usage stats |
| POST | /admin/users/{id}/deactivate | Admin key | Revoke licence |
| POST | /admin/users/{id}/reset-usage | Admin key | Reset daily counts |

---

## Database Schema (Phase 2+)

### Users table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| Email | string | Unique |
| PasswordHash | string | BCrypt hash |
| IsActive | bool | Default true (open registration) |
| DailyRequestCount | int | Resets daily |
| DailyTokenCount | int | Resets daily |
| LastResetDate | date | Nullable — when counts were last reset |
| RequestLimit | int? | Nullable — overrides global limit for this user |
| TokenLimit | int? | Nullable — overrides global limit for this user |
| Notes | string? | Admin notes (e.g. "paying customer") |
| CreatedAt | datetime | |

### RefreshTokens table (Phase 3)
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK → Users |
| TokenHash | string | BCrypt hash of the token |
| ExpiresAt | datetime | |
| CreatedAt | datetime | |

---

## Running Locally

**Prerequisites:** .NET 8 SDK, Gemini API key (free at https://aistudio.google.com/apikey)

```bash
# Add API key via User Secrets (never put keys in appsettings files)
cd src/DynamoCopilot.Server
dotnet user-secrets set "Gemini:ApiKey" "YOUR_KEY_HERE"

# Run
dotnet run
# → http://localhost:8080
```

**Testing with Postman:**

Health check:
```
GET http://localhost:8080/health
```

Chat stream (set response to "Raw" view to see tokens arrive):
```
POST http://localhost:8080/api/chat/stream
Content-Type: application/json

{"messages":[{"role":"user","content":"Write hello world in Python for Dynamo"}]}
```

---

## Configuration Reference

All config uses ASP.NET Core's configuration system. **Priority order (highest wins):**
1. Environment variables — use `__` for nesting: `Gemini__ApiKey=abc`
2. User secrets — `dotnet user-secrets set "Gemini:ApiKey" "abc"`
3. `appsettings.Development.json`
4. `appsettings.json`

| Key | Description | Default |
|-----|-------------|---------|
| `Gemini:ApiKey` | Google Gemini API key | **Required** |
| `Gemini:Model` | Gemini model name | `gemini-2.5-flash` |
| `Gemini:SystemPrompt` | Override built-in Dynamo prompt | Built-in |
| `PORT` | HTTP port (set by Railway automatically) | `8080` |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string (local dev) | — |
| `DATABASE_URL` | PostgreSQL URI (set by Railway automatically) | — |

Phase 3 adds: `Jwt:Secret`, `Jwt:Issuer`, `Jwt:Audience`, `Jwt:AccessTokenExpiryMinutes`
Phase 5 adds: `Admin:ApiKey`

---

## Key Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Auth | Email + password | OAuth requires browser redirects (awkward in desktop app) + Google app verification for production |
| Registration | Open + auto-activate | Early tester phase — anyone who signs up gets access |
| AI Provider | Gemini 2.5 Flash | Cost-effective pre-revenue; model is a config value, swap without code changes |
| Rate limiting | Requests/day + tokens/day | Either limit can trigger; whichever hits first applies |
| Tiers | None (single licence level) | Simplicity for now; add tiers when payment system is added |
| Admin dashboard | None — use direct DB + admin API endpoints | Fastest path; use TablePlus/DBeaver for ad-hoc queries |
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

## Learning Topics by Phase

| Phase | Concept to learn | Where |
|-------|-----------------|-------|
| 1 | Minimal APIs, IAsyncEnumerable, SSE streaming | Done ✅ |
| 2 | EF Core migrations with PostgreSQL | `dotnet ef migrations add` docs |
| 3 | **Middleware** (most important new concept) | Microsoft Docs: "ASP.NET Core Middleware" |
| 3 | JWT Bearer authentication | Microsoft Docs: "JWT Bearer auth in ASP.NET Core" |
| 4 | Writing custom middleware | Applies Phase 3 knowledge |
| 6 | Docker basics | Docker "Getting Started" guide |
