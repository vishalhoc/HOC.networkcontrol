using Microsoft.UI.Xaml;
using WinNetControl.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinNetControl.ViewModels;
using WinNetControl.Models;
using WinUIEx;
using System.Linq;
using System;
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    // Current element theme (tracks user toggle)
    private ElementTheme _currentTheme = ElementTheme.Default;

    // Search debounce timer
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // Context menu target (set when right-click opens)
    private ProcessNetworkInfo? _ctxProcess;

    public MainWindow()
    {
        try
        {
            ViewModel = new MainViewModel();
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinNetControl_crash.log"),
                $"[{DateTime.Now}] [MainViewModel ctor]\n{ex}\n\n");
            throw;
        }

        try
        {
            this.InitializeComponent();
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinNetControl_crash.log"),
                $"[{DateTime.Now}] [InitializeComponent]\n{ex}\n\n");
            throw;
        }

        try { this.SetIcon("Assets\\AppIcon.ico"); } catch { }
        this.Closed += MainWindow_Closed;

        // Set title with version in OS title bar and in-app label
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string versionStr = ver != null ? $"v{ver.Major}.{ver.Minor}" : "v2.0";
        this.Title         = $"WinNetControl {versionStr}";
        AppTitleText.Text  = $"WinNetControl {versionStr}";

        // Apply saved theme
        ApplyTheme(ViewModel.CurrentConfig.AppTheme);

        // Wire up search debounce — works for both TextBox and AutoSuggestBox
        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            ViewModel.SearchText = SearchBox.Text;
        };

        // System tray icon
        this.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _tray = new TrayService(App.WindowHandle, this.DispatcherQueue);
                _tray.SetTooltipProvider(() =>
                {
                    string up   = ViewModel.GlobalUploadText;
                    string down = ViewModel.GlobalDownloadText;
                    return $"WinNetControl  \u2191{up}  \u2193{down}";
                });
            }
            catch { }
        });

        // Keyboard shortcuts
        if (this.Content is FrameworkElement root)
            root.KeyDown += OnGlobalKeyDown;
    }

    private TrayService? _tray;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try { ViewModel.ProxyService.Stop(); } catch { }
        try { _tray?.Dispose(); _tray = null; } catch { }
    }

    // ── DNS Flush ─────────────────────────────────────────────────────────────
    private async void OnFlushDnsClicked(object sender, RoutedEventArgs e)
    {
        var (ok, output) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.FlushDns());
        var dlg = new ContentDialog
        {
            Title           = ok ? "✓  DNS Cache Flushed" : "✗  DNS Flush Failed",
            Content         = ok
                ? "DNS resolver cache has been cleared. New lookups will use fresh DNS results."
                : $"Error: {output}",
            CloseButtonText = "OK",
            XamlRoot        = this.Content.XamlRoot
        };
        await dlg.ShowAsync();
    }

    // ── Theme ─────────────────────────────────────────────────────────────────
    private void OnThemeToggleClicked(object sender, RoutedEventArgs e)
    {
        // Cycle: Default → Dark → Light → Default
        _currentTheme = _currentTheme switch
        {
            ElementTheme.Default => ElementTheme.Dark,
            ElementTheme.Dark    => ElementTheme.Light,
            _                    => ElementTheme.Default
        };
        ApplyTheme(_currentTheme.ToString());
    }

    private void ApplyTheme(string themeName)
    {
        _currentTheme = themeName switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default
        };
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = _currentTheme;

        // Update icon
        ThemeIcon.Glyph = _currentTheme switch
        {
            ElementTheme.Dark  => "\uE706", // Moon
            ElementTheme.Light => "\uE708", // Sun
            _                  => "\uE793"  // Phone Tablet
        };

        ViewModel.CurrentConfig.AppTheme = _currentTheme.ToString();
        ViewModel.SaveConfig();
    }

    // ── Settings ──────────────────────────────────────────────────────────────
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var w = new SettingsWindow(ViewModel);
        w.Activate();
    }

    // ── Global Speed Widget ───────────────────────────────────────────────────
    private SpeedWidgetWindow? _globalWidget;
    private void OnToggleGlobalWidget(object sender, RoutedEventArgs e)
    {
        if (_globalWidget == null)
        {
            _globalWidget = new SpeedWidgetWindow(null, ViewModel);
            _globalWidget.Closed += (s, a) => _globalWidget = null;
            _globalWidget.Activate();
        }
        else { _globalWidget.Close(); _globalWidget = null; }
    }

    // ── Per-App Widget ────────────────────────────────────────────────────────
    private readonly System.Collections.Generic.Dictionary<int, SpeedWidgetWindow> _appWidgets = new();
    private void OnAppWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
        {
            if (_appWidgets.TryGetValue(process.ProcessId, out var ew))
            {
                ew.Close();
                _appWidgets.Remove(process.ProcessId);
                process.ShowFloatingWidget = false;
            }
            else
            {
                var w = new SpeedWidgetWindow(process);
                w.Closed += (s, a) => { _appWidgets.Remove(process.ProcessId); process.ShowFloatingWidget = false; };
                _appWidgets[process.ProcessId] = w;
                process.ShowFloatingWidget = true;
                w.Activate();
            }
        }
    }

    // ── Block (all directions) ────────────────────────────────────────────────
    private void OnBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlock(process);
    }

    // ── Block Inbound / Outbound (per-process) ────────────────────────────────
    private void OnBlockInboundToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlockInbound(process);
    }

    private void OnBlockOutboundToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlockOutbound(process);
    }

    // ── Block (per-connection) ────────────────────────────────────────────────
    private void OnConnectionBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
            ViewModel.ToggleConnectionBlock(conn);
    }

    private void OnConnectionInboundBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
        {
            bool newVal = tb.IsChecked == true;
            conn.BlockInbound = newVal;
            ViewModel.ToggleConnectionBlock(conn, blockInbound: newVal, blockOutbound: conn.BlockOutbound);
        }
    }

    private void OnConnectionOutboundBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
        {
            bool newVal = tb.IsChecked == true;
            conn.BlockOutbound = newVal;
            ViewModel.ToggleConnectionBlock(conn, blockInbound: conn.BlockInbound, blockOutbound: newVal);
        }
    }

    // ── Copy Connection Address ───────────────────────────────────────────────
    private void OnCopyConnectionAddress(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.DataContext is ProcessConnection conn)
        {
            var dp = new DataPackage();
            dp.SetText(conn.RemoteAddressPort);
            Clipboard.SetContent(dp);
        }
    }

    // ── Pin ───────────────────────────────────────────────────────────────────
    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.TogglePin(process);
    }

    // ── HTTP Capture ──────────────────────────────────────────────────────────
    private void OnHttpCaptureToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
        {
            ViewModel.ToggleHttpCapture(process);
            if (process.IsHttpCaptureEnabled)
            {
                var w = new HttpInspectorWindow(ViewModel, process.ProcessId, process.ProcessName);
                w.Activate();
            }
        }
    }

    // ── HTTP Inspector (global) ───────────────────────────────────────────────
    private void OnHttpInspectorClicked(object sender, RoutedEventArgs e)
    {
        var w = new HttpInspectorWindow(ViewModel);
        w.Activate();
    }

    // ── Search (debounced) — TextBox fallback ────────────────────────────────
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // ── Search (debounced) — AutoSuggestBox ───────────────────────────────────
    private void OnSearchTextChanged_Auto(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // ── Clear Filters ─────────────────────────────────────────────────────────
    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ViewModel.SearchText      = string.Empty;
        ViewModel.SelectedFilter  = "All";
        ViewModel.SelectedSort    = "Data Used (High-Low)";
        ViewModel.SelectedProtocol = "All Proto";
    }

    // ── Reset Data ────────────────────────────────────────────────────────────
    private void OnResetDataClicked(object sender, RoutedEventArgs e) => ViewModel.ResetAllData();

    // ── Block New Apps toggle ────────────────────────────────────────────────
    private void OnBlockNewAppsToggled(object sender, RoutedEventArgs e)
    {
        // Binding handles ViewModel.BlockNewApps — no extra code needed
    }

    // ── Export CSV ───────────────────────────────────────────────────────────
    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        var (ok, result) = ViewModel.ExportToCsv();
        var dialog = new ContentDialog
        {
            Title   = ok ? "Export Complete" : "Export Failed",
            Content = ok ? $"Saved to:\n{result}" : result,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Auto-Start with Windows ───────────────────────────────────────────────
    private void OnStartWithWindowsToggled(object sender, RoutedEventArgs e)
    {
        // Binding already updated ViewModel.StartWithWindows
    }

    // ── Show Offline Blocked Apps ─────────────────────────────────────────────
    private void OnShowOfflineToggled(object sender, RoutedEventArgs e)
    {
        // Binding already updates ViewModel.ShowOfflineBlockedApps
    }

    // ── Windows Network Tools ─────────────────────────────────────────────────
    private void OnOpenWindowsFirewall(object sender, RoutedEventArgs e)       => ViewModel.OpenWindowsFirewall();
    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)    => ViewModel.OpenNetworkConnections();
    private void OnOpenNetworkSettings(object sender, RoutedEventArgs e)       => ViewModel.OpenNetworkSettings();
    private void OnOpenNetworkTroubleshooter(object sender, RoutedEventArgs e) => ViewModel.OpenNetworkTroubleshooter();

    // ── Internet Reset Dialog ─────────────────────────────────────────────────
    private async void OnResetInternetClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new InternetResetDialog(ViewModel)
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Adapter Manager ───────────────────────────────────────────────────────
    private AdapterManagerWindow? _adapterWindow;
    private void OnAdapterManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_adapterWindow != null)
        {
            try { _adapterWindow.Activate(); return; } catch { }
        }
        _adapterWindow = new AdapterManagerWindow();
        _adapterWindow.Closed += (_, __) => _adapterWindow = null;
        _adapterWindow.Activate();
    }

    // ── Bulk Select & Block (#36) ─────────────────────────────────────────────
    private bool _allSelected;
    // ── Checkbox tap stop-propagation (prevents Expander from toggling on checkbox click) ──
    private void OnRowCheckboxTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => e.Handled = true;

    private void OnBulkSelectAll(object sender, RoutedEventArgs e)
    {
        _allSelected = !_allSelected;
        foreach (var p in ViewModel.FilteredProcesses)
            p.IsSelected = _allSelected;
    }

    private void OnBulkBlock(object sender, RoutedEventArgs e)
    {
        var targets = ViewModel.FilteredProcesses.Where(p => p.IsSelected).ToList();
        foreach (var p in targets)
        {
            if (!p.IsBlocked)
            {
                p.IsBlocked = true;
                ViewModel.ToggleBlock(p);
            }
        }
    }

    private void OnBulkUnblock(object sender, RoutedEventArgs e)
    {
        var targets = ViewModel.FilteredProcesses.Where(p => p.IsSelected).ToList();
        foreach (var p in targets)
        {
            if (p.IsBlocked)
            {
                p.IsBlocked = false;
                ViewModel.ToggleBlock(p);
            }
        }
    }

    // ── Process Right-Click Context Menu ─────────────────────────────────────
    private void OnProcessContextMenuOpening(object sender, object e)
    {
        // Determine which process this flyout is for via DataContext walk
        if (sender is MenuFlyout flyout)
        {
            // Walk up from Target to find the Expander whose DataContext is a ProcessNetworkInfo
            _ctxProcess = null;
            if (flyout.Target is FrameworkElement fe)
            {
                DependencyObject? current = fe;
                while (current != null)
                {
                    if (current is FrameworkElement f && f.DataContext is ProcessNetworkInfo pni)
                    {
                        _ctxProcess = pni;
                        break;
                    }
                    current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
                }
            }
        }
    }

    private void OnCtxCopyName(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null)
        {
            var dp = new DataPackage();
            dp.SetText(_ctxProcess.ProcessName);
            Clipboard.SetContent(dp);
        }
    }

    private void OnCtxCopyPath(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null && !string.IsNullOrWhiteSpace(_ctxProcess.ProcessPath))
        {
            var dp = new DataPackage();
            dp.SetText(_ctxProcess.ProcessPath);
            Clipboard.SetContent(dp);
        }
    }

    private void OnCtxBlock(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null)
        {
            _ctxProcess.IsBlocked = true;
            ViewModel.ToggleBlock(_ctxProcess);
        }
    }

    private void OnCtxUnblock(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null)
        {
            _ctxProcess.IsBlocked = false;
            ViewModel.ToggleBlock(_ctxProcess);
        }
    }

    private void OnCtxOpenFirewall(object sender, RoutedEventArgs e)
        => ViewModel.OpenWindowsFirewall();

    private async void OnCtxKill(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;

        var confirm = new ContentDialog
        {
            Title = "Kill Process?",
            Content = $"Terminate '{_ctxProcess.ProcessName}' (PID {_ctxProcess.ProcessId})?",
            PrimaryButtonText = "Kill",
            CloseButtonText   = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        var result = await confirm.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var (ok, msg) = ViewModel.KillProcess(_ctxProcess);
            var info = new ContentDialog
            {
                Title = ok ? "Process Terminated" : "Error",
                Content = msg,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await info.ShowAsync();
        }
    }

    // ── Details dialog ────────────────────────────────────────────────────────
    private async void OnCtxDetails(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var dialog = new AppDetailDialog(
            _ctxProcess,
            blockCallback:   p => { p.IsBlocked = true;  ViewModel.ToggleBlock(p); },
            unblockCallback: p => { p.IsBlocked = false; ViewModel.ToggleBlock(p); })
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Block domain in Hosts ─────────────────────────────────────────────────
    private async void OnCtxBlockInHosts(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        // Use process name as a best-effort hostname guess
        string guessHost = _ctxProcess.ProcessName.ToLowerInvariant();
        var hostBox = new TextBox { Text = guessHost, PlaceholderText = "hostname to block in hosts file" };
        var dialog = new ContentDialog
        {
            Title   = "Block Domain in Hosts File",
            Content = hostBox,
            PrimaryButtonText  = "Block",
            CloseButtonText    = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string hostname = hostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var (ok, error) = WinNetControl.Core.HostsFileService.BlockDomain(hostname);
        var result = new ContentDialog
        {
            Title   = ok ? "✓ Blocked in Hosts" : "✗ Error",
            Content = ok ? $"'{hostname}' → 0.0.0.0 added and DNS cache flushed." : error,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await result.ShowAsync();
    }

    // ── Hosts File Manager ────────────────────────────────────────────────────
    private HostsManagerWindow? _hostsWindow;
    private void OnHostsManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_hostsWindow == null)
        {
            _hostsWindow = new HostsManagerWindow();
            _hostsWindow.Closed += (s, _) => _hostsWindow = null;
        }
        _hostsWindow.Activate();
    }

    // ── Internet Optimization Dialog ──────────────────────────────────────────
    private async void OnOptimizeClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OptimizeDialog
        {
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Per-connection Pin ────────────────────────────────────────────────────
    private void OnConnectionPinToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        if (tb.DataContext is not WinNetControl.Models.ProcessConnection conn) return;
        // Find the parent ProcessNetworkInfo and sort its Connections: pinned first
        var proc = ViewModel.FilteredProcesses
            .FirstOrDefault(p => p.Connections.Contains(conn));
        if (proc == null) return;
        // Move pinned connections to the top
        var pinned   = proc.Connections.Where(c => c.IsPinned).ToList();
        var unpinned = proc.Connections.Where(c => !c.IsPinned).ToList();
        proc.Connections.Clear();
        foreach (var c in pinned.Concat(unpinned)) proc.Connections.Add(c);
    }

    // ── Per-connection Block in Hosts ─────────────────────────────────────────
    private async void OnConnectionBlockInHosts(object sender, RoutedEventArgs e)
    {
        // Find which connection row triggered this (via DataContext on the Grid)
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not WinNetControl.Models.ProcessConnection conn) return;

        // Extract hostname from remote address (strip port, strip leading wildcards)
        string rawRemote = conn.RemoteAddress?.Trim() ?? string.Empty;
        // Skip if no useful remote address
        if (string.IsNullOrEmpty(rawRemote) || rawRemote == "*"
            || rawRemote == "0.0.0.0" || rawRemote == "::")
        {
            var warn = new ContentDialog
            {
                Title   = "Cannot Block",
                Content = "This connection has no remote address to block.",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await warn.ShowAsync();
            return;
        }

        // Find parent app name for tagging
        string appName = ViewModel.FilteredProcesses
            .FirstOrDefault(p => p.Connections.Contains(conn))?.ProcessName ?? string.Empty;

        var hostBox = new TextBox
        {
            Text = rawRemote,
            PlaceholderText = "hostname or IP to block"
        };
        var dlg = new ContentDialog
        {
            Title   = "Block in Hosts File",
            Content = hostBox,
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string hostname = hostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var (ok, error) = WinNetControl.Core.HostsFileService.BlockDomain(hostname, appName);
        var result = new ContentDialog
        {
            Title   = ok ? "✓ Blocked in Hosts" : "✗ Error",
            Content = ok ? $"'{hostname}' → 0.0.0.0 added. DNS cache flushed. Tagged: {appName}" : error,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await result.ShowAsync();
    }
}

// Temporary-block helper lives here so it can access the timer dict inline
partial class MainWindow
{
    // ── Network Tools window ──────────────────────────────────────────────────
    private NetworkToolsWindow? _netToolsWindow;
    private void OnNetworkToolsClicked(object sender, RoutedEventArgs e)
    {
        if (_netToolsWindow != null)
        {
            try { _netToolsWindow.Activate(); return; } catch { }
        }
        _netToolsWindow = new NetworkToolsWindow();
        _netToolsWindow.Closed += (_, __) => _netToolsWindow = null;
        _netToolsWindow.Activate();
    }

    // ── Temporary Block (#3) ──────────────────────────────────────────────────
    private readonly System.Collections.Generic.Dictionary<int, DispatcherTimer> _tempBlockTimers = new();

    private void OnCtxBlockTemp(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        if (sender is not MenuFlyoutItem item) return;
        int minutes = int.TryParse(item.Tag?.ToString(), out int m) ? m : 30;
        StartTempBlock(_ctxProcess, minutes);
    }

    private async void OnCtxBlockTempCustom(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var box = new NumberBox { Header = "Block duration (minutes)", Value = 30, Minimum = 1, Maximum = 1440 };
        var dlg = new ContentDialog
        {
            Title = $"Block '{_ctxProcess.ProcessName}' temporarily",
            Content = box,
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StartTempBlock(_ctxProcess, (int)box.Value);
    }

    private void StartTempBlock(Models.ProcessNetworkInfo process, int minutes)
    {
        // Cancel any existing timer for this process
        if (_tempBlockTimers.TryGetValue(process.ProcessId, out var old))
        {
            old.Stop();
            _tempBlockTimers.Remove(process.ProcessId);
        }

        // Block now
        if (!process.IsBlocked)
        {
            process.IsBlocked = true;
            ViewModel.ToggleBlock(process);
        }

        // Auto-unblock after 'minutes'
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        timer.Tick += (_, __) =>
        {
            timer.Stop();
            _tempBlockTimers.Remove(process.ProcessId);
            process.IsBlocked = false;
            ViewModel.ToggleBlock(process);
        };
        _tempBlockTimers[process.ProcessId] = timer;
        timer.Start();
    }

    // ── VirusTotal lookup (#27) ───────────────────────────────────────────────
    private void OnCtxVirusTotal(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        string path = _ctxProcess.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            // Compute SHA-256 and open VirusTotal search
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs  = System.IO.File.OpenRead(path);
                byte[] hash   = sha.ComputeHash(fs);
                string hex    = BitConverter.ToString(hash).Replace("-", "").ToLower();
                string url    = $"https://www.virustotal.com/gui/file/{hex}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }
            catch { }
        }
        // Fallback: search by process name
        string nameUrl = $"https://www.virustotal.com/gui/search/{Uri.EscapeDataString(_ctxProcess.ProcessName)}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(nameUrl) { UseShellExecute = true });
    }

    // ── Ping selected process host (#22) ─────────────────────────────────────
    private void OnCtxPingHost(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        string? host = _ctxProcess.Connections.FirstOrDefault()?.RemoteAddress;
        if (string.IsNullOrEmpty(host) || host == "*") host = _ctxProcess.ProcessName;
        if (_netToolsWindow == null)
        {
            _netToolsWindow = new NetworkToolsWindow();
            _netToolsWindow.Closed += (_, __) => _netToolsWindow = null;
        }
        _netToolsWindow.Activate();
        try { _netToolsWindow.SetPingHost(host); } catch { }
    }

    // ── Local Network Scanner ─────────────────────────────────────────────────
    private LocalNetworkScannerWindow? _localScannerWindow;
    private void OnLocalScannerClicked(object sender, RoutedEventArgs e)
    {
        if (_localScannerWindow == null)
        {
            _localScannerWindow = new LocalNetworkScannerWindow();
            _localScannerWindow.Closed += (_, __) => _localScannerWindow = null;
        }
        _localScannerWindow.Activate();
    }

    // ── Network Map ───────────────────────────────────────────────────────────
    private void OnCtxNetworkMap(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var mapWindow = new NetworkMapWindow(_ctxProcess);
        mapWindow.Activate();
    }

    // ── Keyboard shortcuts (#35) ─────────────────────────────────────────────
    private void OnGlobalKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
                              .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                              .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.F5:
                ViewModel.ApplyFilterAndSort();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.F when ctrl:
                SearchBox.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.E when ctrl:
                OnExportCsvClicked(this, new RoutedEventArgs());
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Delete when _ctxProcess != null:
                if (!_ctxProcess.IsBlocked)
                {
                    _ctxProcess.IsBlocked = true;
                    ViewModel.ToggleBlock(_ctxProcess);
                }
                e.Handled = true;
                break;
        }
    }

    // ── Export / Import Rules (#4 / #37) ─────────────────────────────────────
    private async void ShowMessage(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch { }
    }

    private void OnExportRulesClicked(object sender, RoutedEventArgs e)
    {
        string? path = FirewallService.ExportRules(ViewModel.Processes);
        if (path != null)
        {
            ShowMessage("Rules Exported", $"Firewall rules backed up to:\n{path}");
        }
        else
        {
            ShowMessage("Export Failed", "Could not export firewall rules. Check permissions.");
        }
    }

    private async void OnImportRulesClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            int count = FirewallService.ImportRules(file.Path, (config) =>
            {
                // Update local config
                if (config.BlockInbound && !ViewModel.CurrentConfig.BlockedAppsInbound.Contains(config.ProcessName)) 
                    ViewModel.CurrentConfig.BlockedAppsInbound.Add(config.ProcessName);
                if (config.BlockOutbound && !ViewModel.CurrentConfig.BlockedAppsOutbound.Contains(config.ProcessName)) 
                    ViewModel.CurrentConfig.BlockedAppsOutbound.Add(config.ProcessName);
                if (!ViewModel.CurrentConfig.BlockedApps.Contains(config.ProcessName)) 
                    ViewModel.CurrentConfig.BlockedApps.Add(config.ProcessName);

                if (!string.IsNullOrEmpty(config.Notes)) 
                    ViewModel.CurrentConfig.AppNotes[config.ProcessName] = config.Notes;
                
                if (config.DataLimitMb > 0) 
                    ViewModel.CurrentConfig.DataLimits[config.ProcessName] = (long)(config.DataLimitMb * 1024 * 1024);
            });
            ViewModel.SaveConfig();
            ShowMessage("Import Complete", $"Successfully imported {count} rules. Restart app to apply phantom apps.");
        }
    }

    // ── Minimize to Tray (#40) ───────────────────────────────────────────────
    private WinNetControl.Core.TrayService? _trayService;

    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        if (_trayService == null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayService = new WinNetControl.Core.TrayService(hwnd, DispatcherQueue);
        }
        this.Hide();
        // Since we don't have a full WndProc hook in TrayService yet, just warn:
        System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ => 
        {
            DispatcherQueue.TryEnqueue(() => this.Show());
        });
    }

    // ── Column Sorting (#8) — reads Tag for exact SortOptions key ────────────
    private void OnSortHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string sortKey && !string.IsNullOrEmpty(sortKey))
        {
            // If already sorted by this column, keep it (could toggle A-Z/Z-A in future)
            ViewModel.SelectedSort = sortKey;
        }
    }

    // ── App Notes (#11) ──────────────────────────────────────────────────────
    private void OnAppNotesLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ProcessNetworkInfo info)
        {
            ViewModel.CurrentConfig.AppNotes[info.ProcessName] = info.Notes ?? "";
            ViewModel.SaveConfig();
        }
    }
}
