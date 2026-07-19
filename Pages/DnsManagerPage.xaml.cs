using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl.Pages;

public class DnsServerResult
{
    public string Name        { get; set; } = "";
    public string Address     { get; set; } = "";
    public string MinMs       { get; set; } = "—";
    public string AvgMs       { get; set; } = "—";
    public string MaxMs       { get; set; } = "—";
    public string Loss        { get; set; } = "—";
    public double AvgRaw      { get; set; } = double.MaxValue;
    public SolidColorBrush RatingBrush => new(AvgRaw < 30
        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
        : AvgRaw < 80
            ? Windows.UI.Color.FromArgb(255, 251, 188, 5)
            : Windows.UI.Color.FromArgb(255, 224, 32, 32));
}

public sealed partial class DnsManagerPage : Page
{
    private MainViewModel? _vm;
    private bool _benchRunning;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<DnsServerResult> _benchResults = new();

    // Well-known DNS servers to benchmark
    private static readonly (string Name, string Primary, string Secondary)[] KnownServers =
    {
        ("Google",           "8.8.8.8",          "8.8.4.4"),
        ("Cloudflare",       "1.1.1.1",          "1.0.0.1"),
        ("Quad9",            "9.9.9.9",          "149.112.112.112"),
        ("OpenDNS",          "208.67.222.222",   "208.67.220.220"),
        ("CleanBrowsing",    "185.228.168.9",    "185.228.169.9"),
        ("Comodo",           "8.26.56.26",       "8.20.247.20"),
        ("Norton ConnectSafe","199.85.126.10",   "199.85.127.10"),
        ("Yandex DNS",       "77.88.8.8",        "77.88.8.1"),
    };

    public DnsManagerPage()
    {
        this.InitializeComponent();
        BenchmarkList.ItemsSource = _benchResults;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        LoadAdapterCombo();
        RefreshCurrentDns();
    }

