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
        try { ViewModel.ProxyService.Stop(); } catch { }
        try { _tray?.Dispose(); _tray = null; } catch { }
    }

    // ── NavigationView events ──────────────────────────────────────────────────

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Start on Dashboard by default
        NavView.SelectedItem = NavDashboard;
        ContentFrame.Navigate(typeof(DashboardPage), ViewModel);
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
                HeaderUpText.Text      = $"↑ {ViewModel.GlobalUploadText}";
                HeaderDownText.Text    = $"↓ {ViewModel.GlobalDownloadText}";
                HeaderBlockedText.Text = ViewModel.BlockedCountText;
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
        if (this.Content is FrameworkElement root)
            root.RequestedTheme = _currentTheme;

        ThemeIcon.Glyph = _currentTheme switch
        {
            ElementTheme.Dark  => "\uE706",
            ElementTheme.Light => "\uE708",
            _                  => "\uE793"
        };

        ViewModel.CurrentConfig.AppTheme = _currentTheme.ToString();
        ViewModel.SaveConfig();
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
    private void OnMinimizeToTrayClicked(object sender, RoutedEventArgs e)
    {
        if (_tray == null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _tray = new Core.TrayService(hwnd, DispatcherQueue);
        }
        this.Hide();
        System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() => this.Show());
        });
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────
    private void OnGlobalKeyDown(object sender,
                                  Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
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
        }
    }
}
