using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public class RouteRow
{
    public string Destination { get; set; } = "";
    public string Mask        { get; set; } = "";
    public string Gateway     { get; set; } = "";
    public string Metric      { get; set; } = "";
    public string Interface   { get; set; } = "";
}

public sealed partial class RoutingPage : Page
{
    private List<RouteRow> _all  = new();
    private readonly ObservableCollection<RouteRow> _view = new();
    private bool _showIpv6;

    public RoutingPage() { this.InitializeComponent(); RouteTable.ItemsSource = _view; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadRoutesAsync();
    }

    private void OnRefreshRoutes(object sender, RoutedEventArgs e) => _ = LoadRoutesAsync();
    private void OnIpVersionChanged(object sender, RoutedEventArgs e)
    {
        _showIpv6 = Ipv6Toggle.IsChecked == true;
        _ = LoadRoutesAsync();
    }

    // ── Load routing table ────────────────────────────────────────────────────
    private async Task LoadRoutesAsync()
    {
        RouteStatus.Text = "Loading…";
        _all = await Task.Run(() => _showIpv6 ? ParseIpv6Routes() : ParseIpv4Routes());
        ApplySearch(RouteSearch.Text);
        RouteStatus.Text = $"{_all.Count} routes loaded";
        RouteCountText.Text = $"{_all.Count} routes";
    }

    private void OnRouteSearch(object sender, TextChangedEventArgs e)
        => ApplySearch(RouteSearch.Text);

    private void ApplySearch(string q)
    {
        q = q.Trim().ToLowerInvariant();
        _view.Clear();
        foreach (var r in _all)
        {
            if (q.Length == 0
             || r.Destination.Contains(q, StringComparison.OrdinalIgnoreCase)
             || r.Gateway.Contains(q, StringComparison.OrdinalIgnoreCase)
             || r.Interface.Contains(q, StringComparison.OrdinalIgnoreCase))
                _view.Add(r);
        }
    }

    // ── IPv4 route parser ─────────────────────────────────────────────────────
    private static List<RouteRow> ParseIpv4Routes()
    {
        var rows = new List<RouteRow>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("route", "print -4")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            bool inTable = false;
            foreach (var line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("Network Destination", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
                if (!inTable || t.Length == 0 || t.StartsWith("=") || t.StartsWith("Persistent")) continue;

                // Format: Dest    Mask    Gateway    Interface    Metric
                var parts = Regex.Split(t, @"\s{2,}").Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (parts.Length >= 5)
                    rows.Add(new RouteRow
                    {
                        Destination = parts[0], Mask = parts[1],
                        Gateway = parts[2], Interface = parts[3], Metric = parts[4]
                    });
                else if (parts.Length == 4)
                    rows.Add(new RouteRow
                    {
                        Destination = parts[0], Mask = parts[1],
                        Gateway = parts[2], Metric = parts[3]
                    });
            }
        }
        catch { }
        return rows;
    }

    // ── IPv6 route parser ─────────────────────────────────────────────────────
    private static List<RouteRow> ParseIpv6Routes()
    {
        var rows = new List<RouteRow>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("route", "print -6")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            bool inTable = false;
            foreach (var line in output.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("If  Metric", StringComparison.OrdinalIgnoreCase)) { inTable = true; continue; }
                if (!inTable || t.Length == 0) continue;

                var parts = Regex.Split(t, @"\s{2,}").Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
                if (parts.Length >= 3)
                    rows.Add(new RouteRow
                    {
                        Interface = parts[0], Metric = parts[1],
                        Destination = parts[2],
                        Gateway = parts.Length >= 4 ? parts[3] : "On-link"
                    });
            }
        }
        catch { }
        return rows;
    }

    // ── Add route ─────────────────────────────────────────────────────────────
    private async void OnAddRoute(object sender, RoutedEventArgs e)
    {
        string dest    = DestBox.Text.Trim();
        string mask    = MaskBox.Text.Trim();
        string gateway = GatewayBox.Text.Trim();
        string metric  = MetricBox.Text.Trim();
        string iface   = IfaceBox.Text.Trim();
        bool   persist = PersistToggle.IsOn;

        if (string.IsNullOrEmpty(dest) || string.IsNullOrEmpty(mask) || string.IsNullOrEmpty(gateway))
        {
            RouteStatus.Text = "Destination, mask and gateway are required."; return;
        }

        string args = $"add {dest} mask {mask} {gateway}";
        if (metric.Length > 0) args += $" metric {metric}";
        if (iface.Length  > 0) args += $" if {iface}";
        if (persist)           args += " -p";

        RouteStatus.Text = "Adding route…";
        bool ok = await RunElevatedAndWait("route", args);
        RouteStatus.Text = ok ? $"✅ Route {dest}/{mask} → {gateway} added." : "❌ Failed. Run as Admin?";
        HistoryLogService.AddLog("Routing", "RoutingPage", $"Added route {dest} mask {mask} gw {gateway}");
        await LoadRoutesAsync();
    }

    // ── Delete selected ───────────────────────────────────────────────────────
    private async void OnDeleteSelected(object sender, RoutedEventArgs e)
    {
        if (RouteTable.SelectedItem is not RouteRow row) { RouteStatus.Text = "Select a route first."; return; }
        string args = $"delete {row.Destination}";
        if (!string.IsNullOrEmpty(row.Mask) && row.Mask != "—")
            args += $" mask {row.Mask}";

        RouteStatus.Text = "Deleting…";
        bool ok = await RunElevatedAndWait("route", args);
        RouteStatus.Text = ok ? $"✅ Route {row.Destination} deleted." : "❌ Failed. Run as Admin?";
        HistoryLogService.AddLog("Routing", "RoutingPage", $"Deleted route {row.Destination}");
        await LoadRoutesAsync();
    }

    // ── Default gateway ───────────────────────────────────────────────────────
    private async void OnSetDefaultGw(object sender, RoutedEventArgs e)
    {
        string gw    = GwBox.Text.Trim();
        string iface = GwIfaceBox.Text.Trim();
        if (string.IsNullOrEmpty(gw)) { RouteStatus.Text = "Enter gateway IP."; return; }

        // First delete existing default, then add new
        await RunElevatedAndWait("route", "delete 0.0.0.0");
        string addArgs = $"add 0.0.0.0 mask 0.0.0.0 {gw} -p";
        if (iface.Length > 0) addArgs += $" if {iface}";

        bool ok = await RunElevatedAndWait("route", addArgs);
        RouteStatus.Text = ok ? $"✅ Default gateway set to {gw}." : "❌ Failed. Run as Admin?";
        HistoryLogService.AddLog("Routing", "RoutingPage", $"Set default gateway to {gw}");
        await LoadRoutesAsync();
    }

    // ── Elevated route helper ─────────────────────────────────────────────────
    private static Task<bool> RunElevatedAndWait(string exe, string args) => Task.Run(() =>
    {
        try
        {
            (bool ok, _) = exe.Equals("netsh", StringComparison.OrdinalIgnoreCase)
                ? ElevatedRunner.RunNetsh(args)
                : ElevatedRunner.RunPowerShell(args);
            return ok;
        }
        catch { return false; }
    });
}
