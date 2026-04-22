using System;
using System.Collections;
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
            var phase1 = await Task.Run(() => DownloadZip(packageName), ct);
            if (!phase1.success)
                throw new InvalidOperationException(
                    $"Could not download \"{packageName}\".\n\nDetails: {phase1.error}\n\n" +
                    "Please install it manually via the Dynamo Package Manager " +
                    "(Packages → Search for a Package).");

            // Phase 2 — UI thread (back here after await): Extract zip + hot-load into Dynamo.
            // Must be on UI thread because PackageLoader fires WPF-owned events.
            var phase2 = ExtractAndLoad(phase1, packageName, targetPackagesDir);
            if (!phase2.success)
                throw new InvalidOperationException(
                    $"Downloaded \"{packageName}\" but could not load it.\n\nDetails: {phase2.error}\n\n" +
                    "The package files are on disk. Please restart Dynamo to complete loading, " +
                    "or install manually via the Package Manager.");

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
                // PackageDownloadHandle is in the same assembly as PackageManagerClient (DynamoPackages.dll)
                var handleType = phase1.pmClient!.GetType().Assembly
                    .GetType("Dynamo.PackageManager.PackageDownloadHandle");
                if (handleType == null)
                    return (false, "Type 'Dynamo.PackageManager.PackageDownloadHandle' not found in DynamoPackages");

                var handle = Activator.CreateInstance(handleType)
                    ?? throw new InvalidOperationException("Activator.CreateInstance returned null for PackageDownloadHandle");

                // Set identifying properties on the handle
                SetProp(handleType, handle, "Name",        packageName);
                SetProp(handleType, handle, "Id",          phase1.packageId);
                SetProp(handleType, handle, "VersionName", phase1.latestVersion);

                // Done(string filePath): sets DownloadPath = zipPath, DownloadState = Downloaded
                handleType.GetMethod("Done", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(handle, new object[] { phase1.zipPath });

                // Extract(DynamoModel dynamoModel, string installDirectory, out Package pkg)
                // installDirectory = targetDir — the base packages folder; Extract appends "/{packageName}" itself.
                // If null/empty, Extract falls back to DynamoModel.PathManager.DefaultPackagesDirectory.
                var extractMethod = handleType.GetMethod("Extract", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("PackageDownloadHandle.Extract() not found");

                var extractArgs = new object?[] { phase1.model, targetDir, null };
                var extracted   = extractMethod.Invoke(handle, extractArgs) as bool? ?? false;
                if (!extracted)
                    return (false, "PackageDownloadHandle.Extract returned false — " +
                                   "zip may be empty, corrupt, or missing a pkg.json manifest");

                var pkg = extractArgs[2]; // out Package param
                if (pkg == null)
                    return (false, "Extract returned true but the out Package parameter is null");

                if (phase1.packageLoader == null)
                    return (false, "PackageLoader not available — package extracted but not loaded");

                // Mirror what PackageLoader.LoadAll() does at line 325-334 before calling LoadPackages:
                // pathManager.AddResolutionPath(pkg.BinaryDirectory)
                // Without this the CLR cannot find inter-DLL dependencies inside the package's bin/
                // folder, causing a silent LibraryLoadFailedException inside TryLoadPackageIntoLibrary.
                var rootDir = pkg.GetType()
                    .GetProperty("RootDirectory", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(pkg) as string;

                if (!string.IsNullOrEmpty(rootDir))
                {
                    var binDir = Path.Combine(rootDir, "bin");
                    if (Directory.Exists(binDir))
                    {
                        var pathManager = phase1.model!.GetType()
                            .GetProperty("PathManager", BindingFlags.Public | BindingFlags.Instance)
                            ?.GetValue(phase1.model);

                        pathManager?.GetType()
                            .GetMethod("AddResolutionPath", BindingFlags.Public | BindingFlags.Instance)
                            ?.Invoke(pathManager, new object[] { binDir });
                    }
                }

                // PackageLoader.LoadPackages(IEnumerable<Package> packages)
                // This is the exact public method Dynamo's Package Manager calls after SetPackageState.
                // It calls TryLoadPackageIntoLibrary which fires RequestLoadNodeLibrary (ZeroTouch DLLs)
                // and RequestLoadCustomNodeDirectory (DYF custom nodes) — no restart required.
                var loadMethod = phase1.packageLoader.GetType()
                    .GetMethod("LoadPackages", BindingFlags.Public | BindingFlags.Instance);
                if (loadMethod == null)
                    return (false, "PackageLoader.LoadPackages() not found");

                // Build a strongly-typed Package[] so the IEnumerable<Package> parameter matches.
                // Array.CreateInstance produces e.g. Package[], which implements IEnumerable<Package>.
                var pkgArray = Array.CreateInstance(pkg.GetType(), 1);
                pkgArray.SetValue(pkg, 0);

                try
                {
                    loadMethod.Invoke(phase1.packageLoader, new object[] { pkgArray });
                }
                catch (Exception ex)
                {
                    // Package files are on disk; the load error is non-fatal (a Dynamo restart will finish loading).
                    return (false, "LoadPackages threw: " + Unwrap(ex));
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, "ExtractAndLoad threw: " + Unwrap(ex));
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────

        private static DownloadResult Fail(DownloadResult r, string error)
        {
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
