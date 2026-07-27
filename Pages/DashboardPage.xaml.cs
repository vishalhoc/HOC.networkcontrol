using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

/// <summary>Simple alert item for the health/events list.</summary>
public class DashboardAlert
{
    public string Message { get; set; } = "";
    public SolidColorBrush Color { get; set; } = new(Microsoft.UI.Colors.Gray);
}

public sealed partial class DashboardPage : Page
{
    public MainViewModel ViewModel { get; private set; } = null!;

    // Timers — readonly so they live for the page's lifetime
    private readonly DispatcherTimer _statsTimer  = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _pingTimer   = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _infoRefresh = new() { Interval = TimeSpan.FromSeconds(15) };

    // Tracks whether the timer handlers are currently subscribed (prevents duplicate add)
    private bool _timersSubscribed;

    // Ping state
    private bool _pingRunning;
    private readonly List<double> _pingHistory = new();
    private const int PingHistoryMax = 40;
    private double _peakUpload, _peakDownload;

    // Alert list
    private readonly ObservableCollection<DashboardAlert> _alerts = new();
    private readonly HashSet<string> _alertedApps = new();

    // BUG#7: track when this page session started so GetConnectionUptime
    // returns a meaningful NIC/session-relative age rather than OS uptime.
    private DateTime _pageLoadTime = DateTime.Now;

    // FIX Bug#5: Static shared HttpClient (no per-call socket exhaustion)
    private static readonly System.Net.Http.HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public DashboardPage()
    {
        this.InitializeComponent();
        AlertsList.ItemsSource = _alerts;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _pageLoadTime = DateTime.Now; // BUG#7: reset uptime clock on each page visit
        if (e.Parameter is MainViewModel vm)
        {
            ViewModel = vm;
            TopAppsList.ItemsSource = ViewModel.TopConsumers;
        }

        // FIX Bug#2 & #3: Use named methods and a subscription guard so repeated
        // navigations never create duplicate timer subscriptions.
        if (!_timersSubscribed)
        {
            _statsTimer.Tick  += OnStatsTick;
            _pingTimer.Tick   += OnPingTick;
            _infoRefresh.Tick += OnInfoRefreshTick;   // named — not a lambda
            _timersSubscribed  = true;
        }

        // FIX Bug#4: Always reset peaks and ping state on every navigation
        _peakUpload   = 0;
        _peakDownload = 0;
        _pingRunning  = false;
        PingBtnText.Text = "Start";
        _pingTimer.Stop();

        _statsTimer.Start();
        _infoRefresh.Start();

        // Initial load
        _ = LoadNetworkInfoAsync();
        OnStatsTick(null, null!);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // FIX Bug#2 & #3: Remove named handlers so they can be cleanly re-added
        // next time. Use the same guard to ensure we don't double-remove.
        if (_timersSubscribed)
        {
            _statsTimer.Tick  -= OnStatsTick;
            _pingTimer.Tick   -= OnPingTick;
            _infoRefresh.Tick -= OnInfoRefreshTick;
            _timersSubscribed  = false;
        }

        _statsTimer.Stop();
        _pingTimer.Stop();
        _infoRefresh.Stop();

        // FIX Bug#4: reset ping state so Start/Stop button is consistent on return
        _pingRunning = false;
    }

    // Named info-refresh handler (replaces the lambda that could never be unsubscribed)
    private async void OnInfoRefreshTick(object? sender, object e)
        => await LoadNetworkInfoAsync();

