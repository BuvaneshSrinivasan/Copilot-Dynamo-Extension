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

        // Read from WIN32 file-version resource (set via <FileVersion> in the .csproj).
        // This survives GenerateAssemblyInfo=false which is required to avoid duplicate
        // managed attributes when building with RuntimeIdentifier + PublishSingleFile.
        var fvi = System.Diagnostics.FileVersionInfo
                       .GetVersionInfo(Environment.ProcessPath ?? string.Empty);

        var ver     = fvi.FileVersion ?? "0.0.0";
        var parts   = ver.Split('.');
        var display = parts.Length >= 3 ? $"{parts[0]}.{parts[1]}.{parts[2]}" : ver;
        var company = fvi.CompanyName ?? "";

        VersionText.Text = $"v{display}" + (string.IsNullOrWhiteSpace(company) ? "" : $" · {company}");
    }

    private void InstallBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallBtn.IsEnabled = false;
        _main.NavigateToInstalling();
    }
}
