using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

public class ThreatRow
{
    public string ProcessName { get; set; } = "";
    public string RemoteIp    { get; set; } = "";
    public string RemotePort  { get; set; } = "";
    public string GeoLabel    { get; set; } = "";
    public bool   IsSuspicious { get; set; }
    public string ThreatTag   => IsSuspicious ? "⚠ SUSPICIOUS" : "FOREIGN";
    public string ThreatIcon  => IsSuspicious ? "\uE7BA" : "\uE704";
    public SolidColorBrush ThreatColor => IsSuspicious
        ? new SolidColorBrush(Color.FromArgb(255, 224, 32, 32))
        : new SolidColorBrush(Color.FromArgb(255, 251, 188, 5));
    // Cross-module: store full path for firewall block
    public string ProcessPath { get; set; } = "";
}

public sealed partial class SecurityPage : Page
{
    private MainViewModel? _vm;
    private readonly ObservableCollection<ThreatRow> _threats = new();
    private ThreatRow? _selected;

    // Known risky ports for listening check
    private static readonly Dictionary<int, string> _riskyPorts = new()
    {
        {21, "FTP"}, {23, "Telnet"}, {25, "SMTP"}, {53, "DNS"},
        {111, "RPC"}, {135, "MSRPC"}, {137, "NetBIOS"}, {139, "NetBIOS"},
        {445, "SMB"}, {1433, "SQL Server"}, {3306, "MySQL"},
        {3389, "RDP"}, {5900, "VNC"}, {6379, "Redis"}, {27017, "MongoDB"}
    };

    public SecurityPage() { this.InitializeComponent(); ThreatList.ItemsSource = _threats; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        _ = ScanAsync();
    }

    // ── Scan ──────────────────────────────────────────────────────────────────
    private void OnScan(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async Task ScanAsync()
    {
        if (_vm == null) return;

        // Admin check
        bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
            .IsInRole(WindowsBuiltInRole.Administrator);
        AdminStatusText.Text = isAdmin ? "✅ Yes" : "❌ No";

        // Suspicious / foreign connections from live process list
        var rows = new List<ThreatRow>();
        foreach (var proc in _vm.Processes)
        {
            foreach (var conn in proc.Connections)
            {
                if (string.IsNullOrWhiteSpace(conn.RemoteAddress)
                 || conn.RemoteAddress == "0.0.0.0" || conn.RemoteAddress == "::")
                    continue;

                // GeoIP — async, cached
                string geo = await GeoIpService.GetCountryLabelAsync(conn.RemoteAddress);

                if (proc.IsSuspicious || (!string.IsNullOrEmpty(geo) && geo.Length > 0))
                {
                    rows.Add(new ThreatRow
                    {
                        ProcessName = proc.ProcessName,
                        ProcessPath = proc.ProcessPath ?? "",
                        RemoteIp    = conn.RemoteAddress,
                        RemotePort  = conn.RemotePort.ToString(),
                        GeoLabel    = geo,
                        IsSuspicious = proc.IsSuspicious
                    });
                }
            }
        }

        // Update list on UI thread
        DispatcherQueue.TryEnqueue(() =>
        {
            _threats.Clear();
            foreach (var r in rows.OrderByDescending(r => r.IsSuspicious)) _threats.Add(r);
            ThreatCountText.Text = rows.Count(r => r.IsSuspicious).ToString();
        });

        // Firewall rule count
        int fwCount = await Task.Run(() => NetworkOptimizeService.GetWinNetControlRuleNames().Count);
        DispatcherQueue.TryEnqueue(() => FwRuleCountText.Text = fwCount.ToString());

        // Risky listening ports via netstat
        await CheckRiskyPortsAsync();
    }

    private async Task CheckRiskyPortsAsync()
    {
        string netstatOutput = await Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-an")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o;
        });

