using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dynamo.Wpf.Extensions;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Services.Providers;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.Services;
using DynamoCopilot.GraphInterop;

namespace DynamoCopilot.Extension.ViewModels
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Panel mode enum
    // ─────────────────────────────────────────────────────────────────────────────

    public enum PanelMode { Chat, NodeSuggest }

    // ─────────────────────────────────────────────────────────────────────────────
    // Per-message view model (unchanged)
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class ChatMessageViewModel : INotifyPropertyChanged
    {
        private string _content = string.Empty;
        private bool   _isStreaming;
        private string? _codeSnippet;

        public ChatRole Role { get; set; }

        public string Content
        {
            get => _content;
            set { _content = value; OnPropertyChanged(); }
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(); }
        }

        public string? CodeSnippet
        {
            get => _codeSnippet;
            set { _codeSnippet = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasCode)); }
        }

        public bool HasCode  => !string.IsNullOrWhiteSpace(_codeSnippet);
        public bool IsUser   => Role == ChatRole.User;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Panel ViewModel
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class CopilotPanelViewModel : INotifyPropertyChanged
    {
        private readonly DynamoCopilotSettings   _settings;
        private readonly AuthService             _authService;
        private          ILlmService             _llmService;
        private readonly LocalNodeSearchService  _localSearchService;
        private readonly ChatHistoryService      _historyService;
        private readonly ViewLoadedParams        _dynParams;
        private readonly PackageStateService     _packageState;
        private readonly DynamoPackageDownloader _downloader;

        private ChatSession _currentSession;
        private CancellationTokenSource? _streamingCts;

        // ── Settings panel ────────────────────────────────────────────────────────

        private bool _showSettings;

        public SettingsPanelViewModel SettingsVm { get; }

        public bool ShowSettings
        {
            get => _showSettings;
            private set { _showSettings = value; OnPropertyChanged(); }
        }

        public void ToggleSettings()
        {
            ShowSettings = !ShowSettings;
            if (ShowSettings)
                _ = RefreshUserInfoAsync();
        }

        // ── Auth state ────────────────────────────────────────────────────────────

        private bool   _isLoggedIn;
        private bool   _isRegisterMode;
        private bool   _isAuthBusy;
        private string _authError = string.Empty;

        // Login form fields (password handled in code-behind via PasswordBox)
        private string _loginEmail    = string.Empty;
        private string _registerEmail = string.Empty;

        // ── User info ─────────────────────────────────────────────────────────────

        private bool   _showUserInfo;
        private string _userEmail       = string.Empty;
        private bool   _isLicenceActive = true;
        private int    _requestsUsed;
        private int    _requestLimit    = 30;
        private int    _tokensUsed;
        private int    _tokenLimit      = 40000;

        // ── Panel mode ────────────────────────────────────────────────────────────

        private PanelMode _mode = PanelMode.Chat;

        // ── Chat state ────────────────────────────────────────────────────────────

        private bool   _isStreaming;
        private string _statusMessage = string.Empty;
        private bool   _showWelcome   = true;

        // ── Node suggest state ────────────────────────────────────────────────────

        private string _nodeQuery       = string.Empty;
        private bool   _isSearchingNodes;

        public Action? RequestScrollToBottom { get; set; }

        // ── Bindable collections ─────────────────────────────────────────────────

        public ObservableCollection<ChatMessageViewModel>    Messages        { get; }
            = new ObservableCollection<ChatMessageViewModel>();

        public ObservableCollection<NodeSuggestionCardViewModel> NodeSuggestions { get; }
            = new ObservableCollection<NodeSuggestionCardViewModel>();

        // ── Mode bindings ─────────────────────────────────────────────────────────

        public PanelMode CurrentMode
        {
            get => _mode;
            private set
            {
                _mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsChatMode));
                OnPropertyChanged(nameof(IsNodeMode));
            }
        }

        public bool IsChatMode => _mode == PanelMode.Chat;
        public bool IsNodeMode => _mode == PanelMode.NodeSuggest;

        public void SwitchToChat()        => CurrentMode = PanelMode.Chat;
        public void SwitchToNodeSuggest() => CurrentMode = PanelMode.NodeSuggest;

        // ── Auth bindings ─────────────────────────────────────────────────────────

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

        // ── User info bindings ────────────────────────────────────────────────────

        public bool ShowUserInfo
        {
            get => _showUserInfo;
            private set { _showUserInfo = value; OnPropertyChanged(); }
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

        public int RequestsUsed
        {
            get => _requestsUsed;
            private set { _requestsUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(RequestsDisplay)); }
        }

        public int RequestLimit
        {
            get => _requestLimit;
            private set { _requestLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(RequestsDisplay)); }
        }

        public string RequestsDisplay => $"{_requestsUsed} / {_requestLimit}";

        public int TokensUsed
        {
            get => _tokensUsed;
            private set { _tokensUsed = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokensDisplay)); }
        }

        public int TokenLimit
        {
            get => _tokenLimit;
            private set { _tokenLimit = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokensDisplay)); }
        }

        public string TokensDisplay => $"{_tokensUsed:N0} / {_tokenLimit:N0}";

        // ── Chat bindings ─────────────────────────────────────────────────────────

        public bool IsStreaming
        {
            get => _isStreaming;
            private set { _isStreaming = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotStreaming)); }
        }

        public bool IsNotStreaming => !_isStreaming;

        public string StatusMessage
        {
            get => _statusMessage;
            private set { _statusMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
        }

        public bool HasStatus => !string.IsNullOrWhiteSpace(_statusMessage);

        public bool ShowWelcome
        {
            get => _showWelcome;
            private set { _showWelcome = value; OnPropertyChanged(); }
        }

        // ── Node suggest bindings ─────────────────────────────────────────────────

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

        /// <summary>
        /// True when the search input area should show its placeholder hint
        /// (not searching and no results yet).
        /// </summary>
        public bool ShowNodeHint => !_isSearchingNodes && NodeSuggestions.Count == 0;

        // ── Construction ──────────────────────────────────────────────────────────

        public CopilotPanelViewModel(
            DynamoCopilotSettings    settings,
            AuthService              authService,
            LocalNodeSearchService   localSearchService,
            ChatHistoryService       historyService,
            ViewLoadedParams         dynParams,
            PackageStateService      packageState,
            DynamoPackageDownloader  downloader)
        {
            _settings           = settings;
            _authService        = authService;
            _localSearchService = localSearchService;
            _historyService     = historyService;
            _dynParams          = dynParams;
            _packageState       = packageState;
            _downloader         = downloader;

            _llmService = LlmServiceFactory.Create(settings);

            SettingsVm = new SettingsPanelViewModel(settings);
            SettingsVm.SettingsSaved += OnSettingsSaved;

            _currentSession = _historyService.Load(GetCurrentGraphPath());
            _dynParams.CurrentWorkspaceChanged += OnWorkspaceChanged;
        }

        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            // Rebuild the LLM service whenever the user saves new provider settings
            (_llmService as IDisposable)?.Dispose();
            _llmService = LlmServiceFactory.Create(_settings);
        }

        // ── Startup auth check ────────────────────────────────────────────────────

        /// <summary>
        /// Called once from DynamoCopilotViewExtension.Loaded().
        /// Checks stored tokens → if valid, transitions straight to chat.
        /// </summary>
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

        // ── Auth actions (called by View code-behind) ─────────────────────────────

        public async Task LoginAsync(string password)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(LoginEmail))
            {
                AuthError = "Please enter your email.";
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                AuthError = "Please enter your password.";
                return;
            }

            IsAuthBusy = true;
            AuthError  = string.Empty;

            var result = await _authService.LoginAsync(LoginEmail.Trim(), password);

            IsAuthBusy = false;

            if (!result.Success)
            {
                AuthError = result.ErrorMessage ?? "Login failed.";
                return;
            }

            OnAuthSuccess();
        }

        public async Task RegisterAsync(string password, string confirmPassword)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(RegisterEmail))
            {
                AuthError = "Please enter your email.";
                return;
            }
            if (string.IsNullOrWhiteSpace(password))
            {
                AuthError = "Please enter a password.";
                return;
            }
            if (password.Length < 8)
            {
                AuthError = "Password must be at least 8 characters.";
                return;
            }
            if (password != confirmPassword)
            {
                AuthError = "Passwords do not match.";
                return;
            }

            IsAuthBusy = true;
            AuthError  = string.Empty;

            var result = await _authService.RegisterAsync(RegisterEmail.Trim(), password);

            IsAuthBusy = false;

            if (!result.Success)
            {
                AuthError = result.ErrorMessage ?? "Registration failed.";
                return;
            }

            OnAuthSuccess();
        }

        public void Logout()
        {
            _authService.Logout();

            Messages.Clear();
            NodeSuggestions.Clear();
            ShowWelcome      = true;
            StatusMessage    = string.Empty;
            ShowUserInfo     = false;
            IsLoggedIn       = false;
            IsRegisterMode   = false;
            AuthError        = string.Empty;
            LoginEmail       = string.Empty;
            RegisterEmail    = string.Empty;
            NodeQuery        = string.Empty;
            CurrentMode      = PanelMode.Chat;

            _streamingCts?.Cancel();
        }

        // ── User info (integrated into settings panel) ────────────────────────────

        public void ToggleUserInfo() => ToggleSettings();

        private async Task RefreshUserInfoAsync()
        {
            UserEmail = _authService.Email;

            var info = await _authService.GetUserInfoAsync();
            if (info == null) return;

            UserEmail    = info.Email;
            RequestsUsed = info.DailyRequestCount;
            RequestLimit = info.EffectiveRequestLimit;
            TokensUsed   = info.DailyTokenCount;
            TokenLimit   = info.EffectiveTokenLimit;
        }

        // ── Chat actions ──────────────────────────────────────────────────────────

        public async Task SendMessageAsync(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText) || IsStreaming) return;

            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var ct = _streamingCts.Token;

            var engineName = DetectPythonEngine();
            _currentSession.PythonEngine = engineName;

            _currentSession.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
            var userVm = new ChatMessageViewModel { Role = ChatRole.User, Content = userText };
            AddMessage(userVm);

            var assistantVm = new ChatMessageViewModel
            {
                Role = ChatRole.Assistant, Content = string.Empty, IsStreaming = true
            };
            AddMessage(assistantVm);
            IsStreaming = true;

            var contentBuilder = new StringBuilder();

            try
            {
                var messages = BuildMessageList(engineName);
                await foreach (var token in _llmService.SendStreamingAsync(messages, ct))
                {
                    contentBuilder.Append(token);
                    assistantVm.Content = contentBuilder.ToString();
                    RequestScrollToBottom?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                return;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Session expired") ||
                ex.Message.Contains("log in"))
            {
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                Logout();
                return;
            }
            catch (Exception ex)
            {
                assistantVm.Content     = $"Error: {ex.Message}";
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                return;
            }

            var fullContent = contentBuilder.ToString();
            var codeSnippet = ExtractFirstCodeBlock(fullContent);

            assistantVm.Content     = StripCodeBlock(fullContent);
            assistantVm.CodeSnippet = codeSnippet;
            assistantVm.IsStreaming = false;
            IsStreaming = false;

            _currentSession.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant, Content = fullContent, CodeSnippet = codeSnippet
            });
            _historyService.Save(_currentSession);
        }

        public void InsertCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try
            {
                ClosePythonEditorWindows();
                var model = GetDynamoModel()!;
                var wsVm  = GetCurrentWorkspaceViewModel();
                var node  = PythonNodeInterop.GetSelectedPythonNode(wsVm!);

                if (node != null)
                {
                    GraphChangeCommands.UpdatePythonNodeScript(model, node, code, wsVm);
                    ShowStatus("Code inserted. Double-click the Python node to see updated code.");
                }
                else
                {
                    var newNode = GraphChangeCommands.CreatePythonNode(model, code);
                    ShowStatus(newNode != null
                        ? "New Python Script node created."
                        : "No Python Script node selected. Select one and try again.");
                }
            }
            catch (Exception ex) { ShowStatus($"Insert failed: {ex.Message}"); }
        }

        public async Task FixPythonErrorAsync()
        {
            if (IsStreaming) return;
            try
            {
                var wsVm = GetCurrentWorkspaceViewModel();
                var node = PythonNodeInterop.GetSelectedPythonNode(wsVm!);
                if (node == null) { ShowStatus("Select a Python Script node first."); return; }

                var error = PythonNodeInterop.GetNodeError(node);
                if (string.IsNullOrWhiteSpace(error))
                {
                    ShowStatus("No error detected on the selected Python node.");
                    return;
                }

                var code = PythonNodeInterop.GetScriptContent(node);

                // Check whether the node code matches what we last generated and is still
                // within the history window sent to the LLM. If so, skip re-sending the
                // code — the LLM already has it in context from its previous response.
                var (lastCode, lastCodeIndex) = GetLastGeneratedCode();
                int historyStart = Math.Max(0, _currentSession.Messages.Count - _settings.MaxHistoryMessages);
                bool codeInHistory  = lastCodeIndex >= historyStart;
                bool codeUnchanged  = lastCode != null
                                   && codeInHistory
                                   && !string.IsNullOrWhiteSpace(code)
                                   && NormalizeCode(code) == NormalizeCode(lastCode);

                string message;
                if (codeUnchanged)
                {
                    // Code is identical to what the LLM already sees in history — omit it.
                    message = $"The Python Script node returned this error:\n\n{error}\n\n" +
                              $"The code is unchanged from what you last generated. " +
                              $"Please fix the error and return the complete fixed code in a single ```python ... ``` block.";
                }
                else if (string.IsNullOrWhiteSpace(code))
                {
                    message = $"The Python Script node returned this error:\n\n{error}\n\n" +
                              $"Please provide the complete fixed code in a single ```python ... ``` block.";
                }
                else
                {
                    message = $"The Python Script node returned this error:\n\n{error}\n\n" +
                              $"IMPORTANT: The code has been modified since your last response. " +
                              $"Ignore any code from earlier in this conversation — use only the code below.\n\n" +
                              $"Here is the current code:\n```python\n{code}\n```\n\n" +
                              $"Please fix the error and return the complete fixed code in a single ```python ... ``` block.";
                }

                await SendMessageAsync(message);
            }
            catch (Exception ex) { ShowStatus($"Could not read error: {ex.Message}"); }
        }

        public void CopyToClipboard(string text)
        {
            try { System.Windows.Clipboard.SetText(text); ShowStatus("Copied to clipboard."); }
            catch { }
        }

        public void ClearHistory()
        {
            _historyService.DeleteSession(_currentSession.GraphFilePath);
            _currentSession = new ChatSession
            {
                GraphFilePath = _currentSession.GraphFilePath,
                PythonEngine  = _currentSession.PythonEngine
            };
            Messages.Clear();
            ShowWelcome   = true;
            StatusMessage = string.Empty;
        }

        public void CancelStreaming() => _streamingCts?.Cancel();

        public void Shutdown()
        {
            _streamingCts?.Cancel();
            _dynParams.CurrentWorkspaceChanged -= OnWorkspaceChanged;
            _historyService.Save(_currentSession);
        }

        // ── Node suggest actions ──────────────────────────────────────────────────

        /// <summary>
        /// Runs the node suggestion pipeline:
        ///   1. Reads current graph node names (graph context).
        ///   2. Calls the server (vector search → Gemini re-rank).
        ///   3. Populates <see cref="NodeSuggestions"/>.
        /// </summary>
        public async Task SearchNodesAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query) || _isSearchingNodes) return;

            IsSearchingNodes = true;
            foreach (var card in NodeSuggestions) card.Dispose();
            NodeSuggestions.Clear();
            OnPropertyChanged(nameof(ShowNodeHint));
            ShowStatus("Searching nodes\u2026");

            try
            {
                var results = await _localSearchService.SearchAsync(query);

                foreach (var node in results)
                    NodeSuggestions.Add(new NodeSuggestionCardViewModel(
                        node,
                        _packageState,
                        _downloader,
                        InsertNodeToCanvas));

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

        // ── Node insertion ────────────────────────────────────────────────────────

        private bool InsertNodeToCanvas(NodeSuggestion node)
        {
            try
            {
                var model = GetDynamoModel();
                if (model == null) return false;

                var packageFolderPath = _packageState.GetPackageFolderPath(node.PackageName);
                var (cx, cy) = GetCanvasCenter();

                var ok = DynamoCopilot.GraphInterop.GraphNodeInserter.InsertNode(
                    model,
                    node.Name,
                    node.PackageName,
                    node.NodeType,
                    packageFolderPath,
                    cx, cy);

                if (ok) ShowStatus($"Inserted \"{node.Name}\" onto the canvas.");
                return ok;
            }
            catch (Exception ex)
            {
                ShowStatus($"Insert failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Returns the canvas coordinates of the centre of the currently visible workspace area.
        /// Uses WorkspaceViewModel.X/Y (pan offset) and Zoom to convert viewport centre → canvas coords.
        /// Falls back to (0, 0) when any value cannot be read.
        /// </summary>
        private (double x, double y) GetCanvasCenter()
        {
            try
            {
                var wsVm = GetCurrentWorkspaceViewModel();
                if (wsVm == null) return (0, 0);

                var flags = System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.Instance;

                double panX = 0, panY = 0, zoom = 1.0;
                try { panX = Convert.ToDouble(wsVm.GetType().GetProperty("X",    flags)?.GetValue(wsVm) ?? 0.0); } catch { }
                try { panY = Convert.ToDouble(wsVm.GetType().GetProperty("Y",    flags)?.GetValue(wsVm) ?? 0.0); } catch { }
                try { zoom = Convert.ToDouble(wsVm.GetType().GetProperty("Zoom", flags)?.GetValue(wsVm) ?? 1.0); } catch { }
                if (zoom <= 0) zoom = 1.0;

                // Try to find the WorkspaceView panel dimensions via WPF visual tree.
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

                // Canvas coord of viewport centre:
                //   screenPt = panOffset + canvasPt * zoom
                //   canvasPt = (screenPt - panOffset) / zoom
                var cx = (viewW / 2.0 - panX) / zoom;
                var cy = (viewH / 2.0 - panY) / zoom;
                return (cx, cy);
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

        // ── Private helpers ────────────────────────────────────────────────────────

        private void OnAuthSuccess()
        {
            UserEmail  = _authService.Email;
            IsLoggedIn = true;
            RestoreHistory();
            _ = RefreshUserInfoAsync();
        }

        private void OnWorkspaceChanged(Dynamo.Graph.Workspaces.IWorkspaceModel workspace)
        {
            _historyService.Save(_currentSession);
            _currentSession = _historyService.Load(workspace?.FileName ?? string.Empty);
            Messages.Clear();
            RestoreHistory();
        }

        private void RestoreHistory()
        {
            foreach (var msg in _currentSession.Messages)
            {
                if (msg.Role == ChatRole.System) continue;
                Messages.Add(new ChatMessageViewModel
                {
                    Role        = msg.Role,
                    Content     = msg.Role == ChatRole.Assistant
                                    ? StripCodeBlock(msg.Content)
                                    : msg.Content,
                    CodeSnippet = msg.CodeSnippet
                });
            }
            ShowWelcome = Messages.Count == 0;
        }

        private void AddMessage(ChatMessageViewModel vm)
        {
            Messages.Add(vm);
            ShowWelcome = false;
            RequestScrollToBottom?.Invoke();
        }

        private void ShowStatus(string msg)
        {
            StatusMessage = msg;
            var timer = new System.Timers.Timer(4000) { AutoReset = false };
            timer.Elapsed += (_, _) => { StatusMessage = string.Empty; timer.Dispose(); };
            timer.Start();
        }

        private List<ChatMessage> BuildMessageList(string engineName)
        {
            var result = new List<ChatMessage>();
            result.Add(SystemPromptFactory.Build(engineName));
            var msgs  = _currentSession.Messages;
            int start = Math.Max(0, msgs.Count - _settings.MaxHistoryMessages);
            for (int i = start; i < msgs.Count; i++)
                result.Add(msgs[i]);
            return result;
        }

        /// <summary>
        /// Returns the CodeSnippet and session index of the most recent assistant message
        /// that contains generated code. Returns (null, -1) when none exists.
        /// </summary>
        private (string? code, int index) GetLastGeneratedCode()
        {
            var msgs = _currentSession.Messages;
            for (int i = msgs.Count - 1; i >= 0; i--)
            {
                if (msgs[i].Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(msgs[i].CodeSnippet))
                    return (msgs[i].CodeSnippet, i);
            }
            return (null, -1);
        }

        /// <summary>
        /// Normalises a Python code string for comparison: collapses line endings and trims whitespace.
        /// </summary>
        private static string NormalizeCode(string code) =>
            code.Replace("\r\n", "\n").Replace("\r", "\n").Trim();

        private string DetectPythonEngine()
        {
            try
            {
                var node = PythonNodeInterop.GetSelectedPythonNode(GetCurrentWorkspaceViewModel()!);
                if (node != null) return PythonNodeInterop.GetEngineName(node);
            }
            catch { }
            return _currentSession.PythonEngine;
        }

        /// <summary>
        /// Reads the NickName of every node currently in the workspace.
        /// Returns null when the workspace is empty or inaccessible (server will skip context).
        /// </summary>
        private string[]? GetGraphNodeNames()
        {
            try
            {
                var wsVm  = GetCurrentWorkspaceViewModel();
                if (wsVm == null) return null;

                var names = GraphNodeReader.GetAllNodeNames(wsVm);
                if (names.Count == 0) return null;

                var arr = new string[names.Count];
                for (int i = 0; i < names.Count; i++)
                    arr[i] = names[i];
                return arr;
            }
            catch { return null; }
        }

        private string GetCurrentGraphPath()
        {
            try { return _dynParams.CurrentWorkspaceModel?.FileName ?? string.Empty; }
            catch { return string.Empty; }
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
                .GetProperty("Model",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(dvm);
        }

        private object? GetCurrentWorkspaceViewModel()
        {
            var dvm = GetDynamoViewModel();
            return dvm?.GetType()
                .GetProperty("CurrentSpaceViewModel",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(dvm);
        }

        private static void ClosePythonEditorWindows()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;
                var toClose = new System.Collections.Generic.List<System.Windows.Window>();
                foreach (System.Windows.Window win in app.Windows)
                {
                    var name = win.GetType().FullName ?? string.Empty;
                    if (name.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("ScriptEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("EditScript", StringComparison.OrdinalIgnoreCase) >= 0)
                        toClose.Add(win);
                }
                foreach (var win in toClose) win.Close();
            }
            catch { }
        }

        private static string? ExtractFirstCodeBlock(string markdown)
        {
            var match = Regex.Match(markdown,
                @"```(?:python)?\s*\r?\n(.*?)\r?\n```",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static string StripCodeBlock(string markdown)
        {
            var result = Regex.Replace(markdown,
                @"```(?:\w+)?\s*\r?\n.*?\r?\n```",
                string.Empty,
                RegexOptions.Singleline | RegexOptions.IgnoreCase).Trim();
            return string.IsNullOrWhiteSpace(result) ? string.Empty : result;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
