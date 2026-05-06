using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamoCopilot.Extension.ViewModels;

namespace DynamoCopilot.Extension.Views
{
    public partial class SuggestNodesPanelView : UserControl
    {
        private readonly SuggestNodesPanelViewModel _viewModel;

        public SuggestNodesPanelView(SuggestNodesPanelViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new System.ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _viewModel;
        }

        // ── User info ─────────────────────────────────────────────────────────

        private void OnUserInfoClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleUserPanel();

        private void OnSignOutClick(object sender, RoutedEventArgs e)
            => _viewModel.Logout();

        // ── Group toggles ─────────────────────────────────────────────────────

        private void OnToggleInstalledGroupClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleInstalledGroup();

        private void OnToggleOnlineGroupClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleOnlineGroup();

        // ── Node search ───────────────────────────────────────────────────────

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
    }
}