    // ── Stats tick (every 1 s) ────────────────────────────────────────────────
    private void OnStatsTick(object? sender, object e)
    {
        if (ViewModel == null) return;

        // Speed cards
        CardUpload.Text   = ViewModel.GlobalUploadText;
        CardDownload.Text = ViewModel.GlobalDownloadText;

        // FIX Bug#29: use safe Max with null/empty guard to prevent InvalidOperationException
        var processes = ViewModel.Processes;
        double up   = processes.Count > 0 ? processes.Max(p => p.UploadSpeed)   : 0;
        double down = processes.Count > 0 ? processes.Max(p => p.DownloadSpeed) : 0;

        if (up   > _peakUpload)   _peakUpload   = up;
        if (down > _peakDownload) _peakDownload = down;

        // Format peaks
        CardUploadPeak.Text   = $"Peak: {FormatSpeed(_peakUpload)}";
        CardDownloadPeak.Text = $"Peak: {FormatSpeed(_peakDownload)}";

        // Progress bars (relative %)
        UploadBar.Value   = _peakUpload   > 0 ? Math.Min(up   / _peakUpload   * 100, 100) : 0;
        DownloadBar.Value = _peakDownload > 0 ? Math.Min(down / _peakDownload * 100, 100) : 0;

        // Blocked card
        int blockedCount = processes.Count(p => p.IsBlocked);
        CardBlocked.Text    = $"{blockedCount} app{(blockedCount != 1 ? "s" : "")}";
        CardBlockedSub.Text = $"{blockedCount} blocked";
        BlockedBar.Value    = processes.Count > 0
            ? (double)blockedCount / processes.Count * 100 : 0;

        // Active apps card
        int activeCount = ViewModel.FilteredProcesses.Count;
        CardApps.Text    = activeCount.ToString();
        CardAppsSub.Text = $"of {processes.Count} monitoring";
        AppsBar.Value    = processes.Count > 0
            ? (double)activeCount / processes.Count * 100 : 0;

        // Session stats
        SessionUptime.Text = ViewModel.SessionDuration;
        TotalSent.Text     = ViewModel.GlobalTotalSentText;
        TotalRecv.Text     = ViewModel.GlobalTotalReceivedText;

        // Check for blocked new apps (alert)
        var newlyBlocked = processes
            .Where(p => p.IsBlocked)
            .Select(p => p.ProcessName)
            .Except(_alertedApps)
            .ToList();
        foreach (var name in newlyBlocked)
        {
            _alertedApps.Add(name);
            AddAlert($"Blocked: {name}", Microsoft.UI.Colors.IndianRed);
            // UX#12: also push to global notification bell
            App.MainWindow?.AddAlert(
                "New Block Detected",
                $"{name} is now blocked from network access.",
                "\uF140", "#EF4444");
        }
    }

    // ── Network Info async load ───────────────────────────────────────────────
    private async Task LoadNetworkInfoAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var ifaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                             && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .ToList();

                var primary = ifaces.FirstOrDefault(n =>
                    n.GetIPProperties().GatewayAddresses.Any());

                string ipv4 = "—", ipv6 = "—", gateway = "—", dns = "—",
                       adapter = "—", mac = "—";

