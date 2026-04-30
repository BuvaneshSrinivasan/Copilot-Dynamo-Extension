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
        private string     _apiKey    = string.Empty;   // empty / unused for Ollama
        private string     _modelName = string.Empty;
        private string     _ollamaUrl = string.Empty;

        // Per-provider working state: provider name → (model, apiKey).
        // Ollama is excluded; its model is tracked in _modelName when it is selected,
        // and its URL is always in _ollamaUrl.
        private readonly Dictionary<string, (string Model, string ApiKey)> _workingProviders = new();

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

                // Persist working state for the provider we are leaving
                if (_selectedProvider == AiProvider.Ollama)
                    _workingProviders[_selectedProvider.ToString()] = (_modelName, string.Empty);
                else
                    _workingProviders[_selectedProvider.ToString()] = (_modelName, _apiKey);

                _selectedProvider = value;

                // Restore working state for the new provider
                if (_workingProviders.TryGetValue(value.ToString(), out var state))
                {
                    _modelName = string.IsNullOrWhiteSpace(state.Model)
                        ? DynamoCopilotSettings.DefaultModelFor(value)
                        : state.Model;
                    _apiKey    = value == AiProvider.Ollama ? string.Empty : state.ApiKey;
                }
                else
                {
                    _modelName = DynamoCopilotSettings.DefaultModelFor(value);
                    _apiKey    = string.Empty;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(ApiKey));
                OnPropertyChanged(nameof(ModelName));
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
                if (_selectedProvider != AiProvider.Ollama)
                    _workingProviders[_selectedProvider.ToString()] = (_modelName, value);
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


        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand SaveCommand       { get; }
        public ICommand TestCommand       { get; }
        public ICommand ResetModelCommand { get; }

        private bool CanSave()  => !_isBusy;
        private bool CanTest()  => !_isBusy;

        private System.Threading.Tasks.Task SaveAsync()
        {
            // Flush current working state into the working dict before saving
            if (_selectedProvider == AiProvider.Ollama)
                _workingProviders[_selectedProvider.ToString()] = (_modelName, string.Empty);
            else
                _workingProviders[_selectedProvider.ToString()] = (_modelName, _apiKey);

            // Persist all accumulated per-provider working state
            foreach (var kv in _workingProviders)
            {
                if (!Enum.TryParse<AiProvider>(kv.Key, out var prov)) continue;

                if (prov == AiProvider.Ollama)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Value.Model))
                        _settings.Ollama.Model = kv.Value.Model.Trim();
                }
                else
                {
                    var model  = kv.Value.Model.Trim();
                    var apiKey = kv.Value.ApiKey.Trim();
                    if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(apiKey))
                        _settings.SetProvider(prov, model, apiKey);
                }
            }

            _settings.AiProvider = _selectedProvider;
            _settings.Ollama.Url = string.IsNullOrWhiteSpace(_ollamaUrl)
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
                    AiProvider = _selectedProvider,
                    Ollama = new Core.Settings.OllamaConfig
                    {
                        Model = string.IsNullOrWhiteSpace(_modelName)
                            ? DynamoCopilotSettings.DefaultModelFor(_selectedProvider)
                            : _modelName.Trim(),
                        Url = string.IsNullOrWhiteSpace(_ollamaUrl)
                            ? "http://localhost:11434"
                            : _ollamaUrl.Trim()
                    }
                };

                if (_selectedProvider != AiProvider.Ollama)
                {
                    tempSettings.SetProvider(
                        _selectedProvider,
                        string.IsNullOrWhiteSpace(_modelName)
                            ? DynamoCopilotSettings.DefaultModelFor(_selectedProvider)
                            : _modelName.Trim(),
                        _apiKey.Trim());
                }

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
            ModelName = DynamoCopilotSettings.DefaultModelFor(_selectedProvider);
        }

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Raised after the user clicks Save so the parent VM can rebuild its LLM service.</summary>
        public event EventHandler? SettingsSaved;

        // ── Helpers ───────────────────────────────────────────────────────────

        private void LoadFromSettings()
        {
            _selectedProvider = _settings.AiProvider;
            _ollamaUrl        = string.IsNullOrWhiteSpace(_settings.Ollama.Url)
                ? "http://localhost:11434"
                : _settings.Ollama.Url;

            // Seed working dict from persisted providers
            _workingProviders[AiProvider.Ollama.ToString()] =
                (_settings.Ollama.Model, string.Empty);

            foreach (var kv in _settings.Providers)
            {
                if (Enum.TryParse<AiProvider>(kv.Key, out _))
                    _workingProviders[kv.Key] = (kv.Value.Model, kv.Value.ApiKey);
            }

            // Active provider's working values
            if (_workingProviders.TryGetValue(_selectedProvider.ToString(), out var active))
            {
                _modelName = string.IsNullOrWhiteSpace(active.Model)
                    ? DynamoCopilotSettings.DefaultModelFor(_selectedProvider)
                    : active.Model;
                _apiKey    = _selectedProvider == AiProvider.Ollama ? string.Empty : active.ApiKey;
            }
            else
            {
                _modelName = DynamoCopilotSettings.DefaultModelFor(_selectedProvider);
                _apiKey    = string.Empty;
            }
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
