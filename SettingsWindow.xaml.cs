using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinNetControl;

public sealed partial class SettingsWindow : Window
{
    public static event EventHandler? WidgetSettingsChanged;

    public string? AppVersion { get; set; }
    public string? BuildDate { get; set; }

    public ViewModels.MainViewModel ViewModel { get; }

    public SettingsWindow(ViewModels.MainViewModel viewModel)
    {
        ViewModel = viewModel;
        LoadVersionInfo();
        this.InitializeComponent();
        WinUIEx.WindowExtensions.SetWindowSize(this, 500, 600);
        WinUIEx.WindowExtensions.SetIcon(this, "Assets\\AppIcon.ico");
    }

    private void LoadVersionInfo()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        AppVersion = asm.GetName().Version?.ToString() ?? "Unknown";
        var filePath = asm.Location;
        if (System.IO.File.Exists(filePath))
        {
            BuildDate = System.IO.File.GetLastWriteTime(filePath).ToString("yyyy-MM-dd HH:mm:ss");
        }
        else
        {
            BuildDate = "Unknown";
        }
    }

    private void OnInstallDependencies(object sender, RoutedEventArgs e)
    {
        DependencyStatusText.Text = "Dependencies are already bundled or not required for WinDivert.";
        // We could implement Npcap/Titanium proxy cert installation here if needed
    }

    private void OnWidgetSettingChanged(object sender, object e)
    {
        if (ViewModel != null)
        {
            ViewModel.SaveConfig();
        }
        WidgetSettingsChanged?.Invoke(null, EventArgs.Empty);
    }

    private void OnInstallCertificateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel?.ProxyService?.InstallCertificate();
            ShowDialog("Success", "Certificate installed successfully.");
        }
        catch (Exception ex)
        {
            ShowDialog("Error", "Failed to install certificate: " + ex.Message);
        }
    }

    private void OnUninstallCertificateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel?.ProxyService?.UninstallCertificate();
            ShowDialog("Success", "Certificate uninstalled successfully.");
        }
        catch (Exception ex)
        {
            ShowDialog("Error", "Failed to uninstall certificate: " + ex.Message);
        }
    }

    private async void ShowDialog(string title, string content)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
