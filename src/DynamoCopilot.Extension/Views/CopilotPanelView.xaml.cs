using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamoCopilot.Extension.ViewModels;

namespace DynamoCopilot.Extension.Views
{
    /// <summary>
    /// Code-behind for the Copilot chat panel.
    /// Pure WPF — no WebView2 dependency.
    /// </summary>
    public partial class CopilotPanelView : UserControl
    {
        private readonly CopilotPanelViewModel _viewModel;

        public CopilotPanelView(CopilotPanelViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;

            // Wire scroll-to-bottom callback
            _viewModel.RequestScrollToBottom = ScrollToBottom;

            // Auto-scroll when items change
            _viewModel.Messages.CollectionChanged += (_, _) => ScrollToBottom();
        }

        // ── Send / Stop ───────────────────────────────────────────────────

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel.IsStreaming)
            {
                _viewModel.CancelStreaming();
                return;
            }
            await SendInputAsync();
        }

        private async void OnInputKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Shift+Enter → manual newline
                    var tb = (TextBox)sender;
                    int pos = tb.CaretIndex;
                    tb.Text = tb.Text.Insert(pos, "\n");
                    tb.CaretIndex = pos + 1;
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    // Enter → send (handled before TextBox processes it via PreviewKeyDown)
                    e.Handled = true;
                    if (!_viewModel.IsStreaming)
                        await SendInputAsync();
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

        // ── Code block buttons ────────────────────────────────────────────

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

        // ── Fix Python Error ──────────────────────────────────────────────

        private async void OnFixErrorClick(object sender, RoutedEventArgs e)
            => await _viewModel.FixPythonErrorAsync();

        // ── Clear ─────────────────────────────────────────────────────────

        private void OnClearClick(object sender, RoutedEventArgs e)
            => _viewModel.ClearHistory();

        // ── Settings ──────────────────────────────────────────────────────

        private void OnSettingsClick(object sender, RoutedEventArgs e)
            => _viewModel.OpenSettings();

        private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
            => _viewModel.SaveSettings();

        private void OnCancelSettingsClick(object sender, RoutedEventArgs e)
            => _viewModel.CloseSettings();

        // ── Example prompts ───────────────────────────────────────────────

        private async void OnExampleClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string prompt)
            {
                InputBox.Text = prompt;
                await SendInputAsync();
            }
        }

        // ── Scroll helper ─────────────────────────────────────────────────

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
