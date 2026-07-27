using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

public partial class SocketRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public string Proto         { get; set; } = "";
    public string LocalAddress  { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string State         { get; set; } = "";
    public string ProcessName   { get; set; } = "";
    public string AppName       { get; set; } = "";
    public string ProcessPath   { get; set; } = "";
    public int    Pid           { get; set; }
    public int    LocalPort     { get; set; }
    public int    RemotePort    { get; set; }

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private Microsoft.UI.Xaml.Media.ImageSource? _appIcon;

    public string AppIconGlyph => ProcessName.ToLowerInvariant() switch
    {
        var name when name.Contains("chrome") || name.Contains("msedge") || name.Contains("firefox") || name.Contains("opera") => "\uE774",
        var name when name.Contains("explorer") => "\uE8B7",
        var name when name.Contains("service") || name.Contains("system") || name.Contains("svchost") => "\uE713",
        var name when name.Contains("powershell") || name.Contains("cmd") || name.Contains("terminal") => "\uE756",
        _ => "\uE8A5"
    };

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    [CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(ActionBrush), nameof(BlockBorderBrush), nameof(BlockIcon), nameof(BlockToolTip))]
    private bool _isBlocked;

    public SolidColorBrush ProtoBrush => Proto == "UDP"
        ? new SolidColorBrush(Microsoft.UI.Colors.Orange)
        : new SolidColorBrush(Microsoft.UI.Colors.SteelBlue);

    public SolidColorBrush StateForeground => State switch
    {
        "LISTENING"    => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        "ESTABLISHED"  => new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
        "TIME_WAIT"
        or "CLOSE_WAIT" => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        _              => new SolidColorBrush(Microsoft.UI.Colors.Gray)
    };

    public SolidColorBrush ActionBrush => IsBlocked
        ? new SolidColorBrush(Microsoft.UI.Colors.Red)
        : new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
    public SolidColorBrush BlockBorderBrush => IsBlocked
        ? new SolidColorBrush(Microsoft.UI.Colors.IndianRed)
        : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    public string BlockIcon => IsBlocked ? "\uE711" : "\uE72E";
    public string BlockToolTip => IsBlocked ? "Unblock connection" : "Block connection";
}

public sealed partial class SocketManagerPage : Page
{
    private List<SocketRow>                          _all  = new();
    private readonly ObservableCollection<SocketRow> _view = new();
    private readonly Dictionary<int, string> _pidCache = new();
    private readonly Dictionary<int, string> _pathCache = new();
    private WinNetControl.ViewModels.MainViewModel? _viewModel;

    private System.Threading.Timer? _autoTimer;

    // In-memory QoS expiry timers: policyName → CancellationTokenSource
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _qosTimers = new();

    public SocketManagerPage() { this.InitializeComponent(); SocketList.ItemsSource = _view; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
            _viewModel = vm;
        // Subscribe to cross-module sync
        BlockedConnectionStore.ConnectionBlockChanged += OnExternalBlockChanged;
        _ = LoadSocketsAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        BlockedConnectionStore.ConnectionBlockChanged -= OnExternalBlockChanged;
        _autoTimer?.Dispose();
        _autoTimer = null;
    }

