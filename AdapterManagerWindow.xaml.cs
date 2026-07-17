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
