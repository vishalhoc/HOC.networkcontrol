using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinNetControl;

// ── Port scanner result ───────────────────────────────────────────────────────
public record PortResult(int Port, string Service);

// ── Open-port row ─────────────────────────────────────────────────────────────
public record OpenPortRow(
    string Protocol,
    int    LocalPort,
    string LocalAddress,
    string RemoteAddress,
    string State,
    string ProcessName);

public sealed partial class NetworkToolsWindow : Window
{
    private CancellationTokenSource? _pingCts;
    private CancellationTokenSource? _traceCts;
    private CancellationTokenSource? _scanCts;

    private readonly ObservableCollection<string>     _pingItems  = new();
    private readonly ObservableCollection<string>     _traceItems = new();
    private readonly ObservableCollection<PortResult> _scanItems  = new();
    private readonly ObservableCollection<OpenPortRow>_portItems  = new();
    private List<OpenPortRow> _allPorts = new();

    public NetworkToolsWindow()
    {
        this.InitializeComponent();
        WinUIEx.WindowExtensions.SetWindowSize(this, 980, 640);
        try { WinUIEx.WindowExtensions.SetIcon(this, "Assets\\AppIcon.ico"); } catch { }

        PingResults.ItemsSource   = _pingItems;
        TraceResults.ItemsSource  = _traceItems;
        ScanResults.ItemsSource   = _scanItems;
        OpenPortsList.ItemsSource = _portItems;

        LoadOpenPorts();
    }

    /// <summary>Pre-fills the Ping tab host box and switches to that tab.</summary>
    public void SetPingHost(string host)
    {
        PingHostBox.Text = host;
        MainTabs.SelectedIndex = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PING
    // ══════════════════════════════════════════════════════════════════════════
    private async void OnPingClicked(object s, RoutedEventArgs e)
    {
        string host = PingHostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;

        _pingCts?.Cancel();
        _pingCts = new CancellationTokenSource();
        _pingItems.Clear();
        PingStats.Text  = $"Pinging {host}…";
        PingBtn.IsEnabled  = false;
        StopPingBtn.IsEnabled = true;

        int count  = (int)PingCountBox.Value;
        int ok = 0; long totalMs = 0; long min = long.MaxValue; long max = 0;

        await Task.Run(async () =>
        {
            using var ping = new Ping();
            for (int i = 1; i <= count; i++)
            {
                if (_pingCts.Token.IsCancellationRequested) break;
                string line;
                try
                {
                    var reply = ping.Send(host, 2000);
                    bool success = reply.Status == IPStatus.Success;
                    if (success)
                    {
                        ok++;
                        totalMs += reply.RoundtripTime;
                        if (reply.RoundtripTime < min) min = reply.RoundtripTime;
                        if (reply.RoundtripTime > max) max = reply.RoundtripTime;
                        line = $"Reply from {reply.Address}: bytes=32 time={reply.RoundtripTime}ms TTL=64";
                    }
                    else
                        line = $"Request {i}: {reply.Status}";
                }
                catch (Exception ex) { line = $"Error: {ex.Message}"; }

                DispatcherQueue.TryEnqueue(() =>
                {
                    _pingItems.Add(line);
                    PingScroll.ScrollToVerticalOffset(double.MaxValue);
                });
                await Task.Delay(500, _pingCts.Token).ContinueWith(_ => { });
            }
        }, _pingCts.Token);

        int loss = count - ok;
        double avg = ok > 0 ? (double)totalMs / ok : 0;
        PingStats.Text = $"Sent={count}  Received={ok}  Lost={loss} ({loss * 100 / count}% loss)" +
                         (ok > 0 ? $"   Min={min}ms  Max={max}ms  Avg={avg:F0}ms" : "");
        PingBtn.IsEnabled  = true;
        StopPingBtn.IsEnabled = false;
    }

    private void OnStopPingClicked(object s, RoutedEventArgs e)
    {
        _pingCts?.Cancel();
        StopPingBtn.IsEnabled = false;
        PingBtn.IsEnabled     = true;
        PingStats.Text        = "Stopped.";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRACEROUTE
    // ══════════════════════════════════════════════════════════════════════════
    private async void OnTraceClicked(object s, RoutedEventArgs e)
    {
        string host = TraceHostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;

        _traceCts?.Cancel();
        _traceCts = new CancellationTokenSource();
        _traceItems.Clear();
        _traceItems.Add($"Tracing route to {host} with max 30 hops:");
        _traceItems.Add("");
        TraceBtn.IsEnabled  = false;
        StopTraceBtn.IsEnabled = true;

        await Task.Run(async () =>
        {
            using var ping   = new Ping();
            var opts = new PingOptions { DontFragment = true };

            for (int ttl = 1; ttl <= 30; ttl++)
            {
                if (_traceCts.Token.IsCancellationRequested) break;
                opts.Ttl = ttl;
                string line;
                try
                {
                    var r = ping.Send(host, 2000, new byte[32], opts);
                    string addr  = r.Address?.ToString() ?? "*";
                    string label = "";
                    try { label = Dns.GetHostEntry(addr).HostName; } catch { }
                    string namepart = string.IsNullOrEmpty(label) ? addr : $"{label} [{addr}]";
                    line = $"  {ttl,2}    {r.RoundtripTime,5} ms   {namepart}";

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _traceItems.Add(line);
                        TraceScroll.ScrollToVerticalOffset(double.MaxValue);
                    });

                    if (r.Status == IPStatus.Success) break;
                }
                catch
                {
                    DispatcherQueue.TryEnqueue(() => _traceItems.Add($"  {ttl,2}       *             Request timed out."));
                }
                await Task.Delay(100, _traceCts.Token).ContinueWith(_ => { });
            }
            DispatcherQueue.TryEnqueue(() => _traceItems.Add("\nTrace complete."));
        }, _traceCts.Token);

        TraceBtn.IsEnabled  = true;
        StopTraceBtn.IsEnabled = false;
    }

