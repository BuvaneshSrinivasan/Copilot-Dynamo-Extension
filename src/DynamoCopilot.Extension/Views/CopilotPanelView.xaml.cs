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

        public CopilotPanelView(CopilotPanelViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;

            _viewModel.RequestScrollToBottom = ScrollToBottom;
            _viewModel.Messages.CollectionChanged += (_, _) => ScrollToBottom();
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

        // ── User info ─────────────────────────────────────────────────────────

        private void OnUserIconClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleUserInfo();

        private void OnSignOutClick(object sender, RoutedEventArgs e)
        {
            _viewModel.Logout();
            // Clear password boxes so they don't retain values after sign-out
            LoginPasswordBox.Clear();
            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
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

        // ── Scroll helper ─────────────────────────────────────────────────────

        private void ScrollToBottom()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(ScrollToBottom);
                return;
            }
            ChatScrollViewer.ScrollToBottom();
        }
    }
}
