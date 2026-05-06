using System.Windows;
using System.Windows.Input;
using DynamoCopilot.Extension.ViewModels;

namespace DynamoCopilot.Extension.Views
{
    public partial class AuthFormView : System.Windows.Controls.UserControl
    {
        public AuthFormView()
        {
            InitializeComponent();
        }

        private AuthFormViewModel? ViewModel => DataContext as AuthFormViewModel;

        private async void OnLoginClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.LoginAsync(LoginPasswordBox.Password);
        }

        private async void OnRegisterClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                await ViewModel.RegisterAsync(RegisterPasswordBox.Password, RegisterConfirmPasswordBox.Password);
        }

        private void OnSwitchToRegister(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel != null) ViewModel.IsRegisterMode = true;
            LoginPasswordBox.Clear();
        }

        private void OnSwitchToLogin(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel != null) ViewModel.IsRegisterMode = false;
            RegisterPasswordBox.Clear();
            RegisterConfirmPasswordBox.Clear();
        }
    }
}
