using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinNetControl.ViewModels;
using WinNetControl.Models;
using WinUIEx;
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

        // Wire up search debounce
        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            ViewModel.SearchText = SearchBox.Text;
        };
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try { ViewModel.ProxyService.Stop(); } catch { }
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

    // ── Search (debounced) ────────────────────────────────────────────────────
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // Restart debounce timer on every keystroke
        _searchDebounce.Stop();
        _searchDebounce.Start();
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
    private AdapterManagerWindow? _adapterManagerWindow;
    private void OnAdapterManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_adapterManagerWindow != null)
        {
            try { _adapterManagerWindow.Activate(); return; } catch { }
        }
        _adapterManagerWindow = new AdapterManagerWindow();
        _adapterManagerWindow.Closed += (_, __) => _adapterManagerWindow = null;
        _adapterManagerWindow.Activate();
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

