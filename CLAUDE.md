# DynamoCopilot â€” Project Guide for Claude

This file is read by Claude Code at the start of every session. Keep it updated.

---

## What This Project Is

The project ships **two Dynamo sidebar extensions** under a shared "BIMEra" menu tab, both compiled into a single DLL (`DynamoCopilot.Extension.dll`):

1. **Dynamo Co-pilot** (`ExtensionConstants.CopilotId = "Copilot"`) â€” AI chat for generating and fixing Dynamo Python (Revit API) code
2. **Suggest Nodes** (`ExtensionConstants.SuggestNodesId = "SuggestNodes"`) â€” local vector search over 78,000+ indexed Dynamo package nodes

The **server** (`src/DynamoCopilot.Server/`) is the cloud backend that:
1. Authenticates users (email + password, JWT tokens)
2. Enforces a daily **request limit** per user (default 200/day); token usage is tracked but **never capped** â€” BYOK means the user's own API key is charged
3. Proxies chat requests to Google Gemini (swappable to other providers via `ILlmService`)
4. Manages user accounts and per-extension licences
5. Serves a **Razor Pages admin dashboard** at `/Dashboard` for managing users, licences, and usage analytics

The extension and server are **developed separately**. The server is built and tested with Postman first.

---

## Solution Structure

```
src/
â”œâ”€â”€ DynamoCopilot.Core/          Shared models + LLM service interfaces (used by Extension)
â”œâ”€â”€ DynamoCopilot.Extension/     Dynamo WPF add-in (the UI inside Dynamo)
â”œâ”€â”€ DynamoCopilot.GraphInterop/  Reflection wrappers around Dynamo internals
â”œâ”€â”€ DynamoCopilot.NodeIndexer/   CLI tool â€” builds/updates nodes.db (full rebuild or incremental from dynamopackages.com)
â””â”€â”€ DynamoCopilot.Server/        Cloud backend API
```

---

## Extension â€” Two-Extension Architecture

### Overview

Both extensions live in the same `DynamoCopilot.Extension.dll`. Dynamo discovers them through two separate XML manifests placed in its `viewExtensions\` folder:

| Manifest | TypeName | Purpose |
|----------|----------|---------|
| `DynamoCopilot_ViewExtensionDefinition.xml` | `DynamoCopilotViewExtension` | Python chat panel |
| `SuggestNodes_ViewExtensionDefinition.xml` | `SuggestNodesViewExtension` | Node search panel |

### File structure (Extension project)

```
DynamoCopilot.Extension/
â”œâ”€â”€ DynamoCopilotViewExtension.cs          IViewExtension â€” Copilot chat
â”œâ”€â”€ SuggestNodesViewExtension.cs           IViewExtension â€” Suggest Nodes
â”œâ”€â”€ DynamoCopilot_ViewExtensionDefinition.xml
â”œâ”€â”€ SuggestNodes_ViewExtensionDefinition.xml
â”‚
â”œâ”€â”€ ViewModels/
â”‚   â”œâ”€â”€ CopilotPanelViewModel.cs           Chat + auth + AI settings + user info
â”‚   â”œâ”€â”€ SuggestNodesPanelViewModel.cs      Node search + auth + user info
â”‚   â”œâ”€â”€ AuthFormViewModel.cs               Shared login/register form state (used by both panels)
â”‚   â”œâ”€â”€ SettingsPanelViewModel.cs          AI provider config (Copilot only)
â”‚   â”œâ”€â”€ NodeSuggestionCardViewModel.cs     Per-card state for node results
â”‚   â”œâ”€â”€ SpecCardViewModel.cs               Spec-first feature card
â”‚   â””â”€â”€ ChatMessageViewModel.cs            Per-message display state
â”‚
â”œâ”€â”€ Views/
â”‚   â”œâ”€â”€ CopilotPanelView.xaml              Chat UI (no node suggest tab)
â”‚   â”œâ”€â”€ SuggestNodesPanelView.xaml         Search input + cards + user icon
â”‚   â””â”€â”€ AuthFormView.xaml                  Shared login/register UserControl (inline in both panels)
â”‚
â””â”€â”€ Services/
    â”œâ”€â”€ CopilotLogger.cs
    â”œâ”€â”€ PackageStateService.cs             Used by Suggest Nodes only
    â””â”€â”€ DynamoPackageDownloader.cs         Used by Suggest Nodes only
