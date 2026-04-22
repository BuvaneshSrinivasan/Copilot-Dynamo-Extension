using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamoCopilot.Extension.ViewModels;

namespace DynamoCopilot.Extension.Views
{
    public partial class CopilotPanelView : UserControl
    {
        private readonly CopilotPanelViewModel _viewModel;
        private bool _suppressApiKeyUpdate;

        public CopilotPanelView(CopilotPanelViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.RequestScrollToBottom = ScrollToBottom;
            _viewModel.Messages.CollectionChanged += (_, _) => ScrollToBottom();

            // Populate ApiKeyBox on load and whenever the provider changes
            ApiKeyBox.Password = _viewModel.SettingsVm.ApiKey;
            _viewModel.SettingsVm.PropertyChanged += OnSettingsVmPropertyChanged;
        }

        // ── Auth ──────────────────────────────────────────────────────────────

        private async void OnLoginClick(object sender, RoutedEventArgs e)
            => await _viewModel.LoginAsync(LoginPasswordBox.Password);

        private async void OnRegisterClick(object sender, RoutedEventArgs e)
            => await _viewModel.RegisterAsync(
                   RegisterPasswordBox.Password,
                   RegisterConfirmPasswordBox.Password);

        private void OnSwitchToRegister(object sender, MouseButtonEventArgs e)
        {
            _viewModel.IsRegisterMode = true;
            LoginPasswordBox.Clear();
        }

        private void OnSwitchToLogin(object sender, MouseButtonEventArgs e)
        {
            _viewModel.IsRegisterMode = false;
            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
        }

        // ── Settings (separate AI settings + user info panels) ──────────────────

        private void OnAiSettingsClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleAiPanel();

        private void OnUserInfoClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleUserPanel();

        private void OnSettingsVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsPanelViewModel.ApiKey))
            {
                _suppressApiKeyUpdate = true;
                ApiKeyBox.Password = _viewModel.SettingsVm.ApiKey;
                _suppressApiKeyUpdate = false;
            }
        }

        private void OnApiKeyChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressApiKeyUpdate) return;
            if (sender is System.Windows.Controls.PasswordBox pb)
                _viewModel.SettingsVm.ApiKey = pb.Password;
        }

        private void OnSignOutClick(object sender, RoutedEventArgs e)
        {
            _viewModel.Logout();
            LoginPasswordBox.Clear();
            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
        }

        // ── Mode toggle ───────────────────────────────────────────────────────

        private void OnChatModeClick(object sender, RoutedEventArgs e)
            => _viewModel.SwitchToChat();

        private void OnNodeModeClick(object sender, RoutedEventArgs e)
        {
            _viewModel.SwitchToNodeSuggest();
            // Give focus to the node query box when switching to node mode
            NodeQueryBox.Focus();
        }

        // ── Node suggest ──────────────────────────────────────────────────────

        private async void OnNodeSearchClick(object sender, RoutedEventArgs e)
            => await RunNodeSearchAsync();

        private async void OnNodeQueryKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                await RunNodeSearchAsync();
            }
        }

        private async System.Threading.Tasks.Task RunNodeSearchAsync()
        {
            var query = NodeQueryBox.Text.Trim();
            if (string.IsNullOrEmpty(query)) return;
            await _viewModel.SearchNodesAsync(query);
        }

        // ── Chat ──────────────────────────────────────────────────────────────

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsStreaming) { _viewModel.CancelStreaming(); return; }
            await SendInputAsync();
        }

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    var tb  = (TextBox)sender;
                    int pos = tb.CaretIndex;
                    tb.Text = tb.Text.Insert(pos, "\n");
                    tb.CaretIndex = pos + 1;
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    if (!_viewModel.IsStreaming) await SendInputAsync();
                }
            }
        }

        private async System.Threading.Tasks.Task SendInputAsync()
        {
            var text = InputBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) return;
            InputBox.Clear();
            await _viewModel.SendMessageAsync(text);
        }

        // ── Code block buttons ────────────────────────────────────────────────

        private void OnCopyCodeClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string code)
                _viewModel.CopyToClipboard(code);
        }

        private void OnInsertCodeClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string code)
                _viewModel.InsertCode(code);
        }

        private async void OnFixErrorClick(object sender, RoutedEventArgs e)
            => await _viewModel.FixPythonErrorAsync();

        private void OnClearClick(object sender, RoutedEventArgs e)
            => _viewModel.ClearHistory();

        // ── Example prompts ───────────────────────────────────────────────────

        private async void OnExampleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                InputBox.Text = prompt;
                await SendInputAsync();
            }
        }

        // ── Scroll helpers ────────────────────────────────────────────────────

        private void ScrollToBottom()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(ScrollToBottom);
                return;
            }
            ChatScrollViewer.ScrollToBottom();
        }

        private void OnChatPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ChatScrollViewer.ScrollToVerticalOffset(
                ChatScrollViewer.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
