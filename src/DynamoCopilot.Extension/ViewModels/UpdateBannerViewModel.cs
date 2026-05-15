using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Extension.Commands;
using DynamoCopilot.Extension.Services;
using Microsoft.Data.Sqlite;

namespace DynamoCopilot.Extension.ViewModels
{
    // Singleton shared between CopilotPanelViewModel and SuggestNodesPanelViewModel.
    // Both panels bind to UpdateBannerViewModel.Instance so the banner state stays
    // in sync — Install clicked in one panel hides/updates the banner in both.
    public sealed class UpdateBannerViewModel : INotifyPropertyChanged
    {
        public static readonly UpdateBannerViewModel Instance = new UpdateBannerViewModel();

        private static int _startCount;

        private UpdateBannerViewModel()
        {
            InstallUpdateCommand   = new AsyncRelayCommand(DownloadDllsAsync,
                () => !_isDownloading && !_isDbDownloading && !_isDllDownloadReady && _isDllUpdateVisible);
            UpdateDatabaseCommand  = new AsyncRelayCommand(DownloadDbAsync,
                () => !_isDownloading && !_isDbDownloading && !_isDbDownloaded && _isDbUpdateVisible);
            DismissCommand         = new RelayCommand(Dismiss,
                () => IsBannerVisible && IsDismissible);
        }

        // ── DLL update state ──────────────────────────────────────────────────

        private bool   _isDllUpdateVisible;
        private bool   _isUpdateRequired;
        private bool   _isDownloading;
        private int    _downloadProgress;
        private bool   _isDllDownloadReady;
        private string _dllStatusMessage = "";
        private ReleaseManifest? _manifest;

        public bool IsDllUpdateVisible
        {
            get => _isDllUpdateVisible;
            private set { _isDllUpdateVisible = value; OnPropertyChanged(); Refresh(); }
        }

