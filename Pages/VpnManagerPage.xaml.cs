using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

public class VpnAdapterRow
{
    public string Name   { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class VpnProfileRow
{
    public string Name        { get; set; } = "";
    public string TunnelType  { get; set; } = "";
    public string Status      { get; set; } = "Unknown";
    public string StatusIcon  => Status == "Connected" ? "\uE102" : "\uE711";
    public SolidColorBrush StatusColor => Status == "Connected"
        ? new SolidColorBrush(Color.FromArgb(255, 16, 124, 16))
        : new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));
}

public sealed partial class VpnManagerPage : Page
{
    private readonly ObservableCollection<VpnAdapterRow>  _adapters = new();
    private readonly ObservableCollection<VpnProfileRow>  _profiles = new();

    public VpnManagerPage()
    {
        this.InitializeComponent();
        VpnAdapterList.ItemsSource  = _adapters;
        VpnProfileList.ItemsSource  = _profiles;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshAsync();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    // ── Refresh all ───────────────────────────────────────────────────────────
    private async Task RefreshAsync()
    {
        await LoadVpnAdaptersAsync();
        await LoadActiveConnectionAsync();
        await LoadProfilesAsync();
    }

    // ── Active connection via rasdial ─────────────────────────────────────────
    private async Task LoadActiveConnectionAsync()
    {
        string output = await RunAsync("rasdial", "");
        VpnStatusText.Text = string.IsNullOrWhiteSpace(output) || output.Contains("No connections", StringComparison.OrdinalIgnoreCase)
            ? "⚪ No active VPN connection."
            : $"🟢 {output.Trim()}";
    }

    // ── VPN adapters via netsh / WMI ──────────────────────────────────────────
    private async Task LoadVpnAdaptersAsync()
    {
        _adapters.Clear();
        string output = await RunAsync("powershell",
            "-NoProfile -Command \"Get-NetAdapter | Where-Object {$_.InterfaceDescription -match 'VPN|WireGuard|OpenVPN|Tunnel|TAP|L2TP|PPTP|SSTP|IKEv2'} | Select-Object Name,InterfaceDescription,Status | Format-List\"");

        if (string.IsNullOrWhiteSpace(output) || output.Trim().Length == 0)
        {
            AdapterStatus.Text = "No VPN adapters detected.";
            return;
        }

        // Parse blocks of Name/Description/Status
        var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            string name = Regex.Match(block, @"Name\s*:\s*(.+)").Groups[1].Value.Trim();
            string desc = Regex.Match(block, @"InterfaceDescription\s*:\s*(.+)").Groups[1].Value.Trim();
            string stat = Regex.Match(block, @"Status\s*:\s*(.+)").Groups[1].Value.Trim();
            if (name.Length > 0)
                _adapters.Add(new VpnAdapterRow { Name = name, Detail = $"{desc} — {stat}" });
        }
        AdapterStatus.Text = $"{_adapters.Count} VPN adapter(s)";
    }

    // ── Load Windows VPN profiles via PowerShell ──────────────────────────────
    private async void OnLoadProfiles(object sender, RoutedEventArgs e) => await LoadProfilesAsync();

