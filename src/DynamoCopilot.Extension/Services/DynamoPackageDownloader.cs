using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace DynamoCopilot.Extension.Services
{
    /// <summary>
    /// Downloads and hot-loads a Dynamo package using the same internal call chain
    /// that Dynamo's own Package Manager UI uses (verified by DLL inspection,
    /// Revit 2025 / DynamoCore 3.3.0.6316).
    ///
    /// Verified call chain (mirrors PackageManagerClientViewModel.InstallPackage):
    ///   1. PackageManagerClient.DownloadPackage(id, version, out zipPath)
    ///      → HTTP download → zip on disk
    ///   2. PackageDownloadHandle.Done(zipPath)
    ///      → sets DownloadState = Done
    ///   3. PackageDownloadHandle.Extract(DynamoModel, installDir, out Package)
    ///      → unzips to installDir, builds Package object
    ///   4. PackageLoader.LoadNewCustomNodesAndPackages(IEnumerable&lt;string&gt; newPaths,
    ///                                                  CustomNodeManager customNodeManager)
    ///      → registers ZeroTouch DLLs (via RequestLoadNodeLibrary) AND custom nodes
    ///         (via RequestLoadCustomNodeDirectory) — the exact method Dynamo calls
    ///         post-install so nodes appear without restart.
    ///
    /// Access path to the required objects:
    ///   DynamoViewModel.Model                      → DynamoModel
    ///   DynamoModel.ExtensionManager.Extensions    → find PackageManagerExtension
    ///   PackageManagerExtension.PackageManagerClient
    ///   PackageManagerExtension.PackageLoader
    ///   DynamoModel.CustomNodeManager
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
                    "Please install it manually via the Dynamo Package Manager (Packages → Search for a Package).");

            // Phase 1 (background thread): network I/O — ListAll + DownloadPackage → zip file.
            // Extract and load must run on the UI thread (WPF-owned objects).
            var phase1 = await Task.Run(() => DownloadZip(packageName, targetPackagesDir), ct);
            if (!phase1.success)
                throw new InvalidOperationException(
                    $"Could not download \"{packageName}\".\nDetails: {phase1.error}\n\n" +
                    "Please install it manually via the Dynamo Package Manager (Packages → Search for a Package).");

            // Phase 2 (UI thread — back here after await): Extract zip + hot-load into Dynamo.
            var phase2 = ExtractAndLoad(phase1, packageName, targetPackagesDir);
            if (!phase2.success)
                throw new InvalidOperationException(
                    $"Could not download \"{packageName}\".\nDetails: {phase2.error}\n\n" +
                    "Please install it manually via the Dynamo Package Manager (Packages → Search for a Package).");

            return targetPackagesDir;
        }

        // ── Data carrier between the two phases ───────────────────────────────
        private sealed class DownloadZipResult
        {
            public bool    success;
            public string  error         = string.Empty;
            public string  zipPath       = string.Empty;
            public string  packageId     = string.Empty;
            public string  latestVersion = string.Empty;
            public object? model;
            public object? pmClient;
            public object? packageLoader;
        }

        // ── Phase 1: runs on background thread ────────────────────────────────
        // Network I/O only: ListAll → find package → DownloadPackage → zip on disk.
        private DownloadZipResult DownloadZip(string packageName, string targetDir)
        {
            var r = new DownloadZipResult();

            // DynamoViewModel → DynamoModel
            r.model = _dynamoViewModel!.GetType()
                .GetProperty("Model", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(_dynamoViewModel);
            if (r.model == null) { r.error = "DynamoViewModel.Model not found"; return r; }

            // DynamoModel → ExtensionManager
            var extMgr = r.model.GetType()
                .GetProperty("ExtensionManager", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(r.model);
            if (extMgr == null) { r.error = "DynamoModel.ExtensionManager not found"; return r; }

            // ExtensionManager.Extensions → find PackageManagerExtension
            var extensions = extMgr.GetType()
                .GetProperty("Extensions", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(extMgr) as IEnumerable;
            if (extensions == null) { r.error = "ExtensionManager.Extensions not IEnumerable"; return r; }

            foreach (var ext in extensions)
            {
                if (ext == null) continue;
                var t  = ext.GetType();
                var cp = t.GetProperty("PackageManagerClient", BindingFlags.Public | BindingFlags.Instance);
                if (cp == null) continue;
                r.pmClient      = cp.GetValue(ext);
                r.packageLoader = t.GetProperty("PackageLoader", BindingFlags.Public | BindingFlags.Instance)
                                   ?.GetValue(ext);
                break;
            }
            if (r.pmClient == null) { r.error = "PackageManagerExtension not found in Extensions"; return r; }

            var pmClientType = r.pmClient.GetType();

            // PackageManagerClient.ListAll() → find package by name
            var listAllMethod = pmClientType.GetMethod("ListAll", BindingFlags.NonPublic | BindingFlags.Instance);
            if (listAllMethod == null) { r.error = "PackageManagerClient.ListAll() not found"; return r; }

            IEnumerable? allPackages;
            try { allPackages = listAllMethod.Invoke(r.pmClient, null) as IEnumerable; }
            catch (Exception ex) { r.error = "ListAll() threw: " + (ex.InnerException?.Message ?? ex.Message); return r; }
            if (allPackages == null) { r.error = "ListAll() returned null"; return r; }

            object? matchedHeader = null;
            foreach (var header in allPackages)
            {
                if (header == null) continue;
                var n = header.GetType()
                    .GetProperty("name", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(header) as string;
                if (n?.Equals(packageName, StringComparison.OrdinalIgnoreCase) == true)
                { matchedHeader = header; break; }
            }
            if (matchedHeader == null) { r.error = $"Package \"{packageName}\" not found in ListAll()"; return r; }

            r.packageId = matchedHeader.GetType()
                .GetProperty("_id", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(matchedHeader) as string ?? string.Empty;
            if (string.IsNullOrEmpty(r.packageId)) { r.error = "PackageHeader._id is empty"; return r; }

            // Pick the numerically highest version string.
            // The API may return versions in any order; parse and compare explicitly.
            var versionsList = matchedHeader.GetType()
                .GetProperty("versions", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(matchedHeader) as IEnumerable;
            if (versionsList != null)
            {
                var best = new Version(0, 0, 0, 0);
                foreach (var v in versionsList)
                {
                    var vStr = v?.GetType()
                        .GetProperty("version", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(v) as string;
                    if (string.IsNullOrEmpty(vStr)) continue;
                    if (Version.TryParse(vStr, out var parsed) && parsed > best)
                    {
                        best = parsed;
                        r.latestVersion = vStr!;
                    }
                }
            }
            if (string.IsNullOrEmpty(r.latestVersion)) { r.error = "Could not determine latest version"; return r; }

            // PackageManagerClient.DownloadPackage(id, version, out zipPath)
            var dlMethod = pmClientType.GetMethod("DownloadPackage", BindingFlags.NonPublic | BindingFlags.Instance);
            if (dlMethod == null) { r.error = "PackageManagerClient.DownloadPackage() not found"; return r; }

            var dlArgs = new object?[] { r.packageId, r.latestVersion, null };
            object? dlResult;
            try { dlResult = dlMethod.Invoke(r.pmClient, dlArgs); }
            catch (Exception ex) { r.error = "DownloadPackage() threw: " + (ex.InnerException?.Message ?? ex.Message); return r; }

            var dlSuccess = dlResult?.GetType()
                .GetProperty("Success", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(dlResult) as bool? ?? false;
            if (!dlSuccess)
            {
                var dlErr = dlResult?.GetType()
                    .GetProperty("Error", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(dlResult) as string ?? "unknown";
                r.error = "DownloadPackage failed: " + dlErr;
                return r;
            }

            r.zipPath = dlArgs[2] as string ?? string.Empty;
            if (string.IsNullOrEmpty(r.zipPath) || !File.Exists(r.zipPath))
            { r.error = $"Zip path invalid after download: \"{r.zipPath}\""; return r; }

            r.success = true;
            return r;
        }

        // ── Phase 2: must run on the UI thread ────────────────────────────────
        // Mirrors PackageManagerClientViewModel.InstallPackage exactly:
        //   PackageDownloadHandle.Done(zipPath)
        //   PackageDownloadHandle.Extract(model, installDir, out pkg)
        //   PackageLoader.LoadNewCustomNodesAndPackages(new[]{ pkg.RootDirectory }, customNodeManager)
        private (bool success, string error) ExtractAndLoad(
            DownloadZipResult phase1, string packageName, string targetDir)
        {
            try
            {
                var handleType = phase1.pmClient!.GetType().Assembly
                    .GetType("Dynamo.PackageManager.PackageDownloadHandle");
                if (handleType == null)
                    return (false, "PackageDownloadHandle type not found");

                var handle = Activator.CreateInstance(handleType)
                    ?? throw new InvalidOperationException("Could not instantiate PackageDownloadHandle");

                handleType.GetProperty("Name",        BindingFlags.Public | BindingFlags.Instance)?.SetValue(handle, packageName);
                handleType.GetProperty("Id",          BindingFlags.Public | BindingFlags.Instance)?.SetValue(handle, phase1.packageId);
                handleType.GetProperty("VersionName", BindingFlags.Public | BindingFlags.Instance)?.SetValue(handle, phase1.latestVersion);

                handleType.GetMethod("Done", BindingFlags.Public | BindingFlags.Instance)
                    ?.Invoke(handle, new object[] { phase1.zipPath });

                var extractMethod = handleType.GetMethod("Extract", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("PackageDownloadHandle.Extract not found");

                var extractArgs = new object?[] { phase1.model, targetDir, null };
                var extracted   = extractMethod.Invoke(handle, extractArgs) as bool? ?? false;
                if (!extracted)
                    return (false, "PackageDownloadHandle.Extract returned false");

                var pkg = extractArgs[2];
                if (pkg == null)
                    return (false, "Extract succeeded but Package out-param is null");

                if (phase1.packageLoader != null)
                {
                    var rootDir = pkg.GetType()
                        .GetProperty("RootDirectory", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(pkg) as string;

                    var customNodeManager = phase1.model!.GetType()
                        .GetProperty("CustomNodeManager", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(phase1.model);

                    if (!string.IsNullOrEmpty(rootDir) && customNodeManager != null)
                    {
                        // This is the exact method Dynamo's own Package Manager calls after install —
                        // handles both ZeroTouch DLLs and DYF custom nodes, no restart required.
                        var loadMethod = phase1.packageLoader.GetType()
                            .GetMethod("LoadNewCustomNodesAndPackages",
                                BindingFlags.NonPublic | BindingFlags.Instance);

                        if (loadMethod != null)
                        {
                            var newPaths = new List<string> { rootDir };
                            try { loadMethod.Invoke(phase1.packageLoader, new object[] { newPaths, customNodeManager }); }
                            catch { }
                        }
                    }
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, "ExtractAndLoad threw: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        public void Dispose() { }
    }
}
