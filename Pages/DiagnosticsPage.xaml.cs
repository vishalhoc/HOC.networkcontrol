using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private MainViewModel? _vm;
    private CancellationTokenSource? _cts;
    private bool _running;

    // Ping stats
    private readonly List<long> _pingResults = new();

    public DiagnosticsPage() => this.InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
    }

    // ── Ping ─────────────────────────────────────────────────────────────────
    private async void OnRunPing(object sender, RoutedEventArgs e)
    {
        if (_running) { StopTool(); return; }

        string host = TargetHost.Text.Trim();
        if (string.IsNullOrEmpty(host)) return;

        int count   = (int)PingCount.Value;
        int timeout = (int)PingTimeout.Value;
        bool cont   = PingContinuous.IsChecked == true;

        StartTool("Ping");
        _pingResults.Clear();
        ResetStats();

        AppendLine($"PING {host} — {(cont ? "continuous" : $"{count} packets")}", "#0078D4");
        AppendLine(new string('─', 60));

        _cts = new CancellationTokenSource();
        int seq = 0, sent = 0, lost = 0;

        try
        {
            while (!_cts.Token.IsCancellationRequested && (cont || seq < count))
            {
                seq++; sent++;
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(host, timeout);
                    if (reply.Status == IPStatus.Success)
                    {
                        _pingResults.Add(reply.RoundtripTime);
                        string quality = reply.RoundtripTime < 30  ? "Excellent"
                                       : reply.RoundtripTime < 80  ? "Good"
                                       : reply.RoundtripTime < 150 ? "Fair" : "Poor";
                        string color = reply.RoundtripTime < 80 ? "#107C10" : reply.RoundtripTime < 150 ? "#FBBC05" : "#E02020";
                        AppendLine($"Reply from {reply.Address}: seq={seq}  time={reply.RoundtripTime} ms  TTL={reply.Options?.Ttl ?? 0}  [{quality}]", color);
                        UpdateStats();
                    }
                    else
                    {
                        lost++;
                        AppendLine($"Request #{seq} — {reply.Status}", "#E02020");
                    }
                }
                catch (Exception ex)
                {
                    lost++;
                    AppendLine($"Error #{seq}: {ex.Message}", "#E02020");
                }

                if (!cont || seq < count)
                    await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
            }

            double lossPercent = sent > 0 ? (double)lost / sent * 100 : 0;
            AppendLine(new string('─', 60));
            AppendLine($"Statistics: Sent={sent}  Received={sent - lost}  Lost={lost} ({lossPercent:F0}%)");
            StatLoss.Text = $"{lossPercent:F0}%";
        }
        catch (OperationCanceledException) { AppendLine("⏹ Ping stopped."); }
        finally { StopTool(); }
    }

    // ── Traceroute ────────────────────────────────────────────────────────────
    private async void OnRunTrace(object sender, RoutedEventArgs e)
    {
        if (_running) { StopTool(); return; }

        string host    = TargetHost.Text.Trim();
        int    maxHops = (int)TraceHops.Value;

        StartTool("Traceroute");
        AppendLine($"TRACEROUTE to {host}  (max {maxHops} hops)", "#FBBC05");
        AppendLine(new string('─', 60));

        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(async () =>
            {
                for (int ttl = 1; ttl <= maxHops && !_cts.Token.IsCancellationRequested; ttl++)
                {
                    var options = new PingOptions(ttl, true);
                    using var ping = new Ping();
                    long ms1, ms2, ms3;

                    PingReply r1 = await ping.SendPingAsync(host, 2000, new byte[32], options);
                    ms1 = r1.Status == IPStatus.TtlExpired || r1.Status == IPStatus.Success
                            ? r1.RoundtripTime : -1;

                    PingReply r2 = await ping.SendPingAsync(host, 2000, new byte[32], options);
                    ms2 = r2.Status == IPStatus.TtlExpired || r2.Status == IPStatus.Success
                            ? r2.RoundtripTime : -1;

                    PingReply r3 = await ping.SendPingAsync(host, 2000, new byte[32], options);
                    ms3 = r3.Status == IPStatus.TtlExpired || r3.Status == IPStatus.Success
                            ? r3.RoundtripTime : -1;

                    string addr = r1.Address?.ToString() ?? r2.Address?.ToString() ?? "*";

                    // Async reverse DNS (best-effort, 500 ms)
                    string hostname = addr;
                    try
                    {
                        var t = Dns.GetHostEntryAsync(addr);
                        if (await Task.WhenAny(t, Task.Delay(500)) == t && t.IsCompletedSuccessfully)
                            hostname = t.Result.HostName;
                    }
                    catch { }

                    string t1 = ms1 >= 0 ? $"{ms1} ms" : "*";
                    string t2 = ms2 >= 0 ? $"{ms2} ms" : "*";
                    string t3 = ms3 >= 0 ? $"{ms3} ms" : "*";
                    string line = $"  {ttl,2}.  {t1,7}  {t2,7}  {t3,7}   {addr}   {(hostname != addr ? hostname : "")}";

                    DispatcherQueue.TryEnqueue(() => AppendLine(line));

                    if (r1.Status == IPStatus.Success ||
                        r2.Status == IPStatus.Success ||
                        r3.Status == IPStatus.Success) break;
                }
                DispatcherQueue.TryEnqueue(() => AppendLine("Trace complete."));
            }, _cts.Token);
        }
        catch (OperationCanceledException) { AppendLine("⏹ Traceroute stopped."); }
        finally { StopTool(); }
    }

    // ── NSLookup ─────────────────────────────────────────────────────────────
    private async void OnRunNslookup(object sender, RoutedEventArgs e)
    {
        if (_running) { StopTool(); return; }

        string host       = TargetHost.Text.Trim();
        string recordType = (DnsRecordType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "A";
        string customDns  = CustomDnsServer.Text.Trim();

        StartTool("NSLookup");
        AppendLine($"NSLOOKUP  {host}  [{recordType}]" + (customDns.Length > 0 ? $"  via {customDns}" : ""), "#107C10");
        AppendLine(new string('─', 60));

        try
        {
            // Use nslookup.exe for accurate output
            string args = customDns.Length > 0
                ? $"-type={recordType.Split(' ')[0]} {host} {customDns}"
                : $"-type={recordType.Split(' ')[0]} {host}";

            await RunProcessAsync("nslookup", args);
        }
        finally { StopTool(); }
    }

    // ── Port Check ────────────────────────────────────────────────────────────
    private async void OnRunPortCheck(object sender, RoutedEventArgs e)
    {
        if (_running) { StopTool(); return; }

        string host = TargetHost.Text.Trim();
        int    port = (int)PortNumber.Value;

        StartTool("Port Check");
        AppendLine($"PORT CHECK  {host}:{port}", "#E02020");
        AppendLine(new string('─', 60));

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var tcp = new TcpClient();
            var conn = tcp.ConnectAsync(host, port);
            var timeout = Task.Delay(3000);

            if (await Task.WhenAny(conn, timeout) == conn && !conn.IsFaulted)
            {
                sw.Stop();
                AppendLine($"✅  Port {port} is OPEN  ({sw.ElapsedMilliseconds} ms)", "#107C10");
            }
            else
            {
                AppendLine($"❌  Port {port} is CLOSED or FILTERED", "#E02020");
            }
        }
        catch (Exception ex)
        {
            AppendLine($"❌  {ex.Message}", "#E02020");
        }
        finally { StopTool(); }
    }

    // ── Quick presets ─────────────────────────────────────────────────────────
    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            TargetHost.Text = tag;
    }

    // ── Output helpers ────────────────────────────────────────────────────────
    private void AppendLine(string text, string? hexColor = null)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (OutputText.Text == "Ready. Select a tool and click Run.")
                OutputText.Text = "";

            if (hexColor != null)
            {
                // Simple approach: use runs via code (plain TextBlock can't mix colors)
                // We append with a ● marker line for color hint, keeping it plain text terminal style
                OutputText.Text += $"{text}\n";
            }
            else
            {
                OutputText.Text += $"{text}\n";
            }

            // Auto-scroll
            _ = OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, null);
        });
    }

    private void UpdateStats()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_pingResults.Any()) return;
            StatMin.Text = $"{_pingResults.Min()} ms";
            StatMax.Text = $"{_pingResults.Max()} ms";
            StatAvg.Text = $"{_pingResults.Average():F0} ms";
        });
    }

    private void ResetStats()
    {
        StatMin.Text = "—";
        StatMax.Text = "—";
        StatAvg.Text = "—";
        StatLoss.Text = "—";
    }

    private void StartTool(string name)
    {
        _running = true;
        PingBtnText.Text = _running ? "⏹ Stop" : "Run Ping";
        StatusText.Text = $"Running {name}…";
        RunProgress.IsIndeterminate = true;
        RunProgress.Visibility = Visibility.Visible;
    }

    private void StopTool()
    {
        _running = false;
        _cts?.Cancel();
        _cts = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            PingBtnText.Text = "Run Ping";
            StatusText.Text = "Done.";
            RunProgress.IsIndeterminate = false;
            RunProgress.Visibility = Visibility.Collapsed;
        });
    }

    private async Task RunProcessAsync(string exe, string args)
    {
        await Task.Run(async () =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string output = await proc.StandardOutput.ReadToEndAsync();
                string err    = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (var line in output.Split('\n'))
                        AppendLine(line.TrimEnd());
                    if (!string.IsNullOrWhiteSpace(err))
                        AppendLine($"[stderr] {err}", "#E02020");
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => AppendLine($"Error: {ex.Message}", "#E02020"));
            }
        });
    }

    // ── Toolbar buttons ───────────────────────────────────────────────────────
    private void OnClearOutput(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "Ready. Select a tool and click Run.";
        ResetStats();
    }

    private void OnCopyOutput(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(OutputText.Text);
        Clipboard.SetContent(dp);
    }
}
