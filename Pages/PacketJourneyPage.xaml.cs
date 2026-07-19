using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

public class HopRow
{
    public int    HopNum    { get; set; }
    public string IpAddress { get; set; } = "";
    public string Hostname  { get; set; } = "";
    public string GeoLabel  { get; set; } = "";
    public string Rtt1      { get; set; } = "*";
    public string Rtt2      { get; set; } = "*";
    public string Rtt3      { get; set; } = "*";
    public double AvgMs     { get; set; } = -1;
    public string AvgLatency => AvgMs < 0 ? "  *" : $"{AvgMs:F0} ms";

    public SolidColorBrush LatencyBrush => AvgMs < 0
        ? new SolidColorBrush(Color.FromArgb(255, 128, 128, 128))
        : AvgMs < 30   ? new SolidColorBrush(Color.FromArgb(255, 16, 124, 16))
        : AvgMs < 100  ? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))
        : AvgMs < 300  ? new SolidColorBrush(Color.FromArgb(255, 251, 188, 5))
                       : new SolidColorBrush(Color.FromArgb(255, 224, 32, 32));
}

public sealed partial class PacketJourneyPage : Page
{
    private readonly ObservableCollection<HopRow> _hops = new();
    private CancellationTokenSource? _cts;

    public PacketJourneyPage()
    {
        this.InitializeComponent();
        HopList.ItemsSource = _hops;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
    }

    // ── Input ─────────────────────────────────────────────────────────────────
    public MainViewModel? ViewModel { get; private set; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
        {
            ViewModel = vm;
        }

        if (ViewModel != null && !string.IsNullOrWhiteSpace(ViewModel.TargetPacketJourneyIp))
        {
            TargetBox.Text = ViewModel.TargetPacketJourneyIp;
            ViewModel.TargetPacketJourneyIp = ""; // Consume it
            OnTrace(this, new RoutedEventArgs());
        }
    }

