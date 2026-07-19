using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public class SpeedResult
{
    public string Time { get; set; } = "";
    public string Down { get; set; } = "";
    public string Up   { get; set; } = "";
    public string Ping { get; set; } = "";
}

public sealed partial class SpeedToolsPage : Page
{
    private bool _running;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<SpeedResult> _history = new();

    // Test files — multiple sources for reliability
    private static readonly string[] DownloadUrls =
    {
        "https://speed.hetzner.de/100MB.bin",
        "https://proof.ovh.net/files/100Mb.dat",
        "https://speedtest.tele2.net/100MB.zip"
    };

    private static readonly string UploadUrl = "https://httpbin.org/post";

    public SpeedToolsPage()
    {
        this.InitializeComponent();
        HistoryList.ItemsSource = _history;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ServerName.Text = "Hetzner / OVH / Tele2 (auto)";
    }

    private async void OnRunTest(object sender, RoutedEventArgs e)
    {
        if (_running) { _cts?.Cancel(); return; }

        _running = true;
        RunBtnText.Text   = "Stop";
        RunIcon.Glyph     = "\uE711";
        TestProgress.Value = 0;
        ResultDownload.Text = "—";
        ResultUpload.Text   = "—";
        ResultPing.Text     = "—";

        _cts = new CancellationTokenSource();

        double down = 0, up = 0;
        long   ping = 0;

        try
        {
            // ── Phase 1: Ping ──────────────────────────────
            PhaseLabel.Text     = "Measuring latency…";
            TestStatusText.Text = "Ping";
            ping = await MeasurePingAsync("8.8.8.8", _cts.Token);
            ResultPing.Text  = ping.ToString();
            TestProgress.Value = 20;

            // ── Phase 2: Download ──────────────────────────
            PhaseLabel.Text     = "Measuring download speed…";
            TestStatusText.Text = "Download";

            var progressDown = new Progress<double>(v =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    TestProgress.Value = 20 + v * 40;
                    SpeedDisplay.Text  = $"{v:F1}";
                    SpeedUnit.Text     = "Mbps ↓";
                });
            });

            down = await MeasureDownloadAsync(progressDown, _cts.Token);
            ResultDownload.Text = $"{down:F1}";
            TestProgress.Value  = 60;

            // ── Phase 3: Upload ────────────────────────────
            PhaseLabel.Text     = "Measuring upload speed…";
            TestStatusText.Text = "Upload";

            var progressUp = new Progress<double>(v =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    TestProgress.Value = 60 + v * 35;
                    SpeedDisplay.Text  = $"{v:F1}";
                    SpeedUnit.Text     = "Mbps ↑";
                });
            });

            up = await MeasureUploadAsync(progressUp, _cts.Token);
            ResultUpload.Text  = $"{up:F1}";
            TestProgress.Value = 100;

            // Final display
            PhaseLabel.Text     = "Test complete";
            SpeedDisplay.Text   = $"{down:F1}";
            SpeedUnit.Text      = "Mbps ↓";
            TestStatusText.Text = "Done";

            // Add to history
            _history.Insert(0, new SpeedResult
            {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Down = $"↓ {down:F1} Mbps",
                Up   = $"↑ {up:F1} Mbps",
                Ping = $"⏱ {ping} ms"
            });
        }
        catch (OperationCanceledException)
        {
            PhaseLabel.Text     = "Test stopped.";
            TestStatusText.Text = "Cancelled";
            SpeedDisplay.Text   = "—";
        }
        catch (Exception ex)
        {
            PhaseLabel.Text     = $"Error: {ex.Message}";
            TestStatusText.Text = "Error";
        }
        finally
        {
            _running = false;
            RunBtnText.Text = "Start Speed Test";
            RunIcon.Glyph   = "\uEC4A";
        }
    }

    // ── Ping measurement ──────────────────────────────────────────────────────
    private static async Task<long> MeasurePingAsync(string host, CancellationToken ct)
    {
        long total = 0; int count = 5;
        using var ping = new Ping();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(host, 2000);
            if (reply.Status == IPStatus.Success) total += reply.RoundtripTime;
            await Task.Delay(200, ct);
        }
        return total / count;
    }

    // ── Download measurement ──────────────────────────────────────────────────
    private static async Task<double> MeasureDownloadAsync(
        IProgress<double> progress, CancellationToken ct)
    {
        const int testDurationMs = 8000; // 8 seconds
        const int bufSize = 131072;      // 128 KB buffer

        foreach (string url in DownloadUrls)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var response = await client.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buf  = new byte[bufSize];
                long totalBytes = 0;

                while (sw.ElapsedMilliseconds < testDurationMs)
                {
                    ct.ThrowIfCancellationRequested();
                    int read = await stream.ReadAsync(buf, ct);
                    if (read == 0) break;
                    totalBytes += read;

                    double elapsedSec = sw.Elapsed.TotalSeconds;
                    if (elapsedSec > 0)
                    {
                        double mbps = totalBytes * 8 / (elapsedSec * 1_000_000);
                        progress.Report(mbps);
                    }
                }

                double totalSec = sw.Elapsed.TotalSeconds;
                return totalBytes * 8 / (totalSec * 1_000_000);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try next server */ }
        }
        return 0;
    }

    // ── Upload measurement ────────────────────────────────────────────────────
    private static async Task<double> MeasureUploadAsync(
        IProgress<double> progress, CancellationToken ct)
    {
        const int uploadBytes = 10 * 1024 * 1024; // 10 MB

        try
        {
            var payload = new byte[uploadBytes];
            new Random().NextBytes(payload);

            using var client  = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Simple POST — measure time to send
            await client.PostAsync(UploadUrl, content, ct);
            sw.Stop();

            double uploadMbps = uploadBytes * 8 / (sw.Elapsed.TotalSeconds * 1_000_000);
            progress.Report(uploadMbps);
            return uploadMbps;
        }
        catch (OperationCanceledException) { throw; }
        catch { return 0; }
    }
}
