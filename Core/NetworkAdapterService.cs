using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;

namespace WinNetControl.Core;

// ── Live observable adapter row (updated in-place, scroll-safe) ───────────────
public sealed class LiveAdapterInfo : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    private string _name        = "";
    private string _description = "";
    private string _typeText    = "";
    private string _statusText  = "";
    private bool   _isUp;
    private string _speedText   = "";
    private string _macAddress  = "";
    private string _ipv4First   = "";
    private string _gateway     = "";
    private string _dnsServers  = "";
    private long   _bytesSent;
    private long   _bytesReceived;
    private bool   _isVpn;
    private bool   _isWifi;
    private string _signalBars  = "";

    // Raw lists kept for copy operations (not bound)
    public List<string> IPv4 { get; set; } = new();
    public List<string> IPv6 { get; set; } = new();

    public string Name          { get => _name;          set => Set(ref _name, value); }
    public string Description   { get => _description;   set => Set(ref _description, value); }
    public string TypeText      { get => _typeText;      set => Set(ref _typeText, value); }
    public string StatusText    { get => _statusText;    set => Set(ref _statusText, value); }
    public bool   IsUp          { get => _isUp;          set => Set(ref _isUp, value); }
    public string SpeedText     { get => _speedText;     set => Set(ref _speedText, value); }
    public string MacAddress    { get => _macAddress;    set => Set(ref _macAddress, value); }
    public string IPv4First     { get => _ipv4First;     set => Set(ref _ipv4First, value); }
    public string Gateway       { get => _gateway;       set => Set(ref _gateway, value); }
    public string DnsServers    { get => _dnsServers;    set => Set(ref _dnsServers, value); }
    public long   BytesSent     { get => _bytesSent;     set => Set(ref _bytesSent, value); }
    public long   BytesReceived { get => _bytesReceived; set => Set(ref _bytesReceived, value); }
    public bool   IsVpn         { get => _isVpn;         set => Set(ref _isVpn, value); }
    public bool   IsWifi        { get => _isWifi;        set => Set(ref _isWifi, value); }
    public string SignalBars    { get => _signalBars;    set => Set(ref _signalBars, value); }

    /// <summary>Merge fresh snapshot data into this live row without replacing the object.</summary>
    public void UpdateFrom(AdapterSnapshot s)
    {
        Name          = s.Name;
        Description   = s.Description;
        TypeText      = s.TypeText;
        StatusText    = s.StatusText;
        IsUp          = s.IsUp;
        SpeedText     = s.SpeedText;
        MacAddress    = s.MacAddress;
        IPv4First     = s.IPv4.FirstOrDefault() ?? "";
        Gateway       = s.Gateway;
        DnsServers    = s.DnsServers;
        BytesSent     = s.BytesSent;
        BytesReceived = s.BytesReceived;
        IPv4          = s.IPv4;
        IPv6          = s.IPv6;
        IsVpn         = s.IsVpn;
        IsWifi        = s.IsWifi;
        SignalBars    = s.SignalBars;
    }
}

// ── Immutable snapshot (returned from GetAll) ─────────────────────────────────
public record AdapterSnapshot(
    string       Name,
    string       Description,
    string       TypeText,
    string       StatusText,
    bool         IsUp,
    string       SpeedText,
    string       MacAddress,
    List<string> IPv4,
    List<string> IPv6,
    string       Gateway,
    string       DnsServers,
    long         BytesSent,
    long         BytesReceived,
    bool         IsVpn   = false,
    bool         IsWifi  = false,
    string       SignalBars = ""
);

// Keep old name as alias so existing callers compile
public record AdapterDetail(
    string       Name,
    string       Description,
    string       TypeText,
    string       StatusText,
    bool         IsUp,
    string       SpeedText,
    string       MacAddress,
    List<string> IPv4,
    List<string> IPv6,
    string       Gateway,
    string       DnsServers,
    long         BytesSent,
    long         BytesReceived
);

public static class NetworkAdapterService
{
    // ── Fetch fresh snapshots ─────────────────────────────────────────────────
    public static List<AdapterSnapshot> GetAll()
    {
        var result = new List<AdapterSnapshot>();

        // Read Wi-Fi signal info once (fast netsh call)
        var wifiSignal = GetWifiSignal();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                var stats = ni.GetIPStatistics();
                var ipv4  = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily ==
                                System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).ToList();
                var ipv6  = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily ==
                                System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .Select(a => a.Address.ToString()).ToList();
                string gw  = props.GatewayAddresses.FirstOrDefault()?.Address?.ToString() ?? "";
                string dns = string.Join(", ", props.DnsAddresses.Select(d => d.ToString()));
                string mac = string.Join(":", ni.GetPhysicalAddress().GetAddressBytes()
                                              .Select(b => b.ToString("X2")));

                // VPN detection (#25)
                bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                          || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp
                          || IsVpnByDescription(ni.Description);

                // Wi-Fi detection (#24)
                bool isWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                string signal = isWifi ? wifiSignal : "";