    private void OnStopTraceClicked(object s, RoutedEventArgs e)
    {
        _traceCts?.Cancel();
        StopTraceBtn.IsEnabled = false;
        TraceBtn.IsEnabled     = true;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // PORT SCANNER
    // ══════════════════════════════════════════════════════════════════════════
    private async void OnScanClicked(object s, RoutedEventArgs e)
    {
        string host = ScanHostBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        _scanItems.Clear();
        int from    = (int)ScanFromBox.Value;
        int to      = (int)ScanToBox.Value;
        int timeout = (int)ScanTimeoutBox.Value;
        ScanBtn.IsEnabled  = false;
        StopScanBtn.IsEnabled = true;
        ScanStatus.Text = $"Scanning {host} ports {from}-{to}…";

        int open = 0;
        await Task.Run(async () =>
        {
            var tasks = new List<Task>();
            var sem   = new SemaphoreSlim(128);

            for (int port = from; port <= to; port++)
            {
                if (_scanCts.Token.IsCancellationRequested) break;
                int p = port;
                await sem.WaitAsync(_scanCts.Token).ContinueWith(_ => { });
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var tcp = new TcpClient();
                        var connectTask = tcp.ConnectAsync(host, p);
                        if (await Task.WhenAny(connectTask, Task.Delay(timeout)) == connectTask
                            && tcp.Connected)
                        {
                            string svc = WellKnownService(p);
                            Interlocked.Increment(ref open);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                _scanItems.Add(new PortResult(p, svc));
                                ScanStatus.Text = $"Scanning… {open} open ports found so far";
                            });
                        }
                    }
                    catch { }
                    finally { sem.Release(); }
                }, _scanCts.Token));
            }
            await Task.WhenAll(tasks);
        }, _scanCts.Token);

        ScanStatus.Text = $"Done — {open} open port{(open == 1 ? "" : "s")} found on {host} (range {from}-{to})";
        ScanBtn.IsEnabled  = true;
        StopScanBtn.IsEnabled = false;
    }

    private void OnStopScanClicked(object s, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        StopScanBtn.IsEnabled = false;
        ScanBtn.IsEnabled     = true;
        ScanStatus.Text       = "Scan stopped.";
    }

    // ══════════════════════════════════════════════════════════════════════════
    // OPEN PORTS (local)
    // ══════════════════════════════════════════════════════════════════════════
    private void LoadOpenPorts()
    {
        _allPorts.Clear();
        try
        {
            var props = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            // TCP
            foreach (var l in props.GetActiveTcpListeners())
                _allPorts.Add(new OpenPortRow("TCP", l.Port, l.Address.ToString(), "*", "LISTEN", GetOwner(l.Port, "tcp")));
            // TCP connections
            foreach (var c in props.GetActiveTcpConnections())
                _allPorts.Add(new OpenPortRow("TCP", c.LocalEndPoint.Port,
                    c.LocalEndPoint.ToString(), c.RemoteEndPoint.ToString(),
                    c.State.ToString(), GetOwner(c.LocalEndPoint.Port, "tcp")));
            // UDP
            foreach (var l in props.GetActiveUdpListeners())
                _allPorts.Add(new OpenPortRow("UDP", l.Port, l.Address.ToString(), "*", "LISTEN", GetOwner(l.Port, "udp")));
        }
        catch { }

        _allPorts = _allPorts.OrderBy(r => r.LocalPort).ToList();
        ApplyPortFilter();
    }

    private void ApplyPortFilter()
    {
        string q     = PortSearch?.Text?.ToLowerInvariant() ?? "";
        int    proto = PortProtoFilter?.SelectedIndex ?? 0;

        var filtered = _allPorts.Where(r =>
        {
            bool protoOk = proto == 0 ||
                           (proto == 1 && r.Protocol == "TCP") ||
                           (proto == 2 && r.Protocol == "UDP");
            bool searchOk = string.IsNullOrEmpty(q) ||
                            r.LocalPort.ToString().Contains(q) ||
                            r.ProcessName.ToLowerInvariant().Contains(q) ||
                            r.LocalAddress.Contains(q);
            return protoOk && searchOk;
        }).ToList();

        _portItems.Clear();
        foreach (var r in filtered) _portItems.Add(r);
    }

    private void OnRefreshPorts(object s, RoutedEventArgs e) => LoadOpenPorts();
    private void OnPortSearchChanged(object s, TextChangedEventArgs e) => ApplyPortFilter();

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static string GetOwner(int port, string proto)
    {
        try
        {
            // Use netstat to get process name for port
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true
            };
            // This is a quick approximation — return empty for now
            return "";
        }
        catch { return ""; }
    }

    private static string WellKnownService(int port) => port switch
    {
        21  => "FTP",      22  => "SSH",       23  => "Telnet",
        25  => "SMTP",     53  => "DNS",        80  => "HTTP",
        110 => "POP3",     143 => "IMAP",      443 => "HTTPS",
        445 => "SMB",      3306 => "MySQL",    3389 => "RDP",
        5432 => "PostgreSQL", 6379 => "Redis", 8080 => "HTTP-Alt",
        8443 => "HTTPS-Alt", 27017 => "MongoDB", _ => ""
    };
}
