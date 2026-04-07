using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dynamo.Wpf.Extensions;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Services;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.GraphInterop;

// Provider index constants match AiProvider enum order: Groq=0, Gemini=1, OpenRouter=2, Ollama=3, OpenAI=4

namespace DynamoCopilot.Extension.ViewModels
{
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Per-message view model
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed class ChatMessageViewModel : INotifyPropertyChanged
    {
        private string _content = string.Empty;
        private bool _isStreaming;
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

        public bool HasCode => !string.IsNullOrWhiteSpace(_codeSnippet);
        public bool IsUser => Role == ChatRole.User;
        public bool IsAssistant => Role == ChatRole.Assistant;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Panel ViewModel â€” pure WPF data binding, no JS bridge
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public sealed class CopilotPanelViewModel : INotifyPropertyChanged
    {
        private readonly DynamoCopilotSettings _settings;
        private ILlmService _llmService;
        private readonly ChatHistoryService _historyService;
        private readonly ViewLoadedParams _dynParams;

        private ChatSession _currentSession;
        private CancellationTokenSource? _streamingCts;
        private bool _isStreaming;
        private string _statusMessage = string.Empty;
        private bool _showWelcome = true;

        /// <summary>View wires this to auto-scroll MessageList to the bottom.</summary>
        public Action? RequestScrollToBottom { get; set; }

        // â”€â”€â”€ Bindable â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public ObservableCollection<ChatMessageViewModel> Messages { get; }
            = new ObservableCollection<ChatMessageViewModel>();

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

        // â”€â”€â”€ Construction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public CopilotPanelViewModel(
            DynamoCopilotSettings settings,
            ILlmService llmService,
            ChatHistoryService historyService,
            ViewLoadedParams dynParams)
        {
            _settings = settings;
            _llmService = llmService;
            _historyService = historyService;
            _dynParams = dynParams;

            _currentSession = _historyService.Load(GetCurrentGraphPath());
            _dynParams.CurrentWorkspaceChanged += OnWorkspaceChanged;

            RestoreHistory();
        }

        // â”€â”€â”€ Public actions called by the View â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public async Task SendMessageAsync(string userText)
        {
            if (string.IsNullOrWhiteSpace(userText) || IsStreaming) return;

            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var ct = _streamingCts.Token;

            var engineName = DetectPythonEngine();
            _currentSession.PythonEngine = engineName;

            // Persist user turn
            _currentSession.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = userText });
            var userVm = new ChatMessageViewModel { Role = ChatRole.User, Content = userText };
            AddMessage(userVm);

            // Placeholder for the streaming assistant reply
            var assistantVm = new ChatMessageViewModel
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                IsStreaming = true
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
            catch (Exception ex)
            {
                assistantVm.Content = $"Error: {ex.Message}";
                assistantVm.IsStreaming = false;
                IsStreaming = false;
                return;
            }

            var fullContent = contentBuilder.ToString();
            var codeSnippet = ExtractFirstCodeBlock(fullContent);

            // Show prose and code in separate regions
            assistantVm.Content = StripCodeBlock(fullContent);
            assistantVm.CodeSnippet = codeSnippet;
            assistantVm.IsStreaming = false;
            IsStreaming = false;

