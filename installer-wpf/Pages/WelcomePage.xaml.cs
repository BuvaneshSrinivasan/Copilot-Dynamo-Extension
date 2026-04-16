using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace DynamoCopilot.Installer.Pages;

public partial class WelcomePage : UserControl
{
    private readonly MainWindow _main;

    public WelcomePage(MainWindow main)
    {
        InitializeComponent();
        _main = main;

        // Version and company come from the csproj <Version> and <Company> properties
        var asm     = Assembly.GetExecutingAssembly();
        var ver     = asm.GetName().Version;
        var company = System.Diagnostics.FileVersionInfo
                           .GetVersionInfo(asm.Location).CompanyName;

        if (ver is not null)
            VersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}"
                             + (string.IsNullOrWhiteSpace(company) ? "" : $" · {company}");
    }

    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        _main.NavigateToInstalling();
    }
}