    // ── External sync ─────────────────────────────────────────────────────────
    private void OnExternalBlockChanged(string processName, string remoteIp, int remotePort, int localPort, bool isBlocked)
    {
        // Another module changed a block state — update matching rows in our list
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var row in _all)
            {
                if ((string.IsNullOrWhiteSpace(processName) ||
                     string.Equals(row.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) &&
                    StripPort(row.RemoteAddress) == remoteIp &&
                    row.RemotePort == remotePort &&
                    row.LocalPort == localPort)
                {
                    row.IsBlocked = isBlocked;
                }
            }
        });
    }

    // ── Context menu (right-click socket row) ─────────────────────────────────
    private void OnTraceRoute(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SocketRow row)
        {
            string ip = StripPort(row.RemoteAddress);
            if (string.IsNullOrEmpty(ip) || ip == "—") return;
            if (_viewModel != null)
                _viewModel.TargetPacketJourneyIp = ip;
            var mainWindow = App.MainWindow;
            mainWindow?.NavigateTo("Journey");
        }
    }

    private void OnCopyRemoteIp(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is SocketRow row)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(StripPort(row.RemoteAddress));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
    }

    // ── Whole-app QoS (existing behaviour, app-path policy) ──────────────────
    private async void OnSetSocketQosHigh(object sender, RoutedEventArgs e)
        => await SetSocketAppQosAsync(sender, QosPolicyService.HighPriorityDscp);

    private async void OnSetSocketQosStandard(object sender, RoutedEventArgs e)
        => await SetSocketAppQosAsync(sender, QosPolicyService.StandardPriorityDscp);

    private async void OnSetSocketQosLow(object sender, RoutedEventArgs e)
        => await SetSocketAppQosAsync(sender, QosPolicyService.LowPriorityDscp);

    private async Task SetSocketAppQosAsync(object sender, int dscp)
    {
        if (sender is not MenuFlyoutItem { Tag: SocketRow row }) return;
        if (string.IsNullOrWhiteSpace(row.ProcessPath))
        {
            SocketStatus.Text = "⚠ Windows did not expose this app's executable path. Use 'This socket only' instead.";
            return;
        }
        SocketStatus.Text = $"Applying {QosPolicyService.PriorityLabel(dscp)} to all {row.ProcessName} traffic…";
        var result = await QosPolicyService.SetPriorityAsync(row.ProcessPath, row.ProcessName, dscp);
        SocketStatus.Text = result.Success
            ? $"✓ Whole-app policy applied to {row.ProcessName}. All traffic from this app will be marked {QosPolicyService.PriorityLabel(dscp)}."
            : $"QoS not applied: {result.Message}";
        HistoryLogService.AddLog("Socket", row.ProcessName,
            result.Success ? $"QoS {QosPolicyService.PriorityLabel(dscp)} (whole app)" : $"QoS failed: {result.Message}");
    }

    // ── Socket-only QoS (destination IP:Port policy) ───────────────────────────
    private async void OnSetSocketOnlyQosHigh(object sender, RoutedEventArgs e)
        => await SetSocketOnlyQosAsync(sender, QosPolicyService.HighPriorityDscp);

    private async void OnSetSocketOnlyQosStandard(object sender, RoutedEventArgs e)
        => await SetSocketOnlyQosAsync(sender, QosPolicyService.StandardPriorityDscp);

    private async void OnSetSocketOnlyQosLow(object sender, RoutedEventArgs e)
        => await SetSocketOnlyQosAsync(sender, QosPolicyService.LowPriorityDscp);

    private async Task SetSocketOnlyQosAsync(object sender, int dscp)
    {
        if (sender is not MenuFlyoutItem { Tag: SocketRow row }) return;
        string remoteIp = StripPort(row.RemoteAddress);
        if (string.IsNullOrEmpty(remoteIp) || remoteIp is "—" or "0.0.0.0" or "::")
        {
            SocketStatus.Text = "⚠ This socket has no remote IP — cannot create a destination-specific policy.";
            return;
        }
        if (row.RemotePort <= 0)
        {
            SocketStatus.Text = "⚠ No remote port found. Use 'Set app QoS priority' instead.";
            return;
        }

        string label = QosPolicyService.PriorityLabel(dscp);
        SocketStatus.Text = $"Applying {label} to {remoteIp}:{row.RemotePort}/{row.Proto}…";

        // Destination-only policy: no app path — targets exact IP:port for all apps
        var result = await QosPolicyService.SetPriorityAsync(
            processPath: "",
            processName: row.ProcessName,
            dscp: dscp,
            destinationIp: remoteIp,
            destinationPort: row.RemotePort,
            protocol: row.Proto);

        if (result.Success)
        {
            string policyName = QosPolicyService.BuildPolicyName(row.ProcessName, remoteIp, row.RemotePort);
            SocketStatus.Text =
                $"✓ {label} applied to {remoteIp}:{row.RemotePort}/{row.Proto} only. " +
                $"Policy '{policyName}' is active until reboot or removal.";
            HistoryLogService.AddLog("Socket", row.ProcessName,
                $"QoS {label} socket-only → {remoteIp}:{row.RemotePort}/{row.Proto}");
        }
        else
        {
            SocketStatus.Text = $"QoS not applied: {result.Message}";
        }
    }

    // ── Auto-expire timer dialog ───────────────────────────────────────────────
    private async void OnSetSocketQosTimer(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: SocketRow row }) return;
        string remoteIp  = StripPort(row.RemoteAddress);
        string policyName = QosPolicyService.BuildPolicyName(row.ProcessName, remoteIp, row.RemotePort);

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (lbl, mins) in new[] { ("5 minutes", 5), ("15 minutes", 15),
            ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120) })
            combo.Items.Add(new ComboBoxItem { Content = lbl, Tag = mins });
        combo.SelectedIndex = 1;

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Auto-remove the QoS policy for {remoteIp}:{row.RemotePort} after:",
            TextWrapping = TextWrapping.Wrap, FontSize = 13
        });
        panel.Children.Add(combo);
        panel.Children.Add(new TextBlock
        {
            Text = $"Policy name: {policyName}",
            FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
        });

        var dlg = new ContentDialog
        {
            Title = "Set QoS Policy Auto-Expire",
            Content = panel,
            PrimaryButtonText   = "Set Timer",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        if (combo.SelectedItem is not ComboBoxItem { Tag: int minutes }) return;

        // Cancel any existing timer for this policy
        if (_qosTimers.TryRemove(policyName, out var oldCts))
            oldCts.Cancel();

        var cts = new CancellationTokenSource();
        _qosTimers[policyName] = cts;

        SocketStatus.Text = $"⏱ QoS policy '{policyName}' will auto-expire in {minutes} minute(s).";

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), cts.Token);
                await QosPolicyService.RemovePolicyAsync(policyName);
                _qosTimers.TryRemove(policyName, out _);
                DispatcherQueue.TryEnqueue(() =>
                    SocketStatus.Text = $"⏰ QoS policy '{policyName}' expired and was removed.");
            }
            catch (OperationCanceledException) { /* timer was cancelled — no action needed */ }
        }, CancellationToken.None);
    }

    private void OnOpenQosManager(object sender, RoutedEventArgs e)
        => (App.MainWindow)?.NavigateTo("Qos");

    private void OnRefreshSockets(object sender, RoutedEventArgs e) => _ = LoadSocketsAsync();
    private void OnProtoFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void OnStateFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void OnSocketSearch(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnRefreshRateChanged(object sender, SelectionChangedEventArgs e)
    {
        _autoTimer?.Dispose();
        _autoTimer = null;
        if (RefreshRate?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int seconds) || seconds <= 0) return;

        _autoTimer = new System.Threading.Timer(_ =>
            DispatcherQueue.TryEnqueue(() => _ = LoadSocketsAsync()), null,
            TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds));
    }

    // ── Load ──────────────────────────────────────────────────────────────────
    private async Task LoadSocketsAsync()
    {
        SocketStatus.Text = "Loading…";
        _all = await Task.Run(() =>
        {
            // netstat -ano: all connections + owning PID
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var rows = new List<SocketRow>();
            foreach (var line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Proto", StringComparison.OrdinalIgnoreCase) || t.Length == 0) continue;

                var parts = Regex.Split(t, @"\s+").Where(s => s.Length > 0).ToArray();
                if (parts.Length < 4) continue;

                string proto  = parts[0].ToUpperInvariant();
                string local  = parts[1];
                bool   hasTcp = proto == "TCP";
                string remote = hasTcp && parts.Length >= 3 ? parts[2] : "—";
                string state  = hasTcp && parts.Length >= 4 ? parts[3].ToUpperInvariant() : "";
                int    pid    = 0;
                if (hasTcp && parts.Length >= 5) int.TryParse(parts[4], out pid);
                else if (!hasTcp && parts.Length >= 4) int.TryParse(parts[3], out pid);

                rows.Add(new SocketRow
                {
                    Proto = proto, LocalAddress = local, RemoteAddress = remote,
                    State = state, Pid = pid,
                    LocalPort = ExtractPort(local),
                    RemotePort = ExtractPort(remote)
                });
            }
            return rows;
        });

        // Resolve PID → process names and paths in background
        await Task.Run(() =>
        {
            foreach (var row in _all)
            {
                if (row.Pid == 0) continue;
                if (!_pidCache.TryGetValue(row.Pid, out string? name))
                {
                    try 
                    { 
                        using var p = Process.GetProcessById(row.Pid);
                        name = p.ProcessName; 
                        _pathCache[row.Pid] = p.MainModule?.FileName ?? "";
                    }
                    catch { name = $"PID {row.Pid}"; _pathCache[row.Pid] = ""; }
                    _pidCache[row.Pid] = name;
                }
                row.ProcessName = name;
                row.ProcessPath = _pathCache.TryGetValue(row.Pid, out var path) ? path : "";
                
                if (!string.IsNullOrEmpty(row.ProcessPath))
                {
                    try
                    {
                        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(row.ProcessPath);
                        row.AppName = !string.IsNullOrWhiteSpace(fvi.FileDescription) ? fvi.FileDescription : row.ProcessName;
                    }
                    catch { row.AppName = row.ProcessName; }
                }
                else
                {
                    row.AppName = row.ProcessName;
                }
                
                // Initialize block state
                if (_viewModel != null && _viewModel.CurrentConfig != null)
                {
                    string remoteIp = StripPort(row.RemoteAddress);
                    row.IsBlocked = BlockedConnectionStore.IsBlocked(row.ProcessName, remoteIp, row.RemotePort, row.LocalPort) ||
                        _viewModel.CurrentConfig.BlockedConnections.Any(b =>
                        b.ProcessName == row.ProcessName && b.RemoteAddress == remoteIp &&
                        b.RemotePort == row.RemotePort && b.LocalPort == row.LocalPort);
                }
            }
        });

        // Load Icons (needs UI thread for ImageSource instantiation)
        if (_viewModel != null)
        {
            foreach (var row in _all)
            {
                if (!string.IsNullOrEmpty(row.ProcessPath))
                {
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        var img = await _viewModel.IconCache.GetIconAsync(row.ProcessPath);
                        if (img != null)
                        {
                            App.DispatcherQueue.TryEnqueue(() => row.AppIcon = img);
                        }
                    });
                }
            }
        }

        ApplyFilter();
        SocketCountText.Text = $"Live socket table · {_all.Count} sockets · refreshed {DateTime.Now:HH:mm:ss}";
        SocketStatus.Text = "";
    }

    private static int ExtractPort(string addr)
    {
        if (string.IsNullOrEmpty(addr) || addr == "—") return 0;
        int lastColon = addr.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < addr.Length - 1)
        {
            if (int.TryParse(addr.Substring(lastColon + 1), out int port))
                return port;
        }
        return 0;
    }

    private void ApplyFilter()
    {
        if (SocketSearch == null || ProtoFilter == null || StateFilter == null) return;
        string proto  = (ProtoFilter.SelectedItem  as ComboBoxItem)?.Content?.ToString() ?? "All";
        string state  = (StateFilter.SelectedItem  as ComboBoxItem)?.Content?.ToString() ?? "All States";
        string q      = SocketSearch.Text.Trim().ToLowerInvariant();

        _view.Clear();
        foreach (var row in _all)
        {
            if (proto != "All" && !row.Proto.Equals(proto, StringComparison.OrdinalIgnoreCase)) continue;
            if (state != "All States" && !row.State.Equals(state, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.Length > 0 &&
                !row.LocalAddress.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !row.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !row.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !row.Pid.ToString().Contains(q)) continue;
            _view.Add(row);
        }
        SocketStatus.Text = $"{_view.Count} shown";
    }

    // ── Actions ───────────────────────────────────────────────────────────────
    private async void OnKillProcess(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not int pid || pid == 0) return;
        try
        {
            string name = _pidCache.TryGetValue(pid, out var n) ? n : pid.ToString();
            var dlg = new ContentDialog
            {
                Title   = "Kill Process?",
                Content = $"Kill process {name} (PID {pid})?",
                PrimaryButtonText   = "Kill",
                SecondaryButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            await Task.Run(() => Process.GetProcessById(pid).Kill());
            SocketStatus.Text = $"✅ Process {name} (PID {pid}) killed.";
            HistoryLogService.AddLog("Socket", name, $"Killed PID {pid}");
            await LoadSocketsAsync();
        }
        catch (Exception ex) { SocketStatus.Text = $"Error: {ex.Message}"; }
    }

    private void OnBlockSocket(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SocketRow row) return;

        // Strip port correctly for both IPv4 and IPv6
        string remoteIp = StripPort(row.RemoteAddress);

        if (remoteIp is "—" or "0.0.0.0" or "::" or "" || string.IsNullOrEmpty(remoteIp))
        {
            SocketStatus.Text = "No valid remote IP to block."; return;
        }

        bool isNowBlocked = !row.IsBlocked;
        row.IsBlocked = isNowBlocked;
        SocketStatus.Text = isNowBlocked ? "Blocking…" : "Unblocking…";

        bool syncSuccess = false;
        if (_viewModel != null)
        {
            var process = _viewModel.Processes.FirstOrDefault(p => p.ProcessId == row.Pid);
            if (process != null)
            {
                var conn = process.Connections.FirstOrDefault(c => 
                    c.RemoteAddress == remoteIp && c.RemotePort == row.RemotePort && c.LocalPort == row.LocalPort);
                    
                if (conn == null)
                {
                    conn = new WinNetControl.Models.ProcessConnection
                    {
                        ProcessId = row.Pid,
                        RemoteAddress = remoteIp,
                        RemotePort = row.RemotePort,
                        LocalPort = row.LocalPort,
                        Protocol = row.Proto
                    };
                    process.Connections.Add(conn);
                }
                
                _viewModel.ToggleConnectionBlock(conn, blockInbound: isNowBlocked, blockOutbound: isNowBlocked);
                syncSuccess = true;
            }
        }

        // If ViewModel couldn't handle it (e.g., process not yet monitored), fallback to FirewallService
        if (!syncSuccess)
        {
            Task.Run(() => FirewallService.BlockConnection(row.ProcessPath, remoteIp, row.RemotePort, row.LocalPort, isNowBlocked, isNowBlocked));
            SocketStatus.Text = isNowBlocked ? $"✅ {remoteIp} blocked for {row.ProcessName}." : $"✅ {remoteIp} unblocked for {row.ProcessName}.";
            HistoryLogService.AddLog("Socket", row.ProcessName, $"{(isNowBlocked ? "Blocked" : "Unblocked")} connection {remoteIp}:{row.RemotePort}");
        }

        // ToggleConnectionBlock broadcasts the update. The fallback must do so itself.
        if (!syncSuccess)
            BlockedConnectionStore.NotifyBlockChange(row.ProcessName, remoteIp, row.RemotePort, row.LocalPort, isNowBlocked);
        SocketStatus.Text = isNowBlocked
            ? $"🛡 {remoteIp}:{row.RemotePort} blocked for {row.AppName}."
            : $"✅ {remoteIp}:{row.RemotePort} unblocked for {row.AppName}.";
    }

    /// <summary>Strips port suffix from IPv4 (1.2.3.4:80) and IPv6 ([::1]:80 or ::1) addresses.</summary>
    private static string StripPort(string addr)
    {
        if (string.IsNullOrEmpty(addr) || addr == "—") return addr;
        // IPv6 bracket notation: [::1]:port  →  ::1
        if (addr.StartsWith('['))
        {
            int end = addr.IndexOf(']');
            return end > 0 ? addr[1..end] : addr;
        }
        // IPv4 with port: 1.2.3.4:80 — only strip if exactly one colon (not raw IPv6)
        int colon = addr.LastIndexOf(':');
        if (colon > 0 && addr.IndexOf(':') == colon)   // single colon = IPv4:port
            return addr[..colon];
        // Raw IPv6 without brackets — keep as-is
        return addr;
    }
}
