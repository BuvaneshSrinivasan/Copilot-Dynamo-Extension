using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Extension.Commands;
using DynamoCopilot.Extension.Services;

namespace DynamoCopilot.Extension.ViewModels
{
    public sealed class NodeSuggestionCardViewModel : INotifyPropertyChanged, IDisposable
    {
        // ── Injected dependencies ─────────────────────────────────────────────

        private readonly PackageStateService    _packageState;
        private readonly DynamoPackageDownloader _downloader;
        private readonly Func<NodeSuggestion, bool> _insertAction;
        private readonly NodeSuggestion         _source;

        // ── Display state ─────────────────────────────────────────────────────

        private bool   _isExpanded;
        private bool   _isDownloading;
        private string _statusText = string.Empty;

        // ── Display properties ────────────────────────────────────────────────

        public string Name        { get; }
        public string Category    { get; }
        public string PackageName { get; }
        public string Description { get; }
        public string InputPorts  { get; }
        public string OutputPorts { get; }
        public string ScoreText   { get; }
        public string Reason      { get; }
        public string NodeType    { get; }

        // ── Visibility helpers ────────────────────────────────────────────────

        public bool HasReason      => !string.IsNullOrWhiteSpace(Reason);
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
        public bool HasPorts       => !string.IsNullOrWhiteSpace(InputPorts)
                                   || !string.IsNullOrWhiteSpace(OutputPorts);

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        // ── Install / download state ──────────────────────────────────────────

        /// <summary>True once the package is present in a Dynamo packages folder.</summary>
        public bool IsInstalled => _packageState.IsInstalled(PackageName);

        /// <summary>True while a download is in progress.</summary>
        public bool IsDownloading
        {
            get => _isDownloading;
            private set
            {
                _isDownloading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanInsert));
                ((AsyncRelayCommand)DownloadCommand).RaiseCanExecuteChanged();
                ((RelayCommand)InsertCommand).RaiseCanExecuteChanged();
            }
        }

        /// <summary>Download button is active when the package is not yet installed and no download is running.</summary>
        public bool CanDownload => !IsInstalled && !IsDownloading;

        /// <summary>Insert button is active when the package is installed and no download is running.</summary>
        public bool CanInsert => IsInstalled && !IsDownloading;

        /// <summary>Short status shown below the buttons (errors or progress).</summary>
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(_statusText);

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand DownloadCommand { get; }
        public ICommand InsertCommand   { get; }

        // ── Construction ──────────────────────────────────────────────────────

        public NodeSuggestionCardViewModel(
            NodeSuggestion           node,
            PackageStateService      packageState,
            DynamoPackageDownloader  downloader,
            Func<NodeSuggestion, bool> insertAction)
        {
            _source       = node         ?? throw new ArgumentNullException(nameof(node));
            _packageState = packageState ?? throw new ArgumentNullException(nameof(packageState));
            _downloader   = downloader   ?? throw new ArgumentNullException(nameof(downloader));
            _insertAction = insertAction ?? throw new ArgumentNullException(nameof(insertAction));

            Name        = node.Name;
            Category    = node.Category    ?? string.Empty;
            PackageName = node.PackageName ?? string.Empty;
            Description = node.Description ?? string.Empty;
            Reason      = node.Reason      ?? string.Empty;
            NodeType    = node.NodeType    ?? string.Empty;
            ScoreText   = node.Score > 0f  ? $"{node.Score:P0}" : string.Empty;

            InputPorts  = node.InputPorts  != null && node.InputPorts.Length  > 0
                ? string.Join(", ", node.InputPorts)  : string.Empty;
            OutputPorts = node.OutputPorts != null && node.OutputPorts.Length > 0
                ? string.Join(", ", node.OutputPorts) : string.Empty;

            DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanDownload);
            InsertCommand   = new RelayCommand(Insert, () => CanInsert);

            _packageState.Refreshed += OnPackageStateRefreshed;
        }

        // ── Command implementations ───────────────────────────────────────────

        private async Task DownloadAsync()
        {
            var targetDir = _packageState.CurrentVersionPackagesDir;
            if (string.IsNullOrEmpty(targetDir))
            {
                StatusText = "Cannot find Dynamo packages folder.";
                return;
            }

            IsDownloading = true;
            StatusText    = "Downloading\u2026";

            try
            {
                await _downloader.DownloadAsync(PackageName, targetDir);
                // Refresh fires Refreshed event → notifies all cards (including this one)
                _packageState.Refresh();

                StatusText = IsInstalled
                    ? "Downloaded. Nodes may be available now — or restart Dynamo if they don't appear."
                    : "Downloaded. Restart Dynamo to activate the package, then Insert.";
            }
            catch (Exception ex)
            {
                StatusText = $"Download failed: {ex.Message}";
            }
            finally
            {
                IsDownloading = false;
            }
        }

        private void Insert()
        {
            StatusText = string.Empty;
            var ok = _insertAction(_source);
            if (!ok)
                StatusText = "Insert failed. Make sure Dynamo has loaded the package.";
        }

        // ── PackageStateService.Refreshed handler ─────────────────────────────

        private void OnPackageStateRefreshed()
        {
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(CanDownload));
            OnPropertyChanged(nameof(CanInsert));
            ((AsyncRelayCommand)DownloadCommand).RaiseCanExecuteChanged();
            ((RelayCommand)InsertCommand).RaiseCanExecuteChanged();
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _packageState.Refreshed -= OnPackageStateRefreshed;
        }

        // ── INotifyPropertyChanged ────────────────────────────────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
