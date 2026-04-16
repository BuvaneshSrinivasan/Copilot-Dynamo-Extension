using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace DynamoCopilot.Installer.Pages;

public partial class InstallingPage : UserControl
{
    private readonly MainWindow    _main;
    private readonly InstallerEngine _engine;

    public InstallingPage(MainWindow main, InstallerEngine engine)
    {
        InitializeComponent();
        _main   = main;
        _engine = engine;
    }

    public async void BeginInstall()
    {
        var progress = new Progress<InstallStep>(step =>
        {
            StatusText.Text  = step.Status;
            PercentText.Text = $"{step.Percent}%";
            AnimateBar(step.Percent);
        });

        try
        {
            await _engine.RunAsync(progress);
            _main.NavigateToDone();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Installation failed:\n\n{ex.Message}",
                "DynamoCopilot Setup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current.Shutdown(1);
        }
    }

    private void AnimateBar(double target)
    {
        var anim = new DoubleAnimation(ProgressBar.Value, target,
            TimeSpan.FromMilliseconds(350))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }
}
