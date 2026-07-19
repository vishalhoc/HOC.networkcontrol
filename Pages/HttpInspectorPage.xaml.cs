using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace WinNetControl.Pages;

public sealed partial class HttpInspectorPage : Page
{
    private MainViewModel?       _vm;
    private HttpProxyService?    _proxy;

    private readonly ObservableCollection<HttpRequestInfo> _all     = new();
    private readonly ObservableCollection<HttpRequestInfo> _visible = new();

    private string _filterText = string.Empty;
    private Timer? _pollTimer;

    // ── Init ─────────────────────────────────────────────────────────────────
    public HttpInspectorPage()
    {
        this.InitializeComponent();
        RequestsListView.ItemsSource = _visible;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
        {
            _vm    = vm;
            _proxy = vm.ProxyService;

            // Seed any already-captured requests
            _all.Clear();
            _visible.Clear();
            foreach (var r in _proxy.Requests)
                if (MatchesPidFilter(r)) AddToVisible(r);

            _proxy.Requests.CollectionChanged += OnRequestsChanged;

            // Poll proxy status every 2 s
            _pollTimer = new Timer(_ =>
                DispatcherQueue.TryEnqueue(RefreshProxyStatus), null, 0, 2000);

            UpdateBars();
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_proxy != null)
            _proxy.Requests.CollectionChanged -= OnRequestsChanged;
    }

