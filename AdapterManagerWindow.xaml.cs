using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinNetControl.Core;

namespace WinNetControl;

public sealed partial class AdapterManagerWindow : Window
{
    // ── Observable list of live rows — NEVER cleared, only diffed ────────────
    private readonly ObservableCollection<LiveAdapterInfo> _adapters = new();
    private Timer? _refreshTimer;

    public AdapterManagerWindow()
    {
        this.InitializeComponent();
        WinUIEx.WindowExtensions.SetWindowSize(this, 1100, 620);
        try { WinUIEx.WindowExtensions.SetIcon(this, "Assets\\AppIcon.ico"); } catch { }

        AdapterList.ItemsSource = _adapters;
        Refresh();

        // Auto-refresh every 5 s — only updates changed values in-place,
        // list object stays the same so scroll position is never disturbed.
        _refreshTimer = new Timer(_ => DispatcherQueue.TryEnqueue(Refresh), null, 5000, 5000);
        this.Closed += (_, __) => { _refreshTimer?.Dispose(); _refreshTimer = null; };
    }

    // ── Diff-merge refresh — no Clear(), no scroll jump ───────────────────────
    private void Refresh()
    {
        try
        {
            var fresh = NetworkAdapterService.GetAll();

            // 1. Update existing rows in-place
            foreach (var live in _adapters)
            {
                var snap = fresh.FirstOrDefault(s => s.Name == live.Name);
                if (snap != null) live.UpdateFrom(snap);
            }

            // 2. Remove adapters that disappeared
            var gone = _adapters.Where(a => !fresh.Any(s => s.Name == a.Name)).ToList();
            foreach (var a in gone) _adapters.Remove(a);

            // 3. Add newly appeared adapters at the right sorted position
            var existing = _adapters.Select(a => a.Name).ToHashSet();
            var toAdd    = fresh.Where(s => !existing.Contains(s.Name)).ToList();
            foreach (var snap in toAdd)
            {
                var live = new LiveAdapterInfo();
                live.UpdateFrom(snap);
                // Insert maintaining sort: Up first, then by name
                int idx = _adapters.Count(a =>
                    (a.IsUp ? 1 : 0) > (live.IsUp ? 1 : 0) ||
                    (a.IsUp == live.IsUp && string.Compare(a.Name, live.Name,
                        StringComparison.OrdinalIgnoreCase) < 0));
                _adapters.Insert(Math.Min(idx, _adapters.Count), live);
            }

            // 4. Update status bar only (no list rebuild)
            int up   = _adapters.Count(a => a.IsUp);
            int down = _adapters.Count - up;
            CountBar.Text       = $"{_adapters.Count} adapters  ({up} up, {down} down)";
            LastUpdateText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { StatusBar.Text = $"\u2717 Refresh error: {ex.Message}"; }
    }

    private LiveAdapterInfo? Selected =>
        AdapterList.SelectedItem as LiveAdapterInfo ??
        (_adapters.Count == 1 ? _adapters[0] : null);

    // ── Toolbar ───────────────────────────────────────────────────────────────
    private void OnRefresh(object s, RoutedEventArgs e) => Refresh();

    private async void OnEnableSelected(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        StatusBar.Text = $"Enabling {a.Name}\u2026";
        var (ok, err) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.Enable(a.Name));
        StatusBar.Text = ok ? $"\u2713  {a.Name} enabled." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnDisableSelected(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        var dlg = new ContentDialog
        {
            Title             = "Disable Adapter?",
            Content           = $"Disable \u2018{a.Name}\u2019? You will lose connectivity on this adapter.",
            PrimaryButtonText = "Disable",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StatusBar.Text = $"Disabling {a.Name}\u2026";
        var (ok, err) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.Disable(a.Name));
        StatusBar.Text = ok ? $"\u2713  {a.Name} disabled." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnRenewSelected(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        StatusBar.Text = $"Renewing IP on {a.Name}\u2026";
        var (ok, output) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.RenewAdapter(a.Name));
        StatusBar.Text = ok ? $"\u2713  IP renewed on {a.Name}." : $"\u2717  {output}";
        Refresh();
    }

    private async void OnSetStaticIp(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        var ipBox  = new TextBox { Header = "IP Address",      PlaceholderText = "e.g. 192.168.1.100", Text = a.IPv4First != "" ? a.IPv4First : "" };
        var mskBox = new TextBox { Header = "Subnet Mask",     PlaceholderText = "e.g. 255.255.255.0", Text = "255.255.255.0" };
        var gwBox  = new TextBox { Header = "Default Gateway", PlaceholderText = "e.g. 192.168.1.1",   Text = a.Gateway != "\u2014" ? a.Gateway : "" };
        var dlg = new ContentDialog
        {
            Title = $"Set Static IP \u2014 {a.Name}",
            Content = new StackPanel { Spacing = 8, Children = { ipBox, mskBox, gwBox } },
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StatusBar.Text = "Setting static IP\u2026";
        var (ok, err) = await System.Threading.Tasks.Task.Run(() =>
            NetworkAdapterService.SetStaticIp(a.Name, ipBox.Text.Trim(),
                mskBox.Text.Trim(), gwBox.Text.Trim()));
        StatusBar.Text = ok ? $"\u2713  Static IP {ipBox.Text.Trim()} set on {a.Name}." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnSetDhcp(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        var dlg = new ContentDialog
        {
            Title = "Switch to DHCP?",
            Content = $"'{a.Name}' will be configured to obtain IP and DNS automatically.",
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StatusBar.Text = "Switching to DHCP\u2026";
        var (ok, err) = await System.Threading.Tasks.Task.Run(() =>
            NetworkAdapterService.SetDhcp(a.Name));
        StatusBar.Text = ok ? $"\u2713  {a.Name} now using DHCP." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnSetDns(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        // Quick DNS presets
        var presets = new ComboBox { Header = "Quick Preset", Margin = new Thickness(0,0,0,8) };
        presets.Items.Add("Custom");
        presets.Items.Add("Cloudflare  1.1.1.1 / 1.0.0.1");
        presets.Items.Add("Google      8.8.8.8 / 8.8.4.4");
        presets.Items.Add("OpenDNS     208.67.222.222 / 208.67.220.220");
        presets.Items.Add("Quad9       9.9.9.9 / 149.112.112.112");
        presets.SelectedIndex = 0;
        var p1Box = new TextBox { Header = "Primary DNS",   PlaceholderText = "e.g. 1.1.1.1" };
        var p2Box = new TextBox { Header = "Secondary DNS", PlaceholderText = "e.g. 1.0.0.1 (optional)" };
        presets.SelectionChanged += (_, __) =>
        {
            (p1Box.Text, p2Box.Text) = presets.SelectedIndex switch
            {
                1 => ("1.1.1.1",         "1.0.0.1"),
                2 => ("8.8.8.8",         "8.8.4.4"),
                3 => ("208.67.222.222",  "208.67.220.220"),
                4 => ("9.9.9.9",         "149.112.112.112"),
                _ => ("", "")
            };
        };
        var dlg = new ContentDialog
        {
            Title = $"Set DNS \u2014 {a.Name}",
            Content = new StackPanel { Spacing = 8, Children = { presets, p1Box, p2Box } },
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
            XamlRoot = this.Content.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(p1Box.Text)) { StatusBar.Text = "Primary DNS cannot be empty."; return; }
        StatusBar.Text = "Setting DNS\u2026";
        var (ok, err) = await System.Threading.Tasks.Task.Run(() =>
            NetworkAdapterService.SetDns(a.Name, p1Box.Text.Trim(), p2Box.Text.Trim()));
        StatusBar.Text = ok ? $"\u2713  DNS set to {p1Box.Text.Trim()} on {a.Name}." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnFlushDns(object s, RoutedEventArgs e)
    {
        StatusBar.Text = "Flushing DNS cache\u2026";
        var (ok, output) = await System.Threading.Tasks.Task.Run(NetworkAdapterService.FlushDns);
        StatusBar.Text = ok ? "\u2713  DNS cache flushed." : $"\u2717  {output}";
    }

    private void OnDiagnoseSelected(object s, RoutedEventArgs e)
    {
        var a = Selected; if (a == null) { Hint(); return; }
        NetworkAdapterService.DiagnoseAdapter(a.Name);
    }

    private void OnOpenNcpa(object s, RoutedEventArgs e)
        => NetworkAdapterService.OpenAdapterProperties();

    // ── Context menu ──────────────────────────────────────────────────────────
    private LiveAdapterInfo? MenuRow(object sender)
        => (sender as FrameworkElement)?.DataContext as LiveAdapterInfo;

    private async void OnMenuEnable(object s, RoutedEventArgs e)
    {
        var a = MenuRow(s); if (a == null) return;
        var (ok, err) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.Enable(a.Name));
        StatusBar.Text = ok ? $"\u2713  {a.Name} enabled." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnMenuDisable(object s, RoutedEventArgs e)
    {
        var a = MenuRow(s); if (a == null) return;
        var (ok, err) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.Disable(a.Name));
        StatusBar.Text = ok ? $"\u2713  {a.Name} disabled." : $"\u2717  {err}";
        Refresh();
    }

    private async void OnMenuRenew(object s, RoutedEventArgs e)
    {
        var a = MenuRow(s); if (a == null) return;
        var (ok, output) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.RenewAdapter(a.Name));
        StatusBar.Text = ok ? $"\u2713  Renewed." : $"\u2717  {output}";
        Refresh();
    }

    private void OnMenuDiagnose(object s, RoutedEventArgs e)
        => MenuRow(s)?.Name.Let(n => NetworkAdapterService.DiagnoseAdapter(n));

    private void OnMenuCopyIp(object s, RoutedEventArgs e)
    {
        var a = MenuRow(s); if (a == null) return;
        string ip = a.IPv4.FirstOrDefault() ?? a.IPv6.FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(ip)) return;
        var dp = new DataPackage(); dp.SetText(ip); Clipboard.SetContent(dp);
        StatusBar.Text = $"\u2713  Copied: {ip}";
    }

    private void OnMenuCopyMac(object s, RoutedEventArgs e)
    {
        var a = MenuRow(s); if (a == null) return;
        var dp = new DataPackage(); dp.SetText(a.MacAddress); Clipboard.SetContent(dp);
        StatusBar.Text = $"\u2713  Copied MAC: {a.MacAddress}";
    }

    private void Hint() => StatusBar.Text = "Please select an adapter first.";
}

// Tiny helper to avoid null-check boilerplate in lambdas
file static class Ext
{
    public static void Let<T>(this T? val, Action<T> action) where T : class
    {
        if (val != null) action(val);
    }
}
