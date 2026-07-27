using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Win32;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpPcap;

namespace WinNetControl.Pages;

public sealed partial class SettingsPage : Page
{
    private MainViewModel? _vm;
    private AppConfig?     _cfg;
    private bool           _loaded;

    // GitHub repo for update checks
    private const string GitHubOwner = "vishalhoc";
    private const string GitHubRepo  = "HOC.networkcontrol";
    private static readonly HttpClient _http = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "HOC-NetworkControl-UpdateChecker/1.0" } },
        Timeout = TimeSpan.FromSeconds(10)
    };

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
            "Dark" => 1, "Light" => 2, _ => 0
        };

        // Nav pane
        NavPaneCombo.SelectedIndex = 0;

        // Appearance toggles
        AcrylicSwitch.IsOn    = _cfg?.EnableAcrylic    ?? false;
        AnimationsSwitch.IsOn = _cfg?.EnableAnimations ?? true;

        // Behavior
        StartWithWindowsSwitch.IsOn = FirewallService.IsStartupEnabled();
        StartMinimizedSwitch.IsOn   = _cfg?.StartMinimized     ?? false;
        BlockNewAppsSwitch.IsOn     = _cfg?.BlockNewApps        ?? false;
        ShowOfflineSwitch.IsOn      = _cfg?.ShowOfflineBlockedApps ?? true;

        // Widget
        double opacity = _cfg?.WidgetOpacity ?? 85.0;
        OpacitySlider.Value      = opacity;
        OpacityValueRun.Text     = $"{opacity:F0}%";

        double fontSize = _cfg?.WidgetFontSize ?? 14.0;
        FontSizeSlider.Value     = fontSize;
        FontSizeValueRun.Text    = $"{fontSize:F0} pt";

        WidgetWidthBox.Value     = _cfg?.WidgetWidth  ?? 280;
        WidgetHeightBox.Value    = _cfg?.WidgetHeight ?? 120;

        WidgetLayoutCombo.SelectedIndex = (_cfg?.WidgetLayout ?? "Vertical") switch
        {
            "Horizontal" => 1, "Compact" => 2, _ => 0
        };

        DisableTransparencySwitch.IsOn = _cfg?.WidgetDisableTransparency ?? false;

        int interval = _cfg?.WidgetRefreshRateMs ?? 1000;
        RefreshCombo.SelectedIndex = interval switch
        {
            500 => 0, 2000 => 2, 5000 => 3, _ => 1
        };

        // Notifications
        NotifyBlockSwitch.IsOn     = _cfg?.NotifyOnBlock      ?? true;
        NotifyHighUsageSwitch.IsOn = _cfg?.NotifyOnHighUsage  ?? false;
        NotifyQosSwitch.IsOn       = _cfg?.NotifyOnQos        ?? false;
        ThresholdBox.Value         = _cfg?.BandwidthThresholdMBps ?? 10.0;

        // About
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text     = $"v{ver?.Major}.{ver?.Minor}.{ver?.Build}";

        string cfgPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "WinNetControlConfig.json");
        ConfigPathText.Text  = cfgPath;
        ConfigPathAbout.Text = cfgPath;

        // Check if Npcap driver is installed
        CheckNpcapStatus();

        // API keys
        if (VtApiKeyBox != null)
            VtApiKeyBox.Password = _cfg?.VirusTotalApiKey ?? string.Empty;

        _loaded = true;
    }


    // ── Appearance ────────────────────────────────────────────────────────────
    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        string tag = ((ComboBoxItem)ThemeCombo.SelectedItem)?.Tag?.ToString() ?? "default";
        _cfg.AppTheme = tag switch { "dark" => "Dark", "light" => "Light", _ => "System" };

        // Delegate to the centralised ApplyTheme which also re-colours the
        // NavView pane background (fixes Bug#23 — theme was not applied to
        // the sidebar when changed from Settings).
        App.MainWindow?.ApplyTheme(_cfg.AppTheme);
    }

    private void OnNavPaneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        string tag = ((ComboBoxItem)NavPaneCombo.SelectedItem)?.Tag?.ToString() ?? "left";
        if (App.MainWindow is MainWindow mw) mw.UpdateNavPaneMode(tag);
    }

    private void OnAcrylicToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.EnableAcrylic = AcrylicSwitch.IsOn;
    }

    private void OnAnimationsToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.EnableAnimations = AnimationsSwitch.IsOn;
    }

    // ── Widget Customization ───────────────────────────────────────────────────
    private void OnOpacityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        double v = OpacitySlider.Value;
        OpacityValueRun.Text = $"{v:F0}%";
        if (_cfg != null) _cfg.WidgetOpacity = v;
    }

    private void OnFontSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_loaded) return;
        double v = FontSizeSlider.Value;
        FontSizeValueRun.Text = $"{v:F0} pt";
        if (_cfg != null) _cfg.WidgetFontSize = v;
    }

    private void OnWidgetSizeChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.WidgetWidth  = double.IsNaN(WidgetWidthBox.Value)  ? 280 : WidgetWidthBox.Value;
        _cfg.WidgetHeight = double.IsNaN(WidgetHeightBox.Value) ? 120 : WidgetHeightBox.Value;
    }

    private void OnWidgetLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        string tag = ((ComboBoxItem)WidgetLayoutCombo.SelectedItem)?.Tag?.ToString() ?? "Vertical";
        _cfg.WidgetLayout = tag;
    }

    private void OnDisableTransparencyToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.WidgetDisableTransparency = DisableTransparencySwitch.IsOn;
    }

    private void OnRefreshIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        string tag = ((ComboBoxItem)RefreshCombo.SelectedItem)?.Tag?.ToString() ?? "1000";
        if (int.TryParse(tag, out int ms)) _cfg.WidgetRefreshRateMs = ms;
    }

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

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.StartMinimized = StartMinimizedSwitch.IsOn;
    }

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

    // ── Notifications ─────────────────────────────────────────────────────────
    private void OnNotifyBlockToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.NotifyOnBlock = NotifyBlockSwitch.IsOn;
    }

    private void OnNotifyHighUsageToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.NotifyOnHighUsage = NotifyHighUsageSwitch.IsOn;
    }

    private void OnNotifyQosToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        _cfg.NotifyOnQos = NotifyQosSwitch.IsOn;
    }

    private void OnThresholdChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_loaded || _cfg == null) return;
        if (!double.IsNaN(ThresholdBox.Value))
            _cfg.BandwidthThresholdMBps = ThresholdBox.Value;
    }

    // ── Data & Storage ────────────────────────────────────────────────────────
    private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
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
            DataStatus.Text = $"✓ Exported to Desktop: {Path.GetFileName(dest)}";
        }
        catch (Exception ex) { DataStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void OnImportConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd   = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeFilter.Add(".json");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            string json = await Windows.Storage.FileIO.ReadTextAsync(file);
            var imported = JsonSerializer.Deserialize<AppConfig>(json);
            if (imported == null) { DataStatus.Text = "Invalid config file."; return; }

            string dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WinNetControlConfig.json");
            await Windows.Storage.FileIO.WriteTextAsync(
                await Windows.Storage.StorageFile.GetFileFromPathAsync(dest), json);

            _cfg = imported;
            if (_vm != null) _vm.CurrentConfig = imported;
            LoadSettings();
            DataStatus.Text = $"✓ Config imported from {file.Name}. Settings reloaded.";
        }
        catch (Exception ex) { DataStatus.Text = $"Import error: {ex.Message}"; }
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        try
        {
            HistoryLogService.Clear();
            DataStatus.Text = "✓ History log cleared.";
        }
        catch (Exception ex) { DataStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void OnResetToDefaults(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title               = "Reset to Defaults?",
            Content             = "All settings will be reset to their factory defaults. Firewall rules and blocked apps are NOT affected.",
            PrimaryButtonText   = "Reset",
            SecondaryButtonText = "Cancel",
            XamlRoot            = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _cfg = new AppConfig();
        if (_vm != null) _vm.CurrentConfig = _cfg;
        LoadSettings();
        _vm?.SaveConfig();
        DataStatus.Text = "✓ Settings reset to defaults.";
    }

    // ── About & Update Checker ────────────────────────────────────────────────
    private async void OnCheckForUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesBtn.IsEnabled = false;
        UpdateStatus.Text         = "Checking GitHub releases…";
        UpdateBanner.Visibility   = Visibility.Collapsed;
        UpdateBadge.Visibility    = Visibility.Collapsed;
        UpdateAvailBadge.Visibility = Visibility.Collapsed;

        try
        {
            string url     = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            string json    = await _http.GetStringAsync(url);
            using var doc  = JsonDocument.Parse(json);
            var root       = doc.RootElement;

            string tagName  = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string htmlUrl  = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            string body     = root.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "";
            string name     = root.TryGetProperty("name",     out var n) ? n.GetString() ?? tagName : tagName;

            // Parse versions — strip leading 'v'
            string cleanTag     = tagName.TrimStart('v', 'V');
            var    currentVer   = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            bool   parsed       = Version.TryParse(cleanTag, out Version? latestVer);

            if (!parsed || latestVer == null)
            {
                UpdateStatus.Text = $"Latest release: {tagName} (could not compare versions automatically)";
                ShowBanner($"Latest: {name}", tagName, htmlUrl, isUpdate: true);
                return;
            }

            if (latestVer > currentVer)
            {
                // New version available
                UpdateAvailBadge.Visibility = Visibility.Visible;
                UpdateAvailText.Text        = $"Update available: {tagName}";
                UpdateStatus.Text           = $"🆕 Version {tagName} is available! You have v{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}.";
                string preview = body.Length > 200 ? body[..200] + "…" : body;
                ShowBanner($"Update available — {name}", preview, htmlUrl, isUpdate: true);
            }
            else
            {
                // Up to date
                UpdateBadge.Visibility  = Visibility.Visible;
                UpdateBadgeText.Text    = "✓ Up to date";
                UpdateStatus.Text       = $"✓ You are on the latest version (v{currentVer.Major}.{currentVer.Minor}.{currentVer.Build}).";
                ShowBanner("You're up to date!", $"Latest release is {tagName} — no update needed.", htmlUrl: null, isUpdate: false);
            }
        }
        catch (HttpRequestException ex)
        {
            UpdateStatus.Text = $"Network error: {ex.Message}";
        }
        catch (TaskCanceledException)
        {
            UpdateStatus.Text = "Request timed out. Check your internet connection.";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            CheckUpdatesBtn.IsEnabled = true;
        }
    }

    private void ShowBanner(string title, string body, string? htmlUrl, bool isUpdate)
    {
        UpdateBannerTitle.Text = title;
        UpdateBannerBody.Text  = body;
        UpdateBanner.Background = isUpdate
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(30, 224, 32, 32))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(26, 0, 120, 212));
        UpdateBanner.BorderBrush = isUpdate
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(64, 224, 32, 32))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(64, 0, 120, 212));

        if (!string.IsNullOrEmpty(htmlUrl))
        {
            UpdateDownloadBtn.Visibility = Visibility.Visible;
            UpdateDownloadBtn.NavigateUri = new Uri(htmlUrl);
        }
        else
        {
            UpdateDownloadBtn.Visibility = Visibility.Collapsed;
        }
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)
        => FirewallService.OpenNetworkConnections();

    private void OnOpenWindowsSettings(object sender, RoutedEventArgs e)
        => FirewallService.OpenNetworkSettings();

    // ── Dependencies — Npcap ─────────────────────────────────────────────────
    /// <summary>
    /// Npcap download URL — the stable official installer from the Npcap project.
    /// Update this constant when a new version of Npcap is released.
    /// </summary>
    private const string NpcapInstallerUrl     = "https://npcap.com/dist/npcap-1.79.exe";
    private const string NpcapInstallerVersion = "1.79";
    private CancellationTokenSource? _npcapDlCts;

    private void OnRefreshDependencies(object sender, RoutedEventArgs e) => CheckNpcapStatus();

    private void CheckNpcapStatus()
    {
        NpcapStatusText.Text = "Checking…";
        NpcapStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x22, 0x80, 0x80, 0x80));
        NpcapStatusBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x44, 0x80, 0x80, 0x80));

        // Try registry first — most reliable
        string? regVersion = GetNpcapVersionFromRegistry();

        if (regVersion != null)
        {
            SetNpcapInstalled(regVersion);
            return;
        }

        // Fallback: try initialising SharpPcap; if no DllNotFoundException → Npcap is present
        try
        {
            _ = Pcap.SharpPcapVersion;
            SetNpcapInstalled("detected");
        }
        catch (DllNotFoundException)
        {
            SetNpcapNotInstalled();
        }
        catch
        {
            SetNpcapNotInstalled();
        }
    }

    private static string? GetNpcapVersionFromRegistry()
    {
        // Npcap writes its version to HKLM\SOFTWARE\Npcap (default value)
        string[] keys =
        {
            @"SOFTWARE\Npcap",
            @"SOFTWARE\WOW6432Node\Npcap",
            @"SOFTWARE\WinPcap"
        };
        foreach (var keyPath in keys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                if (key == null) continue;
                // Default value holds the install path; (Version) or just presence is enough
                string? val = key.GetValue("") as string
                           ?? key.GetValue("Version") as string;
                return val ?? "installed";
            }
            catch { }
        }
        return null;
    }

    private void SetNpcapInstalled(string version)
    {
        NpcapStatusText.Text = $"✓ Installed ({version})";
        NpcapStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x22, 0x10, 0x7C, 0x10));
        NpcapStatusBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x44, 0x10, 0x7C, 0x10));
        NpcapStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x10, 0x7C, 0x10));
        NpcapVersionText.Text = $"Npcap is available — ARP spoofing and packet capture are enabled.";
        NpcapInstallBtn.Visibility = Visibility.Collapsed;
    }

    private void SetNpcapNotInstalled()
    {
        NpcapStatusText.Text = "✗ Not installed";
        NpcapStatusBadge.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x22, 0xCC, 0x33, 0x00));
        NpcapStatusBadge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0x44, 0xCC, 0x33, 0x00));
        NpcapStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.ColorHelper.FromArgb(0xFF, 0xCC, 0x33, 0x00));
        NpcapVersionText.Text = $"ARP spoofing (internet cut-off) and packet capture require Npcap. Click install to get v{NpcapInstallerVersion}.";
        NpcapInstallBtn.Visibility = Visibility.Visible;
    }

    private async void OnInstallNpcap(object sender, RoutedEventArgs e)
    {
        NpcapInstallBtn.IsEnabled     = false;
        NpcapInstallText.Text         = "Downloading…";
        NpcapInstallIcon.Glyph        = "\uE896";
        NpcapDownloadProgress.Visibility = Visibility.Visible;
        NpcapDownloadStatus.Visibility   = Visibility.Visible;
        NpcapDownloadProgress.Value      = 0;
        NpcapDownloadStatus.Text         = "Connecting to npcap.com…";

        _npcapDlCts?.Cancel();
        _npcapDlCts = new CancellationTokenSource();
        var ct = _npcapDlCts.Token;

        string installerPath = Path.Combine(Path.GetTempPath(), $"npcap-{NpcapInstallerVersion}.exe");

        try
        {
            // Download with real progress
            using var response = await _http.GetAsync(
                NpcapInstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total   = response.Content.Headers.ContentLength ?? -1;
            long received = 0;

            await using var stream     = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[65536];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (total > 0)
                {
                    double pct = (double)received / total * 100;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        NpcapDownloadProgress.Value = pct;
                        NpcapDownloadStatus.Text    =
                            $"Downloading Npcap {NpcapInstallerVersion}… {received / 1024:N0} KB / {total / 1024:N0} KB";
                    });
                }
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                NpcapDownloadProgress.Value  = 100;
                NpcapDownloadStatus.Text     = "Download complete. Launching installer…";
                NpcapInstallText.Text        = "Launching…";
            });

            // Launch the Npcap installer (shows its own UI + UAC)
            await Task.Delay(400, ct);
            Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });

            DispatcherQueue.TryEnqueue(() =>
            {
                NpcapDownloadStatus.Text  = $"Npcap installer launched. After installation, click ↻ to refresh status.";
                NpcapInstallBtn.IsEnabled = true;
                NpcapInstallText.Text     = "Download & Install";
                NpcapInstallIcon.Glyph    = "\uE896";
            });
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NpcapDownloadStatus.Text  = "Download cancelled.";
                NpcapInstallBtn.IsEnabled = true;
                NpcapInstallText.Text     = "Download & Install";
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                NpcapInstallBtn.IsEnabled = true;
                NpcapInstallText.Text     = "Download & Install";
                NpcapDownloadProgress.Visibility = Visibility.Collapsed;
                NpcapDownloadStatus.Text  = $"Download failed: {ex.Message}";

                // Offer browser fallback
                var dlg = new ContentDialog
                {
                    Title           = "Download failed",
                    Content         = $"Could not download Npcap automatically:\n{ex.Message}\n\nOpen the Npcap website to download manually?",
                    PrimaryButtonText = "Open npcap.com",
                    CloseButtonText   = "Cancel",
                    XamlRoot          = XamlRoot
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });
            });
        }
    }

    // ── Save ──────────────────────────────────────────────────────────────────
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_cfg == null) return;
        _vm?.SaveConfig();
        SaveStatus.Text = $"\u2713 Settings saved — {DateTime.Now:HH:mm:ss}";
    }

    // ── VirusTotal API Key ─────────────────────────────────────────────────────
    private void OnVtApiKeyLostFocus(object sender, RoutedEventArgs e)
    {
        if (_cfg == null || _vm == null) return;
        string key = VtApiKeyBox?.Password?.Trim() ?? string.Empty;
        _cfg.VirusTotalApiKey = key;
        _vm.VtApiKey          = key; // update live ViewModel
        _vm.SaveConfig();
    }

    private void OnOpenVtSite(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://www.virustotal.com/gui/my-apikey") { UseShellExecute = true });
        }
        catch { }
    }
}