```

### BIMEra menu â€” shared tab, two items

Both extensions call `FindOrCreateBIMEraMenu(dynamoMenu.Items, "BIMEra")` in their `Loaded()` method. Whichever loads first creates the "BIMEra" `MenuItem`; the second finds and reuses it. Result: one "BIMEra" top-level menu with two sub-items, load-order independent.

**Do not** let either extension call `loadedParams.dynamoMenu.Items.Add(new MenuItem { Header = "BIMEra" })` directly â€” that creates duplicate top-level entries.

### Panel open/close state

Both extensions track `_panelOpen` via WPF `Loaded`/`Unloaded` events on the view, not in `OnTogglePanel`:

```csharp
_view.Loaded   += (_, __) => _panelOpen = true;
_view.Unloaded += (_, __) => _panelOpen = false;
```

`OnTogglePanel` only calls `AddToExtensionsSideBar` / `CloseExtensioninInSideBar`. This correctly handles the case where the user closes the panel via Dynamo's own X button (not the menu item), which previously left `_panelOpen` stale.

The menu item header never changes â€” it always shows the extension name.

### Shared authentication

Both extensions use separate `AuthService` instances pointing to the same `tokens.json` on disk (`%AppData%\DynamoCopilot\tokens.json`). In-memory login state is kept in sync via **static events** on `AuthService`:

```csharp
public static event Action<string>? GlobalLoggedIn;   // fired after tokens saved
public static event Action?         GlobalLoggedOut;  // fired after tokens deleted
```

**Login sync flow:**
1. User submits credentials in `AuthFormView` â†’ `AuthFormViewModel.LoginAsync` calls `_authService.LoginAsync`
2. That `AuthService` instance saves tokens to disk and fires `GlobalLoggedIn`
3. The *other* VM's `OnGlobalLoggedIn` handler calls `_authService.TryLoadTokens()` first (to load the tokens written by the other instance into its own in-memory state), then calls `OnAuthSuccess()`

**Critical**: the `TryLoadTokens()` call in step 3 is mandatory. Without it, the receiving VM's `AuthService` instance has `_tokens == null`, so `GetGrantedExtensions()` returns empty â†’ "No License" shown, and `RefreshUserInfoAsync()` has no access token to make the `/api/me` call.

**Logout sync flow:**
1. User clicks Sign Out in VM-A â†’ VM-A calls `ClearAuthState()` first (sets `IsLoggedIn = false`)
2. Then calls `_authService.Logout()` â†’ fires `GlobalLoggedOut`
3. VM-A's handler guard `if (!IsLoggedIn) return` skips (already false) â€” no double clear
4. VM-B's handler guard passes â†’ calls `DispatchToUi(ClearAuthState)` â†’ VM-B's UI clears

Both VMs subscribe in their constructor and **unsubscribe in `Shutdown()`** to prevent memory leaks.

### Shared auth form (`AuthFormView`)

The login/register form is a single shared WPF `UserControl` (`AuthFormView.xaml`) used by both panels. It is **not** a popup window â€” it renders inline inside the panel.

- **`AuthFormViewModel`** â€” owns all form state (`LoginEmail`, `RegisterEmail`, `IsRegisterMode`, `IsAuthBusy`, `AuthError`). No event-based coupling to the panel VM; when login succeeds `GlobalLoggedIn` fires and the panel VM's `OnGlobalLoggedIn` hides the form via `IsLoggedIn`.
- Each panel VM creates its own `AuthFormViewModel` instance: `public AuthFormViewModel AuthForm { get; }`, initialized in the constructor with `_authService`.
- The panel XAML wraps it in a `Grid` that collapses when `IsLoggedIn = true`:

```xaml
<Grid Visibility="{Binding IsLoggedIn, Converter={StaticResource InvBoolToVis}}">
    <views:AuthFormView DataContext="{Binding AuthForm}"
                        HorizontalAlignment="Stretch"
                        VerticalAlignment="Center"/>
</Grid>
```

**Do not** make `AuthFormView` a popup `Window` â€” Dynamo's WPF host does not reliably support secondary windows created from extension code, and `Application.Current.MainWindow` ownership is unreliable in that context.

---

## Extension â€” Architecture & Key Design Decisions

### Copilot vs Suggest Nodes â€” feature boundary

- **Copilot** (`CopilotPanelViewModel`) â€” chat only: streaming LLM responses, Python code extraction, Insert/Fix-Error, spec-first flow, AI settings, user info panel
- **Suggest Nodes** (`SuggestNodesPanelViewModel`) â€” node search only: ONNX vector search via `LocalNodeSearchService` (capped at `TopK = 100` results), node cards with Download/Insert, user icon (top-right) reveals user info flyout. Results are split into two expandable groups: **Installed Packages** (`InstalledNodeSuggestions`) and **Online Packages** (`OnlineNodeSuggestions`), partitioned by `PackageStateService.IsInstalled()`. Both groups live inside a single shared `ScrollViewer`; there is no per-group scrollbar.

**Node suggestion cards do NOT appear in the Copilot chat.** If the AI mentions a node name in prose, it stays as text â€” no interactive cards. All node card functionality is isolated to the Suggest Nodes extension.

### Obsolete node strategy (Suggest Nodes)

nodes.db can contain nodes that no longer exist in the installed version of a package (removed or renamed), or that call Revit API removed in a newer Revit version. The ObsoleteNodeStore is the primary runtime defence for both cases â€” it is self-healing and version-aware.

**Runtime detection (`NodeSuggestionCardViewModel`):**
- When a user clicks Insert and the insertion fails (`InsertNode` returns false) AND the package is installed, the node is marked obsolete immediately.
- `_obsoleteStore.MarkObsolete(PackageName, Name)` â€” persists to disk instantly, **scoped to the current Revit year**.
- `IsObsolete = true` â€” disables the Insert button and shows a red âš  banner. The banner text comes from `ObsoleteMessage` (computed property on the card VM): *"Node not found â€” may have been removed or renamed in Revit 2025."* The year is read from `_obsoleteStore.CurrentRevitYear`; falls back to generic text if the year is unknown.

**Persistence (`ObsoleteNodeStore` â€” `DynamoCopilot.Core/Services/ObsoleteNodeStore.cs`):**
- Stores `(PackageName, NodeName, RevitYear?)` tuples in `%AppData%\DynamoCopilot\obsolete-nodes.json`.
- **Version-aware:** a node marked obsolete on Revit 2023 is hidden only on 2023+, not on 2022 where it may still work.
- `IsObsolete(pkg, name)` returns true when there is an entry matching (pkg, name, currentYear) OR a year-less globally-obsolete entry.
- `MarkObsolete(pkg, name)` tags the entry with `_currentRevitYear` set at construction.
- `CurrentRevitYear` property â€” exposed so `NodeSuggestionCardViewModel` can build the banner message.
- Loaded once on startup, saved immediately on each new entry. Thread-safe.
- **`obsolete-nodes.json` is never deleted by the installer** â€” it survives reinstalls and version upgrades.

**JSON format (`obsolete-nodes.json`):**
```json
[
  ["ObjectGNodes", "Analysis.GetAnalyticalModel", "2023"],  // obsolete on Revit 2023 only
  ["SomePackage",  "SomeNode",                    ""],       // globally obsolete
  ["OldPackage",   "OldNode"]                                // legacy 2-element â€” treated as globally obsolete
]
```

**Search filtering (`LocalNodeSearchService`):**
- Accepts `ObsoleteNodeStore` in constructor.
- In `SearchAsync`, obsolete nodes are filtered out of the cache before scoring â€” they never appear in results again.

**Wiring (construction order in `SuggestNodesViewExtension.CreateView`):**
```csharp
var revitYear     = TryGetRevitYear();           // reads VersionNumber via RevitServices reflection
var obsoleteStore = new ObsoleteNodeStore(revitYear);
var localSearch   = new LocalNodeSearchService(embedder, obsoleteStore);
_viewModel        = new SuggestNodesPanelViewModel(..., obsoleteStore, Name);
// SuggestNodesPanelViewModel passes obsoleteStore to each NodeSuggestionCardViewModel
```

**`TryGetRevitYear()`** in `SuggestNodesViewExtension` â€” reads `RevitServices.Persistence.DocumentManager.Instance.CurrentUIApplication.Application.VersionNumber` via reflection. Returns `null` outside a Revit context.

**Known limitation:** Packages that call removed Revit API *internally* (not in their public method signature) cannot be detected statically. For example `ObjectGNodes.Analysis.GetAnalyticalModel` returns `Dictionary<T,U>` â€” `AnalyticalModel` only appears inside the method body. The ObsoleteNodeStore handles these at runtime: the first failed insert marks the node obsolete for that Revit year, and it never appears in search results again on that machine.

### Spec-first flow (Copilot only)

When the user sends a message, `SendMessageCoreAsync` runs a classifier that decides whether this is a code-generation request. If it is, a `SpecCardViewModel` is shown inline in the chat (as a `ChatMessageType.SpecCard` message) instead of immediately calling the LLM.

**Chat input locking:** `IsChatInputEnabled = IsApiKeyPresent && !IsSpecPending`. Both the TextBox and the Send button bind to this. The chat input is disabled while a spec card is waiting for a response, forcing the user to interact with the card. It re-enables on Confirm or Cancel. A tooltip *"Respond to the specification above first"* is shown on hover (`ToolTipService.ShowOnDisabled="True"`).

**Custom instruction field:** Every spec card has an *"Additional instructions (optional)"* TextBox at the bottom (above the action buttons). `SpecCardViewModel.CustomInstruction` is a TwoWay-bound string. `CodeSpecification.CustomInstruction` is `[JsonIgnore]` â€” it is never part of the LLM-generated JSON, only set at runtime in `SpecCardViewModel.OnConfirm()` before the callback fires.

**Cancel behaviour:** Cancelling removes the spec card from `Messages` (the "You" bubble stays) and re-enables the chat input. The removal is done via a closure in `ShowSpecCard` that captures the `ChatMessageViewModel` reference â€” do not change this to `CancelPendingSpec` directly or the card will remain visible in the chat.

**Context sent to the LLM on Confirm:**
1. The original user message is already in `_currentSession.Messages` as a user turn.
2. If `CustomInstruction` is non-empty, it is shown as a new **"You" bubble** in the chat and added to `_currentSession.Messages` as a `ChatRole.User` message.
3. A synthetic code-generation user message is built from the spec steps, inputs, output, clarifying-question answers, and (if present) an `**Additional instructions from user:**` line â€” then added to `_currentSession.Messages`.
4. `RunStreamingAsync` sends the full session history (including all of the above) to the LLM.

### Per-extension licensing

Licences are stored in the `UserLicenses` table (one row per user per extension). Each extension has a fixed string identifier defined in `ExtensionConstants` (Core project) and `AppConstants` (Server project) â€” both files must stay in sync.

**Extension IDs** (`src/DynamoCopilot.Core/ExtensionConstants.cs`):
```csharp
public const string CopilotId      = "Copilot";
public const string SuggestNodesId = "SuggestNodes";
public const string SupportEmail   = "info@BIMEra.com";
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
1. `OnAuthSuccess()` â€” calls `_authService.GetGrantedExtensions()` which decodes the `ext` JWT claims synchronously. Sets `IsLicenceActive` immediately (no network call).
2. `RefreshUserInfoAsync()` â€” hits `/api/me`, finds the extension-specific `UserLicenseInfo` row in the `Licenses[]` array, updates `IsLicenceActive` and `LicenseEndDate` with server-authoritative values.
3. The XAML shows a "Sorry, you don't have a licenceâ€¦" banner when `IsLicenceActive = false`, and hides the chat input / search input. The user info panel shows the expiry date for that extension's licence only.

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

