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
using DynamoCopilot.Core;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Services.Providers;
using DynamoCopilot.Core.Services.Rag;
using DynamoCopilot.Core.Services.Validation;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.Services;
using DynamoCopilot.GraphInterop;

namespace DynamoCopilot.Extension.ViewModels
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Panel mode enum
    // ─────────────────────────────────────────────────────────────────────────────

    public enum ChatMessageType { Normal, SpecCard }

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

        // Feature 1: spec card support
        public ChatMessageType MessageType { get; set; } = ChatMessageType.Normal;
        public SpecCardViewModel? SpecCard  { get; set; }
        public bool IsSpecCard => MessageType == ChatMessageType.SpecCard;

        private string? _validationWarning;
        public string? ValidationWarning
        {
            get => _validationWarning;
            set { _validationWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasValidationWarning)); }
        }
        public bool HasValidationWarning => !string.IsNullOrWhiteSpace(_validationWarning);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Panel ViewModel
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class CopilotPanelViewModel : INotifyPropertyChanged
    {
        private readonly DynamoCopilotSettings     _settings;
        private readonly AuthService               _authService;
        private          ILlmService               _llmService;
        private readonly ChatHistoryService        _historyService;
        private readonly ViewLoadedParams          _dynParams;
        private readonly RevitApiRagService        _ragService;
        private          SpecGeneratorService      _specGenerator;
        private readonly SpecificationStateManager _specState;
        private bool     _isClassifying;

        private ChatSession _currentSession;
        private CancellationTokenSource? _streamingCts;

        // ── Settings panel ────────────────────────────────────────────────────────

        private bool _showAiPanel;
        private bool _showUserPanel;

        public SettingsPanelViewModel SettingsVm { get; }

        public bool ShowAiPanel
        {
            get => _showAiPanel;
            private set { _showAiPanel = value; OnPropertyChanged(); }
        }

        public bool ShowUserPanel
        {
            get => _showUserPanel;
            private set { _showUserPanel = value; OnPropertyChanged(); }
        }

        public void ToggleAiPanel()
        {
            ShowAiPanel   = !ShowAiPanel;
            ShowUserPanel = false;
        }

        public void ToggleUserPanel()
        {
            ShowUserPanel = !ShowUserPanel;
            ShowAiPanel   = false;
            if (ShowUserPanel)
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

        private string    _userEmail       = string.Empty;
        private bool      _isLicenceActive = false;
        private int       _tokensUsed;
        private DateTime? _licenseEndDate;

        // Shown in the main panel when the user is logged in but has no licence.
        public string LicenseMessage =>
            $"Sorry, you don't have a licence for Dynamo Co-pilot.\n\n" +
            $"Contact us at {ExtensionConstants.SupportEmail}";

        // ── Chat state ────────────────────────────────────────────────────────────

        private bool   _isStreaming;
        private string _statusMessage = string.Empty;
        private bool   _showWelcome   = true;

        public Action? RequestScrollToBottom { get; set; }

        // ── Bindable collections ─────────────────────────────────────────────────

        public ObservableCollection<ChatMessageViewModel> Messages { get; }
            = new ObservableCollection<ChatMessageViewModel>();

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
            private set { _licenseEndDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(LicenseExpiryDisplay)); OnPropertyChanged(nameof(IsLicenseExpired)); }
        }

        public string LicenseExpiryDisplay => _licenseEndDate.HasValue
            ? _licenseEndDate.Value.ToLocalTime().ToString("d MMM yyyy")
            : "—";

        public bool IsLicenseExpired => _licenseEndDate.HasValue && _licenseEndDate.Value < DateTime.UtcNow;

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

        // ── Construction ──────────────────────────────────────────────────────────

        public CopilotPanelViewModel(
            DynamoCopilotSettings settings,
            AuthService           authService,
            ChatHistoryService    historyService,
            ViewLoadedParams      dynParams)
        {
            _settings    = settings;
            _authService = authService;
            _historyService = historyService;
            _dynParams   = dynParams;

            _llmService     = LlmServiceFactory.Create(settings);
            _ragService     = new RevitApiRagService(
                string.IsNullOrWhiteSpace(settings.RevitApiXmlPath) ? null : settings.RevitApiXmlPath);
            _specGenerator  = new SpecGeneratorService(_llmService);
            _specState      = new SpecificationStateManager();

            SettingsVm = new SettingsPanelViewModel(settings);
            SettingsVm.SettingsSaved += OnSettingsSaved;

            _currentSession = _historyService.Load(GetCurrentGraphPath());
            _dynParams.CurrentWorkspaceChanged += OnWorkspaceChanged;

            AuthService.GlobalLoggedIn  += OnGlobalLoggedIn;
            AuthService.GlobalLoggedOut += OnGlobalLoggedOut;
        }

        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            (_llmService as IDisposable)?.Dispose();
            _llmService    = LlmServiceFactory.Create(_settings);
            _specGenerator = new SpecGeneratorService(_llmService);
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
            ClearAuthState();       // mark as logged out first so the global event guard skips this VM
            _authService.Logout();  // deletes tokens.json, fires GlobalLoggedOut → other VM clears
        }

        private void ClearAuthState()
        {
            _streamingCts?.Cancel();
            Messages.Clear();
            ShowWelcome    = true;
            StatusMessage  = string.Empty;
            ShowAiPanel    = false;
            ShowUserPanel  = false;
            IsLoggedIn     = false;
            IsRegisterMode = false;
            AuthError      = string.Empty;
            LoginEmail     = string.Empty;
            RegisterEmail  = string.Empty;
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

        // ── User info (integrated into settings panel) ────────────────────────────

        public void ToggleUserInfo() => ToggleUserPanel();

        private async Task RefreshUserInfoAsync()
        {
            UserEmail = _authService.Email;

            var info = await _authService.GetUserInfoAsync();
            if (info == null) return;

            UserEmail  = info.Email;
            if (info.DailyTokenCount > 0 || TokensUsed == 0)
                TokensUsed = info.DailyTokenCount;

            var lic = info.GetLicense(ExtensionConstants.CopilotId);
            IsLicenceActive = info.IsActive && lic != null && lic.IsActive && !lic.Expired;
            LicenseEndDate  = lic?.EndDate;
        }

        // ── Chat actions ──────────────────────────────────────────────────────────

        public async Task SendMessageAsync(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText) || IsStreaming) return;
            CopilotLogger.Log("SendMessageAsync", $"user={_authService.Email}  text={Truncate(userText, 120)}");
            try
            {
                await RefreshUserInfoAsync();
                await SendMessageCoreAsync(userText);
            }
            catch (Exception ex)
            {
                CopilotLogger.Log("SendMessageAsync unhandled", ex);
                IsStreaming    = false;
                StatusMessage  = "An unexpected error occurred. See AppData\\DynamoCopilot\\log for details.";
            }
        }

        /// <summary>
        /// Called by the DispatcherUnhandledException handler when a WPF rendering
        /// exception originating from our code is caught. Resets streaming state so
        /// the user can retry.
        /// </summary>
        public void HandleRenderException(Exception ex)
        {
            IsStreaming = false;
            _isClassifying = false;
            StatusMessage  = "A rendering error occurred. See AppData\\DynamoCopilot\\log for details.";
            CopilotLogger.Log("HandleRenderException", ex.Message);
        }

        private async Task SendMessageCoreAsync(string userText)
        {
            if (!IsLicenceActive)
            {
                StatusMessage = LicenseMessage;
                IsStreaming   = false;
                return;
            }

            // Feature 1: !direct prefix bypasses spec classification
            bool bypassSpec = userText.StartsWith("!direct ", StringComparison.Ordinal);
            if (bypassSpec) userText = userText.Substring("!direct ".Length).Trim();

            // Cancel any pending spec if the user starts a new message
            if (_specState.HasPendingSpec && !bypassSpec)
                CancelPendingSpec();

            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var ct = _streamingCts.Token;

            // Block further sends for the entire request lifecycle (classification + response)
            IsStreaming = true;

            var engineName = DetectPythonEngine();
            _currentSession.PythonEngine = engineName;

            _currentSession.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
            AddMessage(new ChatMessageViewModel { Role = ChatRole.User, Content = userText });

            // Feature 1: spec-first classification (unless bypassed or disabled)
            if (!bypassSpec && _settings.EnableSpecFirst)
            {
                CopilotLogger.Log("Classify", "starting");
                _isClassifying = true;
                StatusMessage  = "Analyzing your request...";
                SpecClassificationResult classification;
                try
                {
                    var historyForClassifier = GetRecentHistoryForClassifier();
                    CopilotLogger.Log("Classify", $"historyMessages={historyForClassifier.Count}");
                    classification = await _specGenerator.ClassifyAsync(userText, historyForClassifier, ct);
                }
                catch (OperationCanceledException)
                {
                    CopilotLogger.Log("Classify", "cancelled");
                    _isClassifying = false;
                    StatusMessage  = string.Empty;
                    IsStreaming    = false;
                    return;
                }
                catch (Exception ex)
                {
                    CopilotLogger.Log("Classify failed — falling back to direct chat", ex);
                    classification = new SpecClassificationResult { IsSpec = false };
                }
                _isClassifying = false;
                StatusMessage  = string.Empty;

                CopilotLogger.Log("Classify", $"isSpec={classification.IsSpec}  hasChatText={!string.IsNullOrEmpty(classification.ChatText)}");

                if (classification.IsSpec && classification.Spec != null)
                {
                    CopilotLogger.Log("Classify", $"spec steps={classification.Spec.Steps.Count}  questions={classification.Spec.Questions.Count}");
                    IsStreaming = false;
                    ShowSpecCard(classification.Spec);
                    return;
                }

                // Chat response from classifier — show it directly without another LLM call
                if (!string.IsNullOrWhiteSpace(classification.ChatText))
                {
                    var chatText   = classification.ChatText!;
                    var fencedText = EnsureCodeFenced(chatText);
                    var code       = ExtractFirstCodeBlock(fencedText);
                    CopilotLogger.Log("Classify", $"returning classifier chat response  codeBlock={code != null}");
                    var chatVm = new ChatMessageViewModel
                    {
                        Role        = ChatRole.Assistant,
                        Content     = code != null ? StripCodeBlock(fencedText) : chatText,
                        CodeSnippet = code
                    };
                    AddMessage(chatVm);
                    _currentSession.Messages.Add(new ChatMessage
                    {
                        Role        = ChatRole.Assistant,
                        Content     = chatText,
                        CodeSnippet = code
                    });
                    try { _historyService.Save(_currentSession); } catch { }
                    IsStreaming = false;
                    return;
                }
            }

            CopilotLogger.Log("Classify", "bypassed or disabled — going to streaming");
            await RunStreamingAsync(userText, engineName, ct);
        }

        private void ShowSpecCard(CodeSpecification spec)
        {
            CopilotLogger.Log("ShowSpecCard", "SetPending");
            _specState.SetPending(spec);

            CopilotLogger.Log("ShowSpecCard", "creating SpecCardViewModel");
            var cardVm = new SpecCardViewModel(
                spec,
                onConfirm: s => ConfirmSpecAsync(s),
                onCancel:  CancelPendingSpec);

            CopilotLogger.Log("ShowSpecCard", "creating ChatMessageViewModel");
            var msgVm = new ChatMessageViewModel
            {
                Role        = ChatRole.Assistant,
                MessageType = ChatMessageType.SpecCard,
                SpecCard    = cardVm
            };

            CopilotLogger.Log("ShowSpecCard", "AddMessage");
            AddMessage(msgVm);
            CopilotLogger.Log("ShowSpecCard", "done");
        }

        private async Task ConfirmSpecAsync(CodeSpecification spec)
        {
            await RefreshUserInfoAsync();
            if (IsLicenseExpired)
            {
                StatusMessage = "Your licence has expired. Please contact support to renew.";
                return;
            }

            CopilotLogger.Log("ConfirmSpec", $"steps={spec.Steps.Count}  questions={spec.Questions.Count}");
            _specState.Clear();

            // Build a code-generation request from the confirmed spec
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Generate complete Python code for the following specification:");
            sb.AppendLine();
            if (spec.Inputs.Count > 0)
            {
                sb.AppendLine("**Inputs:**");
                foreach (var inp in spec.Inputs)
                    sb.AppendLine($"- {inp.Name} ({inp.Type}): {inp.Description}");
                sb.AppendLine();
            }
            sb.AppendLine("**Processing steps:**");
            foreach (var step in spec.Steps)
                sb.AppendLine($"- {step}");
            sb.AppendLine();
            if (spec.Output != null)
                sb.AppendLine($"**Output:** {spec.Output.Type} — {spec.Output.Description}");

            // Include answered clarifying questions if any
            bool hasAnswers = false;
            foreach (var q in spec.Questions)
                if (!string.IsNullOrWhiteSpace(q.Answer)) { hasAnswers = true; break; }
            if (hasAnswers)
            {
                sb.AppendLine();
                sb.AppendLine("**Clarifications:**");
                foreach (var q in spec.Questions)
                    if (!string.IsNullOrWhiteSpace(q.Answer))
                        sb.AppendLine($"- {q.Question} → {q.Answer}");
            }

            sb.AppendLine();
            sb.AppendLine("Return the complete, runnable Python script.");

            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var engineName  = DetectPythonEngine();
            var codeRequest = sb.ToString();

            // Add this synthetic request to history so the LLM has context
            _currentSession.Messages.Add(new ChatMessage
            {
                Role    = ChatRole.User,
                Content = codeRequest
            });

            await RunStreamingAsync(codeRequest, engineName, _streamingCts.Token);
        }

        private void CancelPendingSpec()
        {
            _specState.Clear();
            ShowStatus("Specification cancelled.");
        }

        private async Task RunStreamingAsync(string userText, string engineName, CancellationToken ct)
        {
            CopilotLogger.Log("Streaming", $"engine={engineName}  provider={_settings.AiProvider}  model={_settings.GetModel(_settings.AiProvider)}");

            var assistantVm = new ChatMessageViewModel
            {
                Role = ChatRole.Assistant, Content = string.Empty, IsStreaming = true
            };
            AddMessage(assistantVm);
            IsStreaming = true;

            var contentBuilder = new StringBuilder();
            int tokenCount = 0;

            // Feature 3: fetch RAG context from local RevitAPI.xml
            string? ragContext = null;
            if (_settings.EnableRag)
            {
                try
                {
                    ragContext = await _ragService.FetchContextAsync(userText, ct);
                    CopilotLogger.Log("Streaming", $"RAG context chars={ragContext?.Length ?? 0}");
                }
                catch (Exception ex) { CopilotLogger.Log("RAG fetch failed (non-fatal)", ex); }
            }

            try
            {
                var messages = BuildMessageList(engineName, ragContext);
                CopilotLogger.Log("Streaming", $"sending {messages.Count} messages to LLM");
                bool firstToken = true;
                await foreach (var token in _llmService.SendStreamingAsync(messages, ct))
                {
                    if (firstToken) { CopilotLogger.Log("Streaming", "first token received"); firstToken = false; }
                    tokenCount++;
                    contentBuilder.Append(token);
                    assistantVm.Content = contentBuilder.ToString();
                    RequestScrollToBottom?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                CopilotLogger.Log("Streaming", "cancelled by user");
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                return;
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("Session expired") ||
                ex.Message.Contains("log in"))
            {
                CopilotLogger.Log("Streaming session expired", ex);
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                Logout();
                return;
            }
            catch (Exception ex)
            {
                CopilotLogger.Log("Streaming LLM error", ex);
                assistantVm.Content     = $"Error: {ex.Message}";
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                return;
            }

            CopilotLogger.Log("Streaming", $"complete  tokens={tokenCount}  chars={contentBuilder.Length}");

            var rawContent  = contentBuilder.ToString();
            var fullContent = EnsureCodeFenced(rawContent);
            var codeSnippet = ExtractFirstCodeBlock(fullContent);

            // Diagnostic: log fence detection detail so we can identify regex-mismatch causes
            bool dbgHasFence       = rawContent.Contains("```");
            bool dbgHasPythonFence = rawContent.Contains("```python");
            bool dbgHasNewlineFence= Regex.IsMatch(rawContent, @"```\w*\s*\r?\n", RegexOptions.IgnoreCase);
            bool dbgHasCloseFence  = Regex.IsMatch(rawContent, @"\r?\n```", RegexOptions.IgnoreCase);
            CopilotLogger.Log("Streaming", $"codeBlock={codeSnippet != null}  codeChars={codeSnippet?.Length ?? 0}  " +
                $"hasFence={dbgHasFence}  hasPythonFence={dbgHasPythonFence}  hasNewlineFence={dbgHasNewlineFence}  hasCloseFence={dbgHasCloseFence}  " +
                $"rawLen={rawContent.Length}  fenced={rawContent != fullContent}");

            // Feature 2: validate + auto-fix Revit enum values in generated code
            if (codeSnippet != null && _settings.EnableCodeValidation)
            {
                var validation = RevitEnumValidator.Instance.Validate(codeSnippet);
                CopilotLogger.Log("Validation", $"isValid={validation.IsValid}  issues={validation.Issues.Count}");
                if (!validation.IsValid)
                {
                    string? fixedCode = await RunAutoFixLoopAsync(codeSnippet, validation, ct);
                    CopilotLogger.Log("Validation", $"autoFix={fixedCode != null}");
                    if (fixedCode != null)
                    {
                        fullContent = fullContent.Replace(codeSnippet, fixedCode);
                        codeSnippet = fixedCode;
                    }
                    else
                    {
                        assistantVm.ValidationWarning = FormatValidationWarning(validation);
                    }
                }
            }

            assistantVm.Content     = StripCodeBlock(fullContent);
            assistantVm.CodeSnippet = codeSnippet;
            assistantVm.IsStreaming = false;
            IsStreaming = false;

            // Estimate tokens for this exchange (~4 chars per token)
            TokensUsed += Math.Max(1, (userText.Length + contentBuilder.Length) / 4);
            CopilotLogger.Log("Streaming", "UI updated — done");

            _currentSession.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant, Content = fullContent, CodeSnippet = codeSnippet
            });
            try { _historyService.Save(_currentSession); } catch { }
        }

        private async Task<string?> RunAutoFixLoopAsync(
            string code,
            ValidationResult result,
            CancellationToken ct)
        {
            const int MaxAttempts = 2;
            string currentCode   = code;
            var    currentResult = result;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) return null;

                var fixPrompt = AutoFixRequestBuilder.Build(currentCode, currentResult, attempt);
                var fixMessages = new List<ChatMessage>
                {
                    SystemPromptFactory.Build(DetectPythonEngine()),
                    new ChatMessage { Role = ChatRole.User, Content = fixPrompt }
                };

                var sb = new StringBuilder();
                try
                {
                    await foreach (var token in _llmService.SendStreamingAsync(fixMessages, ct))
                        sb.Append(token);
                }
                catch { return null; }

                var fixedCode = ExtractFirstCodeBlock(sb.ToString());
                if (fixedCode == null) return null;

                var revalidation = RevitEnumValidator.Instance.Validate(fixedCode);
                if (revalidation.IsValid) return fixedCode;

                currentCode   = fixedCode;
                currentResult = revalidation;
            }

            return null;
        }

        private static string FormatValidationWarning(ValidationResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Code Validation: the following Revit API enum values may not exist in this installation:");
            foreach (var issue in result.Issues)
                sb.AppendLine($"  - {issue.Category}.{issue.InvalidValue}");
            sb.Append("Verify these values before running the script.");
            return sb.ToString();
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
                bool codeInHistory = ChatContextBuilder.IsMessageInContext(
                    _currentSession.Messages, lastCodeIndex, _settings.MaxHistoryTokens);
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

                await SendMessageAsync("!direct " + message);
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
            AuthService.GlobalLoggedIn  -= OnGlobalLoggedIn;
            AuthService.GlobalLoggedOut -= OnGlobalLoggedOut;
            _streamingCts?.Cancel();
            _dynParams.CurrentWorkspaceChanged -= OnWorkspaceChanged;
            _historyService.Save(_currentSession);
        }

        private void OnAuthSuccess()
        {
            UserEmail       = _authService.Email;
            // Set licence state immediately from the JWT — no network call needed.
            IsLicenceActive = _authService.GetGrantedExtensions().Contains(ExtensionConstants.CopilotId);
            IsLoggedIn      = true;
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

        private List<ChatMessage> BuildMessageList(string engineName, string? ragContext = null)
        {
            var systemPrompt = SystemPromptFactory.Build(engineName, ragContext);
            return ChatContextBuilder.Build(systemPrompt, _currentSession.Messages, _settings.MaxHistoryTokens);
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
        /// Returns a compact, anchor-first message list for the spec classifier.
        /// Always includes early messages (user instructions) + the recent tail.
        /// </summary>
        private IList<ChatMessage> GetRecentHistoryForClassifier()
            => ChatContextBuilder.BuildForClassifier(_currentSession.Messages);

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

        // Dynamo Python patterns that never appear in prose
        private static readonly Regex _dynaPythonAnchor = new(
            @"^(import clr\b|clr\.AddReference|from Autodesk\.Revit)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        /// <summary>
        /// If the AI returned Python code without fenced backticks, wraps the detected
        /// code region in ```python fences so the normal extraction path can find it.
        /// Called before ExtractFirstCodeBlock / StripCodeBlock.
        /// </summary>
        private static string EnsureCodeFenced(string response)
        {
            // Already has a fenced block — nothing to do
            if (Regex.IsMatch(response, @"```\w*\s*\r?\n", RegexOptions.IgnoreCase))
                return response;

            // No Dynamo Python anchors — no code to wrap
            if (!_dynaPythonAnchor.IsMatch(response))
                return response;

            var lines = response.Split('\n');
            int codeStart = -1;
            int codeEnd   = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                if (IsPythonCodeLine(lines[i]))
                {
                    if (codeStart == -1) codeStart = i;
                    codeEnd = i;
                }
            }

            if (codeStart == -1) return response;

            // Extend codeEnd forward: skip blank lines, absorb any subsequent code lines
            for (int i = codeEnd + 1; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;          // blank — peek further
                if (IsPythonCodeLine(lines[i])) codeEnd = i; // more code — extend
                else break;                                  // prose — stop
            }

            var sb = new StringBuilder();

            if (codeStart > 0)
            {
                sb.Append(string.Join("\n", lines, 0, codeStart).TrimEnd());
                sb.Append("\n\n");
            }

            sb.AppendLine("```python");
            sb.AppendLine(string.Join("\n", lines, codeStart, codeEnd - codeStart + 1).Trim());
            sb.Append("```");

            if (codeEnd < lines.Length - 1)
            {
                var after = string.Join("\n", lines, codeEnd + 1, lines.Length - codeEnd - 1).TrimStart();
                if (!string.IsNullOrWhiteSpace(after))
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append(after);
                }
            }

            return sb.ToString();
        }

        private static bool IsPythonCodeLine(string line)
        {
            var t = line.TrimStart();
            if (t.Length == 0) return false;
            return t.StartsWith("import ")
                || t.StartsWith("from ")
                || t.StartsWith("clr.")
                || t.StartsWith("def ")
                || t.StartsWith("class ")
                || t.StartsWith("OUT ")
                || t.StartsWith("OUT=")
                || t.StartsWith("IN[")
                || t.StartsWith("TransactionManager")
                || t.StartsWith("DocumentManager")
                || (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')); // indented line
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