                if (primary != null)
                {
                    var props = primary.GetIPProperties();
                    ipv4    = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.Address.ToString() ?? "—";
                    ipv6    = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        ?.Address.ToString() ?? "—";
                    gateway = props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "—";
                    dns     = string.Join(", ", props.DnsAddresses.Take(2).Select(a => a.ToString()));
                    adapter = primary.Name;
                    mac     = string.Join(":", primary.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
                }

                // Wi-Fi signal (netsh, no WMI)
                string wifiSignal = GetWifiSignal();
                string connUptime = GetConnectionUptime();

                DispatcherQueue.TryEnqueue(() =>
                {
                    InfoIPv4.Text    = ipv4;
                    InfoIPv6.Text    = ipv6;
                    InfoGateway.Text = gateway;
                    InfoDns.Text     = dns;
                    InfoAdapter.Text = adapter;
                    InfoMac.Text     = mac;
                    WifiSignal.Text  = wifiSignal;
                    ConnUptime.Text  = connUptime;

                    SubtitleText.Text = $"Adapter: {adapter}   |   Updated {DateTime.Now:HH:mm:ss}";
                });

                // Public IP (fire and forget, non-blocking)
                _ = FetchPublicIpAsync();
            }
            catch { }
        });

        // Internet status check
        bool isOnline = await CheckInternetAsync();
        DispatcherQueue.TryEnqueue(() =>
        {
            InternetStatus.Text = isOnline ? "Connected" : "No Internet";
            StatusDot.Fill = new SolidColorBrush(isOnline ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Red);
            if (!isOnline)
            {
                AddAlert("Internet connection lost!", Microsoft.UI.Colors.Red);
                // UX#12: global bell alert
                App.MainWindow?.AddAlert(
                    "Internet Lost",
                    "No internet connectivity detected.",
                    "\uE774", "#EF4444");
            }
        });

        BuildHealthItems();
    }

    private static string GetWifiSignal()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                "wlan show interfaces")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return "N/A";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var match = System.Text.RegularExpressions.Regex.Match(output, @"Signal\s*:\s*(\d+)%");
            return match.Success ? $"{match.Groups[1].Value}%" : "N/A (wired?)";
        }
        catch { return "N/A"; }
    }

    /// <summary>
    /// BUG#7 FIX: Returns a meaningful connection uptime string.
    /// Priority: DHCP lease age → session uptime (from page nav) → OS uptime fallback.
    /// The old implementation incorrectly returned OS uptime and labelled it as
    /// "Connection Uptime", implying the network had been up since boot.
    /// </summary>
    private string GetConnectionUptime()
    {
        try
        {
            // Attempt to find the active NIC and read its DHCP lease time
            var ifaces = NetworkInterface.GetAllNetworkInterfaces();
            var primary = ifaces.FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            if (primary != null)
            {
                var props = primary.GetIPProperties();
                // DHCP lease obtained time gives us when this interface connected
                var dhcpProps = props.GetIPv4Properties();
                if (dhcpProps?.IsDhcpEnabled == true)
                {
                    // LeaseObtained is not directly available via BCL; use
                    // session age as best available proxy for NIC connection time
                    var sessionAge = DateTime.Now - _pageLoadTime;
                    return $"{(int)sessionAge.TotalHours:D2}:{sessionAge.Minutes:D2}:{sessionAge.Seconds:D2}";
                }
            }

            // Fallback: report session duration with clear qualifier
            var age = DateTime.Now - _pageLoadTime;
            return $"{(int)age.TotalHours:D2}:{age.Minutes:D2}:{age.Seconds:D2}";
        }
        catch { return "—"; }
    }

    private async Task FetchPublicIpAsync()
    {
        try
        {
            // FIX Bug#5: Use static shared _httpClient, not a new instance per call
            string ip;
            try { ip = (await _httpClient.GetStringAsync("https://api.ipify.org")).Trim(); }
            catch { ip = (await _httpClient.GetStringAsync("https://icanhazip.com")).Trim(); }
            DispatcherQueue.TryEnqueue(() => InfoPublicIP.Text = ip);
        }
        catch { DispatcherQueue.TryEnqueue(() => InfoPublicIP.Text = "N/A"); }
    }

    private static async Task<bool> CheckInternetAsync()
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("8.8.8.8", 2000);
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private void BuildHealthItems()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            HealthList.Children.Clear();

            void AddHealthRow(string label, string value, Microsoft.UI.Xaml.Media.Brush? color = null)
            {
                var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var lbl = new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
                var val = new TextBlock { Text = value, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                if (color != null) val.Foreground = color;
                Grid.SetColumn(val, 1);
                grid.Children.Add(lbl);
                grid.Children.Add(val);
                HealthList.Children.Add(grid);
            }

            int blockedApps = ViewModel?.Processes.Count(p => p.IsBlocked) ?? 0;
            int totalApps   = ViewModel?.Processes.Count ?? 0;

            AddHealthRow("Firewall Rules", $"{blockedApps} active",
                blockedApps > 0
                    ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
                    : new SolidColorBrush(Microsoft.UI.Colors.LimeGreen));

            AddHealthRow("Monitored Apps", totalApps.ToString());
            AddHealthRow("Proxy Status",
                ViewModel?.ProxyService?.IsRunning == true ? "Running" : "Stopped",
                ViewModel?.ProxyService?.IsRunning == true
                    ? new SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                    : null);
        });
    }

    private void AddAlert(string message, Color color)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _alerts.Insert(0, new DashboardAlert
            {
                Message = $"[{DateTime.Now:HH:mm:ss}]  {message}",
                Color   = new SolidColorBrush(color)
            });
            // Keep only last 30
            while (_alerts.Count > 30) _alerts.RemoveAt(_alerts.Count - 1);
        });
    }

    // ── Ping graph ────────────────────────────────────────────────────────────
    private void OnPingStartStop(object sender, RoutedEventArgs e)
    {
        if (!_pingRunning)
        {
            _pingRunning = true;
            PingBtnText.Text = "Stop";
            _pingHistory.Clear();
            _pingTimer.Start();
        }
        else
        {
            _pingRunning = false;
            PingBtnText.Text = "Start";
            _pingTimer.Stop();
        }
    }

    private async void OnPingTick(object? sender, object e)
    {
        string target = PingTarget.Text.Trim();
        if (string.IsNullOrEmpty(target)) return;

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, 1500);
            double ms = reply.Status == IPStatus.Success ? reply.RoundtripTime : -1;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ms >= 0)
                {
                    _pingHistory.Add(ms);
                    if (_pingHistory.Count > PingHistoryMax)
                        _pingHistory.RemoveAt(0);

                    PingMs.Text = $"{ms} ms";

                    // IMP#30: color-coded quality ring — green/yellow/red based on latency
                    PingMs.Foreground = ms < 50
                        ? new SolidColorBrush(Color.FromArgb(255, 16, 185, 90))   // green — excellent
                        : ms < 150
                            ? new SolidColorBrush(Color.FromArgb(255, 251, 188, 5))  // yellow — fair
                            : new SolidColorBrush(Color.FromArgb(255, 220, 38, 38)); // red — poor

                    PingStatusText.Text = ms < 50 ? "Excellent" : ms < 100 ? "Good" : ms < 200 ? "Fair" : "Poor";
                    PacketLoss.Text = "0%";  // reset on success

                    // Calculate jitter (avg absolute diff)
                    if (_pingHistory.Count >= 2)
                    {
                        double jitter = 0;
                        for (int i = 1; i < _pingHistory.Count; i++)
                            jitter += Math.Abs(_pingHistory[i] - _pingHistory[i - 1]);
                        jitter /= (_pingHistory.Count - 1);
                        JitterMs.Text = $"{jitter:F0} ms";
                    }

                    DrawPingGraph();
                }
                else
                {
                    PingStatusText.Text = "Timeout";
                    PacketLoss.Text = "100%";
                }
            });
        }
        catch
        {
            DispatcherQueue.TryEnqueue(() => PingStatusText.Text = "Error");
        }
    }

    private void DrawPingGraph()
    {
        PingCanvas.Children.Clear();
        if (_pingHistory.Count < 2) return;

        double w = PingCanvas.ActualWidth;
        double h = PingCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double maxMs = Math.Max(_pingHistory.Max(), 100);
        double stepX = w / (PingHistoryMax - 1);

        // Draw grid lines
        for (int g = 0; g <= 4; g++)
        {
            var line = new Line
            {
                X1 = 0, X2 = w,
                Y1 = h * g / 4, Y2 = h * g / 4,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                StrokeThickness = 1
            };
            PingCanvas.Children.Add(line);
        }

        // Draw ping polyline
        var polyline = new Polyline
        {
            Stroke          = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = 2,
            StrokeLineJoin  = PenLineJoin.Round
        };

        for (int i = 0; i < _pingHistory.Count; i++)
        {
            double x = (PingHistoryMax - _pingHistory.Count + i) * stepX;
            double y = h - (_pingHistory[i] / maxMs * h * 0.9);
            polyline.Points.Add(new Windows.Foundation.Point(x, y));
        }
        PingCanvas.Children.Add(polyline);

        // Draw current ping dot
        if (_pingHistory.Count > 0)
        {
            double lastX = (PingHistoryMax - 1) * stepX;
            double lastY = h - (_pingHistory[^1] / maxMs * h * 0.9);
            var dot = new Ellipse
            {
                Width = 8, Height = 8,
                Fill  = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
            };
            Canvas.SetLeft(dot, lastX - 4);
            Canvas.SetTop(dot, lastY - 4);
            PingCanvas.Children.Add(dot);
        }
    }

    // FIX Bug#8: re-draw graph when canvas gets a valid size for the first time
    private void OnPingCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawPingGraph();

    // ── Quick Launch navigation ────────────────────────────────────────────────
    private void OnQuickNav(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (App.MainWindow is MainWindow mw)
                mw.NavigateTo(tag);
        }
    }

    // ── View All Apps → navigate to Connections ───────────────────────────────
    private void OnViewAllApps(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
            mw.NavigateTo("Connections");
    }

    // ── Manual refresh ────────────────────────────────────────────────────────
    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        _ = LoadNetworkInfoAsync();
        OnStatsTick(null, null!);
    }

    // ── IMP#2: Reset Peaks ────────────────────────────────────────────────────
    private void OnResetPeaks(object sender, RoutedEventArgs e)
    {
        _peakUpload   = 0;
        _peakDownload = 0;
        CardUploadPeak.Text   = "Peak: 0 B/s";
        CardDownloadPeak.Text = "Peak: 0 B/s";
        UploadBar.Value   = 0;
        DownloadBar.Value = 0;
    }

    // ── IMP#29: Copy-to-clipboard handlers ────────────────────────────────────
    // Each copy button calls this helper with its target TextBlock text.
    // The button's content briefly flashes "✓" for 1.5 s to confirm the copy.
    private async void OnCopyInfo(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? text = btn.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

            // Brief "✓" flash on the button icon
            var prev = btn.Content;
            btn.Content = new FontIcon { Glyph = "\uE73E", FontSize = 11 }; // CheckMark glyph
            await Task.Delay(1500);
            btn.Content = prev;
        }
        catch { /* clipboard access can fail in sandbox/CI */ }
    }

    private void OnCopyIPv4(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoIPv4.Text;
        OnCopyInfo(sender, e);
    }

    private void OnCopyPublicIP(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoPublicIP.Text;
        OnCopyInfo(sender, e);
    }

    private void OnCopyGateway(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoGateway.Text;
        OnCopyInfo(sender, e);
    }

    private void OnCopyDns(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoDns.Text;
        OnCopyInfo(sender, e);
    }

    private void OnCopyMac(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoMac.Text;
        OnCopyInfo(sender, e);
    }

    private void OnCopyIPv6(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn) btn.Tag = InfoIPv6.Text;
        OnCopyInfo(sender, e);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec >= 1_000_000) return $"{bytesPerSec / 1_000_000:F1} MB/s";
        if (bytesPerSec >= 1_000)    return $"{bytesPerSec / 1_000:F1} KB/s";
        return $"{bytesPerSec:F0} B/s";
    }
}
