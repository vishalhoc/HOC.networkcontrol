using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using WinNetControl.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed partial class ReportingPage : Page
{
    private MainViewModel? _vm;
    private string _lastReportText = "";
    private string _lastJsonText   = "";

    public ReportingPage() { this.InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        ReportTimestamp.Text = $"Last run: —";
        ReportMachine.Text   = $"Machine: {Environment.MachineName}";
        ReportSections.Text  = "0 sections";
    }

    // ── Generate ──────────────────────────────────────────────────────────────
    private async void OnGenerateReport(object sender, RoutedEventArgs e)
    {
        ReportProgress.Visibility = Visibility.Visible;
        ReportStatus.Text = "Building…";

        var md  = new StringBuilder();
        var jso = new StringBuilder();

        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        int sectionCount = 0;

        md.AppendLine($"# WinNetControl Network Report");
        md.AppendLine($"> Generated: {ts}  |  Machine: {Environment.MachineName}  |  OS: {Environment.OSVersion}");
        md.AppendLine();

        jso.AppendLine("{");
        jso.AppendLine($"  \"GeneratedAt\": \"{ts}\",");
        jso.AppendLine($"  \"Machine\": \"{Environment.MachineName}\",");
        jso.AppendLine($"  \"OS\": \"{Environment.OSVersion}\",");

        // ── Section: System Info ──────────────────────────────────────────────
        if (SecSystem.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## System Information");
            string sysInfo = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine($"- **Hostname**: {Environment.MachineName}");
                sb.AppendLine($"- **OS**: {Environment.OSVersion}");
                sb.AppendLine($"- **64-bit**: {Environment.Is64BitOperatingSystem}");
                sb.AppendLine($"- **Processors**: {Environment.ProcessorCount}");
                sb.AppendLine($"- **Uptime**: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm\\:ss}");
                return sb.ToString();
            });
            md.AppendLine(sysInfo);
            jso.AppendLine($"  \"SystemInfo\": {{ \"Hostname\": \"{Environment.MachineName}\", \"OS\": \"{Environment.OSVersion}\", \"Processors\": {Environment.ProcessorCount} }},");
        }

        // ── Section: Network Adapters ─────────────────────────────────────────
        if (SecAdapters.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## Network Adapters");
            string adapterInfo = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    sb.AppendLine($"### {nic.Name}");
                    sb.AppendLine($"- Type: {nic.NetworkInterfaceType}");
                    sb.AppendLine($"- Status: {nic.OperationalStatus}");
                    sb.AppendLine($"- Speed: {nic.Speed / 1_000_000:F0} Mbps");
                    var ip = nic.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ip != null) sb.AppendLine($"- IPv4: {ip.Address}");
                }
                return sb.ToString();
            });
            md.AppendLine(adapterInfo);
        }

        // ── Section: Active Connections ───────────────────────────────────────
        if (SecConnections.IsChecked == true && _vm != null)
        {
            sectionCount++;
            md.AppendLine("## Active Connections (Top 20)");
            int count = 0;
            foreach (var proc in _vm.Processes.Take(20))
            {
                md.AppendLine($"- **{proc.ProcessName}** (PID {proc.ProcessId})  — {proc.Connections.Count} connections");
                count++;
            }
            md.AppendLine($"> Total processes tracked: {_vm.Processes.Count}");
            md.AppendLine();
        }

        // ── Section: DNS ──────────────────────────────────────────────────────
        if (SecDns.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## DNS Configuration");
            string dnsInfo = await Task.Run(() =>
            {
                var sb = new StringBuilder();
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up))
                {
                    var dns = nic.GetIPProperties().DnsAddresses;
                    if (dns.Count == 0) continue;
                    sb.AppendLine($"- **{nic.Name}**: {string.Join(", ", dns.Select(d => d.ToString()))}");
                }
                return sb.ToString();
            });
            md.AppendLine(dnsInfo);
        }

        // ── Section: Firewall ─────────────────────────────────────────────────
        if (SecFirewall.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## Firewall Summary");
            var fwInfo = await Task.Run(() =>
            {
                var names = NetworkOptimizeService.GetWinNetControlRuleNames();
                var sb = new StringBuilder();
                sb.AppendLine($"- **WinNetControl rules**: {names.Count}");
                foreach (var n in names.Take(10)) sb.AppendLine($"  - {n}");
                if (names.Count > 10) sb.AppendLine($"  - ...and {names.Count - 10} more");
                return sb.ToString();
            });
            md.AppendLine(fwInfo);
        }

        // ── Section: Routing Table ────────────────────────────────────────────
        if (SecRoutes.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## Routing Table (IPv4)");
            string routeInfo = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo("route", "print -4")
                {
                    RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string o = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return $"```\n{o.Trim()}\n```";
            });
            md.AppendLine(routeInfo);
            md.AppendLine();
        }

        // ── Section: Proxy ────────────────────────────────────────────────────
        if (SecProxy.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## Proxy Status");
            var proxy = HttpProxyService.GetSystemProxyStatus();
            md.AppendLine($"- **Enabled**: {proxy.Enabled}");
            md.AppendLine($"- **Server**: {(proxy.Server.Length > 0 ? proxy.Server : "—")}");
            md.AppendLine($"- **Bypass**: {(proxy.Bypass.Length > 0 ? proxy.Bypass : "—")}");
            md.AppendLine();
        }

        // ── Section: Security ─────────────────────────────────────────────────
        if (SecSecurity.IsChecked == true && _vm != null)
        {
            sectionCount++;
            md.AppendLine("## Security Summary");
            int suspCount = _vm.Processes.Count(p => p.IsSuspicious);
            md.AppendLine($"- **Suspicious processes**: {suspCount}");
            md.AppendLine($"- **Total monitored processes**: {_vm.Processes.Count}");
            bool isAdmin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            md.AppendLine($"- **Running as Admin**: {isAdmin}");
            md.AppendLine();
        }

        // ── Section: Activity Log ─────────────────────────────────────────────
        if (SecLog.IsChecked == true)
        {
            sectionCount++;
            md.AppendLine("## Recent Activity Log (Last 30)");
            md.AppendLine("| Time | Type | App | Details |");
            md.AppendLine("|------|------|-----|---------|");
            foreach (var entry in HistoryLogService.Logs.Take(30))
                md.AppendLine($"| {entry.TimestampString} | {entry.EventType} | {entry.AppName} | {entry.Details} |");
            md.AppendLine();
        }

        // Build JSON
        jso.AppendLine($"  \"SectionsIncluded\": {sectionCount}");
        jso.AppendLine("}");

        _lastReportText = md.ToString();
        _lastJsonText   = jso.ToString();

        ReportPreview.Text    = _lastReportText;
        ReportTimestamp.Text  = $"Last run: {ts}";
        ReportSections.Text   = $"{sectionCount} sections included";
        ReportProgress.Visibility = Visibility.Collapsed;
        ReportStatus.Text = $"✅ Report generated — {sectionCount} sections";
        HistoryLogService.AddLog("Report", "ReportingPage", $"Snapshot generated, {sectionCount} sections");
    }

    // ── Export ────────────────────────────────────────────────────────────────
    private async void OnExportJson(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastJsonText)) { ReportStatus.Text = "Generate a report first."; return; }
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"WNC_Report_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(path, _lastJsonText);
        ReportStatus.Text = $"✅ JSON exported to Desktop: {Path.GetFileName(path)}";
    }

    private async void OnExportMarkdown(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastReportText)) { ReportStatus.Text = "Generate a report first."; return; }
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"WNC_Report_{DateTime.Now:yyyyMMdd_HHmmss}.md");
        await File.WriteAllTextAsync(path, _lastReportText);
        ReportStatus.Text = $"✅ Markdown exported to Desktop: {Path.GetFileName(path)}";
    }

    private void OnCopyToClipboard(object sender, RoutedEventArgs e)
    {
        string content = _lastReportText.Length > 0 ? _lastReportText : "No report generated yet.";
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(content);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        ReportStatus.Text = "✅ Report copied to clipboard.";
    }
}