- `_installedCurrentVersion` â€” packages found under `CurrentVersionPackagesDir` (version-scoped, gates the Download/Insert buttons)
- `_installed` â€” all packages across every version (used only for path resolution via `GetPackageFolderPath`)
- `_paths` â€” prefers the current-version path when a package exists in multiple versions

**Do not revert to checking `_installed` for `IsInstalled`** â€” it caused the Download button to be disabled in the wrong Dynamo version.

---

### Node Insertion (`GraphNodeInserter`)

All insertion goes through `InsertNode(model, nodeName, packageName, nodeType, packageFolderPath, x, y, log)`.

The `log` parameter is `Action<string>?` â€” pass `CopilotLogger.Log` from the Extension call site. `GraphInterop` has no reference to `Extension` so it cannot call `CopilotLogger` directly.

**ZeroTouch insertion flow:**

1. `ResolveCreationName` scans loaded assemblies from the package's `bin/` folder and returns `type.Name + "." + method.Name` â€” **simple class name, no namespace**.
   - This must match `FunctionDescriptor.QualifiedName = ClassName + "." + UserFriendlyName` which is the dictionary key in `LibraryServices` (Dynamo source: `FunctionDescriptor.cs:417`).
   - Using `type.FullName` (with namespace) adds extra segments and breaks `LibraryServices.CanbeResolvedTo` which requires the search term to have â‰¤ segments than the key (`LibraryServices.cs:511`).

2. `TryResolveMangledName` queries `DynamoModel.LibraryServices` (NonPublic property) to promote from `ClassName.Method` to the exact `MangledName = ClassName.Method@T1,T2` needed for overloaded nodes.
   - Uses `GetFunctionDescriptor(string)` first, then `GetAllFunctionDescriptors` for overloads (`FunctionGroup.cs:71`).

3. `ExecuteCreateNode` fires `DynamoModel.CreateNodeCommand` via reflection. Node creation success is confirmed by finding the new GUID in `workspace.Nodes`.

**DYF insertion flow:**
- Finds `.dyf` file by simple node name, parses GUID from XML, calls `CustomNodeManager.AddUninitializedCustomNode`, then `CreateNodeCommand` with the GUID string.

**Critical**: `CanInsert` is gated on `IsInstalled` (disk presence), NOT on whether the node actually exists in Dynamo's runtime `LibraryServices`. If a node name in our index doesn't exist in the installed package version, Insert will fail with Dynamo's own "Could not create node" exception.

---

### Node Index (`nodes.db`)

