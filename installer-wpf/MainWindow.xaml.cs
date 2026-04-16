using System.Windows;
using System.Windows.Input;
using DynamoCopilot.Installer.Pages;

namespace DynamoCopilot.Installer;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowPage(new WelcomePage(this));
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void NavigateToInstalling()
    {
        var page = new InstallingPage(this, _engine);
        ShowPage(page);
        page.BeginInstall();
    }

    public void NavigateToDone()
    {
        ShowPage(new DonePage());
    }

    private void ShowPage(UIElement page) => PageHost.Content = page;

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => DragMove();

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();
}