    // ── Load adapters into combo ──────────────────────────────────────────────
    private void LoadAdapterCombo()
    {
        AdapterCombo.Items.Clear();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            AdapterCombo.Items.Add(ni.Name);
        }
        if (AdapterCombo.Items.Count > 0) AdapterCombo.SelectedIndex = 0;
    }

    // ── Current DNS display ───────────────────────────────────────────────────
    private void OnRefreshDns(object sender, RoutedEventArgs e) => RefreshCurrentDns();

    private void RefreshCurrentDns()
    {
        try
        {
            var lines = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var dns = ni.GetIPProperties().DnsAddresses;
                if (!dns.Any()) continue;
                lines.Add($"{ni.Name}:");
                foreach (var d in dns)
                    lines.Add($"  {d}");
            }
            CurrentDnsText.Text = lines.Any()
                ? string.Join("\n", lines)
                : "No DNS servers found.";
        }
        catch (Exception ex)
        {
            CurrentDnsText.Text = $"Error: {ex.Message}";
        }
    }

    // ── Preset selection ──────────────────────────────────────────────────────
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomDnsPanel == null) return; // guard: fires during XAML load before InitializeComponent completes
        if (DnsPresetCombo.SelectedItem is ComboBoxItem item)
        {
            bool isCustom = item.Tag?.ToString() == "custom";
            CustomDnsPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Apply DNS ─────────────────────────────────────────────────────────────
    private async void OnApplyDns(object sender, RoutedEventArgs e)
    {
        string adapter = AdapterCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrEmpty(adapter)) { ApplyStatus.Text = "Select an adapter first."; return; }

        if (DnsPresetCombo.SelectedItem is not ComboBoxItem item) return;
        string tag = item.Tag?.ToString() ?? "";

        ApplyStatus.Text = "Applying…";

        try
        {
            if (tag == "dhcp")
            {
                await RunElevatedAsync("netsh",
                    $"interface ip set dns name=\"{adapter}\" dhcp");
                ApplyStatus.Text = "Set to DHCP (auto).";
            }
            else if (tag == "custom")
            {
                string p = CustomPrimary.Text.Trim();
                string s = CustomSecondary.Text.Trim();
                if (string.IsNullOrEmpty(p)) { ApplyStatus.Text = "Enter a primary DNS."; return; }

                await RunElevatedAsync("netsh",
                    $"interface ip set dns name=\"{adapter}\" static {p}");
                if (!string.IsNullOrEmpty(s))
                    await RunElevatedAsync("netsh",
                        $"interface ip add dns name=\"{adapter}\" {s} index=2");
                ApplyStatus.Text = $"Custom DNS applied: {p} / {s}";
            }
            else
            {
                var parts = tag.Split('|');
                string primary   = parts[0];
                string secondary = parts.Length > 1 ? parts[1] : "";

                await RunElevatedAsync("netsh",
                    $"interface ip set dns name=\"{adapter}\" static {primary}");
                if (!string.IsNullOrEmpty(secondary))
                    await RunElevatedAsync("netsh",
                        $"interface ip add dns name=\"{adapter}\" {secondary} index=2");
                ApplyStatus.Text = $"DNS set to {primary} / {secondary}";
            }

            RefreshCurrentDns();
        }
        catch (Exception ex) { ApplyStatus.Text = $"Error: {ex.Message}"; }
    }

    // ── DNS Benchmark ─────────────────────────────────────────────────────────
    private async void OnRunBenchmark(object sender, RoutedEventArgs e)
    {
        if (_benchRunning) { _cts?.Cancel(); return; }

        _benchRunning = true;
        BenchBtnText.Text = "Stop";
        BenchProgress.Visibility = Visibility.Visible;
        BenchProgress.IsIndeterminate = true;
        _benchResults.Clear();

        _cts = new CancellationTokenSource();
        // Domains to resolve — using a mix so cached answers don't skew results
        string[] testDomains = { "google.com", "github.com", "microsoft.com", "cloudflare.com", "amazon.com" };
        int reps = testDomains.Length; // 5 — must not be const (array.Length isn't a compile-time constant)

        try
        {
            var tasks = KnownServers.Select(async s =>
            {
                var times = new List<long>();
                int lost = 0;

                foreach (var domain in testDomains)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    try
                    {
                        // Use nslookup to force resolution through the specific DNS server
                        // This measures actual UDP/TCP DNS query latency, not ICMP
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var psi = new System.Diagnostics.ProcessStartInfo("nslookup",
                            $"{domain} {s.Primary}")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError  = true,
                            UseShellExecute        = false,
                            CreateNoWindow         = true
                        };
                        using var proc = System.Diagnostics.Process.Start(psi)!;
                        await proc.WaitForExitAsync(_cts.Token);
                        sw.Stop();

                        // nslookup exit 0 = success
                        if (proc.ExitCode == 0)
                            times.Add(sw.ElapsedMilliseconds);
                        else
                            lost++;
                    }
                    catch { lost++; }

                    await Task.Delay(100, _cts.Token).ConfigureAwait(false);
                }

                double avg = times.Count > 0 ? times.Average() : double.MaxValue;
                string loss = $"{(int)((double)lost / reps * 100)}%";

                return new DnsServerResult
                {
                    Name    = s.Name,
                    Address = $"{s.Primary} / {s.Secondary}",
                    MinMs   = times.Count > 0 ? $"{times.Min()} ms" : "×",
                    AvgMs   = times.Count > 0 ? $"{avg:F0} ms"      : "×",
                    MaxMs   = times.Count > 0 ? $"{times.Max()} ms" : "×",
                    Loss    = loss,
                    AvgRaw  = avg
                };
            });

            var results = await Task.WhenAll(tasks);
            var sorted  = results.OrderBy(r => r.AvgRaw).ToList();

            DispatcherQueue.TryEnqueue(() =>
            {
                _benchResults.Clear();
                foreach (var r in sorted) _benchResults.Add(r);
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            _benchRunning = false;
            DispatcherQueue.TryEnqueue(() =>
            {
                BenchBtnText.Text = "Run Benchmark";
                BenchProgress.Visibility = Visibility.Collapsed;
            });
        }
    }

    // Double-click bench result to apply that DNS
    private async void OnBenchResultDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (BenchmarkList.SelectedItem is DnsServerResult r)
        {
            string adapter = AdapterCombo.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrEmpty(adapter)) return;

            string primary = r.Address.Split('/')[0].Trim();
            ApplyStatus.Text = $"Applying {r.Name} ({primary})…";
            await RunElevatedAsync("netsh",
                $"interface ip set dns name=\"{adapter}\" static {primary}");
            ApplyStatus.Text = $"Applied {r.Name}.";
            RefreshCurrentDns();
        }
    }

    // ── Quick actions ─────────────────────────────────────────────────────────
    private async void OnFlushDns(object sender, RoutedEventArgs e)
        => await ShowOutput("ipconfig", "/flushdns");

    private async void OnDisplayDnsCache(object sender, RoutedEventArgs e)
        => await ShowOutput("ipconfig", "/displaydns");

    private async void OnRegisterDns(object sender, RoutedEventArgs e)
        => await ShowOutput("ipconfig", "/registerdns");

    private async Task ShowOutput(string exe, string args)
    {
        OutputBorder.Visibility = Visibility.Visible;
        OutputText.Text = $"Running {exe} {args}…\n";

        string result = await Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string o = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return o;
        });

        OutputText.Text = result;
    }

    // ── Elevated helper ───────────────────────────────────────────────────────
    private static Task RunElevatedAsync(string exe, string args) => Task.Run(() =>
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            Verb            = "runas",
            UseShellExecute = true,
            WindowStyle     = System.Diagnostics.ProcessWindowStyle.Hidden
        };
        System.Diagnostics.Process.Start(psi)?.WaitForExit();
    });
}