- Location: `%AppData%\DynamoCopilot\nodes.db`
- Hosted on GitHub Releases `v1.0.0` as a release asset â€” the installer downloads it from there.
- Built by `DynamoCopilot.NodeIndexer` CLI tool.
- **67,140 nodes from 2,448 packages** â€” last full rebuild: 2026-05-13.
- `nodes.db` stores a `last_built_at` timestamp in its `Metadata` table; the `--update` mode reads this to fetch only packages changed since then.

**Incremental update (run periodically â€” monthly recommended):**
```powershell
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- `
  --update `
  --sqlite        "$env:APPDATA\DynamoCopilot\nodes.db" `
  --model         "$env:APPDATA\DynamoCopilot\models\model.onnx" `
  --vocab         "$env:APPDATA\DynamoCopilot\models\vocab.txt" `
  --keep-packages "$env:APPDATA\DynamoCopilot\packages"
```
Downloads only packages updated since `last_built_at`, re-indexes them, updates the timestamp. Zips are saved to `--keep-packages` dir and skipped on future runs.

**Full rebuild from scratch (use when DB is stale or after a long gap):**
```powershell
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- `
  --update --full `
  --sqlite        "$env:APPDATA\DynamoCopilot\nodes.db" `
  --model         "$env:APPDATA\DynamoCopilot\models\model.onnx" `
  --vocab         "$env:APPDATA\DynamoCopilot\models\vocab.txt" `
  --keep-packages "$env:APPDATA\DynamoCopilot\packages"
```
Clears all nodes, downloads all 2,448 packages, rebuilds from scratch. Cached zips in `--keep-packages` are skipped on re-download (`skipIfExists`).

**Download URL format** (resolved 2026-05-12): `http://www.dynamopackages.com/download/{package._id}/{version}`  
Do NOT use `{version.url}` â€” that field is a legacy S3 key and always returns 404.

After updating, upload to the GitHub release:
```powershell
gh release upload v1.0.0 "$env:APPDATA\DynamoCopilot\nodes.db" --repo BuvaneshSrinivasan/Copilot-Dynamo-Extension --clobber
```

To download the GitHub release version locally (e.g. to replace a dev copy with dead schema columns):
```powershell
gh release download v1.0.0 --repo BuvaneshSrinivasan/Copilot-Dynamo-Extension --pattern "nodes.db" --output "$env:APPDATA\DynamoCopilot\nodes.db" --clobber
```

**Diagnostic flags (NodeIndexer):**
```powershell
# Show total node count and last_built_at timestamp
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- --stats --sqlite "$env:APPDATA\DynamoCopilot\nodes.db"

# List all nodes extracted from a specific package zip (no embedding)
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- --inspect-pkg "C:\path\to\Package.zip"

# Inspect a DLL's referenced assemblies and Analysis class method signatures
dotnet run --project src/DynamoCopilot.NodeIndexer -c Release -f net8.0 -- --inspect-dll "C:\path\to\bin\SomeDll.dll"
```

**`node_libraries` filter (critical correctness rule):**

`PackageExtractor` only indexes DLLs whose base filename matches an assembly listed in `pkg.json`'s `node_libraries` field. This mirrors Dynamo's own `Package.IsNodeLibrary` logic (`Package.cs:459`):
- `node_libraries` absent â†’ index all DLLs (legacy packages)
- `node_libraries` present â†’ only index DLLs listed there

**Do not remove this filter.** Without it, packages that bundle third-party DLLs (e.g. Summerisle bundles LunchBox.dll) produce ghost node suggestions â€” nodes that exist in a bundled DLL but are never exposed by that package in Dynamo.

**Extraction approach (reflection-based):**
- **DYF nodes**: both XML (Dynamo 1.x) and JSON (Dynamo 2.x) formats handled by `DyfParser`
- **ZeroTouch nodes**: discovered via `MetadataLoadContext` (`DeclaredOnly` methods on public + nested-public, non-(abstract-and-not-sealed) types). `GetParameters()` is intentionally not called â€” it can stack-overflow on packages with deeply nested generic type signatures referencing unresolvable Dynamo assemblies.
- **NodeModel nodes**: types with `[NodeName("...")]` attribute
- Attribute access uses per-attribute try-catch (Dynamo SDK assemblies are not in the MLC resolver)
- A dynamic blocklist is built from the installed DynamoForRevit folder â€” any DLL found there is never indexed from a third-party package (prevents ghost nodes from bundled Dynamo/Revit DLLs)

---

### Logging

`CopilotLogger.Log(string)` appends to `%AppData%\DynamoCopilot\log`. It is in `DynamoCopilot.Extension` â€” not accessible from `GraphInterop` or `Core`. Pass it as `Action<string>` when crossing project boundaries.

---

## Server â€” Build Phases

| Phase | Status | What It Adds |
|-------|--------|-------------|
| 1 | âœ… Complete | Gemini streaming endpoint, no auth |
| 2 | âœ… Complete | PostgreSQL + Users table (EF Core) |
| 3 | âœ… Complete | Email/password auth, JWT access + refresh tokens |
| 4 | âœ… Complete | Rate limiting middleware (requests/day; tokens tracked, not capped) |
| 5 | âœ… Complete | Admin API endpoints + per-extension `UserLicenses` table |
| 6 | âœ… Complete | Razor Pages admin dashboard (`/Dashboard`) + `UsageLogs` history table |
| 7 | â³ Pending | Railway deployment (Dockerfile, env vars) |

---

## Server â€” File Structure

