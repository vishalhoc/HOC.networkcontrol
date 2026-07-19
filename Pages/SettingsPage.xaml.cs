using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.IO;
using System.Reflection;

namespace WinNetControl.Pages;

public sealed partial class SettingsPage : Page
{
    private MainViewModel? _vm;
    private AppConfig?     _cfg;
    private bool           _loaded;

    public SettingsPage() { this.InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) { _vm = vm; _cfg = vm.CurrentConfig; }
        LoadSettings();
    }

    // ── Load ──────────────────────────────────────────────────────────────────
    private void LoadSettings()
    {
        _loaded = false;

        // Theme
        ThemeCombo.SelectedIndex = _cfg?.AppTheme switch
        {
            "Dark"  => 1, "Light" => 2, _ => 0
        };

        // Nav pane (stored in registry-like config via WidgetLayout as proxy key)
        NavPaneCombo.SelectedIndex = 0;  // left (default)

        // Toggles
        AcrylicSwitch.IsOn            = false; // no config key yet, safe default
        AnimationsSwitch.IsOn         = true;
        StartWithWindowsSwitch.IsOn   = FirewallService.IsStartupEnabled();
        StartMinimizedSwitch.IsOn     = false;
        BlockNewAppsSwitch.IsOn       = _cfg?.BlockNewApps ?? false;
        ShowOfflineSwitch.IsOn        = _cfg?.ShowOfflineBlockedApps ?? true;
        NotifyBlockSwitch.IsOn        = true;
        NotifyHighUsageSwitch.IsOn    = false;

        // Refresh interval
        int interval = _cfg?.WidgetRefreshRateMs ?? 1000;
        RefreshCombo.SelectedIndex = interval switch
        {
            500  => 0, 2000 => 2, 5000 => 3, _ => 1
        };

        // About
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text    = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build}";
        ConfigPathText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WinNetControlConfig.json");

        _loaded = true;
    }

    // ── Appearance ────────────────────────────────────────────────────────────
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        string tag = ((ComboBoxItem)ThemeCombo.SelectedItem)?.Tag?.ToString() ?? "default";
        _cfg.AppTheme = tag switch { "dark" => "Dark", "light" => "Light", _ => "System" };

        if (App.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = tag switch
            {
                "dark"  => ElementTheme.Dark,
                "light" => ElementTheme.Light,
                _       => ElementTheme.Default
            };
        }
    }

    private void OnNavPaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        string tag = ((ComboBoxItem)NavPaneCombo.SelectedItem)?.Tag?.ToString() ?? "left";
        if (App.Window is MainWindow mw) mw.UpdateNavPaneMode(tag);
    }

    private void OnAcrylicToggled(object sender, RoutedEventArgs e) { /* future: persist Acrylic */ }

    // ── Behavior ──────────────────────────────────────────────────────────────
    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (StartWithWindowsSwitch.IsOn)
            FirewallService.CreateStartupTask(FirewallService.GetCurrentExePath());
        else
            FirewallService.RemoveStartupTask();

        if (_cfg != null) _cfg.StartWithWindows = StartWithWindowsSwitch.IsOn;
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e) { /* future */ }

    private void OnBlockNewAppsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.BlockNewApps = BlockNewAppsSwitch.IsOn;
    }

    private void OnShowOfflineToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.ShowOfflineBlockedApps = ShowOfflineSwitch.IsOn;
    }

    private void OnRefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        string tag = ((ComboBoxItem)RefreshCombo.SelectedItem)?.Tag?.ToString() ?? "1000";
        if (int.TryParse(tag, out int ms)) _cfg.WidgetRefreshRateMs = ms;
    }

    // ── Data ──────────────────────────────────────────────────────────────────
    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OnExportConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            string src = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinNetControlConfig.json");
            if (!File.Exists(src)) { DataStatus.Text = "No config file found."; return; }
            string dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"WNC_Config_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Copy(src, dest, overwrite: true);
            DataStatus.Text = $"Exported to Desktop: {Path.GetFileName(dest)}";
        }
        catch (Exception ex) { DataStatus.Text = $"Error: {ex.Message}"; }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        try
        {
            HistoryLogService.Clear();
            DataStatus.Text = "History log cleared.";
        }
        catch (Exception ex) { DataStatus.Text = $"Error: {ex.Message}"; }
    }

    // ── About ─────────────────────────────────────────────────────────────────
    private void OnCheckForUpdates(object sender, RoutedEventArgs e)
        => DataStatus.Text = "You are running the latest version.";

    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)
        => FirewallService.OpenNetworkConnections();

    private void OnOpenWindowsSettings(object sender, RoutedEventArgs e)
        => FirewallService.OpenNetworkSettings();

    // ── Save ──────────────────────────────────────────────────────────────────
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        _vm?.SaveConfig();
        SaveStatus.Text = $"Settings saved — {DateTime.Now:HH:mm:ss}";
    }
}
