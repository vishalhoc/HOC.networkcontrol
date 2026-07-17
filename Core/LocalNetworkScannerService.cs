using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Core;

public sealed class LocalNetworkDevice
{
    public string IpAddress   { get; set; } = "";
    public string MacAddress  { get; set; } = "";
    public string Hostname    { get; set; } = "";
    public string Vendor      { get; set; } = "";
    public bool   IsOnline    { get; set; }
    public long   LatencyMs   { get; set; }
    public bool   IsBlocked   { get; set; }
    public bool   IsGateway   { get; set; }
    public string DeviceType  { get; set; } = "Unknown";  // Router, PC, Phone, Printer…
}

public sealed class LocalNetworkScannerService
{
    // ── OUI vendor prefix table (top ~80 common vendors) ──────────────────────
    private static readonly Dictionary<string, string> _ouiTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00:50:56"] = "VMware",        ["00:0C:29"] = "VMware",
        ["00:1A:11"] = "Google",        ["3C:5A:B4"] = "Google",
        ["DC:A6:32"] = "Raspberry Pi",  ["B8:27:EB"] = "Raspberry Pi",
        ["18:60:24"] = "Apple",         ["AC:BC:32"] = "Apple",         ["F0:18:98"] = "Apple",
        ["00:1C:B3"] = "Apple",         ["3C:15:C2"] = "Apple",
        ["00:50:F2"] = "Microsoft",     ["28:18:78"] = "Microsoft",
        ["00:15:5D"] = "Microsoft (Hyper-V)",
        ["00:1B:21"] = "Intel",         ["8C:EC:4B"] = "Intel",         ["A4:C3:F0"] = "Intel",
        ["EC:F4:BB"] = "Intel",
        ["00:23:AE"] = "NVIDIA",
        ["C8:D3:FF"] = "Samsung",       ["00:26:37"] = "Samsung",       ["84:C5:A6"] = "Samsung",
        ["10:DA:43"] = "Samsung",
        ["FC:F1:36"] = "Huawei",        ["48:FD:8E"] = "Huawei",
        ["E4:F0:42"] = "TP-Link",       ["50:C7:BF"] = "TP-Link",       ["54:C8:0F"] = "TP-Link",
        ["00:1D:0F"] = "ASUS",          ["10:BF:48"] = "ASUS",          ["2C:FD:A1"] = "ASUS",
        ["00:26:18"] = "Netgear",       ["A0:04:60"] = "Netgear",
        ["00:14:BF"] = "Linksys",       ["00:1C:10"] = "Linksys",
        ["00:18:E7"] = "Cisco",         ["00:1A:2F"] = "Cisco",
        ["00:17:F2"] = "D-Link",        ["1C:7E:E5"] = "D-Link",
        ["00:0E:2E"] = "Belkin",        ["94:44:52"] = "Belkin",
        ["00:04:4B"] = "NVIDIA",
        ["00:1E:8C"] = "ASRock",
        ["00:23:24"] = "Gigabyte",
        ["00:30:67"] = "Realtek",
        ["00:50:BA"] = "D-Link",
        ["00:26:5A"] = "Motorola",      ["AC:3A:7A"] = "Motorola",
        ["00:09:0F"] = "Fortinet",
        ["00:0F:DE"] = "TP-Link",
        ["68:FF:7B"] = "Amazon",        ["FC:A6:67"] = "Amazon",        ["B4:7C:9C"] = "Amazon",
        ["00:BB:3A"] = "Amazon",
        ["F0:27:65"] = "Xiaomi",        ["28:6C:07"] = "Xiaomi",
        ["F4:F5:D8"] = "OnePlus",
        ["8C:BE:BE"] = "Synology",
        ["00:11:32"] = "Synology",
    };

    // ── Win32 ARP ─────────────────────────────────────────────────────────────
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIp, int srcIp, byte[] pMacAddr, ref uint phyAddrLen);

    // ── Public API ────────────────────────────────────────────────────────────
    public async Task<List<LocalNetworkDevice>> ScanAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        // Determine local subnet
        var (localIp, subnetMask) = GetLocalIPAndMask();
        if (localIp == null) return new();

        var gateway = GetDefaultGateway();
        var ips = GetSubnetIPs(localIp, subnetMask);
        int total = ips.Count;
        int done  = 0;

        var results = new ConcurrentBag<LocalNetworkDevice>();
        var semaphore = new SemaphoreSlim(64); // max 64 concurrent pings

        var tasks = ips.Select(async ip =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var dev = await ProbeDeviceAsync(ip, gateway, ct);
                if (dev != null) results.Add(dev);
            }
            catch (OperationCanceledException) { }
            finally
            {
                semaphore.Release();
                Interlocked.Increment(ref done);
                progress?.Report((done, total));
            }
        });

        await Task.WhenAll(tasks);
        return results.OrderBy(d => ParseIP(d.IpAddress)).ToList();
    }

    private async Task<LocalNetworkDevice?> ProbeDeviceAsync(
        string ip, string? gateway, CancellationToken ct)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(ip, 600);
            if (reply.Status != IPStatus.Success) return null;

            // Online — get MAC via ARP
            string mac  = GetMacAddress(ip);
            string host = await ReverseLookupAsync(ip);
            string vendor = LookupVendor(mac);
            string type  = GuessDeviceType(vendor, host, ip == gateway);

            return new LocalNetworkDevice
            {
                IpAddress  = ip,
                MacAddress = mac,
                Hostname   = host,
                Vendor     = vendor,
                DeviceType = type,
                IsOnline   = true,
                LatencyMs  = reply.RoundtripTime,
                IsGateway  = ip == gateway,
                IsBlocked  = IsIpBlocked(ip),
            };
        }
        catch { return null; }
    }

    // ── Block / Unblock ───────────────────────────────────────────────────────
    public static void BlockDevice(string ip)
    {
        string ruleName = $"WNC_Block_{ip.Replace('.', '_')}";
        RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block remoteip={ip}");
        RunNetsh($"advfirewall firewall add rule name=\"{ruleName}_in\" dir=in action=block remoteip={ip}");
    }

    public static void UnblockDevice(string ip)
    {
        string ruleName = $"WNC_Block_{ip.Replace('.', '_')}";
        RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
        RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}_in\"");
    }

    private static bool IsIpBlocked(string ip)
    {
        string ruleName = $"WNC_Block_{ip.Replace('.', '_')}";
        var psi = new ProcessStartInfo("netsh",
            $"advfirewall firewall show rule name=\"{ruleName}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var p = Process.Start(psi);
        string output = p?.StandardOutput.ReadToEnd() ?? "";
        return output.Contains(ruleName);
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(3000);
    }

    // ── Network helpers ───────────────────────────────────────────────────────
    private static string GetMacAddress(string ip)
    {
        try
        {
            var destIp  = BitConverter.ToInt32(IPAddress.Parse(ip).GetAddressBytes().Reverse().ToArray(), 0);
            // little-endian: parse normally then get bytes
            var addr    = IPAddress.Parse(ip).GetAddressBytes();
            int destInt = BitConverter.ToInt32(addr, 0);
            byte[] mac  = new byte[6];
            uint   len  = (uint)mac.Length;
            int    res  = SendARP(destInt, 0, mac, ref len);
            if (res == 0 && len == 6)
                return string.Join(":", mac.Select(b => b.ToString("X2")));
        }
        catch { }
        return "";
    }

    private static async Task<string> ReverseLookupAsync(string ip)
    {
        try
        {
            var host = await Dns.GetHostEntryAsync(ip);
            return host.HostName;
        }
        catch { return ip; }
    }

    private static string LookupVendor(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";
        string prefix = mac[..8]; // "XX:XX:XX"
        return _ouiTable.TryGetValue(prefix, out var v) ? v : "";
    }

    private static string GuessDeviceType(string vendor, string host, bool isGateway)
    {
        if (isGateway) return "🌐 Router/Gateway";
        string combo = $"{vendor} {host}".ToLower();
        if (combo.Contains("apple") || combo.Contains("iphone") || combo.Contains("ipad") || combo.Contains("mac"))
            return "🍎 Apple Device";
        if (combo.Contains("android") || combo.Contains("samsung") || combo.Contains("xiaomi") || combo.Contains("pixel"))
            return "📱 Android Phone";
        if (combo.Contains("raspberry"))   return "🍓 Raspberry Pi";
        if (combo.Contains("amazon") || combo.Contains("echo") || combo.Contains("fire"))
            return "📦 Amazon Device";
        if (combo.Contains("printer") || combo.Contains("hp") || combo.Contains("canon"))
            return "🖨 Printer";
        if (combo.Contains("synology") || combo.Contains("nas") || combo.Contains("storage"))
            return "💾 NAS/Storage";
        if (combo.Contains("camera") || combo.Contains("cam"))
            return "📷 IP Camera";
        if (combo.Contains("vmware") || combo.Contains("hyper") || combo.Contains("virtual"))
            return "💻 Virtual Machine";
        if (combo.Contains("microsoft") || combo.Contains("windows") || combo.Contains("desktop") || combo.Contains("laptop"))
            return "🖥 Windows PC";
        if (!string.IsNullOrEmpty(vendor)) return $"📡 {vendor}";
        return "❓ Unknown";
    }

    private static (string? ip, string? mask) GetLocalIPAndMask()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                return (ua.Address.ToString(), ua.IPv4Mask.ToString());
            }
        }
        return (null, null);
    }

    private static string? GetDefaultGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var gw in ni.GetIPProperties().GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                    return gw.Address.ToString();
            }
        }
        return null;
    }

    private static List<string> GetSubnetIPs(string localIp, string? subnetMask)
    {
        var result = new List<string>();
        try
        {
            var ip   = IPAddress.Parse(localIp).GetAddressBytes();
            var mask = IPAddress.Parse(subnetMask ?? "255.255.255.0").GetAddressBytes();
            var net  = ip.Zip(mask, (a, b) => (byte)(a & b)).ToArray();
            var wild = mask.Select(b => (byte)~b).ToArray();

            // Enumerate all host addresses in subnet (cap at /24 = 254)
            int hostCount = (wild[0] << 24 | wild[1] << 16 | wild[2] << 8 | wild[3]);
            if (hostCount > 254) hostCount = 254;

            for (int i = 1; i <= hostCount; i++)
            {
                var addr = new byte[4];
                addr[0] = (byte)(net[0] | (i >> 24 & wild[0]));
                addr[1] = (byte)(net[1] | (i >> 16 & wild[1]));
                addr[2] = (byte)(net[2] | (i >>  8 & wild[2]));
                addr[3] = (byte)(net[3] | (i       & wild[3]));
                result.Add(new IPAddress(addr).ToString());
            }
        }
        catch { }
        return result;
    }

    private static long ParseIP(string ip)
    {
        try
        {
            var b = IPAddress.Parse(ip).GetAddressBytes();
            return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
        }
        catch { return 0; }
    }
}