    private void OnTargetBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnTrace(sender, e);
    }

    private void OnTrace(object sender, RoutedEventArgs e)
    {
        string host = TargetBox.Text.Trim();
        if (string.IsNullOrEmpty(host)) { TraceStatus.Text = "Enter a host or IP."; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunTraceAsync(host, _cts.Token);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        TraceStatus.Text = "Cancelled.";
        TraceProgress.Visibility = Visibility.Collapsed;
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Traceroute: {TargetBox.Text}  — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"{"Hop",-4} {"IP",-16} {"Hostname",-32} {"Geo",-12} {"Avg RTT",8}");
        foreach (var h in _hops)
            sb.AppendLine($"{h.HopNum,-4} {h.IpAddress,-16} {h.Hostname,-32} {h.GeoLabel,-12} {h.AvgLatency,8}");

        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(sb.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        SummaryText.Text = "Copied to clipboard ✅";
        await Task.Delay(2000);
        SummaryText.Text = "";
    }

    // ── Trace ─────────────────────────────────────────────────────────────────
    private async Task RunTraceAsync(string host, CancellationToken ct)
    {
        _hops.Clear();
        HopCanvas.Children.Clear();
        TraceProgress.Visibility = Visibility.Visible;
        TraceStatus.Text   = $"Tracing route to {host}…";
        SummaryText.Text   = "";
        GeoPathText.Text   = "—";
        AnalysisText.Text  = "Tracing…";

        var rows = new List<HopRow>();

        await Task.Run(async () =>
        {
            var psi = new ProcessStartInfo("tracert", $"-d -h 30 {host}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var proc = Process.Start(psi)!;

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (ct.IsCancellationRequested) { proc.Kill(); break; }

                // Match tracert output: "  1     1 ms     1 ms     2 ms  192.168.1.1"
                var m = Regex.Match(line,
                    @"^\s*(\d+)\s+([\d<*]+\s*ms|[\*]+)\s+([\d<*]+\s*ms|[\*]+)\s+([\d<*]+\s*ms|[\*]+)\s+(\S+)");
                if (!m.Success) continue;

                int hopNum = int.Parse(m.Groups[1].Value.Trim());
                string r1 = m.Groups[2].Value.Trim();
                string r2 = m.Groups[3].Value.Trim();
                string r3 = m.Groups[4].Value.Trim();
                string ip  = m.Groups[5].Value.Trim();

                double avg = -1;
                var times = new[] { r1, r2, r3 }
                    .Select(s => { var n = Regex.Match(s, @"\d+"); return n.Success ? (double?)double.Parse(n.Value) : null; })
                    .Where(d => d.HasValue).Select(d => d!.Value).ToList();
                if (times.Count > 0) avg = times.Average();

                string geo = await GeoIpService.GetCountryLabelAsync(ip);

                string hostname = ip;
                try
                {
                    if (avg >= 0) // only resolve if responsive
                    {
                        var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromSeconds(2));
                        hostname = entry.HostName;
                    }
                }
                catch { }

                var row = new HopRow
                {
                    HopNum = hopNum, IpAddress = ip,
                    Hostname = hostname != ip ? hostname : "",
                    GeoLabel = geo, Rtt1 = r1, Rtt2 = r2, Rtt3 = r3, AvgMs = avg
                };
                rows.Add(row);

                DispatcherQueue.TryEnqueue(() =>
                {
                    _hops.Add(row);
                    DrawHopBar(row);
                    TraceStatus.Text = $"Hop {hopNum}: {ip}  {geo}";
                });
            }
            proc.WaitForExit();
        }, ct);

        DispatcherQueue.TryEnqueue(() =>
        {
            TraceProgress.Visibility = Visibility.Collapsed;

            if (_hops.Count == 0)
            {
                TraceStatus.Text = ct.IsCancellationRequested ? "Cancelled." : "No hops found.";
                return;
            }

            var responsive = _hops.Where(h => h.AvgMs >= 0).ToList();
            double totalMs = responsive.Sum(h => h.AvgMs);
            double maxMs   = responsive.Any() ? responsive.Max(h => h.AvgMs) : 0;
            int    timeouts= _hops.Count(h => h.AvgMs < 0);

            TraceStatus.Text = $"Done — {_hops.Count} hops";
            SummaryText.Text = $"Total: {totalMs:F0}ms  Max: {maxMs:F0}ms  Timeouts: {timeouts}";

            // GeoIP path
            var geos = _hops.Where(h => h.GeoLabel.Length > 0).Select(h => h.GeoLabel).Distinct().ToList();
            GeoPathText.Text = geos.Count > 0 ? string.Join(" → ", geos) : "All private hops";

            // Analysis
            var sb = new StringBuilder();
            if (responsive.Any())
            {
                sb.AppendLine($"✅ {_hops.Count} hops to destination");
                sb.AppendLine($"• Total RTT: {totalMs:F0} ms");
                sb.AppendLine($"• Max hop RTT: {maxMs:F0} ms");
                if (timeouts > 0) sb.AppendLine($"⚠ {timeouts} hop(s) timed out (ICMP filtered)");
                var slow = _hops.Where(h => h.AvgMs > 200).ToList();
                if (slow.Count > 0) sb.AppendLine($"⚠ Slow hops: {string.Join(", ", slow.Select(h => $"#{h.HopNum} {h.IpAddress}"))}");
            }
            else sb.AppendLine("❌ All hops timed out. Host unreachable or ICMP blocked.");
            AnalysisText.Text = sb.ToString().TrimEnd();

            HistoryLogService.AddLog("PacketJourney", host, $"{_hops.Count} hops, {totalMs:F0}ms total");
        });
    }

    // ── Canvas bar chart ──────────────────────────────────────────────────────
    private double _canvasMaxMs = 1;

    private void DrawHopBar(HopRow hop)
    {
        const double barW = 40, gap = 10, baseY = 140, maxBarH = 110;

        double ms = Math.Max(hop.AvgMs, 0);
        if (ms > _canvasMaxMs) _canvasMaxMs = ms;

        // Redraw all bars to re-scale
        HopCanvas.Children.Clear();
        HopCanvas.Width = Math.Max(600, (_hops.Count + 1) * (barW + gap));

        foreach (var h in _hops)
        {
            double x     = gap + (h.HopNum - 1) * (barW + gap);
            double barH  = h.AvgMs < 0 ? 6 : Math.Max(6, (h.AvgMs / _canvasMaxMs) * maxBarH);

            // Bar
            var rect = new Rectangle
            {
                Width = barW, Height = barH,
                Fill = h.LatencyBrush, RadiusX = 5, RadiusY = 5
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect,  baseY - barH);
            HopCanvas.Children.Add(rect);

            // Hop number label
            var numLabel = new TextBlock
            {
                Text = h.HopNum.ToString(), FontSize = 10,
                Foreground = new SolidColorBrush(Colors.Gray),
                Width = barW, TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(numLabel, x);
            Canvas.SetTop(numLabel,  baseY + 2);
            HopCanvas.Children.Add(numLabel);

            // Latency label above bar
            if (h.AvgMs >= 0)
            {
                var msLabel = new TextBlock
                {
                    Text = $"{h.AvgMs:F0}", FontSize = 9,
                    Foreground = h.LatencyBrush,
                    Width = barW, TextAlignment = TextAlignment.Center
                };
                Canvas.SetLeft(msLabel, x);
                Canvas.SetTop(msLabel,  baseY - barH - 14);
                HopCanvas.Children.Add(msLabel);
            }
        }
    }
}
