using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using WinNetControl.Core;
using WinNetControl.Models;

namespace WinNetControl;

public sealed partial class HttpInspectorWindow : Window
{
    private readonly HttpProxyService              _proxy;
    private readonly ViewModels.MainViewModel      _viewModel;
    private readonly int?                          _filterPid;
    private readonly string                        _filterApp;

    private readonly ObservableCollection<HttpRequestInfo> _all     = new();
    private readonly ObservableCollection<HttpRequestInfo> _visible = new();
    private string _filterText = string.Empty;

    private Timer? _proxyPollTimer;

    // ── Constructor ───────────────────────────────────────────────────────────
    public HttpInspectorWindow(ViewModels.MainViewModel viewModel,
                               int? processId = null, string appName = "")
    {
        this.InitializeComponent();
        WinUIEx.WindowExtensions.SetWindowSize(this, 1080, 720);
        try { WinUIEx.WindowExtensions.SetIcon(this, "Assets\\AppIcon.ico"); } catch { }

        _viewModel = viewModel;
        _proxy     = viewModel.ProxyService;
        _filterPid = processId;
        _filterApp = appName;

        this.Title = processId.HasValue
            ? $"HTTP Inspector  \u2014  {appName} (PID {processId})"
            : "HTTP Inspector  \u2014  Global";

        // Seed existing captured requests
        foreach (var r in _proxy.Requests)
            if (MatchesPidFilter(r)) _all.Add(r);

        _proxy.Requests.CollectionChanged += OnRequestsCollectionChanged;
        ApplyTextFilter();
        RequestsListView.ItemsSource = _visible;

        // Poll proxy status every 2 s to keep the panel fresh
        _proxyPollTimer = new Timer(_ =>
            DispatcherQueue.TryEnqueue(RefreshProxyStatus), null, 0, 2000);

        UpdateBars();
    }