```
src/DynamoCopilot.Server/
â”œâ”€â”€ Program.cs                    Entry point: services + middleware + routes
â”œâ”€â”€ AppConstants.cs               Extension ID strings (must match ExtensionConstants in Core)
â”œâ”€â”€ appsettings.json              Default config (NO secrets here)
â”œâ”€â”€ appsettings.Development.json  Local dev overrides (never commit API keys)
â”œâ”€â”€ Dockerfile                    Phase 7
â”‚
â”œâ”€â”€ Models/
â”‚   â”œâ”€â”€ ChatRequest.cs            ChatRequest + ChatMessage records (DTOs)
â”‚   â”œâ”€â”€ User.cs                   EF Core entity â€” Users table
â”‚   â”œâ”€â”€ UserLicense.cs            EF Core entity â€” UserLicenses table (per-extension)
â”‚   â”œâ”€â”€ RefreshToken.cs           EF Core entity â€” RefreshTokens table
â”‚   â”œâ”€â”€ DynamoNode.cs             EF Core entity â€” DynamoNodes table
â”‚   â”œâ”€â”€ UsageLog.cs               EF Core entity â€” UsageLogs table (one row per user per day)
â”‚   â””â”€â”€ AuthRequests.cs           Login/register/refresh request DTOs
â”‚
â”œâ”€â”€ Services/
â”‚   â”œâ”€â”€ ILlmService.cs            Interface: any AI provider must implement this
â”‚   â”œâ”€â”€ GeminiService.cs          Google Gemini implementation
â”‚   â”œâ”€â”€ TokenService.cs           JWT generation + refresh token handling
â”‚   â”œâ”€â”€ UsageTracker.cs           Scoped mailbox â€” GeminiService writes tokens, middleware reads
â”‚   â”œâ”€â”€ EmbeddingService.cs       Gemini text embedding for node search
â”‚   â”œâ”€â”€ NodeSearchService.cs      Vector + keyword hybrid search
â”‚   â””â”€â”€ NodeRerankService.cs      Gemini re-ranking of search results
â”‚
â”œâ”€â”€ Endpoints/
â”‚   â”œâ”€â”€ ChatEndpoints.cs          POST /api/chat/stream  (requires Copilot licence)
â”‚   â”œâ”€â”€ NodeEndpoints.cs          POST /api/nodes/suggest (requires SuggestNodes licence)
â”‚   â”œâ”€â”€ AuthEndpoints.cs          POST /auth/register, /auth/login, /auth/refresh
â”‚   â”œâ”€â”€ UserEndpoints.cs          GET /api/me
â”‚   â””â”€â”€ AdminEndpoints.cs         GET /admin/users, POST /admin/grant, POST /admin/revoke, â€¦
â”‚
â”œâ”€â”€ Filters/
â”‚   â””â”€â”€ LicenseFilter.cs          Endpoint filter â€” checks JWT "ext" claim per extension
â”‚
â”œâ”€â”€ Data/
â”‚   â”œâ”€â”€ AppDbContext.cs           EF Core DbContext
â”‚   â””â”€â”€ Migrations/               Generated by `dotnet ef migrations add`
â”‚
â”œâ”€â”€ Middleware/
â”‚   â””â”€â”€ RateLimitMiddleware.cs    Checks IsActive + daily request limit; tracks tokens (no cap)
â”‚
â””â”€â”€ Pages/                        Razor Pages admin dashboard
    â”œâ”€â”€ _ViewImports.cshtml        Tag helpers + namespace
    â””â”€â”€ Dashboard/
        â”œâ”€â”€ _ViewStart.cshtml      Sets _Layout for all dashboard pages
        â”œâ”€â”€ _Layout.cshtml         Dark sidebar layout (Bootstrap 5.3 + Chart.js via CDN)
        â”œâ”€â”€ DashboardPageModel.cs  Base class with [Authorize(AuthenticationSchemes="AdminCookie")]
        â”œâ”€â”€ Login.cshtml(.cs)      Admin key login form â†’ issues 8-hour session cookie
        â”œâ”€â”€ Logout.cshtml(.cs)     GET /Dashboard/Logout â†’ signs out â†’ redirect to Login
        â”œâ”€â”€ Index.cshtml(.cs)      Dashboard: stat cards + registrations chart + top users
        â”œâ”€â”€ Users.cshtml(.cs)      User list with email search + status filter
        â””â”€â”€ UserDetail.cshtml(.cs) Per-user: licences, daily/monthly usage chart, request limit, notes
```

---

## Server â€” API Reference

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
| POST | /admin/grant | `{ email, extension, months }` â€” grant or extend a licence |
| POST | /admin/revoke | `{ email, extension }` â€” revoke a licence (sets IsActive=false) |
| POST | /admin/users/{id}/activate | Re-enable a deactivated account |
| POST | /admin/users/{id}/deactivate | Global account kill switch |
| POST | /admin/users/{id}/reset-usage | Reset daily counters |
| PATCH | /admin/users/{id}/limits | Override per-user rate limits |

### Admin Dashboard (Razor Pages)

Accessed at `https://your-server/Dashboard/Login`. Protected by an 8-hour session cookie â€” enter the `Admin:ApiKey` value to sign in.

| Page | URL | What it shows |
|------|-----|--------------|
| Login | `/Dashboard/Login` | Admin key form |
| Dashboard | `/Dashboard` | Stat cards (users, licences, tokens today), registrations chart, top users |
| Users | `/Dashboard/Users` | Searchable/filterable user table with licence badges and usage bars |
| User Detail | `/Dashboard/UserDetail?id=â€¦` | 4 usage cards (today + month), 30-day dual-axis chart, grant/revoke licences, request limit override, notes |

### POST /api/chat/stream â€” Request / Response

```json
{ "messages": [{ "role": "user", "content": "Write hello world in Python for Dynamo" }] }
```
```
data: {"type":"token","value":"Sure"}
data: {"type":"done"}
data: {"type":"error","message":"..."}   â† on failure
```

### JWT payload

```json
{
  "sub":   "<user-guid>",
  "email": "user@example.com",
  "jti":   "<unique-token-id>",
  "ext":   ["Copilot", "SuggestNodes"],   â† one entry per active licence
  "exp":   1234567890
}
```

`ext` is populated at login and refresh from the `UserLicenses` table. A user with no licences gets an empty `ext` array â€” they can log in but all extension endpoints return 403.

### GET /api/me â€” Response

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
| DailyRequestCount | int | Today's request count â€” resets lazily on first request of a new day |
| DailyTokenCount | int | Today's token count â€” resets lazily; tracked but never capped (BYOK) |
| LastResetDate | date | Nullable â€” date the daily counters were last zeroed |
| RequestLimit | int? | Nullable â€” per-user override; falls back to `RateLimit:DailyRequestLimit` (default 1000) |
| TokenLimit | int? | Kept in schema but **not enforced** â€” token cap was removed (BYOK) |
| InstalledVersion | string? | Last extension version reported by the client. Set lazily by `GET /api/me` when the extension sends `X-Client-Version` header. Used by the Releases dashboard for version-distribution analytics. |
| Notes | string? | Admin notes |
| CreatedAt | datetime | |