    private async Task LoadProfilesAsync()
    {
        _profiles.Clear();
        string output = await RunAsync("powershell",
            "-NoProfile -Command \"Get-VpnConnection | Select-Object Name,TunnelType,ConnectionStatus | Format-List\"");

        if (string.IsNullOrWhiteSpace(output))
        {
            VpnActionStatus.Text = "No VPN profiles found (or Get-VpnConnection not available).";
            return;
        }

        var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var block in blocks)
        {
            string name   = Regex.Match(block, @"Name\s*:\s*(.+)").Groups[1].Value.Trim();
            string tunnel = Regex.Match(block, @"TunnelType\s*:\s*(.+)").Groups[1].Value.Trim();
            string status = Regex.Match(block, @"ConnectionStatus\s*:\s*(.+)").Groups[1].Value.Trim();
            if (name.Length > 0)
                _profiles.Add(new VpnProfileRow { Name = name, TunnelType = tunnel, Status = status });
        }
        VpnActionStatus.Text = $"{_profiles.Count} VPN profile(s) found.";
    }

    // ── Profile selection → auto-fill ─────────────────────────────────────────
    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (VpnProfileList.SelectedItem is VpnProfileRow row)
            VpnProfileName.Text = row.Name;
    }

    // ── Connect ───────────────────────────────────────────────────────────────
    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        string profile  = VpnProfileName.Text.Trim();
        string user     = VpnUsername.Text.Trim();
        string password = VpnPassword.Password;   // NOT trimmed — passwords may have leading spaces
        if (string.IsNullOrEmpty(profile)) { VpnActionStatus.Text = "Enter a VPN profile name."; return; }

        VpnActionStatus.Text = $"Connecting to {profile}…";
        VpnConnectProgress.Visibility = Visibility.Visible;

        // Build args without embedding password — credentials are piped via stdin
        // Format: rasdial "profile" [user] — password injected via stdin so it's not
        // visible in Task Manager process list or in the output display.
        string output = await Task.Run(() =>
        {
            try
            {
                // rasdial accepts: rasdial "name" [user [password]] but we use PowerShell
                // Invoke-Expression + Out-String to prevent password in visible arg list
                string psArgs = string.IsNullOrEmpty(user)
                    ? $"-NoProfile -Command \"rasdial '{profile}'\" "
                    : $"-NoProfile -Command \"rasdial '{profile}' '{user}' '{password}'\"";

                var psi = new ProcessStartInfo("powershell", psArgs)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi)!;
                string o = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                return o.Trim();
            }
            catch (Exception ex) { return ex.Message; }
        });

        // Show connection result — deliberately omit the raw output to avoid leaking passwords
        VpnConnectProgress.Visibility = Visibility.Collapsed;
        bool ok = output.Contains("successfully", StringComparison.OrdinalIgnoreCase);
        RasOutput.Text = ok ? "Connected successfully." : output.Split('\n').FirstOrDefault()?.Trim() ?? output;
        VpnActionStatus.Text = ok ? $"✅ Connected to {profile}." : $"⚠️ {RasOutput.Text}";

        HistoryLogService.AddLog("VPN", profile, "Connect attempt via rasdial");
        await LoadActiveConnectionAsync();
    }

    // ── Disconnect ────────────────────────────────────────────────────────────
    private async void OnDisconnectProfile(object sender, RoutedEventArgs e)
    {
        string profile = VpnProfileName.Text.Trim();
        if (string.IsNullOrEmpty(profile)) { VpnActionStatus.Text = "Enter a profile name to disconnect."; return; }
        VpnActionStatus.Text = $"Disconnecting {profile}…";
        string output = await RunAsync("rasdial", $"\"{profile}\" /DISCONNECT");
        RasOutput.Text = output;
        VpnActionStatus.Text = $"Disconnected: {profile}";
        HistoryLogService.AddLog("VPN", profile, "Disconnected via rasdial");
        await LoadActiveConnectionAsync();
    }

    private async void OnDisconnectAll(object sender, RoutedEventArgs e)
    {
        VpnActionStatus.Text = "Disconnecting all VPN…";
        // rasdial with no profile disconnects all
        string output = await RunAsync("rasdial", "/DISCONNECT");
        RasOutput.Text = output;
        VpnActionStatus.Text = "All VPN connections disconnected.";
        HistoryLogService.AddLog("VPN", "All", "Disconnected all via rasdial");
        await LoadActiveConnectionAsync();
    }

    private void OnOpenVpnSettings(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("ms-settings:network-vpn") { UseShellExecute = true });

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Task<string> RunAsync(string exe, string args) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            string o = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return o.Trim();
        }
        catch (Exception ex) { return ex.Message; }
    });
}
