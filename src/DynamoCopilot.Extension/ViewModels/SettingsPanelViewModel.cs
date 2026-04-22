using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DynamoCopilot.Core.Models;
using DynamoCopilot.Core.Settings;
using DynamoCopilot.Extension.Commands;
using RelayCommand = DynamoCopilot.Extension.Commands.RelayCommand;

namespace DynamoCopilot.Extension.ViewModels
{
    public sealed class SettingsPanelViewModel : INotifyPropertyChanged
    {
        private readonly DynamoCopilotSettings _settings;

        // Working copies — committed to settings only when Save is clicked
        private AiProvider _selectedProvider;
        private string     _apiKey    = string.Empty;
        private string     _modelName = string.Empty;
        private string     _ollamaUrl = string.Empty;

        // Unsaved per-provider keys accumulated during this session
        private readonly Dictionary<string, string> _workingKeys = new();

        private string _statusMessage = string.Empty;
        private bool   _isBusy;

        public SettingsPanelViewModel(DynamoCopilotSettings settings)
        {
            _settings = settings;
            LoadFromSettings();

            SaveCommand        = new AsyncRelayCommand(SaveAsync, CanSave);
            TestCommand        = new AsyncRelayCommand(TestConnectionAsync, CanTest);
            ResetModelCommand  = new RelayCommand(ResetModel);
        }

        // ── Bindable providers list ───────────────────────────────────────────

        public AiProvider[] Providers { get; } =
            (AiProvider[])Enum.GetValues(typeof(AiProvider));

        // ── Properties ───────────────────────────────────────────────────────

        public AiProvider SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (_selectedProvider == value) return;

                // Save current key before switching
                _workingKeys[_selectedProvider.ToString()] = _apiKey;

                _selectedProvider = value;

                // Autofill the saved key for the new provider
                _apiKey = _workingKeys.TryGetValue(value.ToString(), out var k) ? k : string.Empty;

                OnPropertyChanged();
                OnPropertyChanged(nameof(ApiKey));
                OnPropertyChanged(nameof(IsOllamaSelected));
                OnPropertyChanged(nameof(NeedsApiKey));
                OnPropertyChanged(nameof(ApiKeyLabel));
                OnPropertyChanged(nameof(ModelPlaceholder));
                StatusMessage = string.Empty;
            }
        }

        public string ApiKey
        {
            get => _apiKey;
            set
            {
                _apiKey = value;
                _workingKeys[_selectedProvider.ToString()] = value;
                OnPropertyChanged();
                StatusMessage = string.Empty;
            }
        }

        public string ModelName
        {
            get => _modelName;
            set { _modelName = value; OnPropertyChanged(); StatusMessage = string.Empty; }
        }

        public string OllamaUrl
        {
            get => _ollamaUrl;
            set { _ollamaUrl = value; OnPropertyChanged(); StatusMessage = string.Empty; }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStatus));
            }
        }

        public bool HasStatus  => !string.IsNullOrEmpty(_statusMessage);
        public bool IsBusy     => _isBusy;
        public bool IsNotBusy  => !_isBusy;

        // ── Computed display helpers ──────────────────────────────────────────

        public bool IsOllamaSelected => _selectedProvider == AiProvider.Ollama;

        /// <summary>Ollama doesn't need an API key.</summary>
        public bool NeedsApiKey => _selectedProvider != AiProvider.Ollama;

        public string ApiKeyLabel => _selectedProvider switch
        {
            AiProvider.OpenAI   => "OpenAI API Key",
            AiProvider.Gemini   => "Gemini API Key",
            AiProvider.Claude   => "Anthropic API Key",
            AiProvider.DeepSeek => "DeepSeek API Key",
            _                   => "API Key"
        };

        public string ModelPlaceholder =>
            DynamoCopilotSettings.DefaultModelFor(_selectedProvider);

        // ── AI config expand / collapse ───────────────────────────────────────

        private bool _isAiConfigExpanded = true;

        public bool IsAiConfigExpanded
        {
            get => _isAiConfigExpanded;
            set { _isAiConfigExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(AiConfigArrow)); }
        }

        public string AiConfigArrow => _isAiConfigExpanded ? "▼" : "▶";

        public void ToggleAiConfig() => IsAiConfigExpanded = !IsAiConfigExpanded;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand SaveCommand       { get; }
        public ICommand TestCommand       { get; }
        public ICommand ResetModelCommand { get; }

        private bool CanSave()  => !_isBusy;
        private bool CanTest()  => !_isBusy;

        private System.Threading.Tasks.Task SaveAsync()
        {
            // Flush all per-provider keys collected this session
            foreach (var kv in _workingKeys)
            {
                if (!string.IsNullOrWhiteSpace(kv.Value))
                    _settings.ApiKeys[kv.Key] = kv.Value.Trim();
            }

            _settings.AiProvider  = _selectedProvider;
            _settings.ApiKey      = _apiKey.Trim();   // active provider's key (kept in sync)
            _settings.ModelName   = _modelName.Trim();
            _settings.OllamaUrl   = string.IsNullOrWhiteSpace(_ollamaUrl)
                ? "http://localhost:11434"
                : _ollamaUrl.Trim();
            _settings.Save();

            StatusMessage = "Settings saved.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private async System.Threading.Tasks.Task TestConnectionAsync()
        {
            SetBusy(true);
            StatusMessage = "Testing connection…";
            try
            {
                var tempSettings = new DynamoCopilotSettings
                {
                    AiProvider  = _selectedProvider,
                    ApiKey      = _apiKey.Trim(),
                    ModelName   = string.IsNullOrWhiteSpace(_modelName)
                        ? DynamoCopilotSettings.DefaultModelFor(_selectedProvider)
                        : _modelName.Trim(),
                    OllamaUrl   = string.IsNullOrWhiteSpace(_ollamaUrl)
                        ? "http://localhost:11434"
                        : _ollamaUrl.Trim()
                };

                var svc = Core.Services.LlmServiceFactory.Create(tempSettings);
                if (!svc.IsConfigured(out var reason))
                {
                    StatusMessage = reason;
                    return;
                }

                // Send a minimal 1-token probe
                var probe = new[] { new Core.Models.ChatMessage
                {
                    Role    = Core.Models.ChatRole.User,
                    Content = "Reply with the single word OK and nothing else."
                }};

                var ct  = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromSeconds(20)).Token;
                var got = false;

                await foreach (var token in svc.SendStreamingAsync(probe, ct))
                {
                    got = true;
                    break;   // one token is enough to confirm connectivity
                }

                StatusMessage = got
                    ? "Connection successful!"
                    : "Connected but received no response.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ResetModel()
        {
            ModelName = string.Empty;
            OnPropertyChanged(nameof(ModelPlaceholder));
        }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Raised after the user clicks Save so the parent VM can rebuild its LLM service.</summary>
        public event EventHandler? SettingsSaved;

        // ── Helpers ───────────────────────────────────────────────────────────

        private void LoadFromSettings()
        {
            _selectedProvider = _settings.AiProvider;
            _modelName        = _settings.ModelName ?? string.Empty;
            _ollamaUrl        = string.IsNullOrWhiteSpace(_settings.OllamaUrl)
                ? "http://localhost:11434"
                : _settings.OllamaUrl;

            // Seed working keys from persisted per-provider keys
            foreach (var kv in _settings.ApiKeys)
                _workingKeys[kv.Key] = kv.Value;

            // Active provider's key
            _apiKey = _workingKeys.TryGetValue(_selectedProvider.ToString(), out var k)
                ? k
                : _settings.ApiKey ?? string.Empty;
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsNotBusy));
            (SaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

}