### UserLicenses table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK â†’ Users (cascade delete) |
| Extension | string | `"Copilot"` or `"SuggestNodes"` (max 64 chars) |
| IsActive | bool | Per-extension kill switch |
| StartDate | datetime | |
| EndDate | datetime? | Null = never expires |
| CreatedAt | datetime | |

Unique index on `(UserId, Extension)` â€” one row per user per extension.

### UsageLogs table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK â†’ Users (cascade delete) |
| Date | DateOnly | The calendar day this row covers |
| RequestCount | int | Total requests made on that day |
| TokenCount | int | Total tokens used on that day |

Unique index on `(UserId, Date)` â€” one row per user per day. Written by `RateLimitMiddleware` just before it resets the daily counters (lazy reset on first request of a new day). This gives the dashboard permanent historical data for daily and monthly analytics. Today's live counters are always read from `User.DailyRequestCount` / `User.DailyTokenCount` directly.

### RefreshTokens table
| Column | Type | Notes |
|--------|------|-------|
| Id | UUID | Primary key |
| UserId | UUID | FK â†’ Users (cascade delete) |
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
# â†’ http://localhost:8080
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

**Postman â€” grant a licence (alternative to dashboard):**
```
POST /admin/grant
X-Admin-Key: your-admin-key
{ "email": "user@example.com", "extension": "Copilot", "months": 12 }
```

---

## Configuration Reference

**Railway env var naming:** Railway runs on Linux, which allows `:` in environment variable names. Set variables exactly as shown (e.g. `Gemini:ApiKey`, `Jwt:Secret`) â€” ASP.NET Core reads them directly. The `__` double-underscore convention is only needed on Windows.

| Key | Description | Default |
|-----|-------------|---------|
| `Gemini:ApiKey` | Google Gemini API key | **Required** |
| `Gemini:Model` | Gemini model name | `gemini-2.5-flash` |
| `Gemini:SystemPrompt` | Override built-in Dynamo prompt | Built-in |
| `Jwt:Secret` | HMAC-SHA256 signing key (â‰¥32 chars) | **Required** |
| `Jwt:Issuer` | JWT issuer | `DynamoCopilot` |
| `Jwt:Audience` | JWT audience | `DynamoCopilot` |
| `Jwt:AccessTokenExpiryMinutes` | Access token lifetime | `60` |
| `Admin:ApiKey` | Secret for X-Admin-Key header **and** dashboard login | **Required** |
| `RateLimit:DailyRequestLimit` | Default daily request cap per user (token limit removed â€” BYOK) | `1000` |
| `PORT` | HTTP port (set by Railway automatically) | `8080` |
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string (local dev) | â€” |
| `DATABASE_URL` | PostgreSQL URI (set by Railway automatically) | â€” |

---

## Key Design Decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Auth | Email + password | OAuth requires browser redirects (awkward in desktop app) + Google app verification for production |
| Registration | Open + free SuggestNodes on register | Anyone can create an account and gets SuggestNodes automatically (no expiry); Copilot requires manual admin grant after payment |
| Licensing | `UserLicenses` junction table | Per-extension expiry dates; scales to many extensions without schema changes |
| Licence grant workflow | Postman â†’ `POST /admin/grant` by email | No payment system yet â€” manual Excel tracking; email avoids GUID lookup |
| Licence check â€” server | `LicenseFilter` endpoint filter reads JWT `ext` claims | Runs before handler, rejects 403 if extension absent; no DB call per request |
| Licence check â€” extension | JWT decoded client-side in `GetGrantedExtensions()` | Instant at login, no extra network call; `/api/me` confirms on panel open |
| No-licence UX | Panel visible but content replaced with banner | User can see the tool exists (upsell) but can't use it |
| AI Provider | Gemini 2.5 Flash | Cost-effective pre-revenue; model is a config value, swap without code changes |
| Rate limiting | Requests/day only (default 1000); tokens tracked but never capped | BYOK â€” user's own API key is charged for tokens, so a server-side token cap makes no sense |
| Token history | `UsageLogs` table (one row per user per day) | Written by middleware before lazy reset; enables daily/monthly analytics in dashboard without storing per-request logs |
| Admin dashboard | Razor Pages at `/Dashboard`, protected by session cookie | Single deployment (embedded in the server); direct DbContext access avoids a separate API layer; Bootstrap + Chart.js via CDN means no build step |
| Dashboard auth | Admin key login form â†’ 8-hour cookie (`dc_admin`) | Reuses existing `Admin:ApiKey` secret; GET-based logout for simplicity (CSRF on logout is acceptable for an internal tool) |
| Hosting | Railway | Native PostgreSQL addon, reads PORT + DATABASE_URL automatically |
| Two extensions, one DLL | Single DLL, two `IViewExtension` classes | Dynamo requires one XML manifest per extension; single DLL avoids duplicating shared services |
| Cross-extension auth sync | Static events on `AuthService` | Both extensions share the same AppDomain; static events are the correct in-process signal â€” no IPC needed |
| Panel state tracking | WPF `Loaded`/`Unloaded` events | Correctly detects user closing the panel via Dynamo's own X button, not just our menu item |
| Auto-update delivery | DLL-only zip in AppData, no new installer | DLLs live in AppData (user-writable); swap requires no UAC; XML manifests in Program Files never change for version bumps |
| Update apply mechanism | Separate `DynamoCopilot.Updater.exe` (net48) | DLL is locked while Dynamo runs; Updater waits for the Revit process to exit, then copies staged files â€” no Windows restart needed |
| Banner sync across panels | `UpdateBannerViewModel.Instance` static singleton | Both panel VMs bind to the same object; Install in one panel updates the other via `INotifyPropertyChanged` with no extra wiring |
| Version source of truth | `<Version>` in Extension `.csproj` | Both build scripts read from there; bumping the .csproj is the only step before invoking a release skill |
| nodes.db update | Separate optional "Update DB" button in banner | 186 MB; independent from DLL updates; SQLite file can be overwritten while Dynamo is running (no staging needed) |
| User version tracking | `X-Client-Version` header on `GET /api/me` | Captured lazily on every panel-open; stored in `User.InstalledVersion`; powers the version-distribution table in the Releases dashboard |

---

## Installer Build

The installer is a self-contained WPF exe (`installer-wpf/`) that bundles the extension DLLs as an embedded zip payload.

