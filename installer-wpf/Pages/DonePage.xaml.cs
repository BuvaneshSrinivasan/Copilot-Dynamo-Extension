using System.Windows;
using System.Windows.Controls;

namespace DynamoCopilot.Installer.Pages;

public partial class DonePage : UserControl
{
    public DonePage() => InitializeComponent();

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
        => Application.Current.Shutdown();
}
