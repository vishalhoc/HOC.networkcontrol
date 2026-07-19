using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using Microsoft.Win32;

namespace WinNetControl.Pages;

/// <summary>View model row for the captured-request list.</summary>
public class ProxyRequestRow
{
    private readonly HttpRequestInfo _src;
    public ProxyRequestRow(HttpRequestInfo src) { _src = src; }
    public string TimestampStr    => _src.Timestamp.ToString("HH:mm:ss");
    public string Method          => _src.Method;
    public string Url             => _src.Url;
    public string ProcessName     => _src.ProcessName;
    public int    StatusCode      => _src.StatusCode;
    public string ResponseSizeStr => _src.ResponseSize > 0
        ? $"{_src.ResponseSize / 1024.0:F1} KB" : "—";
}

public sealed partial class ProxyManagerPage : Page
{
    private MainViewModel?  _vm;
    private HttpProxyService? _proxy => _vm?.ProxyService;
    private readonly ObservableCollection<ProxyRequestRow> _rows = new();

    public ProxyManagerPage() { this.InitializeComponent(); RequestsList.ItemsSource = _rows; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
        {
            _vm = vm;
            // Mirror live captures from the shared ProxyService
            _vm.ProxyService.Requests.CollectionChanged += OnProxyCapturesChanged;
            SyncRequestRows();
        }
        RefreshStatus();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_vm != null)
            _vm.ProxyService.Requests.CollectionChanged -= OnProxyCapturesChanged;
    }

    // ── Status ────────────────────────────────────────────────────────────────
    private void RefreshStatus()
    {
        var s = HttpProxyService.GetSystemProxyStatus();
        ProxyStatusBadge.Text      = s.Enabled ? "🟢 Enabled" : "⚪ Disabled";
        ProxyStatusBadge.Foreground = s.Enabled
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16))
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
        ProxyServerText.Text  = s.Server.Length > 0  ? s.Server  : "—";
        ProxyBypassText.Text  = s.Bypass.Length > 0  ? s.Bypass  : "—";
        CaptureStatusText.Text = _proxy?.IsRunning == true
            ? $"🟢 Running (port {HttpProxyService.ProxyPort})"
            : "⚪ Stopped";
        RequestCountText.Text = $"{_rows.Count} requests captured";
    }

    private void OnRefreshStatus(object sender, RoutedEventArgs e) => RefreshStatus();

    // ── Capture proxy ─────────────────────────────────────────────────────────
    private void OnStartCapture(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try { _proxy.Start(setSystemProxy: true); ProxyActionStatus.Text = "✅ Capture proxy started and set as system proxy."; }
        catch (Exception ex) { ProxyActionStatus.Text = $"Error: {ex.Message}"; }
        RefreshStatus();
    }

    private void OnStartCaptureNoProxy(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try { _proxy.Start(setSystemProxy: false); ProxyActionStatus.Text = "✅ Capture proxy started (capture only, system proxy unchanged)."; }
        catch (Exception ex) { ProxyActionStatus.Text = $"Error: {ex.Message}"; }
        RefreshStatus();
    }

    private void OnStopCapture(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try { _proxy.Stop(); ProxyActionStatus.Text = "✅ Capture proxy stopped. System proxy restored."; }
        catch (Exception ex) { ProxyActionStatus.Text = $"Error: {ex.Message}"; }
        RefreshStatus();
    }

    private void OnInstallCert(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try { _proxy.InstallCertificate(); ProxyActionStatus.Text = "✅ Root certificate installed and trusted."; }
        catch (Exception ex) { ProxyActionStatus.Text = $"Cert error: {ex.Message}"; }
    }

    private void OnRemoveCert(object sender, RoutedEventArgs e)
    {
        if (_proxy == null) return;
        try { _proxy.UninstallCertificate(); ProxyActionStatus.Text = "✅ Root certificate removed."; }
        catch (Exception ex) { ProxyActionStatus.Text = $"Cert error: {ex.Message}"; }
    }

    // ── System proxy direct ───────────────────────────────────────────────────
    private void OnSetSystemProxy(object sender, RoutedEventArgs e)
    {
        string hostPort = ProxyHostBox.Text.Trim();
        string bypass   = ProxyBypassBox.Text.Trim();
        if (string.IsNullOrEmpty(hostPort)) { ProxyActionStatus.Text = "Enter host:port first."; return; }

        try
        {
            const string regKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            using var key = Registry.CurrentUser.OpenSubKey(regKey, writable: true);
            if (key == null) { ProxyActionStatus.Text = "Cannot open registry."; return; }
            key.SetValue("ProxyEnable", 1,        RegistryValueKind.DWord);
            key.SetValue("ProxyServer", hostPort,  RegistryValueKind.String);
            if (bypass.Length > 0)
                key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
            ProxyActionStatus.Text = $"✅ System proxy set to {hostPort}.";
        }
        catch (Exception ex) { ProxyActionStatus.Text = $"Error: {ex.Message}"; }
        RefreshStatus();
    }

    private void OnClearSystemProxy(object sender, RoutedEventArgs e)
    {
        HttpProxyService.ForceRestoreSystemProxy();
        ProxyActionStatus.Text = "✅ System proxy cleared.";
        RefreshStatus();
    }

    // ── Request list ──────────────────────────────────────────────────────────
    private void OnProxyCapturesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(SyncRequestRows);
    }

    private void SyncRequestRows()
    {
        if (_vm == null) return;
        _rows.Clear();
        foreach (var r in _vm.ProxyService.Requests)
            _rows.Insert(0, new ProxyRequestRow(r));
        RequestCountText.Text = $"{_rows.Count} requests captured";
    }

    private void OnClearRequests(object sender, RoutedEventArgs e)
    {
        _vm?.ProxyService.Requests.Clear();
        _rows.Clear();
        RequestCountText.Text = "0 requests captured";
    }
}