                result.Add(new AdapterSnapshot(
                    ni.Name, ni.Description,
                    FormatType(ni.NetworkInterfaceType),
                    ni.OperationalStatus.ToString(),
                    ni.OperationalStatus == OperationalStatus.Up,
                    FormatSpeed(ni.Speed),
                    string.IsNullOrEmpty(mac) ? "\u2014" : mac,
                    ipv4, ipv6,
                    string.IsNullOrEmpty(gw)  ? "\u2014" : gw,
                    string.IsNullOrEmpty(dns) ? "\u2014" : dns,
                    stats.BytesSent, stats.BytesReceived,
                    isVpn, isWifi, signal
                ));
            }
            catch { }
        }
        return result.OrderByDescending(a => a.IsUp).ThenBy(a => a.Name).ToList();
    }

    private static bool IsVpnByDescription(string desc)
    {
        string d = desc.ToLowerInvariant();
        return d.Contains("vpn") || d.Contains("wireguard") || d.Contains("openvpn")
            || d.Contains("tap-") || d.Contains("tun") || d.Contains("nordvpn")
            || d.Contains("expressvpn") || d.Contains("zerotier") || d.Contains("hamachi");
    }

    // Read Wi-Fi signal via netsh (cached; one quick call per refresh)
    private static string GetWifiSignal()
    {
        try
        {
            var (_, output) = RunCmd("netsh", "wlan show interfaces");
            var match = System.Text.RegularExpressions.Regex.Match(
                output, @"Signal\s*:\s*(\d+)%");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
            {
                // Convert percentage to signal bars emoji
                string bars = pct >= 80 ? "████" : pct >= 60 ? "███░" :
                              pct >= 40 ? "██░░" : pct >= 20 ? "█░░░" : "░░░░";
                return $"{bars} {pct}%";
            }
        }
        catch { }
        return "";
    }


    // ── Enable / Disable ──────────────────────────────────────────────────────
    public static (bool ok, string error) Enable(string name)
        => RunNetsh($"interface set interface \"{name}\" enable");

    public static (bool ok, string error) Disable(string name)
        => RunNetsh($"interface set interface \"{name}\" disable");

    // ── Renew IP ──────────────────────────────────────────────────────────────
    public static (bool ok, string output) RenewAdapter(string name)
    {
        RunCmd("ipconfig", $"/release \"{name}\"");
        return RunCmd("ipconfig", $"/renew \"{name}\"");
    }

    // ── Open helpers ──────────────────────────────────────────────────────────
    public static void OpenAdapterProperties()
        => Process.Start(new ProcessStartInfo("ncpa.cpl") { UseShellExecute = true });

    public static void DiagnoseAdapter(string name)
        => Process.Start(new ProcessStartInfo("msdt.exe",
            "/id NetworkDiagnosticsNetworkAdapter") { UseShellExecute = true });

    // ── DNS Flush ──────────────────────────────────────────────────────────────
    public static (bool ok, string output) FlushDns()
        => RunCmd("ipconfig", "/flushdns");

    // ── Set Static IP ─────────────────────────────────────────────────────────
    public static (bool ok, string error) SetStaticIp(
        string adapterName, string ip, string mask, string gateway)
    {
        // Set IP + mask + gateway
        var (ok1, e1) = RunNetsh(
            $"interface ipv4 set address name=\"{adapterName}\" " +
            $"source=static address={ip} mask={mask} gateway={gateway}");
        return (ok1, e1);
    }

    // ── Set DHCP ──────────────────────────────────────────────────────────────
    public static (bool ok, string error) SetDhcp(string adapterName)
    {
        var (ok1, e1) = RunNetsh(
            $"interface ipv4 set address name=\"{adapterName}\" source=dhcp");
        var (ok2, e2) = RunNetsh(
            $"interface ipv4 set dns name=\"{adapterName}\" source=dhcp");
        return (ok1 && ok2, ok1 ? e2 : e1);
    }

    // ── Set Custom DNS ────────────────────────────────────────────────────────
    public static (bool ok, string error) SetDns(
        string adapterName, string primary, string secondary = "")
    {
        var (ok1, e1) = RunNetsh(
            $"interface ipv4 set dns name=\"{adapterName}\" source=static address={primary} register=primary");
        if (!ok1) return (false, e1);
        if (!string.IsNullOrEmpty(secondary))
        {
            var (ok2, e2) = RunNetsh(
                $"interface ipv4 add dns name=\"{adapterName}\" address={secondary} index=2");
            if (!ok2) return (false, e2);
        }
        return (true, "");
    }


    // ── Private helpers ───────────────────────────────────────────────────────
    private static string FormatType(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet      => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Loopback      => "Loopback",
        NetworkInterfaceType.Tunnel        => "Tunnel/VPN",
        NetworkInterfaceType.Ppp           => "PPP",
        _                                  => t.ToString()
    };

    private static string FormatSpeed(long bps)
    {
        if (bps <= 0)               return "\u2014";
        if (bps >= 1_000_000_000)   return $"{bps / 1_000_000_000.0:F0} Gbps";
        if (bps >= 1_000_000)       return $"{bps / 1_000_000.0:F0} Mbps";
        return $"{bps / 1_000.0:F0} Kbps";
    }

    private static (bool ok, string error) RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(10_000);
            bool ok = p.ExitCode == 0;
            return (ok, ok ? "" : (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool ok, string output) RunCmd(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8_000);
            return (p.ExitCode == 0, o.Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
