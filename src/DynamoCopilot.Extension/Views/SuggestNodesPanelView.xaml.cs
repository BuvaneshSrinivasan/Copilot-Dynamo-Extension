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

        private void OnUserInfoClick(object sender, RoutedEventArgs e)
            => _viewModel.ToggleUserPanel();

        private void OnSignOutClick(object sender, RoutedEventArgs e)
        {
            _viewModel.Logout();
            LoginPasswordBox.Clear();
            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
        }

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