            _currentSession.Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = fullContent,
                CodeSnippet = codeSnippet
            });
            _historyService.Save(_currentSession);
        }

        public void InsertCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try
            {
                ClosePythonEditorWindows();

                var model = GetDynamoModel();
                var wsVm = GetCurrentWorkspaceViewModel();
                var node = PythonNodeInterop.GetSelectedPythonNode(wsVm);

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
            catch (Exception ex)
            {
                ShowStatus($"Insert failed: {ex.Message}");
            }
        }

        private static void ClosePythonEditorWindows()
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;

                // Collect first to avoid modifying the collection while iterating
                var toClose = new System.Collections.Generic.List<System.Windows.Window>();
                foreach (System.Windows.Window win in app.Windows)
                {
                    var name = win.GetType().FullName ?? string.Empty;
                    if (name.IndexOf("Python", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("ScriptEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("EditScript", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        toClose.Add(win);
                    }
                }
                foreach (var win in toClose)
                    win.Close();
            }
            catch { }
        }

        public async Task FixPythonErrorAsync()
        {
            if (IsStreaming) return;

            try
            {
                var wsVm = GetCurrentWorkspaceViewModel();
                var node = PythonNodeInterop.GetSelectedPythonNode(wsVm);
                if (node == null)
                {
                    ShowStatus("Select a Python Script node first.");
                    return;
                }

                var error = PythonNodeInterop.GetNodeError(node);
                if (string.IsNullOrWhiteSpace(error))
                {
                    ShowStatus("No error detected on the selected Python node.");
                    return;
                }

                var code = PythonNodeInterop.GetScriptContent(node);
                var message = string.IsNullOrWhiteSpace(code)
                    ? $"The Python Script node returned this error:\n\n{error}\n\nPlease provide a fix."
                    : $"The Python Script node returned this error:\n\n{error}\n\nHere is the current code:\n```python\n{code}\n```\n\nPlease fix the error.";

                await SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not read error: {ex.Message}");
            }
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
                PythonEngine = _currentSession.PythonEngine
            };
            Messages.Clear();
            ShowWelcome = true;
            StatusMessage = string.Empty;
        }

        public void CancelStreaming() => _streamingCts?.Cancel();

        // ─── Settings panel ───────────────────────────────────────────────────────

        private bool _showSettings;
        private int _selectedProviderIndex;
        private string _settingsApiKey = string.Empty;
        private string _settingsModel = string.Empty;
        private string _settingsEndpoint = string.Empty;

        public bool ShowSettings
        {
            get => _showSettings;
            set { _showSettings = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowChat)); }
        }

        public bool ShowChat => !_showSettings;

        public int SelectedProviderIndex
        {
            get => _selectedProviderIndex;
            set
            {
                _selectedProviderIndex = value;
                var p = (AiProvider)value;
                _settingsApiKey = GetSavedApiKey(p);
                _settingsModel = GetSavedModel(p);
                _settingsEndpoint = _settings.OllamaEndpoint;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SettingsApiKey));
                OnPropertyChanged(nameof(SettingsModel));
                OnPropertyChanged(nameof(SettingsEndpoint));
                OnPropertyChanged(nameof(IsOllama));
                OnPropertyChanged(nameof(ApiKeyLabel));
                OnPropertyChanged(nameof(ModelHint));
                OnPropertyChanged(nameof(ProviderNote));
            }
        }

        public bool IsOllama => _selectedProviderIndex == (int)AiProvider.Ollama;

        public string SettingsApiKey
        {
            get => _settingsApiKey;
            set { _settingsApiKey = value; OnPropertyChanged(); }
        }

        public string SettingsModel
        {
            get => _settingsModel;
            set { _settingsModel = value; OnPropertyChanged(); }
        }

        public string SettingsEndpoint
        {
            get => _settingsEndpoint;
            set { _settingsEndpoint = value; OnPropertyChanged(); }
        }

        public string ApiKeyLabel
        {
            get
            {
                switch ((AiProvider)_selectedProviderIndex)
                {
                    case AiProvider.Groq: return "Groq API Key:";
                    case AiProvider.Gemini: return "Gemini API Key:";
                    case AiProvider.OpenRouter: return "OpenRouter API Key:";
                    case AiProvider.OpenAI: return "OpenAI API Key:";
                    default: return "API Key:";
                }
            }
        }

        public string ModelHint
        {
            get
            {
                switch ((AiProvider)_selectedProviderIndex)
                {
                    case AiProvider.Groq: return "e.g. llama-3.3-70b-versatile · llama-3.1-8b-instant";
                    case AiProvider.Gemini: return "e.g. gemini-2.0-flash · gemini-1.5-flash";
                    case AiProvider.OpenRouter: return "e.g. meta-llama/llama-3.3-70b-instruct:free · google/gemma-3-27b-it:free";
                    case AiProvider.Ollama: return "e.g. llama3 · mistral · codellama (must be pulled first)";
                    case AiProvider.OpenAI: return "e.g. gpt-4o · gpt-4o-mini";
                    default: return string.Empty;
                }
            }
        }

        public string ProviderNote
        {
            get
            {
                switch ((AiProvider)_selectedProviderIndex)
                {
                    case AiProvider.Groq: return "Free · Get your key at console.groq.com";
                    case AiProvider.Gemini: return "Free · Get your key at aistudio.google.com";
                    case AiProvider.OpenRouter: return "Free models available · Get your key at openrouter.ai";
                    case AiProvider.Ollama: return "Completely free · Run locally with ollama.com";
                    case AiProvider.OpenAI: return "Paid account required · platform.openai.com";
                    default: return string.Empty;
                }
            }
        }

        public void OpenSettings()
        {
            _selectedProviderIndex = (int)_settings.Provider;
            _settingsApiKey = GetSavedApiKey(_settings.Provider);
            _settingsModel = GetSavedModel(_settings.Provider);
            _settingsEndpoint = _settings.OllamaEndpoint;
            OnPropertyChanged(nameof(SelectedProviderIndex));
            OnPropertyChanged(nameof(SettingsApiKey));
            OnPropertyChanged(nameof(SettingsModel));
            OnPropertyChanged(nameof(SettingsEndpoint));
            OnPropertyChanged(nameof(IsOllama));
            OnPropertyChanged(nameof(ApiKeyLabel));
            OnPropertyChanged(nameof(ModelHint));
            OnPropertyChanged(nameof(ProviderNote));
            ShowSettings = true;
        }

        public void CloseSettings() => ShowSettings = false;

        public void SaveSettings()
        {
            if (IsStreaming) return;
            _settings.Provider = (AiProvider)_selectedProviderIndex;
            switch (_settings.Provider)
            {
                case AiProvider.Groq:        _settings.GroqApiKey = _settingsApiKey;        _settings.GroqModel = _settingsModel;        break;
                case AiProvider.Gemini:      _settings.GeminiApiKey = _settingsApiKey;      _settings.GeminiModel = _settingsModel;      break;
                case AiProvider.OpenRouter:  _settings.OpenRouterApiKey = _settingsApiKey;  _settings.OpenRouterModel = _settingsModel;   break;
                case AiProvider.Ollama:      _settings.OllamaEndpoint = _settingsEndpoint;  _settings.OllamaModel = _settingsModel;      break;
                case AiProvider.OpenAI:      _settings.OpenAiApiKey = _settingsApiKey;      _settings.OpenAiModel = _settingsModel;      break;
            }
            _settings.Save();
            if (_llmService is IDisposable d) d.Dispose();
            _llmService = LlmServiceFactory.Create(_settings);
            ShowSettings = false;
            ShowStatus("Settings saved — using " + _settings.Provider.ToString() + ".");
        }

        private string GetSavedApiKey(AiProvider p)
        {
            switch (p)
            {
                case AiProvider.Groq:       return _settings.GroqApiKey;
                case AiProvider.Gemini:     return _settings.GeminiApiKey;
                case AiProvider.OpenRouter: return _settings.OpenRouterApiKey;
                case AiProvider.OpenAI:     return _settings.OpenAiApiKey;
                default:                    return string.Empty;
            }
        }

        private string GetSavedModel(AiProvider p)
        {
            switch (p)
            {
                case AiProvider.Groq:       return _settings.GroqModel;
                case AiProvider.Gemini:     return _settings.GeminiModel;
                case AiProvider.OpenRouter: return _settings.OpenRouterModel;
                case AiProvider.Ollama:     return _settings.OllamaModel;
                case AiProvider.OpenAI:     return _settings.OpenAiModel;
                default:                    return string.Empty;
            }
        }

        public void Shutdown()
        {
            _streamingCts?.Cancel();
            _dynParams.CurrentWorkspaceChanged -= OnWorkspaceChanged;
            _historyService.Save(_currentSession);
        }

        // â”€â”€â”€ Private â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
                    Role = msg.Role,
                    Content = msg.Role == ChatRole.Assistant
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
            var msgs = _currentSession.Messages;
            int start = Math.Max(0, msgs.Count - _settings.MaxHistoryMessages);
            for (int i = start; i < msgs.Count; i++)
                result.Add(msgs[i]);
            return result;
        }

        private string DetectPythonEngine()
        {
            try
            {
                var node = PythonNodeInterop.GetSelectedPythonNode(GetCurrentWorkspaceViewModel());
                if (node != null) return PythonNodeInterop.GetEngineName(node);
            }
            catch { }
            return _currentSession.PythonEngine;
        }

        private string GetCurrentGraphPath()
        {
            try { return _dynParams.CurrentWorkspaceModel?.FileName ?? string.Empty; }
            catch { return string.Empty; }
        }

        private object? GetDynamoViewModel()
        {
            // DynamoViewModel is a private field in ViewLoadedParams — must use NonPublic flags
            var field = _dynParams.GetType()
                .GetField("dynamoViewModel",
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
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
