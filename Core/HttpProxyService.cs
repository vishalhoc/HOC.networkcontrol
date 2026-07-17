using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using WinNetControl.Models;

namespace WinNetControl.Core;

// ── Current system-proxy state (read from registry) ──────────────────────────
public record ProxyStatus(bool Enabled, string Server, string Bypass, bool IsOurs);

public class HttpProxyService
{
    // ── WinInet P/Invoke ──────────────────────────────────────────────────────
    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(nint hInternet, int dwOption,
                                                 nint lpBuffer, int dwBufferLength);
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH           = 37;
    private const string RegKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    public const int ProxyPort = 8080;

    // ── Fields ────────────────────────────────────────────────────────────────
    private ProxyServer?    _proxyServer;
    private DispatcherQueue _dispatcherQueue;
    private readonly ViewModels.MainViewModel _viewModel;

    public ObservableCollection<HttpRequestInfo> Requests { get; } = new();
    public bool IsRunning => _proxyServer?.ProxyRunning ?? false;

    // ── Constructor ───────────────────────────────────────────────────────────
    public HttpProxyService(ViewModels.MainViewModel viewModel)
    {
        _viewModel       = viewModel;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Auto-fix on startup: if proxy is stuck at our port, clear it
        var current = GetSystemProxyStatus();
        if (current.IsOurs)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Proxy] Found stale proxy pointing to our port on startup — clearing.");
            ForceRestoreSystemProxy();
        }

        // Guarantee cleanup even if app crashes / process is killed
        AppDomain.CurrentDomain.ProcessExit += (_, __) => ForceRestoreSystemProxy();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void InstallCertificate()
    {
        var srv = EnsureServer();
        srv.CertificateManager.CreateRootCertificate();
        srv.CertificateManager.TrustRootCertificate();
    }

    public void UninstallCertificate()
    {
        var srv = EnsureServer();
        srv.CertificateManager.RemoveTrustedRootCertificate();
    }

    public void Start(bool setSystemProxy)
    {
        if (_proxyServer?.ProxyRunning == true) return;

        _proxyServer     = new ProxyServer();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        var endpoint = new ExplicitProxyEndPoint(IPAddress.Any, ProxyPort, true);
        _proxyServer.AddEndPoint(endpoint);
        _proxyServer.BeforeRequest  += OnRequest;
        _proxyServer.BeforeResponse += OnResponse;
        _proxyServer.Start();

        if (setSystemProxy)
        {
            _proxyServer.SetAsSystemHttpProxy(endpoint);
            _proxyServer.SetAsSystemHttpsProxy(endpoint);
        }
    }

    public void Stop()
    {
        if (_proxyServer == null) return;
        try
        {
            _proxyServer.BeforeRequest  -= OnRequest;
            _proxyServer.BeforeResponse -= OnResponse;
            try { _proxyServer.RestoreOriginalProxySettings(); } catch { }
            try { _proxyServer.Stop(); } catch { }
        }
        catch { }
        finally
        {
            // Guaranteed fallback — always clear proxy from registry directly
            ForceRestoreSystemProxy();
            _proxyServer = null;
        }
    }

    // ── Read live system proxy state ──────────────────────────────────────────
    public static ProxyStatus GetSystemProxyStatus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey);
            if (key == null) return new ProxyStatus(false, "", "", false);

            bool   enabled = ((int?)key.GetValue("ProxyEnable") ?? 0) != 0;
            string server  = (key.GetValue("ProxyServer") as string) ?? "";
            string bypass  = (key.GetValue("ProxyOverride") as string) ?? "";
            bool   isOurs  = server.Contains($":{ProxyPort}", StringComparison.Ordinal);
            return new ProxyStatus(enabled, server, bypass, isOurs);
        }
        catch { return new ProxyStatus(false, "", "", false); }
    }

    // ── Force-clear system proxy via registry (no Titanium dependency) ────────
    public static void ForceRestoreSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
            if (key == null) return;

            // Only clear if it''s still pointing to our port
            string server = (key.GetValue("ProxyServer") as string) ?? "";
            if (server.Contains($":{ProxyPort}", StringComparison.Ordinal))
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", "", RegistryValueKind.String);
            }
        }
        catch { }
        finally
        {
            // Notify WinInet so all apps pick up the change immediately
            try
            {
                InternetSetOption(0, INTERNET_OPTION_SETTINGS_CHANGED, 0, 0);
                InternetSetOption(0, INTERNET_OPTION_REFRESH, 0, 0);
            }
            catch { }
        }
    }

    /// <summary>Set / clear system proxy directly without going through Titanium.</summary>
    public static void SetSystemProxyDirect(bool enable, int port = ProxyPort)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, writable: true);
            if (key == null) return;
            key.SetValue("ProxyEnable", enable ? 1 : 0, RegistryValueKind.DWord);
            if (enable)
                key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
        }
        catch { }
        finally
        {
            try
            {
                InternetSetOption(0, INTERNET_OPTION_SETTINGS_CHANGED, 0, 0);
                InternetSetOption(0, INTERNET_OPTION_REFRESH, 0, 0);
            }
            catch { }
        }
    }

    // ── Request handler ───────────────────────────────────────────────────────
    private async Task OnRequest(object sender, SessionEventArgs e)
    {
        int pid = 0;
        try { pid = e.HttpClient.ProcessId?.Value ?? 0; } catch { return; }

        bool captureEnabled = _viewModel.Processes.Any(p =>
            p.ProcessId == pid && p.IsHttpCaptureEnabled);
        if (!captureEnabled) return;

        string procName = _viewModel.Processes
            .FirstOrDefault(p => p.ProcessId == pid)?.ProcessName ?? string.Empty;

        var req = new HttpRequestInfo
        {
            Id          = Guid.NewGuid(),
            Url         = e.HttpClient.Request.Url    ?? string.Empty,
            Method      = e.HttpClient.Request.Method ?? string.Empty,
            Host        = e.HttpClient.Request.Host   ?? string.Empty,
            ProcessId   = pid,
            ProcessName = procName,
            Timestamp   = DateTime.Now
        };
        e.UserData = req;
        _dispatcherQueue.TryEnqueue(() => Requests.Add(req));
    }

    // ── Response handler ──────────────────────────────────────────────────────
    private async Task OnResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is not HttpRequestInfo req) return;
        try
        {
            int    code  = (int)e.HttpClient.Response.StatusCode;
            long   size  = e.HttpClient.Response.ContentLength > 0
                           ? e.HttpClient.Response.ContentLength : 0;
            string ctype = e.HttpClient.Response.ContentType ?? string.Empty;
            _dispatcherQueue.TryEnqueue(() =>
            {
                req.StatusCode   = code;
                req.ResponseSize = size;
                req.ContentType  = ctype;
            });
        }
        catch { }
    }

    private ProxyServer EnsureServer()
    {
        _proxyServer ??= new ProxyServer();
        return _proxyServer;
    }
}
