using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dynamo.Wpf.Extensions;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.Services;

namespace DynamoCopilot.Extension.ViewModels
{
    public sealed class SuggestNodesPanelViewModel : INotifyPropertyChanged
    {
        private readonly AuthService             _authService;
        private readonly LocalNodeSearchService  _localSearchService;
        private readonly ViewLoadedParams        _dynParams;
        private readonly PackageStateService     _packageState;
        private readonly DynamoPackageDownloader _downloader;

        // ── Auth state ────────────────────────────────────────────────────────

        private bool   _isLoggedIn;
        private bool   _isRegisterMode;
        private bool   _isAuthBusy;
        private string _authError     = string.Empty;
        private string _loginEmail    = string.Empty;
        private string _registerEmail = string.Empty;

        // ── User info ─────────────────────────────────────────────────────────

        private bool      _showUserPanel;
        private string    _userEmail       = string.Empty;
        private bool      _isLicenceActive = true;
        private int       _tokensUsed;
        private DateTime? _licenseEndDate;

        // ── Node suggest state ────────────────────────────────────────────────

        private string _nodeQuery       = string.Empty;
        private bool   _isSearchingNodes;
        private string _statusMessage   = string.Empty;

        // ── Collections ───────────────────────────────────────────────────────

        public ObservableCollection<NodeSuggestionCardViewModel> NodeSuggestions { get; }
            = new ObservableCollection<NodeSuggestionCardViewModel>();

        // ── Auth bindings ─────────────────────────────────────────────────────

        public bool IsLoggedIn
        {
            get => _isLoggedIn;
            private set { _isLoggedIn = value; OnPropertyChanged(); }
        }

