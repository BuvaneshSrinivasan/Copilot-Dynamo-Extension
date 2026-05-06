using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DynamoCopilot.Core.Services;

namespace DynamoCopilot.Extension.ViewModels
{
    public sealed class AuthFormViewModel : INotifyPropertyChanged
    {
        private readonly AuthService _authService;

        private bool   _isRegisterMode;
        private bool   _isAuthBusy;
        private string _authError     = string.Empty;
        private string _loginEmail    = string.Empty;
        private string _registerEmail = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public AuthFormViewModel(AuthService authService)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
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

        public async Task LoginAsync(string password)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(LoginEmail))  { AuthError = "Please enter your email.";    return; }
            if (string.IsNullOrWhiteSpace(password))    { AuthError = "Please enter your password."; return; }

            IsAuthBusy = true;
            AuthError  = string.Empty;
            var result = await _authService.LoginAsync(LoginEmail.Trim(), password);
            IsAuthBusy = false;

            if (!result.Success) AuthError = result.ErrorMessage ?? "Login failed.";
            // On success GlobalLoggedIn fires → parent panel VM sets IsLoggedIn = true → form hides
        }

        public async Task RegisterAsync(string password, string confirmPassword)
        {
            if (IsAuthBusy) return;
            if (string.IsNullOrWhiteSpace(RegisterEmail)) { AuthError = "Please enter your email.";                 return; }
            if (string.IsNullOrWhiteSpace(password))      { AuthError = "Please enter a password.";                return; }
            if (password.Length < 8)                      { AuthError = "Password must be at least 8 characters."; return; }
            if (password != confirmPassword)              { AuthError = "Passwords do not match.";                  return; }

            IsAuthBusy = true;
            AuthError  = string.Empty;
            var result = await _authService.RegisterAsync(RegisterEmail.Trim(), password);
            IsAuthBusy = false;

            if (!result.Success) AuthError = result.ErrorMessage ?? "Registration failed.";
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
