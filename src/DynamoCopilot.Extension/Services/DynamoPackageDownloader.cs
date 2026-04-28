using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Downloads and hot-loads a Dynamo package using the same call chain that
    /// Dynamo's own Package Manager UI uses (verified against Dynamo-master source).
    ///
    /// Call chain (mirrors PackageManagerClientViewModel.SetPackageState):
    ///   1. PackageManagerClient.ListAll()              → find PackageHeader by name
    ///   2. PackageManagerClient.DownloadPackage(id, version, out zipPath)
    ///   3. PackageDownloadHandle.Done(zipPath)         → marks download complete, sets DownloadPath
    ///   4. PackageDownloadHandle.Extract(model, installDir, out pkg)
    ///      → unzips to {installDir}\{packageName}, sets pkg.RootDirectory
    ///   5. PackageLoader.LoadPackages(new[]{ pkg })    → hot-loads into the library (no restart)
    ///
    /// Object access path:
    ///   DynamoViewModel.Model                          → DynamoModel
    ///   DynamoModel.ExtensionManager.Extensions        → PackageManagerExtension
    ///   PackageManagerExtension.PackageManagerClient   (public property)
    ///   PackageManagerExtension.PackageLoader          (public property)
    /// </summary>
    public sealed class DynamoPackageDownloader : IDisposable
    {
        private readonly object? _dynamoViewModel;

        public DynamoPackageDownloader(object? dynamoViewModel = null)
        {
            _dynamoViewModel = dynamoViewModel;
        }

        public async Task<string> DownloadAsync(
            string            packageName,
            string            targetPackagesDir,
            CancellationToken ct = default)
        {
            Log($"DownloadAsync START — package={packageName}  targetDir={targetPackagesDir}");

            if (string.IsNullOrWhiteSpace(targetPackagesDir))
                throw new InvalidOperationException(
                    "Cannot determine the Dynamo packages folder. " +
                    "Please install the package manually via the Dynamo Package Manager.");

            if (_dynamoViewModel == null)
                throw new InvalidOperationException(
                    $"Could not download \"{packageName}\": DynamoViewModel not available. " +
                    "Please install it manually via the Dynamo Package Manager " +
                    "(Packages → Search for a Package).");

            // Phase 1 — background thread: network I/O only (ListAll + DownloadPackage → zip on disk)
            Log($"Phase 1 starting (background thread) — {packageName}");
            var phase1 = await Task.Run(() => DownloadZip(packageName), ct);

            if (!phase1.success)
            {
                Log($"Phase 1 FAILED — {phase1.error}");
                throw new InvalidOperationException(
                    $"Could not download \"{packageName}\".\n\nDetails: {phase1.error}\n\n" +
                    "Please install it manually via the Dynamo Package Manager " +
                    "(Packages → Search for a Package).");
            }
            Log($"Phase 1 OK — zip={phase1.zipPath}  id={phase1.packageId}  ver={phase1.latestVersion}");

            // Phase 2 — UI thread (back here after await): Extract zip + hot-load into Dynamo.
            // Must be on UI thread because PackageLoader fires WPF-owned events.
            Log($"Phase 2 starting (UI thread) — {packageName}");
            var phase2 = ExtractAndLoad(phase1, packageName, targetPackagesDir);

            if (!phase2.success)
            {
                Log($"Phase 2 FAILED — {phase2.error}");
                throw new InvalidOperationException(
                    $"Downloaded \"{packageName}\" but could not load it.\n\nDetails: {phase2.error}\n\n" +
                    "The package files are on disk. Please restart Dynamo to complete loading, " +
                    "or install manually via the Package Manager.");
            }

            Log($"DownloadAsync COMPLETE — {packageName}");
            return targetPackagesDir;
        }

        // ── Data carrier between the two phases ──────────────────────────────────────────
        private sealed class DownloadResult
        {
            public bool    success;
            public string  error         = string.Empty;
            public string  zipPath       = string.Empty;
            public string  packageId     = string.Empty;   // PackageHeader._id (or PackageVersion.id if present)
            public string  latestVersion = string.Empty;
            public object? model;
            public object? pmClient;
            public object? packageLoader;
        }

        // ── Phase 1: runs on a background thread ─────────────────────────────────────────
        // Network I/O only: ListAll → find header → pick latest version → DownloadPackage → zip on disk.
        private DownloadResult DownloadZip(string packageName)
        {
            var r = new DownloadResult();

            // DynamoViewModel.Model → DynamoModel
            r.model = _dynamoViewModel!.GetType()
                .GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(_dynamoViewModel);
            if (r.model == null)
                return Fail(r, "DynamoViewModel.Model not found");

            // DynamoModel.ExtensionManager
            var extMgr = r.model.GetType()
                .GetProperty("ExtensionManager", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(r.model);
            if (extMgr == null)
                return Fail(r, "DynamoModel.ExtensionManager not found");

            // ExtensionManager.Extensions → find PackageManagerExtension
            // (identified by having both PackageManagerClient and PackageLoader public properties)
            var extensions = extMgr.GetType()
                .GetProperty("Extensions", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(extMgr) as IEnumerable;
            if (extensions == null)
                return Fail(r, "ExtensionManager.Extensions is null or not IEnumerable");

            foreach (var ext in extensions)
            {
                if (ext == null) continue;
                var t = ext.GetType();
                var clientProp = t.GetProperty("PackageManagerClient", BindingFlags.Public | BindingFlags.Instance);
                if (clientProp == null) continue;
                r.pmClient      = clientProp.GetValue(ext);
                r.packageLoader = t.GetProperty("PackageLoader", BindingFlags.Public | BindingFlags.Instance)
                                   ?.GetValue(ext);
                break;
            }
            if (r.pmClient == null)
                return Fail(r, "PackageManagerExtension not found in ExtensionManager.Extensions");

            var clientType = r.pmClient.GetType();

            // PackageManagerClient.ListAll() → IEnumerable<PackageHeader>
            // Method is internal in DynamoPackages, so requires NonPublic flag.
            var listAll = clientType.GetMethod(
                "ListAll",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (listAll == null)
                return Fail(r, "PackageManagerClient.ListAll() not found");

            IEnumerable? headers;
            try   { headers = listAll.Invoke(r.pmClient, null) as IEnumerable; }
            catch (Exception ex) { return Fail(r, "ListAll() threw: " + Unwrap(ex)); }
            if (headers == null)
                return Fail(r, "ListAll() returned null");

            // Find matching PackageHeader by name (case-insensitive)
            object? matchedHeader = null;
            foreach (var h in headers)
            {
                if (h == null) continue;
                var n = h.GetType()
                    .GetProperty("name", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(h) as string;
                if (n?.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                { matchedHeader = h; break; }
            }
            if (matchedHeader == null)
                return Fail(r, $"Package \"{packageName}\" not found on the Dynamo package server");

            // PackageHeader._id — the package's unique server ID used for download
            r.packageId = matchedHeader.GetType()
                .GetProperty("_id", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(matchedHeader) as string ?? string.Empty;
            if (string.IsNullOrEmpty(r.packageId))
                return Fail(r, "PackageHeader._id is empty — cannot download without a package ID");

            // PackageHeader.versions — pick the numerically highest version string.
            // Also try to grab PackageVersion.id because the ViewModel uses dep.id (from PackageVersion)
            // rather than PackageHeader._id when calling DownloadPackage. They are usually the same value,
            // but prefer the version-level id when available.
            var versionsList = matchedHeader.GetType()
                .GetProperty("versions", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(matchedHeader) as IEnumerable;

            if (versionsList != null)
            {
                var best = new Version(0, 0, 0, 0);
                foreach (var v in versionsList)
                {
                    if (v == null) continue;
                    var vStr = v.GetType()
                        .GetProperty("version", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(v) as string;
                    if (string.IsNullOrEmpty(vStr)) continue;

                    if (!Version.TryParse(vStr, out var parsed)) continue;
                    if (parsed <= best) continue;

                    best = parsed;
                    r.latestVersion = vStr!;

                    // PackageVersion.id overrides PackageHeader._id when present
                    var vId = v.GetType()
                        .GetProperty("id", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(v) as string;
                    if (!string.IsNullOrEmpty(vId))
                        r.packageId = vId!;
                }
            }

            if (string.IsNullOrEmpty(r.latestVersion))
                return Fail(r, $"No valid versions found for package \"{packageName}\"");

            // PackageManagerClient.DownloadPackage(string packageId, string version, out string pathToPackage)
            // Method is internal, requires NonPublic flag.
            var dlMethod = clientType.GetMethod(
                "DownloadPackage",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (dlMethod == null)
                return Fail(r, "PackageManagerClient.DownloadPackage() not found");

            var dlArgs    = new object?[] { r.packageId, r.latestVersion, null };
            object? dlResult;
            try   { dlResult = dlMethod.Invoke(r.pmClient, dlArgs); }
            catch (Exception ex) { return Fail(r, "DownloadPackage() threw: " + Unwrap(ex)); }

            var dlSuccess = dlResult?.GetType()
                .GetProperty("Success", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(dlResult) as bool? ?? false;
            if (!dlSuccess)
            {
                var dlErr = dlResult?.GetType()
                    .GetProperty("Error", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dlResult) as string ?? "unknown error";
                return Fail(r, "DownloadPackage failed: " + dlErr);
            }

            r.zipPath = dlArgs[2] as string ?? string.Empty;
            if (string.IsNullOrEmpty(r.zipPath) || !File.Exists(r.zipPath))
                return Fail(r, $"Zip file not found after download: \"{r.zipPath}\"");

            r.success = true;
            return r;
        }

        // ── Phase 2: must run on the UI thread ───────────────────────────────────────────
        // Mirrors PackageManagerClientViewModel.SetPackageState exactly:
        //   handle.Done(zipPath)                             — marks downloaded, sets DownloadPath
        //   handle.Extract(model, installDir, out pkg)       — unzips + builds Package object
        //   packageLoader.LoadPackages(new[]{ pkg })         — hot-loads ZeroTouch DLLs + custom nodes
        private (bool success, string error) ExtractAndLoad(
            DownloadResult phase1, string packageName, string targetDir)
        {
            try
            {
                Log($"ExtractAndLoad: locating PackageDownloadHandle type in DynamoPackages assembly");
                // PackageDownloadHandle is in the same assembly as PackageManagerClient (DynamoPackages.dll)
                var handleType = phase1.pmClient!.GetType().Assembly
                    .GetType("Dynamo.PackageManager.PackageDownloadHandle");
                if (handleType == null)
                {
                    Log("ExtractAndLoad FAIL: PackageDownloadHandle type not found");
                    return (false, "Type 'Dynamo.PackageManager.PackageDownloadHandle' not found in DynamoPackages");
                }

                var handle = Activator.CreateInstance(handleType)
                    ?? throw new InvalidOperationException("Activator.CreateInstance returned null for PackageDownloadHandle");

                SetProp(handleType, handle, "Name",        packageName);
                SetProp(handleType, handle, "Id",          phase1.packageId);
                SetProp(handleType, handle, "VersionName", phase1.latestVersion);

                // Done(string filePath): sets DownloadPath = zipPath, DownloadState = Downloaded
                handleType.GetMethod("Done", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(handle, new object[] { phase1.zipPath });

                Log($"ExtractAndLoad: calling Extract — targetDir={targetDir}");
                var extractMethod = handleType.GetMethod("Extract", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("PackageDownloadHandle.Extract() not found");

                var extractArgs = new object?[] { phase1.model, targetDir, null };
                var extracted   = extractMethod.Invoke(handle, extractArgs) as bool? ?? false;
                if (!extracted)
                {
                    Log("ExtractAndLoad FAIL: Extract returned false");
                    return (false, "PackageDownloadHandle.Extract returned false — " +
                                   "zip may be empty, corrupt, or missing a pkg.json manifest");
                }

                var pkg = extractArgs[2]; // out Package param
                if (pkg == null)
                {
                    Log("ExtractAndLoad FAIL: Extract returned true but out Package is null");
                    return (false, "Extract returned true but the out Package parameter is null");
                }

                // Log the actual runtime type of the model so we know what we're reflecting against.
                Log($"ExtractAndLoad: phase1.model type = {phase1.model?.GetType().FullName ?? "(null)"}");

                var rootDir = pkg.GetType()
                    .GetProperty("RootDirectory", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pkg) as string;
                Log($"ExtractAndLoad: Extract OK — RootDirectory={rootDir}");

                // Log node_libraries from pkg.json so we know what Dynamo treats as a node library.
                LogNodeLibraries(pkg);

                if (phase1.packageLoader == null)
                {
                    Log("ExtractAndLoad FAIL: PackageLoader is null");
                    return (false, "PackageLoader not available — package extracted but not loaded");
                }

                // Mirror what PackageLoader.LoadAll() does before calling LoadPackages:
                // pathManager.AddResolutionPath(pkg.BinaryDirectory)
                // Without this the CLR cannot find inter-DLL dependencies inside the package's bin/
                // folder, causing a silent LibraryLoadFailedException inside TryLoadPackageIntoLibrary.
                if (!string.IsNullOrEmpty(rootDir))
                {
                    var binDir = Path.Combine(rootDir, "bin");
                    Log($"ExtractAndLoad: binDir={binDir}  exists={Directory.Exists(binDir)}");
                    if (Directory.Exists(binDir))
                    {
                        // Log every DLL file present so we can spot any that TryLoadFrom silently skipped.
                        foreach (var f in Directory.GetFiles(binDir, "*.dll"))
                            Log($"  bin file: {Path.GetFileName(f)}");

                        var pathManager = phase1.model!.GetType()
                            .GetProperty("PathManager", BindingFlags.Public | BindingFlags.Instance)
                            ?.GetValue(phase1.model);

                        var addResolution = pathManager?.GetType()
                            .GetMethod("AddResolutionPath", BindingFlags.Public | BindingFlags.Instance);
                        Log($"ExtractAndLoad: AddResolutionPath method found={addResolution != null}");
                        addResolution?.Invoke(pathManager, new object[] { binDir });
                    }
                }

                // PackageLoader.LoadPackages(IEnumerable<Package> packages)
                var loadMethod = phase1.packageLoader.GetType()
                    .GetMethod("LoadPackages", BindingFlags.Public | BindingFlags.Instance);
                if (loadMethod == null)
                {
                    Log("ExtractAndLoad FAIL: PackageLoader.LoadPackages() not found");
                    return (false, "PackageLoader.LoadPackages() not found");
                }

                var pkgArray = Array.CreateInstance(pkg.GetType(), 1);
                pkgArray.SetValue(pkg, 0);

                // Snapshot SearchModel entry count — if it doesn't increase after LoadPackages,
                // the PackagesLoaded → OnLibrariesImported event chain failed silently.
                int countBefore = GetSearchModelEntryCount(phase1.model);
                Log($"ExtractAndLoad: SearchModel entries before LoadPackages = {countBefore}");

                try
                {
                    Log("ExtractAndLoad: calling LoadPackages...");
                    loadMethod.Invoke(phase1.packageLoader, new object[] { pkgArray });
                    Log("ExtractAndLoad: LoadPackages returned OK");
                }
                catch (Exception ex)
                {
                    Log($"ExtractAndLoad FAIL: LoadPackages threw — {Unwrap(ex)}");
                    return (false, "LoadPackages threw: " + Unwrap(ex));
                }

                int countAfter = GetSearchModelEntryCount(phase1.model);
                Log($"ExtractAndLoad: SearchModel entries after LoadPackages = {countAfter}  (delta={countAfter - countBefore})");

                // Log Package.LoadedAssemblies to see what was actually loaded
                LogLoadedAssemblies(pkg);

                if (countAfter <= countBefore)
                {
                    Log("ExtractAndLoad: event chain added 0 entries — falling back to TryManualLibraryRefresh");
                    if (phase1.model != null)
                        TryManualLibraryRefresh(phase1.model, pkg);

                    int countFinal = GetSearchModelEntryCount(phase1.model);
                    Log($"ExtractAndLoad: SearchModel entries after manual refresh = {countFinal}");
                    if (countFinal <= countBefore)
                    {
                        Log("ExtractAndLoad: manual refresh also added 0 entries — library will update on restart");
                    }
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                Log($"ExtractAndLoad EXCEPTION: {ex}");
                return (false, "ExtractAndLoad threw: " + Unwrap(ex));
            }
        }

        // ── Library-refresh helpers ───────────────────────────────────────────────────────

        private static void LogNodeLibraries(object pkg)
        {
            try
            {
                // pkg.Header.node_libraries — the list Dynamo uses to decide IsNodeLibrary.
                var header = pkg.GetType()
                    .GetProperty("Header", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pkg);
                if (header == null) { Log("LogNodeLibraries: Header is null"); return; }

                var nodeLibs = header.GetType()
                    .GetProperty("node_libraries", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(header) as IEnumerable;
                if (nodeLibs == null) { Log("LogNodeLibraries: node_libraries is null (package has no ZeroTouch DLLs listed)"); return; }

                int count = 0;
                foreach (var lib in nodeLibs)
                {
                    Log($"  node_libraries entry: {lib}");
                    count++;
                }
                Log($"LogNodeLibraries: {count} entry/entries in node_libraries");
            }
            catch (Exception ex) { Log($"LogNodeLibraries exception: {ex.Message}"); }
        }

        private static void LogLoadedAssemblies(object pkg)
        {
            try
            {
                var loadedAssemblies = pkg.GetType()
                    .GetProperty("LoadedAssemblies", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pkg) as IEnumerable;

                if (loadedAssemblies == null) { Log("LoadedAssemblies: property not found or null"); return; }

                int total = 0, nodeLibCount = 0;
                foreach (var pa in loadedAssemblies)
                {
                    if (pa == null) continue;
                    total++;
                    var paType     = pa.GetType();
                    var isNodeLib  = paType.GetProperty("IsNodeLibrary", BindingFlags.Public | BindingFlags.Instance)?.GetValue(pa) as bool? ?? false;
                    var assem      = paType.GetProperty("Assembly",     BindingFlags.Public | BindingFlags.Instance)?.GetValue(pa);
                    var loc        = assem?.GetType().GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)?.GetValue(assem) as string ?? "(null)";
                    Log($"  LoadedAssembly: IsNodeLibrary={isNodeLib}  Location={loc}");
                    if (isNodeLib) nodeLibCount++;
                }
                Log($"LoadedAssemblies total={total}  nodeLibraries={nodeLibCount}");
            }
            catch (Exception ex) { Log($"LogLoadedAssemblies exception: {ex.Message}"); }
        }

        /// <summary>
        /// Returns the current number of entries in DynamoModel.SearchModel.
        /// Used to detect whether LoadPackages triggered the event chain.
        /// </summary>
        private static int GetSearchModelEntryCount(object? model)
        {
            try
            {
                if (model == null) return -1;
                var modelType = model.GetType();
                var smProp = modelType.GetProperty("SearchModel", BindingFlags.Public | BindingFlags.Instance)
                          ?? modelType.GetProperty("SearchModel", BindingFlags.NonPublic | BindingFlags.Instance);

                if (smProp == null)
                {
                    // Dump all property names so we can find the real one.
                    var names = string.Join(", ", Array.ConvertAll(
                        modelType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                        p => p.Name));
                    Log($"GetSearchModelEntryCount: SearchModel not found on {modelType.Name}. Public properties: {names}");
                    return -1;
                }

                var sm = smProp.GetValue(model);
                if (sm == null) { Log("GetSearchModelEntryCount: SearchModel property is null"); return -1; }

                var entries = sm.GetType()
                    .GetProperty("Entries", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(sm);
                if (entries == null) { Log("GetSearchModelEntryCount: Entries property not found"); return -1; }

                // Dictionary.KeyCollection exposes a Count property directly.
                var countProp = entries.GetType()
                    .GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                if (countProp != null)
                    return (int)(countProp.GetValue(entries) ?? 0);

                // Fallback: iterate
                int c = 0;
                foreach (var _ in (IEnumerable)entries) c++;
                return c;
            }
            catch (Exception ex) { Log($"GetSearchModelEntryCount exception: {ex.Message}"); return -1; }
        }

        /// <summary>
        /// Directly calls DynamoModel.AddZeroTouchNodesToSearch for each node-library DLL
        /// in the package, bypassing the PackagesLoaded event chain.
        ///
        /// Called only when the event chain produced zero new SearchModel entries, meaning
        /// either PackagesLoaded didn't fire or GetFunctionGroups returned empty. Doing
        /// this directly avoids duplicates because we only reach this branch when the
        /// normal path added nothing.
        /// </summary>
        private static void TryManualLibraryRefresh(object model, object pkg)
        {
            try
            {
                Log("TryManualLibraryRefresh: collecting node-library DLL paths from LoadedAssemblies");
                var loadedAssemblies = pkg.GetType()
                    .GetProperty("LoadedAssemblies", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pkg) as IEnumerable;
                if (loadedAssemblies == null) { Log("TryManualLibraryRefresh: LoadedAssemblies null — aborting"); return; }

                var dllPaths = new List<string>();
                foreach (var pa in loadedAssemblies)
                {
                    if (pa == null) continue;
                    var paType = pa.GetType();

                    var isNodeLib = paType
                        .GetProperty("IsNodeLibrary", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(pa) as bool? ?? false;
                    if (!isNodeLib) continue;

                    var assem = paType
                        .GetProperty("Assembly", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(pa);
                    if (assem == null) continue;

                    var loc = assem.GetType()
                        .GetProperty("Location", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(assem) as string;
                    if (!string.IsNullOrEmpty(loc))
                        dllPaths.Add(loc!);
                }
                Log($"TryManualLibraryRefresh: found {dllPaths.Count} node-library path(s): {string.Join(", ", dllPaths)}");

                if (dllPaths.Count == 0)
                {
                    Log("TryManualLibraryRefresh: no node-library DLLs loaded — package may be custom-nodes only or DLL load failed");
                    return;
                }

                var libServices = model.GetType()
                    .GetProperty("LibraryServices", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(model);
                Log($"TryManualLibraryRefresh: LibraryServices found={libServices != null}");
                if (libServices == null) { Debugger.Launch(); return; }

                // LibraryServices.GetFunctionGroups(string library) — internal
                var getFunctionGroups = libServices.GetType()
                    .GetMethod("GetFunctionGroups",
                               BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                               null, new[] { typeof(string) }, null);
                Log($"TryManualLibraryRefresh: GetFunctionGroups method found={getFunctionGroups != null}");

                // DynamoModel.AddZeroTouchNodesToSearch(IEnumerable<FunctionGroup>) — internal
                var addZTNodes = model.GetType()
                    .GetMethod("AddZeroTouchNodesToSearch",
                               BindingFlags.NonPublic | BindingFlags.Instance);
                Log($"TryManualLibraryRefresh: AddZeroTouchNodesToSearch method found={addZTNodes != null}");

                if (getFunctionGroups == null || addZTNodes == null)
                {
                    Log("TryManualLibraryRefresh FAIL: could not reflect required methods");
                    return;
                }

                foreach (var path in dllPaths)
                {
                    Log($"TryManualLibraryRefresh: GetFunctionGroups({path})");
                    var funcGroups = getFunctionGroups.Invoke(libServices, new object[] { path });
                    if (funcGroups == null) { Log($"  → GetFunctionGroups returned null for {path}"); Debugger.Launch(); continue; }

                    // Count how many groups were returned
                    int groupCount = 0;
                    foreach (var _ in (IEnumerable)funcGroups) groupCount++;
                    Log($"  → {groupCount} FunctionGroup(s) returned");

                    if (groupCount == 0)
                    {
                        // LibraryServices has no functions registered for this DLL —
                        // LoadNodeLibrary failed (DesignScript compile error or already-loaded conflict).
                        Log($"  → 0 function groups for {path}: LibraryServices.LoadNodeLibrary likely failed");
                    }
                    else
                    {
                        // AddZeroTouchNodesToSearch fires EntryAdded → library view observer →
                        // NotifySearchModelUpdate → SpecificationUpdated → RefreshLibraryView (WebView2).
                        Log($"  → calling AddZeroTouchNodesToSearch with {groupCount} group(s)");
                        addZTNodes.Invoke(model, new object[] { funcGroups });
                        Log($"  → AddZeroTouchNodesToSearch returned");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"TryManualLibraryRefresh EXCEPTION: {ex}");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        private static void Log(string message) =>
            CopilotLogger.Log($"[PackageDownloader] {message}");

        private static DownloadResult Fail(DownloadResult r, string error)
        {
            Log($"DownloadZip FAIL: {error}");
            r.success = false;
            r.error   = error;
            return r;
        }

        private static void SetProp(Type type, object obj, string name, string? value)
        {
            if (value == null) return;
            type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.SetValue(obj, value);
        }

        private static string Unwrap(Exception ex) =>
            ex.InnerException?.Message ?? ex.Message;

        public void Dispose() { }
    }
}