### Version â€” single source of truth

The `<Version>` tag in `src/DynamoCopilot.Extension/DynamoCopilot.Extension.csproj` is the **only** place you set the version. Both build scripts read it from there automatically â€” never pass `-Version` manually.

**To release:** bump `<Version>` in the .csproj, then invoke a skill (see Developer Skills below).

### Build command
```powershell
.\build-installer.ps1
# Reads version from .csproj automatically
# Output: installer-wpf\Output\DynamoCopilot-Setup.exe
```

### Build pipeline (in order)
1. `dotnet publish` Extension â†’ `installer-wpf\staging-dist\net48\` and `net8.0-windows\`
2. `dotnet publish` **DynamoCopilot.Updater** (net48) â†’ placed at `staging-dist\DynamoCopilot.Updater.exe`
3. `dotnet publish` installer WPF exe â†’ `installer-wpf\Output\`
4. Copies staging dist â†’ `installer-wpf\Output\dist\`
5. **Obfuscates** the 3 DLLs in a temp staging copy (`obfuscate.ps1`)
6. Zips the obfuscated staging copy â†’ `payload.zip`
7. Appends zip to the exe (`append_payload.ps1`)

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
- **Obfuscated:** private/internal members, fields, local variables, string literals (system prompts, server URL) â€” via `KeepPublicApi=true`
- **Preserved:** public class/method names (required for Dynamo ViewExtension loading and WPF BAML deserialization)
- **Stripped:** all `.pdb` files from the shipped DLLs

### Mapping files
`installer-wpf/obfuscation-mappings/Mapping_*.xml` maps obfuscated names back to originals â€” needed to decode crash stack traces. These are gitignored; keep them private.

### Key obfuscation constraints
- `SkipType` and `SkipNamespace` rules inside `<Module>` are silently ignored by Obfuscar 2.2.50 â€” only `KeepPublicApi` reliably controls what gets renamed
- `DynamoCopilotViewExtension` must keep its name â€” Dynamo reads it from `DynamoCopilot_ViewExtensionDefinition.xml`
- `SuggestNodesViewExtension` must keep its name â€” Dynamo reads it from `SuggestNodes_ViewExtensionDefinition.xml`
- All WPF view/viewmodel type names must be preserved â€” BAML embeds them as strings

---

## Obfuscation Compatibility Rules

### NEVER use anonymous types with `JsonSerializer`
Anonymous types (`new { email, password }`) generate compiler-produced generic types. Obfuscar renames their constructor parameter names to `null`. `System.Text.Json` inspects those parameter names when building its type cache â€” even for serialization â€” and throws:

> `The deserialization constructor for type 'A.a\`2[...]' contains parameters with null names.`

**Always use `Dictionary<string, T>` instead:**

```csharp
// WRONG â€” breaks after obfuscation
JsonSerializer.Serialize(new { email, password });

// CORRECT
JsonSerializer.Serialize(new Dictionary<string, string> { ["email"] = email, ["password"] = password });
```

This applies in: `AuthService`, `ServerLlmService`, `NodeSuggestService`, and all LLM provider services (`GeminiLlmService`, `OpenAiLlmService`, `OllamaLlmService`, `ClaudeLlmService`). All have already been converted.

The JSON wire format is byte-for-byte identical â€” dictionaries serialize the same way as anonymous types, so no server API changes are needed.

---

## Adding a New AI Provider

1. Create `Services/AnthropicService.cs` (or any name) implementing `ILlmService`
2. In `Program.cs`, change one line:
   ```csharp
   builder.Services.AddScoped<ILlmService, AnthropicService>();
   ```
3. Done â€” `ChatEndpoints.cs` doesn't change at all.

---

## Adding a New Extension

1. Add the extension ID to `ExtensionConstants.cs` (Core) and `AppConstants.cs` (Server)
2. Create the server endpoint, apply `.AddEndpointFilter(LicenseFilter.Require(AppConstants.Extensions.NewId))`
3. In the VM: set `IsLicenceActive = _authService.GetGrantedExtensions().Contains(ExtensionConstants.NewId)` in `OnAuthSuccess()`
4. In `RefreshUserInfoAsync()`: `var lic = info.GetLicense(ExtensionConstants.NewId)`
5. In the XAML: bind content rows to `IsLicenceActive`, add the no-licence banner (same pattern as Copilot/SuggestNodes)

---

## Auto-Update Delivery System

Users receive DLL-only updates silently â€” no new installer exe, no UAC prompt. The DLLs live in `%AppData%\DynamoCopilot\` (user-writable), so the update applies without elevation.

**Critical:** Users must close **Revit** entirely â€” not just the Dynamo panel. The DLLs are loaded into the Revit process. The Updater.exe watches the Revit PID; closing only the Dynamo panel keeps Revit running and the Updater waiting indefinitely.

### End-to-end flow

```
Developer                        Server / GitHub             User (Dynamo open)
â”€â”€â”€â”€â”€â”€â”€â”€â”€                        â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€             â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
Bump <Version> in .csproj
/publish-update skill â†’
  build DLL zip (~18 MB)
  upload to GitHub releases
  POST /admin/release        â†’  AppReleases table updated

                                                             Opens Revit/Dynamo
                                                             Extension calls GET /api/version/latest
                                                             (40s timeout â€” Railway cold starts ~20-30s)
                                                             Sees newer version â†’ shows banner
                                                             Clicks Install â†’ downloads zip in background
                                                             Launches Updater.exe --apply-update <pid> <newVersion>
                                                             Banner: "Update ready â€” close Revit to apply"
                                                             
                                                             User closes REVIT (not just Dynamo)
                                                             Updater.exe wakes up
                                                             Reads current DLL version for logging
                                                             Copies update\ â†’ live AppData folder
                                                             Logs: "v1.0.5 â†’ v1.0.6 (55 files updated)"
                                                             
                                                             User opens Revit
                                                             New DLL loads automatically
                                                             User info panel shows new version number