    // ── Collection change ─────────────────────────────────────────────────────
    private void OnRequestsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        foreach (HttpRequestInfo r in e.NewItems)
        {
            if (!MatchesPidFilter(r)) continue;
            _all.Add(r);
            if (PassesTextFilter(r))
            {
                _visible.Add(r);
                if (AutoScrollToggle?.IsChecked == true && _visible.Count > 0)
                    RequestsListView.ScrollIntoView(_visible[^1]);
            }
        }
        UpdateBars();
    }

    private bool MatchesPidFilter(HttpRequestInfo r)
    {
        if (CaptureAllToggle?.IsChecked == true) return true;
        return !_filterPid.HasValue || r.ProcessId == _filterPid.Value;
    }

    private bool PassesTextFilter(HttpRequestInfo r)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        var t = _filterText;
        return r.Host.Contains(t, StringComparison.OrdinalIgnoreCase)
            || r.Url.Contains(t, StringComparison.OrdinalIgnoreCase)
            || r.Method.Contains(t, StringComparison.OrdinalIgnoreCase)
            || r.ProcessName.Contains(t, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyTextFilter()
    {
        _visible.Clear();
        foreach (var r in _all)
            if (PassesTextFilter(r)) _visible.Add(r);
    }

    private void UpdateBars()
    {
        if (CountBar == null) return;
        int  n     = _visible.Count;
        long total = _visible.Sum(r => r.ResponseSize);
        CountBar.Text = $"{n} request{(n == 1 ? "" : "s")}";
        SizeBar.Text  = total > 0
            ? $"\u2211 {FormatSize(total)}" : "";
    }

    // ── Proxy status panel ────────────────────────────────────────────────────
    private void RefreshProxyStatus()
    {
        var st = HttpProxyService.GetSystemProxyStatus();

        // Dot color
        ProxyDot.Background = new SolidColorBrush(
            st.Enabled
                ? (st.IsOurs ? Color.FromArgb(255, 16, 124, 16)   // green  = our proxy
                             : Color.FromArgb(255, 255, 165, 0))   // orange = someone else''s
                : Color.FromArgb(255, 160, 160, 160));             // grey   = no proxy

        ProxyServerText.Text = st.Enabled
            ? (st.IsOurs ? $"Our Proxy  127.0.0.1:{HttpProxyService.ProxyPort}"
                         : $"External: {st.Server}")
            : "No Proxy Active";

        ProxyDetailText.Text = st.Enabled
            ? (st.IsOurs
                ? (_proxy.IsRunning ? "Capture is running." : "\u26A0  Proxy set but capture is STOPPED!")
                : "Another proxy is set. WinNetControl is NOT capturing.")
            : (_proxy.IsRunning ? "\u26A0  Capture is running but system proxy is NOT set."
                               : "Press ''Enable System Proxy'' then ''Start Capture''.");

        // Warning banner: proxy set to us, but capture stopped  = apps may be offline
        bool stale = st.Enabled && st.IsOurs && !_proxy.IsRunning;
        OfflineBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;

        // Capture-enabled apps list
        var captureApps = _viewModel.Processes
            .Where(p => p.IsHttpCaptureEnabled)
            .Select(p => p.ProcessName)
            .ToList();
        CaptureAppsText.Text = captureApps.Count > 0
            ? string.Join(", ", captureApps)
            : "(none — enable on an app in the main list)";
    }

    // ── Proxy toolbar actions ─────────────────────────────────────────────────
    private void OnEnableSystemProxy(object s, RoutedEventArgs e)
    {
        HttpProxyService.SetSystemProxyDirect(true);
        RefreshProxyStatus();
        StatusBar.Text = $"\u2713  System proxy set to 127.0.0.1:{HttpProxyService.ProxyPort}";
    }

    private void OnDisableSystemProxy(object s, RoutedEventArgs e)
    {
        HttpProxyService.ForceRestoreSystemProxy();
        RefreshProxyStatus();
        StatusBar.Text = "\u2713  System proxy removed. Apps connect directly.";
    }

    private void OnFixConnectivity(object s, RoutedEventArgs e)
    {
        // Stop proxy (if running) and force-clear the registry
        if (_proxy.IsRunning) _proxy.Stop();
        HttpProxyService.ForceRestoreSystemProxy();
        RefreshProxyStatus();
        StatusBar.Text = "\u2713  Proxy cleared from registry. Internet should be restored.";
    }

    private void OnInstallCert(object s, RoutedEventArgs e)
    {
        try
        {
            _proxy.InstallCertificate();
            StatusBar.Text = "\u2713  HTTPS certificate installed. Restart browser if needed.";
        }
        catch (Exception ex) { StatusBar.Text = $"\u2717  Cert error: {ex.Message}"; }
    }

    // ── Capture toolbar ───────────────────────────────────────────────────────
    private void OnStartCapture(object sender, RoutedEventArgs e)
    {
        try
        {
            bool useSystemProxy = _viewModel.CurrentConfig.EnableSystemProxy;
            _proxy.Start(useSystemProxy);
            RefreshProxyStatus();
            StatusBar.Text = $"\u25cf  Capturing on port {HttpProxyService.ProxyPort}" +
                             (useSystemProxy ? " (system proxy active)" : " (manual proxy mode)");
        }
        catch (Exception ex) { StatusBar.Text = $"\u2717  {ex.Message}"; }
    }

    private void OnStopCapture(object sender, RoutedEventArgs e)
    {
        _proxy.Stop();   // internally calls ForceRestoreSystemProxy as fallback
        RefreshProxyStatus();
        StatusBar.Text = "\u25a0  Capture stopped. System proxy cleared.";
    }

    private void OnClearRequests(object sender, RoutedEventArgs e)
    {
        _proxy.Requests.Clear();
        _all.Clear();
        _visible.Clear();
        UpdateBars();
        StatusBar.Text = "Cleared.";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = FilterBox.Text.Trim();
        ApplyTextFilter();
        UpdateBars();
    }

    // ── Row helpers ───────────────────────────────────────────────────────────
    private HttpRequestInfo? RowItem(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HttpRequestInfo r) return r;
        return RequestsListView.SelectedItem as HttpRequestInfo;
    }

    // ── Copy ──────────────────────────────────────────────────────────────────
    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        var dp = new DataPackage(); dp.SetText(r.Url); Clipboard.SetContent(dp);
    }

    private void OnCopyHost(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        var dp = new DataPackage(); dp.SetText(r.Host); Clipboard.SetContent(dp);
    }

    private async void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(r.Url)); }
        catch { StatusBar.Text = "\u2717  Could not open URL."; }
    }

    // ── Block in Hosts ────────────────────────────────────────────────────────
    private async void OnBlockHost(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender);
        if (r == null || string.IsNullOrWhiteSpace(r.Host)) return;

        string app = !string.IsNullOrEmpty(r.ProcessName) ? r.ProcessName : _filterApp;
        var box = new TextBox { Text = r.Host };
        var dlg = new ContentDialog
        {
            Title = "Block host in Hosts File",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Host to block (will map to 0.0.0.0):", FontSize = 12 },
                    box,
                    new TextBlock
                    {
                        Text = "This adds an entry to C:\\Windows\\System32\\drivers\\etc\\hosts.\nRequires Administrator rights.",
                        FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            },
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var (ok, err) = HostsFileService.BlockDomain(box.Text.Trim().ToLower(), app);
        StatusBar.Text = ok ? $"\u2713  {box.Text.Trim()} blocked in Hosts" : $"\u2717  {err}";
    }

    // ── Block in Firewall ─────────────────────────────────────────────────────
    private async void OnBlockFirewall(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender);
        if (r == null || string.IsNullOrWhiteSpace(r.Host)) return;
        await DoFirewallBlock(r);
    }

    private async void OnBlockBoth(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender);
        if (r == null || string.IsNullOrWhiteSpace(r.Host)) return;
        string app = !string.IsNullOrEmpty(r.ProcessName) ? r.ProcessName : _filterApp;
        var (hOk, hErr) = HostsFileService.BlockDomain(r.Host, app);
        var (fOk, fErr) = await DoFirewallBlockSilent(r);
        StatusBar.Text = $"Hosts: {(hOk ? "\u2713" : "\u2717 " + hErr)}  |  Firewall: {(fOk ? "\u2713" : "\u2717 " + fErr)}";
    }

    private async Task DoFirewallBlock(HttpRequestInfo r)
    {
        string host = r.Host.Trim().ToLower();
        string app  = !string.IsNullOrEmpty(r.ProcessName) ? r.ProcessName : _filterApp;
        StatusBar.Text = $"Resolving {host}...";
        string ip = await FirewallService.ResolveHostAsync(host);

        var ipBox = new TextBox { Text = string.IsNullOrEmpty(ip) ? host : ip };
        var dlg   = new ContentDialog
        {
            Title = $"Block in Windows Firewall",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"Host: {host}", FontSize=12, FontWeight=Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = $"Resolved IP: {(string.IsNullOrEmpty(ip) ? "could not resolve" : ip)}", FontSize=11 },
                    ipBox,
                    new TextBlock
                    {
                        Text = "Adds an outbound BLOCK rule for port 80 (HTTP) and 443 (HTTPS).\nRequires Administrator rights.",
                        FontSize=11, TextWrapping=TextWrapping.Wrap,
                        Foreground=(Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    }
                }
            },
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string target   = ipBox.Text.Trim();
        var (ok80, e80) = FirewallService.AddOutboundBlockRule($"WinNetControl HTTP {host} [{app}]", target, 80);
        var (ok4, e4)   = FirewallService.AddOutboundBlockRule($"WinNetControl HTTPS {host} [{app}]", target, 443);
        StatusBar.Text = (ok80 && ok4)
            ? $"\u2713  Firewall: blocked {target} port 80+443"
            : $"\u2717  FW80: {e80}  FW443: {e4}";
    }

    private async Task<(bool, string)> DoFirewallBlockSilent(HttpRequestInfo r)
    {
        string host   = r.Host.Trim().ToLower();
        string app    = !string.IsNullOrEmpty(r.ProcessName) ? r.ProcessName : _filterApp;
        string ip     = await FirewallService.ResolveHostAsync(host);
        string target = string.IsNullOrEmpty(ip) ? host : ip;
        var (ok80, e80) = FirewallService.AddOutboundBlockRule($"WinNetControl HTTP {host} [{app}]", target, 80);
        var (ok4, e4)   = FirewallService.AddOutboundBlockRule($"WinNetControl HTTPS {host} [{app}]", target, 443);
        return (ok80 && ok4, ok80 ? e4 : e80);
    }

    // ── Export CSV ────────────────────────────────────────────────────────────
    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        try
        {
            var lines = new System.Collections.Generic.List<string>
                { "Time,App,PID,Method,Host,URL,Status,Size" };
            foreach (var r in _visible)
                lines.Add($"{r.TimeText},{r.ProcessName},{r.ProcessId},{r.Method}," +
                           $"\"{r.Host}\",\"{r.Url}\",{r.StatusText},{r.SizeText}");
            string path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"http_capture_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllLines(path, lines, System.Text.Encoding.UTF8);
            StatusBar.Text = $"\u2713  Exported {_visible.Count} rows to {path}";
        }
        catch (Exception ex) { StatusBar.Text = $"\u2717  Export: {ex.Message}"; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
        return $"{b / 1024.0 / 1024:F2} MB";
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _proxyPollTimer?.Dispose();
        _proxyPollTimer = null;
        _proxy.Requests.CollectionChanged -= OnRequestsCollectionChanged;
        _all.Clear();
        _visible.Clear();
        // Do NOT stop the proxy here — proxy lifecycle is controlled by main window
    }
}