        public bool IsRegisterMode
        {
            get => _isRegisterMode;
            set { _isRegisterMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLoginMode)); AuthError = string.Empty; }
        }

        public bool IsLoginMode => !_isRegisterMode;

        public bool IsAuthBusy
        {
            get => _isAuthBusy;
            private set { _isAuthBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsAuthIdle)); }
        }

        public bool IsAuthIdle => !_isAuthBusy;

        public string AuthError
        {
            get => _authError;
            private set { _authError = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasAuthError)); }
        }

        public bool HasAuthError => !string.IsNullOrWhiteSpace(_authError);

        public string LoginEmail
        {
            get => _loginEmail;
            set { _loginEmail = value; OnPropertyChanged(); }
        }

        public string RegisterEmail
        {
            get => _registerEmail;
            set { _registerEmail = value; OnPropertyChanged(); }
        }

        // ── User info bindings ────────────────────────────────────────────────

        public bool ShowUserPanel
        {
            get => _showUserPanel;
            private set { _showUserPanel = value; OnPropertyChanged(); }
        }

        public void ToggleUserPanel()
        {
            ShowUserPanel = !ShowUserPanel;
            if (ShowUserPanel)
                _ = RefreshUserInfoAsync();
        }

        public string UserEmail
        {
            get => _userEmail;
            private set { _userEmail = value; OnPropertyChanged(); }
        }

        public bool IsLicenceActive
        {
            get => _isLicenceActive;
            private set { _isLicenceActive = value; OnPropertyChanged(); }
        }

        public int TokensUsed
        {
            get => _tokensUsed;
            private set { _tokensUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokensDisplay)); }
        }

        public string TokensDisplay => $"{_tokensUsed:N0}";

        public DateTime? LicenseEndDate
        {
            get => _licenseEndDate;
            private set
            {
                _licenseEndDate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LicenseExpiryDisplay));
                OnPropertyChanged(nameof(IsLicenseExpired));
            }
        }

        public string LicenseExpiryDisplay => _licenseEndDate.HasValue
            ? _licenseEndDate.Value.ToLocalTime().ToString("d MMM yyyy")
            : "—";

        public bool IsLicenseExpired => _licenseEndDate.HasValue && _licenseEndDate.Value < DateTime.UtcNow;

        // ── Node suggest bindings ─────────────────────────────────────────────

        public string NodeQuery
        {
            get => _nodeQuery;
            set { _nodeQuery = value; OnPropertyChanged(); }
        }

        public bool IsSearchingNodes
        {
            get => _isSearchingNodes;
            private set
            {
                _isSearchingNodes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSearchNodes));
                OnPropertyChanged(nameof(ShowNodeHint));
            }
        }

        public bool CanSearchNodes => !_isSearchingNodes;

        public bool ShowNodeHint => !_isSearchingNodes && NodeSuggestions.Count == 0;

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

        // ── Construction ──────────────────────────────────────────────────────

        public SuggestNodesPanelViewModel(
            AuthService              authService,
            LocalNodeSearchService   localSearchService,
            ViewLoadedParams         dynParams,
            PackageStateService      packageState,
            DynamoPackageDownloader  downloader)
        {
            _authService        = authService        ?? throw new ArgumentNullException(nameof(authService));
            _localSearchService = localSearchService ?? throw new ArgumentNullException(nameof(localSearchService));
            _dynParams          = dynParams          ?? throw new ArgumentNullException(nameof(dynParams));
            _packageState       = packageState       ?? throw new ArgumentNullException(nameof(packageState));
            _downloader         = downloader         ?? throw new ArgumentNullException(nameof(downloader));

            AuthService.GlobalLoggedIn  += OnGlobalLoggedIn;
            AuthService.GlobalLoggedOut += OnGlobalLoggedOut;
        }

        // ── Startup auth check ────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            if (!_authService.TryLoadTokens())
                return;

            IsAuthBusy = true;
            var token  = await _authService.GetValidTokenAsync();
            IsAuthBusy = false;

            if (string.IsNullOrEmpty(token))
                return;

            OnAuthSuccess();
        }

        // ── Auth actions ──────────────────────────────────────────────────────

        public async Task LoginAsync(string password)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(LoginEmail))  { AuthError = "Please enter your email.";    return; }
            if (string.IsNullOrWhiteSpace(password))    { AuthError = "Please enter your password."; return; }

            IsAuthBusy = true;
            AuthError  = string.Empty;
            var result = await _authService.LoginAsync(LoginEmail.Trim(), password);
            IsAuthBusy = false;

            if (!result.Success) { AuthError = result.ErrorMessage ?? "Login failed."; return; }
            OnAuthSuccess();
        }

        public async Task RegisterAsync(string password, string confirmPassword)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(RegisterEmail)) { AuthError = "Please enter your email.";                          return; }
            if (string.IsNullOrWhiteSpace(password))      { AuthError = "Please enter a password.";                          return; }
            if (password.Length < 8)                      { AuthError = "Password must be at least 8 characters.";           return; }
            if (password != confirmPassword)              { AuthError = "Passwords do not match.";                           return; }

            IsAuthBusy = true;
            AuthError  = string.Empty;
            var result = await _authService.RegisterAsync(RegisterEmail.Trim(), password);
            IsAuthBusy = false;

            if (!result.Success) { AuthError = result.ErrorMessage ?? "Registration failed."; return; }
            OnAuthSuccess();
        }

        public void Logout()
        {
            ClearAuthState();       // mark as logged out first so the global event guard skips this VM
            _authService.Logout();  // deletes tokens.json, fires GlobalLoggedOut → other VM clears
        }

        private void ClearAuthState()
        {
            foreach (var card in NodeSuggestions) card.Dispose();
            NodeSuggestions.Clear();
            ShowUserPanel  = false;
            IsLoggedIn     = false;
            IsRegisterMode = false;
            AuthError      = string.Empty;
            LoginEmail     = string.Empty;
            RegisterEmail  = string.Empty;
            NodeQuery      = string.Empty;
            StatusMessage  = string.Empty;
        }

        private void OnGlobalLoggedOut()
        {
            if (!IsLoggedIn) return;  // we initiated this logout — already cleared
            DispatchToUi(ClearAuthState);
        }

        private void OnGlobalLoggedIn(string _)
        {
            if (IsLoggedIn) return;  // we initiated this login — already set
            DispatchToUi(OnAuthSuccess);
        }

        private static void DispatchToUi(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null) { action(); return; }
            if (app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.InvokeAsync(action);
        }

        // ── User info ─────────────────────────────────────────────────────────

        private async Task RefreshUserInfoAsync()
        {
            UserEmail = _authService.Email;
            var info  = await _authService.GetUserInfoAsync();
            if (info == null) return;

            UserEmail       = info.Email;
            if (info.DailyTokenCount > 0 || TokensUsed == 0)
                TokensUsed  = info.DailyTokenCount;
            IsLicenceActive = info.IsActive && !info.LicenseExpired;
            LicenseEndDate  = info.LicenseEndDate;
        }

        // ── Node search ───────────────────────────────────────────────────────

        public async Task SearchNodesAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _isSearchingNodes) return;

            await RefreshUserInfoAsync();
            if (IsLicenseExpired)
            {
                StatusMessage = "Your licence has expired. Please contact support to renew.";
                return;
            }

            IsSearchingNodes = true;
            foreach (var card in NodeSuggestions) card.Dispose();
            NodeSuggestions.Clear();
            OnPropertyChanged(nameof(ShowNodeHint));
            ShowStatus("Searching nodes…");

            try
            {
                var results = await _localSearchService.SearchAsync(query);

                foreach (var node in results)
                    NodeSuggestions.Add(new NodeSuggestionCardViewModel(
                        node, _packageState, _downloader, InsertNodeToCanvas));

                StatusMessage = results.Count == 0
                    ? "No matching nodes found."
                    : $"Found {results.Count} node{(results.Count == 1 ? "" : "s")}.";
            }
            catch (Exception ex)
            {
                ShowStatus($"Search failed: {ex.Message}");
            }
            finally
            {
                IsSearchingNodes = false;
                OnPropertyChanged(nameof(ShowNodeHint));
            }
        }

        // ── Node insertion ────────────────────────────────────────────────────

        private bool InsertNodeToCanvas(NodeSuggestion node)
        {
            try
            {
                var model = GetDynamoModel();
                if (model == null) return false;
                CopilotLogger.Log($"[Insert] model runtime type = {model.GetType().FullName}");

                var packageFolderPath = _packageState.GetPackageFolderPath(node.PackageName);
                var (cx, cy) = GetCanvasCenter();

                var ok = DynamoCopilot.GraphInterop.GraphNodeInserter.InsertNode(
                    model, node.Name, node.PackageName, node.NodeType,
                    packageFolderPath, cx, cy, log: CopilotLogger.Log);

                if (ok) ShowStatus($"Inserted \"{node.Name}\" onto the canvas.");
                return ok;
            }
            catch (Exception ex)
            {
                ShowStatus($"Insert failed: {ex.Message}");
                return false;
            }
        }

        private (double x, double y) GetCanvasCenter()
        {
            try
            {
                var wsVm = GetCurrentWorkspaceViewModel();
                if (wsVm == null) return (0, 0);

                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;

                double panX = 0, panY = 0, zoom = 1.0;
                try { panX = Convert.ToDouble(wsVm.GetType().GetProperty("X",    flags)?.GetValue(wsVm) ?? 0.0); } catch { }
                try { panY = Convert.ToDouble(wsVm.GetType().GetProperty("Y",    flags)?.GetValue(wsVm) ?? 0.0); } catch { }
                try { zoom = Convert.ToDouble(wsVm.GetType().GetProperty("Zoom", flags)?.GetValue(wsVm) ?? 1.0); } catch { }
                if (zoom <= 0) zoom = 1.0;

                double viewW = 1000, viewH = 600;
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app != null)
                    {
                        foreach (System.Windows.Window win in app.Windows)
                        {
                            var candidate = FindDescendantByTypeName(win, "WorkspaceView");
                            if (candidate is System.Windows.FrameworkElement fe &&
                                fe.ActualWidth > 0 && fe.ActualHeight > 0)
                            {
                                viewW = fe.ActualWidth;
                                viewH = fe.ActualHeight;
                                break;
                            }
                        }
                    }
                }
                catch { }

                return ((viewW / 2.0 - panX) / zoom, (viewH / 2.0 - panY) / zoom);
            }
            catch { return (0, 0); }
        }

        private static System.Windows.DependencyObject? FindDescendantByTypeName(
            System.Windows.DependencyObject parent, string typeName)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child?.GetType().Name == typeName) return child;
                var result = FindDescendantByTypeName(child!, typeName);
                if (result != null) return result;
            }
            return null;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        public void Shutdown()
        {
            AuthService.GlobalLoggedIn  -= OnGlobalLoggedIn;
            AuthService.GlobalLoggedOut -= OnGlobalLoggedOut;
        }

        private void OnAuthSuccess()
        {
            UserEmail  = _authService.Email;
            IsLoggedIn = true;
            _ = RefreshUserInfoAsync();
        }

        private void ShowStatus(string msg)
        {
            StatusMessage = msg;
            var timer = new System.Timers.Timer(4000) { AutoReset = false };
            timer.Elapsed += (_, _) => { StatusMessage = string.Empty; timer.Dispose(); };
            timer.Start();
        }

        private object? GetDynamoViewModel()
        {
            var field = _dynParams.GetType().GetField("dynamoViewModel",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(_dynParams);
        }

        private object? GetDynamoModel()
        {
            var dvm = GetDynamoViewModel();
            return dvm?.GetType()
                .GetProperty("Model", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(dvm);
        }

        private object? GetCurrentWorkspaceViewModel()
        {
            var dvm = GetDynamoViewModel();
            return dvm?.GetType()
                .GetProperty("CurrentSpaceViewModel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(dvm);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
