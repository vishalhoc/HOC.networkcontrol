using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNetControl.ViewModels;
using WinNetControl.Pages;
using WinUIEx;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WinNetControl;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    // Current element theme (tracks user toggle)
    private ElementTheme _currentTheme = ElementTheme.Default;

    // Global widget window
    private SpeedWidgetWindow? _globalWidget;

    // Tray service
    private WinNetControl.Core.TrayService? _tray;

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

        // Set title with version
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string versionStr = ver != null ? $"v{ver.Major}.{ver.Minor}" : "v3.0";
        this.Title = $"WinNetControl {versionStr}";
        AppTitleText.Text = $"WinNetControl {versionStr}";

        // Apply saved theme
        ApplyTheme(ViewModel.CurrentConfig.AppTheme);

        // Restore saved window size and position (Imp#29)
        RestoreWindowBounds();

        // Wire up live speed header ticker
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // System tray icon
        this.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _tray = new Core.TrayService(App.WindowHandle, this.DispatcherQueue);
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

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        // Save window bounds before anything else disposes (Imp#29)
        SaveWindowBounds();
        try { ViewModel.ProxyService.Stop(); } catch { }
        try { _tray?.Dispose(); _tray = null; } catch { }
    }

    // ── Window bounds persistence (Imp#29) ────────────────────────────────────
    private void RestoreWindowBounds()
    {
        try
        {
            var cfg = ViewModel.CurrentConfig;
            if (cfg.WindowX < 0 || cfg.WindowY < 0) return; // first launch — let OS decide

            var appWin = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(this)));

            appWin.MoveAndResize(new Windows.Graphics.RectInt32(
                (int)cfg.WindowX, (int)cfg.WindowY,
                (int)Math.Max(cfg.WindowWidth,  640),
                (int)Math.Max(cfg.WindowHeight, 480)));
        }
        catch { /* non-fatal — window opens at default position */ }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var appWin = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                Microsoft.UI.Win32Interop.GetWindowIdFromWindow(
                    WinRT.Interop.WindowNative.GetWindowHandle(this)));

            var pos  = appWin.Position;
            var size = appWin.Size;
            ViewModel.CurrentConfig.WindowX      = pos.X;
            ViewModel.CurrentConfig.WindowY      = pos.Y;
            ViewModel.CurrentConfig.WindowWidth  = size.Width;
            ViewModel.CurrentConfig.WindowHeight = size.Height;
            ViewModel.SaveConfig();
        }
        catch { }
    }

    // ── NavigationView events ──────────────────────────────────────────────────

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Start on Dashboard by default
        NavView.SelectedItem = NavDashboard;
        ContentFrame.Navigate(typeof(DashboardPage), ViewModel);

        // Apply saved theme to the NavigationView pane AFTER it has fully loaded.
        // The pane panel does not exist in the visual tree until Loaded fires,
        // so any PaneBackground set before this point has no effect.
        UpdateNavViewTheme(_currentTheme);

        // IMP#28: Restore saved nav pane mode (left / compact / top)
        UpdateNavPaneMode(ViewModel.CurrentConfig.NavPaneMode);
    }

    /// <summary>Updates the NavigationView pane mode from Settings.</summary>
    public void UpdateNavPaneMode(string mode)
    {
        NavView.PaneDisplayMode = mode switch
        {
            "compact" => Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.LeftCompact,
            "top"     => Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Top,
            _         => Microsoft.UI.Xaml.Controls.NavigationViewPaneDisplayMode.Left
        };
    }

    private void NavView_SelectionChanged(NavigationView sender,
                                          NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        string tag = item.Tag?.ToString() ?? "";

        Type? pageType = tag switch
        {
            "Dashboard"     => typeof(DashboardPage),
            "Connections"   => typeof(ConnectionManagerPage),
            "Monitoring"    => typeof(MonitoringPage),
            "Interfaces"    => typeof(NetworkInterfacesPage),
            "IpConfig"      => typeof(IpConfigPage),
            "Dns"           => typeof(DnsManagerPage),
            "Hosts"         => typeof(HostsManagerPage),
            "Wireless"      => typeof(WirelessPage),
            "WifiPentest"   => typeof(WifiPentestPage),
            "Hashcat"       => typeof(HashcatPage),
            "Firewall"      => typeof(FirewallPage),
            "Security"      => typeof(SecurityPage),
            "Proxy"         => typeof(ProxyManagerPage),
            "Vpn"           => typeof(VpnManagerPage),
            "PacketCapture" => typeof(PacketCapturePage),
            "HttpInspector" => typeof(HttpInspectorPage),
            "Socket"        => typeof(SocketManagerPage),
            "PortScanner"   => typeof(PortScannerPage),
            "Lan"           => typeof(LanScannerPage),
            "Speed"         => typeof(SpeedToolsPage),
            "Diagnostics"   => typeof(DiagnosticsPage),
            "Optimizer"     => typeof(OptimizerPage),
            "Qos"           => typeof(QosManagerPage),
            "Routing"       => typeof(RoutingPage),
            "Logs"          => typeof(LogsPage),
            "Automation"    => typeof(AutomationPage),
            "Reset"         => typeof(NetworkResetPage),
            "Terminal"      => typeof(TerminalPage),
            "Reporting"     => typeof(ReportingPage),
            "Journey"       => typeof(PacketJourneyPage),
            "Settings"      => typeof(SettingsPage),
            _               => null
        };

        if (pageType != null)
            ContentFrame.Navigate(pageType, ViewModel);
    }

    private void NavView_PaneOpening(NavigationView sender, object args) { }

    private void NavView_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args) { }

    private void ContentFrame_NavigationFailed(object sender,
                                               Microsoft.UI.Xaml.Navigation.NavigationFailedEventArgs e)
    {
        // Log full exception so we can diagnose blank-page failures
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinNetControl_crash.log"),
            $"[{DateTime.Now}] [NavFailed] {e.SourcePageType?.FullName}\n{e.Exception}\n\n");
        e.Handled = true;
    }

    // ── Navigate to a specific module from outside (e.g., from Dashboard shortcuts) ──
    public void NavigateTo(string tag)
    {
        if (TrySelectItem(NavView.MenuItems, tag)) return;
        TrySelectItem(NavView.FooterMenuItems, tag);
    }

    // CM-08: recursively walks MenuItems AND NavigationViewItemGroup children
    private bool TrySelectItem(IEnumerable<object> items, string tag)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem item)
            {
                if (item.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = item;
                    return true;
                }
                // Recurse into group children
                if (item.MenuItems?.Count > 0 && TrySelectItem(item.MenuItems.Cast<object>(), tag))
                    return true;
            }
            else if (obj is NavigationViewItemHeader) { /* skip */ }
        }
        return false;
    }

    // ── Live speed header update ───────────────────────────────────────────────
    private void OnViewModelPropertyChanged(object? sender,
                                            System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.GlobalUploadText)
                           or nameof(ViewModel.GlobalDownloadText)
                           or nameof(ViewModel.BlockedCountText))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                HeaderUpText.Text   = $"\u2191 {ViewModel.GlobalUploadText}";
                HeaderDownText.Text = $"\u2193 {ViewModel.GlobalDownloadText}";

                // FIX Bug#21: HeaderBlockedBadge was permanently visible even when
                // 0 apps are blocked because Visibility was never toggled.
                // Parse the count from BlockedCountText ("N blocked") and hide
                // the entire red badge when there is nothing to report.
                int blockedCount = 0;
                var parts = ViewModel.BlockedCountText?.Split(' ');
                if (parts?.Length > 0) int.TryParse(parts[0], out blockedCount);

                if (blockedCount > 0)
                {
                    HeaderBlockedBadge.Visibility = Visibility.Visible;
                    HeaderBlockedText.Text        = ViewModel.BlockedCountText;
                }
                else
                {
                    HeaderBlockedBadge.Visibility = Visibility.Collapsed;
                    HeaderBlockedText.Text        = string.Empty;
                }
            });
        }
    }

    // ── Theme ─────────────────────────────────────────────────────────────────
    private void OnThemeToggleClicked(object sender, RoutedEventArgs e)
    {
        _currentTheme = _currentTheme switch
        {
            ElementTheme.Default => ElementTheme.Dark,
            ElementTheme.Dark    => ElementTheme.Light,
            _                    => ElementTheme.Default
        };
        ApplyTheme(_currentTheme.ToString());
    }

    internal void ApplyTheme(string themeName)
    {
        _currentTheme = themeName switch
        {
            "Dark"  => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _       => ElementTheme.Default
        };

        // Apply to the root grid so page content switches
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = _currentTheme;

        // Apply to NavView + ContentFrame explicitly (pane needs special handling)
        UpdateNavViewTheme(_currentTheme);

        ThemeIcon.Glyph = _currentTheme switch
        {
            ElementTheme.Dark  => "\uE706",
            ElementTheme.Light => "\uE708",
            _                  => "\uE793"
        };

        ViewModel.CurrentConfig.AppTheme = _currentTheme.ToString();
        ViewModel.SaveConfig();
    }

    // ── IMP#26: App-wide toast notification ───────────────────────────────────
    /// <summary>
    /// Shows a floating InfoBar toast that auto-dismisses after <paramref name="durationMs"/> ms.
    /// Call from any page via <c>App.MainWindow.ShowToast(...)</c>.
    /// </summary>
    /// <param name="title">Bold title line.</param>
    /// <param name="message">Descriptive body text.</param>
    /// <param name="severity">"success" | "warning" | "error" | "info"</param>
    /// <param name="durationMs">Auto-close delay (default 3 s).</param>
    public async void ShowToast(string title, string message,
                                string severity = "info", int durationMs = 3000)
    {
        AppToast.Severity = severity.ToLowerInvariant() switch
        {
            "success" => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
            "warning" => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
            "error"   => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
            _         => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational
        };
        AppToast.Title   = title;
        AppToast.Message = message;
        AppToast.IsOpen  = true;

        await System.Threading.Tasks.Task.Delay(durationMs);

        // Only close if still showing this toast (user may have closed it already)
        if (AppToast.IsOpen) AppToast.IsOpen = false;
    }

    /// <summary>
    /// Explicitly updates the NavigationView pane background and RequestedTheme.
    /// WinUI 3's NavigationView.PaneBackground does not re-resolve ThemeResources at
    /// runtime when RequestedTheme changes — the SplitView pane panel caches the brush
    /// set at first layout. We bypass this by setting PaneBackground directly so the
    /// correct light/dark colour is always applied.
    /// </summary>
    private void UpdateNavViewTheme(ElementTheme theme)
    {
        // Determine effective theme (resolve Default from OS)
        bool isDark = theme == ElementTheme.Dark ||
            (theme == ElementTheme.Default &&
             Application.Current.RequestedTheme == ApplicationTheme.Dark);

        // These colours match WinUI 3's NavigationViewExpandedPaneBackground defaults
        var paneColor = isDark
            ? Windows.UI.Color.FromArgb(255, 32,  32,  32)   // #202020 dark pane
            : Windows.UI.Color.FromArgb(255, 243, 243, 243); // #F3F3F3 light pane

        // Override the pane background resource directly on the control.
        // NavView.PaneBackground is not exposed in all WinUI 3 versions, but
        // overriding the named resource forces the pane's SplitView to pick up
        // the correct brush on every paint cycle — bypassing the ThemeResource
        // caching bug that leaves the pane dark when switching to light mode.
        var brush = new Microsoft.UI.Xaml.Media.SolidColorBrush(paneColor);
        NavView.Resources["NavigationViewExpandedPaneBackground"] = brush;
        NavView.Resources["NavigationViewCompactPaneBackground"]  = brush;

        NavView.RequestedTheme      = theme;   // item foreground + headers adapt
        ContentFrame.RequestedTheme = theme;
    }

    // ── Global Speed Widget ────────────────────────────────────────────────────
    private void OnToggleGlobalWidget(object sender, RoutedEventArgs e)
    {
        if (_globalWidget == null)
        {
            _globalWidget = new SpeedWidgetWindow(null, ViewModel);
            _globalWidget.Closed += (s, a) => _globalWidget = null;
            _globalWidget.Activate();
        }
        else
        {
            _globalWidget.Close();
            _globalWidget = null;
        }
    }

    // ── Minimize to Tray ──────────────────────────────────────────────────────
    // FIX Bug#1: removed the 5-second auto-restore (Task.Delay) that always
    //            brought the window back — the window now stays hidden until
    //            the user clicks the tray icon.
    // FIX Bug#22: reuse the existing _tray if it was already created in the
    //             constructor to avoid instantiating two TrayService instances.
    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        if (_tray == null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _tray = new Core.TrayService(hwnd, DispatcherQueue);
        }
        this.Hide();
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────
    private void OnGlobalKeyDown(object sender,
                                   Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource
                              .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                              .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift = Microsoft.UI.Input.InputKeyboardSource
                               .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                               .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.F5:
                ViewModel.ApplyFilterAndSort();
                e.Handled = true;
                break;

            // ── Navigation shortcuts (Ctrl+1 – Ctrl+9) ──────────────────────
            case Windows.System.VirtualKey.Number1 when ctrl:
                NavigateTo("Dashboard");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number2 when ctrl:
                NavigateTo("Connections");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number3 when ctrl:
                NavigateTo("Firewall");
                e.Handled = true;
                break;

            // IMP#17: Ctrl+4–Ctrl+9 shortcut keys added
            case Windows.System.VirtualKey.Number4 when ctrl:
                NavigateTo("Monitoring");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number5 when ctrl:
                NavigateTo("Diagnostics");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number6 when ctrl:
                NavigateTo("Lan");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number7 when ctrl:
                NavigateTo("PacketCapture");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number8 when ctrl:
                NavigateTo("Security");
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Number9 when ctrl:
                NavigateTo("Settings");
                e.Handled = true;
                break;

            // IMP#27: Ctrl+Shift+T cycles dark / light / default theme
            case Windows.System.VirtualKey.T when ctrl && shift:
                OnThemeToggleClicked(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }
}
