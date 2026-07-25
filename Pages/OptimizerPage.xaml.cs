using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed partial class OptimizerPage : Page
{
    public OptimizerPage() { this.InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = CheckStatusAsync();
    }

    private void OnCheckStatus(object sender, RoutedEventArgs e) => _ = CheckStatusAsync();

    private async Task CheckStatusAsync()
    {
        AutotuneStatus.Text = "Checking…";
        string atune = await RunAsync("netsh", "interface tcp show global");
        AutotuneStatus.Text = atune.Contains("normal", StringComparison.OrdinalIgnoreCase)
            ? "✅ Normal (optimal)" : "⚠ Not set to Normal";

        // INCOMPLETE-007: Read actual Nagle state from registry (was hardcoded)
        string nagle = await RunAsync("reg",
            @"query HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters /v TcpNoDelay");
        NagleStatus.Text = nagle.Contains("0x1", StringComparison.OrdinalIgnoreCase)
            ? "✅ Disabled (low-latency)" : "Default (enabled)";

        string dns = await RunAsync("reg",
            @"query HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters /v MaxCacheTtl");
        DnsCacheStatus.Text = dns.Contains("MaxCacheTtl", StringComparison.OrdinalIgnoreCase)
            ? "✅ Custom TTL configured" : "Default TTL (86400s)";

        string thr = await RunAsync("reg",
            @"query ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"" /v NetworkThrottlingIndex");
        ThrottleStatus.Text = thr.Contains("ffffffff", StringComparison.OrdinalIgnoreCase)
            ? "✅ Throttling disabled" : "Default throttling active";
    }

    // ── Individual tweaks ─────────────────────────────────────────────────────
    private async void OnTweak(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        Log($"Applying: {tag}…");

        try
        {
            switch (tag)
            {
                // TCP Autotuning
                case "autotune_normal":
                    await Netsh("interface tcp set global autotuninglevel=normal");
                    Log("✅ TCP Autotuning → Normal"); break;
                case "autotune_disabled":
                    await Netsh("interface tcp set global autotuninglevel=disabled");
                    Log("✅ TCP Autotuning → Disabled"); break;
                case "autotune_restricted":
                    await Netsh("interface tcp set global autotuninglevel=restricted");
                    Log("✅ TCP Autotuning → Restricted"); break;

                // Nagle
                case "nagle_disable":
                    await RegWrite(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                        "TcpNoDelay", "REG_DWORD", "1");
                    await RegWrite(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters",
                        "TcpAckFrequency", "REG_DWORD", "1");
                    Log("✅ Nagle disabled (low-latency mode)"); break;
                case "nagle_enable":
                    await RegDelete(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpNoDelay");
                    await RegDelete(@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpAckFrequency");
                    Log("✅ Nagle enabled (default)"); break;

                // DNS Cache TTL
                case "dnscache_max":
                    await RegWrite(@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                        "MaxCacheTtl", "REG_DWORD", "3600");
                    await RegWrite(@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                        "MaxNegativeCacheTtl", "REG_DWORD", "0");
                    Log("✅ DNS Cache TTL → 3600s"); break;
                case "dnscache_default":
                    await RegDelete(@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl");
                    Log("✅ DNS Cache TTL → Default (86400s)"); break;
                case "dnscache_low":
                    await RegWrite(@"HKLM\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                        "MaxCacheTtl", "REG_DWORD", "300");
                    Log("✅ DNS Cache TTL → 300s"); break;

                // Network Throttle Index
                case "throttle_off":
                    await RegWrite(
                        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "NetworkThrottlingIndex", "REG_DWORD", "ffffffff");
                    Log("✅ Network Throttling → Disabled"); break;
                case "throttle_on":
                    await RegWrite(
                        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
                        "NetworkThrottlingIndex", "REG_DWORD", "10");
                    Log("✅ Network Throttling → Default (10)"); break;

                // TCP Timestamps
                case "timestamps_off":
                    await Netsh("interface tcp set global timestamps=disabled");
                    Log("✅ TCP Timestamps → Disabled"); break;
                case "timestamps_on":
                    await Netsh("interface tcp set global timestamps=enabled");
                    Log("✅ TCP Timestamps → Enabled"); break;

                // RSS
                case "rss_on":
                    await Netsh("interface tcp set global rss=enabled");
                    Log("✅ Receive Side Scaling → Enabled"); break;
                case "rss_off":
                    await Netsh("interface tcp set global rss=disabled");
                    Log("✅ Receive Side Scaling → Disabled"); break;

                // ECN
                case "ecn_on":
                    await Netsh("interface tcp set global ecncapability=enabled");
                    Log("✅ ECN Capability → Enabled"); break;
                case "ecn_off":
                    await Netsh("interface tcp set global ecncapability=disabled");
                    Log("✅ ECN Capability → Disabled"); break;

                // Chimney
                case "chimney_on":
                    await Netsh("interface tcp set global chimney=enabled");
                    Log("✅ TCP Chimney Offload → Enabled"); break;
                case "chimney_off":
                    await Netsh("interface tcp set global chimney=disabled");
                    Log("✅ TCP Chimney Offload → Disabled"); break;
            }
        }
        catch (Exception ex) { Log($"❌ Error: {ex.Message}"); }
    }

    // ── Full optimize ─────────────────────────────────────────────────────────
    private async void OnFullOptimize(object sender, RoutedEventArgs e)
    {
        Log("=== Applying all optimizations ===");
        var tweaks = new[]
        {
            "autotune_normal", "nagle_disable", "dnscache_max",
            "throttle_off", "timestamps_off", "rss_on", "ecn_on", "chimney_off"
        };
        foreach (var t in tweaks)
        {
            // Simulate button click per tag
            var fakeBtn = new Button { Tag = t };
            OnTweak(fakeBtn, new RoutedEventArgs());
            await Task.Delay(200);
        }
        Log("=== Done. Restart recommended. ===");
        await CheckStatusAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void Log(string msg) =>
        DispatcherQueue.TryEnqueue(() => OptLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n");

    private static Task Netsh(string args) => Task.Run(() => ElevatedRunner.RunNetsh(args));

    private static Task RegWrite(string key, string name, string type, string value) => Task.Run(() =>
        ElevatedRunner.RunPowerShell($"Set-ItemProperty -Path 'Registry::HKEY_LOCAL_MACHINE\\{key.Replace("HKLM\\", "")}' -Name '{name}' -Value '{value}' -Type {(type.Contains("DWORD") ? "DWord" : "String")} -Force"));

    private static Task RegDelete(string key, string name) => Task.Run(() =>
        ElevatedRunner.RunPowerShell($"Remove-ItemProperty -Path 'Registry::HKEY_LOCAL_MACHINE\\{key.Replace("HKLM\\", "")}' -Name '{name}' -Force -ErrorAction SilentlyContinue"));

    private static Task<string> RunAsync(string exe, string args) => Task.Run(() =>
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string o = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return o;
        }
        catch { return ""; }
    });
}