        public bool IsUpdateRequired
        {
            get => _isUpdateRequired;
            private set { _isUpdateRequired = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDismissible)); }
        }

        public bool IsDownloading
        {
            get => _isDownloading;
            private set { _isDownloading = value; OnPropertyChanged(); Refresh(); }
        }

        public int DownloadProgress
        {
            get => _downloadProgress;
            private set { _downloadProgress = value; OnPropertyChanged(); }
        }

        public bool IsDllDownloadReady
        {
            get => _isDllDownloadReady;
            private set { _isDllDownloadReady = value; OnPropertyChanged(); Refresh(); }
        }

        public string DllStatusMessage
        {
            get => _dllStatusMessage;
            private set { _dllStatusMessage = value; OnPropertyChanged(); }
        }

        // ── DB update state ───────────────────────────────────────────────────

        private bool   _isDbUpdateVisible;
        private bool   _isDbDownloading;
        private int    _dbDownloadProgress;
        private bool   _isDbDownloaded;
        private string _dbStatusMessage = "";

        public bool IsDbUpdateVisible
        {
            get => _isDbUpdateVisible;
            private set { _isDbUpdateVisible = value; OnPropertyChanged(); Refresh(); }
        }

        public bool IsDbDownloading
        {
            get => _isDbDownloading;
            private set { _isDbDownloading = value; OnPropertyChanged(); Refresh(); }
        }

        public int DbDownloadProgress
        {
            get => _dbDownloadProgress;
            private set { _dbDownloadProgress = value; OnPropertyChanged(); }
        }

        public bool IsDbDownloaded
        {
            get => _isDbDownloaded;
            private set { _isDbDownloaded = value; OnPropertyChanged(); Refresh(); }
        }

        public string DbStatusMessage
        {
            get => _dbStatusMessage;
            private set { _dbStatusMessage = value; OnPropertyChanged(); }
        }

        // ── Computed / combined ───────────────────────────────────────────────

        // The outer banner border is visible if either update is showing.
        public bool IsBannerVisible  => _isDllUpdateVisible || _isDbUpdateVisible;

        // Install button row: DLL update available, not downloading, not already staged.
        public bool ShowInstallButtons => _isDllUpdateVisible && !_isDownloading && !_isDllDownloadReady;

        // DB update button: db update available, not busy, not already done.
        public bool ShowDbUpdateButton => _isDbUpdateVisible && !_isDbDownloading && !_isDbDownloaded;

        // Any progress bar activity.
        public bool ShowProgressBar  => _isDownloading || _isDbDownloading;
        public int  AnyProgress      => _isDownloading ? _downloadProgress : _dbDownloadProgress;

        // Dismiss only allowed for non-required updates.
        public bool IsDismissible => !_isUpdateRequired;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand InstallUpdateCommand  { get; }
        public ICommand UpdateDatabaseCommand { get; }
        public ICommand DismissCommand        { get; }

        private void Refresh()
        {
            OnPropertyChanged(nameof(IsBannerVisible));
            OnPropertyChanged(nameof(ShowInstallButtons));
            OnPropertyChanged(nameof(ShowDbUpdateButton));
            OnPropertyChanged(nameof(ShowProgressBar));
            OnPropertyChanged(nameof(AnyProgress));
            ((AsyncRelayCommand)InstallUpdateCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)UpdateDatabaseCommand).RaiseCanExecuteChanged();
            ((RelayCommand)DismissCommand).RaiseCanExecuteChanged();
        }

        // ── Public API ────────────────────────────────────────────────────────

        public async Task StartAsync(string serverUrl)
        {
            if (Interlocked.Exchange(ref _startCount, 1) != 0) return;
            await CheckForUpdatesAsync(serverUrl);
        }

        // ── Version check ─────────────────────────────────────────────────────

        private static readonly Version FallbackVersion = new Version(1, 0, 0);

        private static Version GetInstalledVersion()
        {
            try { return Assembly.GetExecutingAssembly().GetName().Version ?? FallbackVersion; }
            catch { return FallbackVersion; }
        }

        private async Task CheckForUpdatesAsync(string serverUrl)
        {
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) })
                {
                    http.DefaultRequestHeaders.Add("User-Agent", "DynamoCopilot-Extension");

                    using var response = await http.GetAsync(
                        serverUrl.TrimEnd('/') + "/api/version/latest").ConfigureAwait(false);

                    // 404 = no release published yet → nothing to show, not an error
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;

                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json);
                    if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version)) return;

                    _manifest = manifest;

                    // ── DLL version check ─────────────────────────────────────────
                    if (manifest.Dlls != null && !string.IsNullOrWhiteSpace(manifest.Dlls.Url))
                    {
                        var installed = GetInstalledVersion();
                        if (Version.TryParse(manifest.Version, out var latest))
                        {
                            Version minimum = new Version(1, 0, 0);
                            Version.TryParse(manifest.MinVersion, out minimum);

                            bool required  = installed < minimum;
                            bool available = installed < latest;

                            if (available || required)
                            {
                                DispatchToUi(() =>
                                {
                                    IsUpdateRequired  = required;
                                    DllStatusMessage  = required
                                        ? "Update v" + manifest.Version + " required — install to continue"
                                        : "Update v" + manifest.Version + " is available";
                                    IsDllUpdateVisible = true;
                                });
                            }
                        }
                    }

                    // ── nodes.db version check ────────────────────────────────────
                    if (manifest.NodesDb != null &&
                        !string.IsNullOrWhiteSpace(manifest.NodesDb.Url) &&
                        !string.IsNullOrWhiteSpace(manifest.NodesDb.DbVersion))
                    {
                        bool dbOutdated = await Task.Run(() =>
                            IsDbOutdated(manifest.NodesDb.DbVersion)).ConfigureAwait(false);

                        if (dbOutdated)
                        {
                            DispatchToUi(() =>
                            {
                                DbStatusMessage    = "Node database update available (" + manifest.NodesDb.DbVersion + ")";
                                IsDbUpdateVisible  = true;
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                CopilotLogger.Log("UpdateBannerViewModel: version check failed", ex);
            }
        }

        // Reads last_built_at from the local nodes.db Metadata table and compares
        // it to the manifest's dbVersion string. Returns true if an update is needed.
        private static bool IsDbOutdated(string manifestDbVersion)
        {
            try
            {
                var dbPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DynamoCopilot", "nodes.db");

                if (!File.Exists(dbPath)) return false;

                string? localVersion = null;

                using (var conn = new SqliteConnection("Data Source=" + dbPath + ";Mode=ReadOnly"))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT value FROM Metadata WHERE key = 'last_built_at' LIMIT 1";
                        localVersion = cmd.ExecuteScalar() as string;
                    }
                }

                if (string.IsNullOrWhiteSpace(localVersion)) return false;

                // Compare as DateTimes (manifest is "2026-05-15", local is ISO datetime)
                if (!DateTime.TryParse(localVersion,    out var localDate))   return false;
                if (!DateTime.TryParse(manifestDbVersion, out var manifestDate)) return false;

                return localDate.Date < manifestDate.Date;
            }
            catch { return false; }
        }

        // ── DLL download + stage ──────────────────────────────────────────────

        private async Task DownloadDllsAsync()
        {
            if (_manifest?.Dlls == null) return;

            var appData    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var destBase   = Path.Combine(appData, "DynamoCopilot");
            var stagingDir = Path.Combine(destBase, "update");
            var zipPath    = Path.Combine(destBase, "update.zip");

            DispatchToUi(() => { IsDownloading = true; DownloadProgress = 0; DllStatusMessage = "Downloading update…"; });

            try
            {
                await DownloadFileAsync(_manifest.Dlls.Url, zipPath,
                    pct => DispatchToUi(() => { DownloadProgress = pct; DllStatusMessage = "Downloading update… " + pct + "%"; }))
                    .ConfigureAwait(false);

                DispatchToUi(() => DllStatusMessage = "Preparing update…");

                await Task.Run(() =>
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                    ZipFile.ExtractToDirectory(zipPath, stagingDir);
                    File.Delete(zipPath);
                }).ConfigureAwait(false);

                var updaterPath = Path.Combine(destBase, "DynamoCopilot.Updater.exe");
                var pid         = Process.GetCurrentProcess().Id;

                if (File.Exists(updaterPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = updaterPath,
                        Arguments       = "--apply-update " + pid,
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                        WindowStyle     = ProcessWindowStyle.Hidden
                    });
                }

                DispatchToUi(() =>
                {
                    IsDownloading      = false;
                    IsDllDownloadReady = true;
                    DownloadProgress   = 100;
                    DllStatusMessage   = "Update ready — restart Dynamo to apply";
                });
            }
            catch (Exception ex)
            {
                DispatchToUi(() => { IsDownloading = false; DllStatusMessage = "Download failed. Try again later."; });
                CopilotLogger.Log("UpdateBannerViewModel: DLL download failed", ex);
            }
        }

        // ── DB download (replaces live file directly — SQLite safe to overwrite) ──

        private async Task DownloadDbAsync()
        {
            if (_manifest?.NodesDb == null) return;

            var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var destBase = Path.Combine(appData, "DynamoCopilot");
            var tmpPath  = Path.Combine(destBase, "nodes.db.download");
            var livePath = Path.Combine(destBase, "nodes.db");

            DispatchToUi(() =>
            {
                IsDbDownloading    = true;
                DbDownloadProgress = 0;
                DbStatusMessage    = "Downloading node database…";
            });

            try
            {
                await DownloadFileAsync(_manifest.NodesDb.Url, tmpPath,
                    pct => DispatchToUi(() => { DbDownloadProgress = pct; DbStatusMessage = "Downloading node database… " + pct + "%"; }))
                    .ConfigureAwait(false);

                // Swap the file — the search service will pick up the new DB on the next query
                await Task.Run(() =>
                {
                    if (File.Exists(livePath)) File.Delete(livePath);
                    File.Move(tmpPath, livePath);
                }).ConfigureAwait(false);

                DispatchToUi(() =>
                {
                    IsDbDownloading    = false;
                    IsDbDownloaded     = true;
                    DbDownloadProgress = 100;
                    DbStatusMessage    = "Node database updated — new nodes available now";
                });
            }
            catch (Exception ex)
            {
                DispatchToUi(() =>
                {
                    IsDbDownloading = false;
                    DbStatusMessage = "Database download failed. Try again later.";
                    if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }
                });
                CopilotLogger.Log("UpdateBannerViewModel: DB download failed", ex);
            }
        }

        // ── Shared download helper ────────────────────────────────────────────

        private static async Task DownloadFileAsync(string url, string destPath, Action<int> reportProgress)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                http.DefaultRequestHeaders.Add("User-Agent", "DynamoCopilot-Extension");

                using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength ?? -1L;

                    using (var src = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = File.Create(destPath))
                    {
                        var buf        = new byte[81920];
                        long downloaded = 0;
                        int  read;

                        while ((read = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, read).ConfigureAwait(false);
                            downloaded += read;
                            if (total > 0)
                                reportProgress((int)(downloaded * 100L / total));
                        }
                    }
                }
            }
        }

        private void Dismiss()
        {
            IsDllUpdateVisible = false;
            IsDbUpdateVisible  = false;
        }

        private static void DispatchToUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