        var found = new List<string>();
        foreach (var line in netstatOutput.Split('\n'))
        {
            if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in _riskyPorts)
            {
                if (line.Contains($":{kv.Key} ") || line.Contains($":{kv.Key}\t"))
                    found.Add($"Port {kv.Key} ({kv.Value})");
            }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            RiskyPortCountText.Text = found.Count.ToString();
            RiskyPortsText.Text     = found.Count > 0
                ? string.Join(" · ", found.Distinct())
                : "✅ No known risky ports listening";
        });
    }

    // ── Selection actions ─────────────────────────────────────────────────────
    private void OnThreatSelected(object sender, SelectionChangedEventArgs e)
    {
        _selected = ThreatList.SelectedItem as ThreatRow;
        SelectedIpText.Text = _selected != null
            ? $"{_selected.RemoteIp}:{_selected.RemotePort}  ({_selected.ProcessName})  {_selected.GeoLabel}"
            : "Select a row →";
    }

    private async void OnBlockSelectedIp(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { SecurityStatus.Text = "Select a connection first."; return; }
        string ruleName = $"WinNetControl_BlockIP_{_selected.RemoteIp.Replace('.', '_').Replace(':', '_')}";
        string args = $"advfirewall firewall add rule name=\"{ruleName}\" " +
                      $"dir=out action=block remoteip=\"{_selected.RemoteIp}\" enable=yes";
        SecurityStatus.Text = "Blocking IP…";
        await RunElevatedAsync("netsh", args);
        SecurityStatus.Text = $"✅ IP {_selected.RemoteIp} blocked via firewall.";
        HistoryLogService.AddLog("Security", "SecurityPage", $"Blocked IP {_selected.RemoteIp}");
    }

    private async void OnBlockSelectedApp(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { SecurityStatus.Text = "Select a connection first."; return; }
        if (string.IsNullOrEmpty(_selected.ProcessPath))
        {
            SecurityStatus.Text = "Process path unknown — cannot block by path."; return;
        }
        SecurityStatus.Text = "Blocking app…";
        await Task.Run(() => FirewallService.BlockApp(
            _selected.ProcessName, _selected.ProcessPath, blockInbound: true, blockOutbound: true));
        SecurityStatus.Text = $"✅ {_selected.ProcessName} blocked via firewall.";
        HistoryLogService.AddLog("Security", _selected.ProcessName, "Blocked outbound + inbound via SecurityPage");
    }

    private async void OnBlockSelectedInHosts(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { SecurityStatus.Text = "Select a connection first."; return; }
        // Use the cross-module static from HostsManagerPage
        var (ok, err) = await Task.Run(()
            => HostsManagerPage.BlockDomain(_selected.RemoteIp, _selected.ProcessName));
        SecurityStatus.Text = ok
            ? $"✅ {_selected.RemoteIp} added to hosts file."
            : $"Hosts error: {err}";
    }

    private async void OnGeoLookupSelected(object sender, RoutedEventArgs e)
    {
        if (_selected == null) { SecurityStatus.Text = "Select a connection first."; return; }
        SecurityStatus.Text = "Looking up…";
        GeoIpService.ClearCache(); // force fresh lookup
        string geo = await GeoIpService.GetCountryLabelAsync(_selected.RemoteIp);
        SecurityStatus.Text = $"GeoIP for {_selected.RemoteIp}: {(geo.Length > 0 ? geo : "Private/Unknown")}";
    }

    // ── Kill switch ───────────────────────────────────────────────────────────
    private async void OnKillSwitchOn(object sender, RoutedEventArgs e)
    {
        SecurityStatus.Text = "Activating kill switch…";
        var (ok, msg) = await Task.Run(() => NetworkOptimizeService.EnableKillSwitch());
        SecurityStatus.Text = ok ? $"⚠ {msg}" : $"Error: {msg}";
        HistoryLogService.AddLog("Security", "KillSwitch", "Internet kill switch ENABLED");
    }

    private async void OnKillSwitchOff(object sender, RoutedEventArgs e)
    {
        SecurityStatus.Text = "Restoring internet…";
        var (ok, msg) = await Task.Run(() => NetworkOptimizeService.DisableKillSwitch());
        SecurityStatus.Text = ok ? $"✅ {msg}" : $"Error: {msg}";
        HistoryLogService.AddLog("Security", "KillSwitch", "Internet kill switch DISABLED");
    }

    private static Task RunElevatedAsync(string exe, string args) => Task.Run(() =>
    {
        if (exe.Equals("netsh", StringComparison.OrdinalIgnoreCase))
            ElevatedRunner.RunNetsh(args);
        else
            ElevatedRunner.RunPowerShell(args);
    });
}