```

### Key files

| File | Purpose |
|------|---------|
| `src/DynamoCopilot.Core/Models/ReleaseManifest.cs` | DTO for deserializing `GET /api/version/latest` |
| `src/DynamoCopilot.Extension/ViewModels/UpdateBannerViewModel.cs` | Static singleton; checks version on startup, downloads DLLs + nodes.db, launches Updater |
| `installer-wpf/DynamoCopilot.Updater/Program.cs` | net48 helper; waits for Revit PID to exit then copies staged files |
| `src/DynamoCopilot.Server/Models/AppRelease.cs` | DB entity â€” one row per published release |
| `src/DynamoCopilot.Server/Endpoints/ReleaseEndpoints.cs` | `GET /api/version/latest` (public) |
| `publish-update.ps1` | Developer release script (used by `/publish-update` skill) |

### Server endpoints for releases

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | /api/version/latest | None | Extension polls this on startup |
| POST | /admin/release | X-Admin-Key | Publish a new release manifest |
| PATCH | /admin/release/{id}/minVersion | X-Admin-Key | Update the version gate without republishing |
| PATCH | /admin/release/latest/db | X-Admin-Key | Update nodes.db info on the latest release |

### UpdateBannerViewModel â€” critical design rules

- **Static singleton** â€” `UpdateBannerViewModel.Instance` is shared between both panel VMs. Both panels bind to the same object, so Install/Dismiss in one panel instantly updates the other. Do not create new instances.
- **`StartAsync` is idempotent** â€” uses `Interlocked.Exchange` so both ViewExtensions can call it; only the first one triggers the version check. Runs once per Revit session.
- **40s HTTP timeout** â€” Railway cold starts take 20â€“30 seconds. 12s was too short and caused timeouts on the first open after a period of inactivity.
- **DLL update** â€” downloads `dlls-vX.Y.Z.zip` to `%AppData%\DynamoCopilot\update\`, launches `DynamoCopilot.Updater.exe --apply-update <pid> <newVersion>`, shows "close Revit to apply".
- **Close Revit, not Dynamo** â€” the Updater waits for the Revit PID (`Process.GetCurrentProcess().Id`) to exit. Closing the Dynamo panel alone keeps Revit alive and the Updater blocking.
- **nodes.db update** â€” reads `last_built_at` from the local Metadata SQLite table; if older than `manifest.NodesDb.DbVersion`, shows a secondary "Update DB" button. The DB file is overwritten directly (SQLite is safe to swap while running).
- **`IsBannerVisible`** is a computed property (`IsDllUpdateVisible || IsDbUpdateVisible`) â€” do not set it directly.
- **Version comparison** â€” uses `System.Version` parsing and `<` operator. The installed version comes from `Assembly.GetExecutingAssembly().GetName().Version`.
- **Version display** â€” `InstalledVersionDisplay` property on both panel VMs reads `Assembly.GetExecutingAssembly().GetName().Version` and is shown in the user info panel as "Version: v1.0.6".

### Updater.exe logging (`%AppData%\DynamoCopilot\updater.log`)

Each run appends lines like:
```
[2026-05-15 14:23:17] Staged: v1.0.6 | Installed: 1.0.5 | Waiting for Revit PID 35040 to exitâ€¦
[2026-05-15 14:27:11] Revit exited. Installing v1.0.6â€¦
[2026-05-15 14:27:11] Done. v1.0.5 â†’ v1.0.6 (55 files updated, 1 skipped).
```
If the Updater is stuck (last line is still "Waiting for Revit PIDâ€¦"), Revit hasn't been fully closed yet.

### DLL zip structure (inside `dlls-vX.Y.Z.zip`)

```
net48/                         â†’ copied to %AppData%\DynamoCopilot\net48\
  DynamoCopilot.Extension.dll
  DynamoCopilot.Core.dll
  DynamoCopilot.GraphInterop.dll
  runtimes/win-*/...
net8.0-windows/                â†’ copied to %AppData%\DynamoCopilot\net8.0-windows\
  (same files)
DynamoCopilot.Updater.exe      â†’ copied to %AppData%\DynamoCopilot\ (root)
```

### Version tracking (user analytics)

- The extension sends `X-Client-Version: {assembly version}` on every `GET /api/me` call.
- `UserEndpoints.GetMeAsync` reads this header and updates `User.InstalledVersion` lazily.
- The admin Releases dashboard (`/Dashboard/Releases`) shows a version-distribution table built from `User.InstalledVersion` groupings.

---

## Developer Skills

Three Claude Code slash commands live in `.claude/commands/`. Invoke them by typing the skill name in the chat.

### `/build-installer [version]`

Bumps the version in `.csproj`, builds `DynamoCopilot-Setup.exe`, and reports the output path. Use this when you have a full new release for new users.

```
/build-installer          # patch increment: 1.0.3 â†’ 1.0.4
/build-installer minor    # minor increment: 1.0.3 â†’ 1.1.0
/build-installer 1.2.0   # exact version
```

### `/publish-update [version]`

Bumps the version, builds the DLL zip (~5 MB), uploads to GitHub, and calls `POST /admin/release`. Existing users get the banner automatically. **Does not build a new installer exe.**

```
/publish-update           # patch increment
/publish-update force     # patch increment + sets minVersion gate (forces all users)
/publish-update 1.0.5 force   # exact version + force gate
```

Requires env vars: `DYNAMO_ADMIN_KEY`, `DYNAMO_SERVER_URL`.

### `/update-nodesdb [full|stats]`

Runs the NodeIndexer incrementally (or fully), uploads the new `nodes.db` to GitHub, and calls `PATCH /admin/release/latest/db` to update the manifest. Users see "Update database" in the Suggest Nodes panel.

```
/update-nodesdb           # incremental update (recommended monthly)
/update-nodesdb full      # full rebuild from scratch (use after long gap)
/update-nodesdb stats     # show current DB stats only, no update
```

Requires env vars: `DYNAMO_ADMIN_KEY`, `DYNAMO_SERVER_URL`.

---

## Learning Topics by Phase

| Phase | Concept to learn | Where |
|-------|-----------------|-------|
| 1 | Minimal APIs, IAsyncEnumerable, SSE streaming | Done âœ… |
| 2 | EF Core migrations with PostgreSQL | Done âœ… |
| 3 | JWT Bearer authentication + refresh token rotation | Done âœ… |
| 4 | Writing custom middleware | Done âœ… |
| 5 | Endpoint filters, per-resource authorization | Done âœ… |
| 6 | Razor Pages, cookie authentication, Chart.js | Done âœ… |
| 7 | Docker basics | Docker "Getting Started" guide |