    // ── Collection change ─────────────────────────────────────────────────────
    private void OnRequestsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (HttpRequestInfo r in e.NewItems)
            {
                if (!MatchesPidFilter(r)) continue;
                _all.Add(r);
                if (PassesTextFilter(r)) AddToVisible(r);
            }
            UpdateBars();
        });
    }

    private void AddToVisible(HttpRequestInfo r)
    {
        _visible.Add(r);
        if (AutoScrollToggle?.IsChecked == true && _visible.Count > 0)
            RequestsListView.ScrollIntoView(_visible[^1]);
    }

    // ── Filters ───────────────────────────────────────────────────────────────
    private bool MatchesPidFilter(HttpRequestInfo r)
    {
        // CaptureAllToggle = show everything; otherwise filter by enabled apps
        if (CaptureAllToggle.IsOn) return true;
        if (_vm == null) return true;
        return _vm.Processes.Any(p =>
            p.IsHttpCaptureEnabled &&
            p.ProcessName.Equals(r.ProcessName, StringComparison.OrdinalIgnoreCase));
    }

    private bool PassesTextFilter(HttpRequestInfo r)
    {
        if (string.IsNullOrEmpty(_filterText)) return true;
        string t = _filterText;
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
        int  n     = _visible.Count;
        long total = _visible.Sum(r => r.ResponseSize);
        CountBar.Text = $"{n} request{(n == 1 ? "" : "s")}";
        SizeBar.Text  = total > 0 ? $"∑ {FormatSize(total)}" : "";
    }

    // ── Proxy status refresh ──────────────────────────────────────────────────
    private void RefreshProxyStatus()
    {
        if (_proxy == null) return;
        var st = HttpProxyService.GetSystemProxyStatus();

        ProxyDot.Fill = new SolidColorBrush(
            st.Enabled
                ? (st.IsOurs ? Color.FromArgb(255, 16, 124, 16)
                             : Color.FromArgb(255, 255, 165, 0))
                : Color.FromArgb(255, 160, 160, 160));

        ProxyServerText.Text = st.Enabled
            ? (st.IsOurs ? $"Our Proxy  127.0.0.1:{HttpProxyService.ProxyPort}"
                         : $"External: {st.Server}")
            : "No Proxy Active";

        ProxyDetailText.Text = st.Enabled
            ? (st.IsOurs
                ? (_proxy.IsRunning ? "Capture is running." : "⚠  Proxy set but capture is STOPPED!")
                : "Another proxy is set. WinNetControl is NOT capturing.")
            : (_proxy.IsRunning
                ? "⚠  Capture running but system proxy is NOT set — traffic won't be intercepted."
                : "Press 'Enable Proxy' then 'Start Capture'.");

        bool stale = st.Enabled && st.IsOurs && !_proxy.IsRunning;
        OfflineBanner.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;

        if (_vm != null)
        {
            var captureApps = _vm.Processes
                .Where(p => p.IsHttpCaptureEnabled)
                .Select(p => p.ProcessName)
                .ToList();
            CaptureAppsText.Text = captureApps.Count > 0
                ? "Capturing: " + string.Join(", ", captureApps)
                : "(no apps selected for capture — enable HTTP Capture on an app in Connections)";
        }
    }

    // ── Proxy toolbar ─────────────────────────────────────────────────────────
    private void OnEnableSystemProxy(object sender, RoutedEventArgs e)
    {
        HttpProxyService.SetSystemProxyDirect(true);
        RefreshProxyStatus();
        StatusBar.Text = $"✓  System proxy set to 127.0.0.1:{HttpProxyService.ProxyPort}";
    }

    private void OnDisableSystemProxy(object sender, RoutedEventArgs e)
    {
        HttpProxyService.ForceRestoreSystemProxy();
        RefreshProxyStatus();
        StatusBar.Text = "✓  System proxy removed. Apps connect directly.";
    }

    private void OnFixConnectivity(object sender, RoutedEventArgs e)
    {
        if (_proxy?.IsRunning == true) _proxy.Stop();
        HttpProxyService.ForceRestoreSystemProxy();
        RefreshProxyStatus();
        StatusBar.Text = "✓  Proxy cleared from registry. Internet should be restored.";
    }

    private void OnInstallCert(object sender, RoutedEventArgs e)
    {
        try
        {
            _proxy?.InstallCertificate();
            StatusBar.Text = "✓  HTTPS certificate installed. Restart browser if needed.";
        }
        catch (Exception ex) { StatusBar.Text = $"✗  Cert error: {ex.Message}"; }
    }

    // ── Capture toolbar ───────────────────────────────────────────────────────
    private void OnStartCapture(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try
        {
            if (_proxy.IsRunning)
            {
                _proxy.Stop();
                HttpCaptureIcon.Glyph     = "\uE102";
                HttpCaptureBtnText.Text   = "Start Capture";
                StatusBar.Text            = "■  Capture stopped. System proxy cleared.";
                RefreshProxyStatus();
                return;
            }
            bool useSystemProxy = _vm?.CurrentConfig.EnableSystemProxy ?? true;
            _proxy.Start(useSystemProxy);
            HttpCaptureIcon.Glyph   = "\uE103";
            HttpCaptureBtnText.Text = "Stop Capture";
            RefreshProxyStatus();
            StatusBar.Text = $"●  Capturing on port {HttpProxyService.ProxyPort}" +
                             (useSystemProxy ? " (system proxy active)" : " (manual proxy mode)");
        }
        catch (Exception ex) { StatusBar.Text = $"✗  {ex.Message}"; }
    }

    private void OnClearRequests(object sender, RoutedEventArgs e)
    {
        _proxy?.Requests.Clear();
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

    private void OnCaptureAllToggled(object sender, RoutedEventArgs e)
    {
        // Re-apply PID filter when toggle changes
        _all.Clear();
        _visible.Clear();
        if (_proxy != null)
            foreach (var r in _proxy.Requests)
                if (MatchesPidFilter(r)) { _all.Add(r); if (PassesTextFilter(r)) _visible.Add(r); }
        UpdateBars();
    }

    // ── Context-menu helpers ──────────────────────────────────────────────────
    private HttpRequestInfo? RowItem(object sender)
    {
        if (sender is FrameworkElement fe && fe.DataContext is HttpRequestInfo r) return r;
        return RequestsListView.SelectedItem as HttpRequestInfo;
    }

    private void OnCopyUrl(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        var dp = new DataPackage(); dp.SetText(r.Url); Clipboard.SetContent(dp);
        StatusBar.Text = "URL copied.";
    }

    private void OnCopyHost(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        var dp = new DataPackage(); dp.SetText(r.Host); Clipboard.SetContent(dp);
        StatusBar.Text = "Host copied.";
    }

    private async void OnOpenInBrowser(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender); if (r == null) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri(r.Url)); }
        catch { StatusBar.Text = "✗  Could not open URL."; }
    }

    // ── Block actions ─────────────────────────────────────────────────────────
    private async void OnBlockHost(object sender, RoutedEventArgs e)
    {
        var r = RowItem(sender);
        if (r == null || string.IsNullOrWhiteSpace(r.Host)) return;

        var box = new TextBox { Text = r.Host };
        var dlg = new ContentDialog
        {
            Title             = "Block host in Hosts file",
            Content           = new StackPanel
            {
                Spacing  = 8,
                Children =
                {
                    new TextBlock { Text = "Host to block (will map to 0.0.0.0):", FontSize = 12 },
                    box,
                    new TextBlock
                    {
                        Text = "Adds an entry to C:\\Windows\\System32\\drivers\\etc\\hosts.\nRequires Administrator rights.",
                        FontSize = 11,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            },
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var (ok, err) = HostsFileService.BlockDomain(box.Text.Trim().ToLower(), r.ProcessName);
        StatusBar.Text = ok ? $"✓  {box.Text.Trim()} blocked in Hosts" : $"✗  {err}";
    }

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
        var (hOk, hErr) = HostsFileService.BlockDomain(r.Host, r.ProcessName);
        var (fOk, fErr) = await DoFirewallBlockSilent(r);
        StatusBar.Text =
            $"Hosts: {(hOk ? "✓" : "✗ " + hErr)}  |  Firewall: {(fOk ? "✓" : "✗ " + fErr)}";
    }

    private async System.Threading.Tasks.Task DoFirewallBlock(HttpRequestInfo r)
    {
        string host = r.Host.Trim().ToLower();
        StatusBar.Text = $"Resolving {host}…";
        string ip = await FirewallService.ResolveHostAsync(host);

        var ipBox = new TextBox { Text = string.IsNullOrEmpty(ip) ? host : ip };
        var dlg   = new ContentDialog
        {
            Title   = "Block in Windows Firewall",
            Content = new StackPanel
            {
                Spacing  = 8,
                Children =
                {
                    new TextBlock { Text = $"Host: {host}", FontSize = 12,
                                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = $"Resolved IP: {(string.IsNullOrEmpty(ip) ? "could not resolve" : ip)}", FontSize = 11 },
                    ipBox,
                    new TextBlock
                    {
                        Text = "Adds an outbound BLOCK rule for port 80 and 443.\nRequires Administrator rights.",
                        FontSize = 11, TextWrapping = TextWrapping.Wrap,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    }
                }
            },
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string target       = ipBox.Text.Trim();
        var (ok80, e80)     = FirewallService.AddOutboundBlockRule($"WinNetControl HTTP {host} [{r.ProcessName}]",  target, 80);
        var (ok443, e443)   = FirewallService.AddOutboundBlockRule($"WinNetControl HTTPS {host} [{r.ProcessName}]", target, 443);

        StatusBar.Text = (ok80 && ok443)
            ? $"✓  Firewall: blocked {target} on ports 80+443"
            : $"✗  FW80: {e80}  FW443: {e443}";
    }

    private async System.Threading.Tasks.Task<(bool, string)> DoFirewallBlockSilent(HttpRequestInfo r)
    {
        string host    = r.Host.Trim().ToLower();
        string ip      = await FirewallService.ResolveHostAsync(host);
        string target  = string.IsNullOrEmpty(ip) ? host : ip;
        var (ok80, e80) = FirewallService.AddOutboundBlockRule(
            $"WinNetControl HTTP {host} [{r.ProcessName}]",  target, 80);
        var (ok443, e443) = FirewallService.AddOutboundBlockRule(
            $"WinNetControl HTTPS {host} [{r.ProcessName}]", target, 443);
        return (ok80 && ok443, ok80 ? e443 : e80);
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
            StatusBar.Text = $"✓  Exported {_visible.Count} rows → {path}";
        }
        catch (Exception ex) { StatusBar.Text = $"✗  Export: {ex.Message}"; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string FormatSize(long b)
    {
        if (b < 1024)            return $"{b} B";
        if (b < 1024 * 1024)     return $"{b / 1024.0:F1} KB";
        return $"{b / 1024.0 / 1024:F2} MB";
    }
}
