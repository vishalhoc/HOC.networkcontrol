using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Core;

/// <summary>Result of multi-probe device fingerprinting.</summary>
public record FingerprintResult(
    string? Hostname,
    string  Vendor,
    string  DeviceType,
    string  DeviceIcon);

/// <summary>
/// Identifies LAN devices using OUI lookup, NetBIOS NBNS (UDP 137),
/// mDNS PTR (UDP 5353), Android .local hostname, HTTP banner, and DNS.
/// </summary>
public static class DeviceFingerprinter
{
    // ── OUI table ─────────────────────────────────────────────────────────────
    // Key  : first 6 uppercase hex chars of MAC (no separators)
    // Value: (Vendor, DeviceType, DisplayIcon)
    private static readonly Dictionary<string, (string V, string T, string I)> _oui
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Apple ──────────────────────────────────────────────────────────────
        {"000393",("Apple","MacBook / iMac","🍎")},
        {"000A27",("Apple","MacBook / iMac","🍎")},
        {"000A95",("Apple","MacBook / iMac","🍎")},
        {"001124",("Apple","MacBook / iMac","🍎")},
        {"001451",("Apple","MacBook / iMac","🍎")},
        {"0016CB",("Apple","MacBook / iMac","🍎")},
        {"001CB3",("Apple","iPhone / iPad","🍎")},
        {"001D4F",("Apple","iPhone / iPad","🍎")},
        {"001EC2",("Apple","iPhone / iPad","🍎")},
        {"001FF3",("Apple","iPhone / iPad","🍎")},
        {"002312",("Apple","iPhone / iPad","🍎")},
        {"002436",("Apple","iPhone / iPad","🍎")},
        {"002500",("Apple","MacBook / iMac","🍎")},
        {"0026BB",("Apple","MacBook / iMac","🍎")},
        {"041E64",("Apple","iPhone / iPad","🍎")},
        {"0C3E9F",("Apple","iPhone / iPad","🍎")},
        {"1C5CF2",("Apple","MacBook / iMac","🍎")},
        {"2C61F6",("Apple","iPhone / iPad","🍎")},
        {"3C22FB",("Apple","iPhone / iPad","🍎")},
        {"40ECF8",("Apple","iPhone / iPad","🍎")},
        {"48BF6B",("Apple","iPhone / iPad","🍎")},
        {"5C8D4E",("Apple","iPhone / iPad","🍎")},
        {"6CBF0E",("Apple","MacBook / iMac","🍎")},
        {"70CD60",("Apple","iPhone / iPad","🍎")},
        {"8894EF",("Apple","iPhone / iPad","🍎")},
        {"8C8590",("Apple","iPhone / iPad","🍎")},
        {"A4B197",("Apple","iPhone / iPad","🍎")},
        {"A8519B",("Apple","iPhone / iPad","🍎")},
        {"ACBC32",("Apple","iPhone / iPad","🍎")},
        {"D461C9",("Apple","iPhone / iPad","🍎")},
        {"F0B479",("Apple","MacBook / iMac","🍎")},

        // ── Samsung ────────────────────────────────────────────────────────────
        {"0017C4",("Samsung","Smart TV","📺")},
        {"20CF30",("Samsung","Android Phone","📱")},
        {"404E36",("Samsung","Android Phone","📱")},
        {"40E230",("Samsung","Smart TV","📺")},
        {"4CEB42",("Samsung","Android Phone","📱")},
        {"600960",("Samsung","Android Phone","📱")},
        {"78BD7E",("Samsung","Smart TV","📺")},
        {"8CB220",("Samsung","Android Phone","📱")},
        {"B4EF13",("Samsung","Android Phone","📱")},
        {"BC20A4",("Samsung","Smart TV","📺")},
        {"CC4EEC",("Samsung","Smart TV","📺")},
        {"D820E2",("Samsung","Android Phone","📱")},
        {"E05A1B",("Samsung","Android Phone","📱")},
        {"F0E77E",("Samsung","Android Phone","📱")},

        // ── Xiaomi ─────────────────────────────────────────────────────────────
        {"28E31F",("Xiaomi","Android Phone","📱")},
        {"286C07",("Xiaomi","Android Phone","📱")},
        {"2C3666",("Xiaomi","Android Phone","📱")},
        {"40313C",("Xiaomi","Android Phone","📱")},
        {"6C5648",("Xiaomi","Android Phone","📱")},
        {"7CF831",("Xiaomi","Android Phone","📱")},
        {"986DC0",("Xiaomi","Android Phone","📱")},
        {"ACF7F3",("Xiaomi","Android Phone","📱")},
        {"CCE1D5",("Xiaomi","Android Phone","📱")},
        {"FC643F",("Xiaomi","Android Phone","📱")},

        // ── OnePlus ────────────────────────────────────────────────────────────
        {"204601",("OnePlus","Android Phone","📱")},
        {"AC3743",("OnePlus","Android Phone","📱")},
        {"D055DC",("OnePlus","Android Phone","📱")},

        // ── Google (Pixel + Chromecast + Home) ────────────────────────────────
        {"1CABB3",("Google","Google Home","🔊")},
        {"1C226D",("Google","Android Phone","📱")},
        {"3C5AB4",("Google","Chromecast","📺")},
        {"48D6D5",("Google","Google Home","🔊")},
        {"54600F",("Google","Chromecast","📺")},
        {"6C2E85",("Google","Chromecast","📺")},
        {"B47C9C",("Google","Google Home","🔊")},
        {"D4F57E",("Google","Chromecast","📺")},
        {"F4F5D8",("Google","Android Phone","📱")},

        // ── Huawei ─────────────────────────────────────────────────────────────
        {"489022",("Huawei","Android Phone","📱")},
        {"4C114D",("Huawei","Android Phone","📱")},
        {"5CADCF",("Huawei","Android Phone","📱")},
        {"707360",("Huawei","Android Phone","📱")},
        {"9CB2A1",("Huawei","Android Phone","📱")},
        {"C8B4A0",("Huawei","Android Phone","📱")},
        {"E87FF2",("Huawei","Android Phone","📱")},

        // ── OPPO / Realme / Vivo ───────────────────────────────────────────────
        {"04D3B0",("OPPO","Android Phone","📱")},
        {"9C28EF",("OPPO","Android Phone","📱")},
        {"A48640",("OPPO","Android Phone","📱")},
        {"A4D987",("Vivo","Android Phone","📱")},
        {"B0E235",("Realme","Android Phone","📱")},
        {"CC2D8C",("Vivo","Android Phone","📱")},
        {"DC538A",("Realme","Android Phone","📱")},

        // ── Motorola / Sony Mobile ─────────────────────────────────────────────
        {"000822",("Motorola","Android Phone","📱")},
        {"001A7D",("Motorola","Android Phone","📱")},
        {"0016B8",("Sony Mobile","Android Phone","📱")},
        {"18F46B",("Motorola","Android Phone","📱")},
        {"40B7F3",("Sony Mobile","Android Phone","📱")},
        {"9CB6D0",("Motorola","Android Phone","📱")},
        {"A45046",("Sony Mobile","Android Phone","📱")},

        // ── Raspberry Pi ───────────────────────────────────────────────────────
        {"B827EB",("Raspberry Pi","IoT Device","🔧")},
        {"D83ADD",("Raspberry Pi","IoT Device","🔧")},
        {"DCA632",("Raspberry Pi","IoT Device","🔧")},
        {"E45F01",("Raspberry Pi","IoT Device","🔧")},

        // ── Espressif (ESP32 / ESP8266) ────────────────────────────────────────
        {"18FE34",("Espressif","IoT Device","🔧")},
        {"246F28",("Espressif","IoT Device","🔧")},
        {"30C6F7",("Espressif","IoT Device","🔧")},
        {"5CCF7F",("Espressif","IoT Device","🔧")},
        {"7CDFA1",("Espressif","IoT Device","🔧")},
        {"84F3EB",("Espressif","IoT Device","🔧")},
        {"8CAAB5",("Espressif","IoT Device","🔧")},
        {"A4CF12",("Espressif","IoT Device","🔧")},
        {"BCDDC2",("Espressif","IoT Device","🔧")},

        // ── Arduino / Microchip / TI ───────────────────────────────────────────
        {"00124B",("Texas Instruments","IoT Device","🔧")},
        {"A8610A",("Arduino","IoT Device","🔧")},
        {"D88039",("Microchip","IoT Device","🔧")},

        // ── Amazon Echo / Fire TV ──────────────────────────────────────────────
        {"34D270",("Amazon","Smart Speaker","🔊")},
        {"40B4CD",("Amazon","Fire TV","📺")},
        {"44650D",("Amazon","Smart Speaker","🔊")},
        {"6454BD",("Amazon","Fire TV","📺")},
        {"A002DC",("Amazon","Fire TV","📺")},
        {"FC65DE",("Amazon","Smart Speaker","🔊")},

        // ── Philips Hue / LIFX ─────────────────────────────────────────────────
        {"001788",("Philips Hue","Smart Light","💡")},
        {"D073D5",("LIFX","Smart Light","💡")},
        {"ECB5FA",("Philips Hue","Smart Sensor","💡")},

        // ── TP-Link (routers + Kasa smart plugs) ───────────────────────────────
        {"1C74D7",("TP-Link","IoT Device","🔧")},
        {"545864",("TP-Link","IoT Device","🔧")},
        {"50C7BF",("TP-Link","Router / AP","📡")},
        {"64702E",("TP-Link","Router / AP","📡")},
        {"88DC96",("TP-Link","Router / AP","📡")},
        {"B0487A",("TP-Link","Router / AP","📡")},
        {"C46E1F",("TP-Link","Router / AP","📡")},
        {"E83E26",("TP-Link","Router / AP","📡")},

        // ── Netgear ────────────────────────────────────────────────────────────
        {"201893",("Netgear","Router / AP","📡")},
        {"30B5C2",("Netgear","Router / AP","📡")},
        {"6C0E0D",("Netgear","Router / AP","📡")},
        {"9C3426",("Netgear","Router / AP","📡")},
        {"A040A0",("Netgear","Router / AP","📡")},

        // ── ASUS ───────────────────────────────────────────────────────────────
        {"04D4C4",("ASUS","Router / AP","📡")},
        {"08606E",("ASUS","Router / AP","📡")},
        {"107B44",("ASUS","Router / AP","📡")},
        {"308D99",("ASUS","Router / AP","📡")},
        {"50465D",("ASUS","Router / AP","📡")},

        // ── D-Link ─────────────────────────────────────────────────────────────
        {"00215C",("D-Link","Router / AP","📡")},
        {"1CBDB9",("D-Link","Router / AP","📡")},
        {"28107B",("D-Link","Router / AP","📡")},
        {"8CBEBE",("D-Link","Router / AP","📡")},

        // ── Ubiquiti ───────────────────────────────────────────────────────────
        {"044BC4",("Ubiquiti","Router / AP","📡")},
        {"24A43C",("Ubiquiti","Router / AP","📡")},
        {"68D79A",("Ubiquiti","Router / AP","📡")},
        {"74ACB9",("Ubiquiti","Router / AP","📡")},
        {"788A20",("Ubiquiti","Router / AP","📡")},
        {"80218E",("Ubiquiti","Router / AP","📡")},

        // ── MikroTik ───────────────────────────────────────────────────────────
        {"2CC8DE",("MikroTik","Router / AP","📡")},
        {"48A99A",("MikroTik","Router / AP","📡")},
        {"4C5E0C",("MikroTik","Router / AP","📡")},
        {"744D28",("MikroTik","Router / AP","📡")},

        // ── Cisco ──────────────────────────────────────────────────────────────
        {"001A1E",("Cisco","Router / Switch","📡")},
        {"001B53",("Cisco","Router / Switch","📡")},
        {"001CA2",("Cisco","Router / Switch","📡")},
        {"0025B5",("Cisco","Router / Switch","📡")},

        // ── Printers ───────────────────────────────────────────────────────────
        {"000EBF",("Brother","Printer","🖨️")},
        {"001E8F",("Canon","Printer","🖨️")},
        {"003065",("Canon","Printer","🖨️")},
        {"00268B",("Epson","Printer","🖨️")},
        {"0026AB",("Epson","Printer","🖨️")},
        {"008077",("Brother","Printer","🖨️")},
        {"1CC1DE",("HP","Printer","🖨️")},
        {"2CAA8E",("HP","Printer","🖨️")},
        {"306D97",("HP","Printer","🖨️")},
        {"3C4A92",("HP","Printer","🖨️")},
        {"5065F3",("HP","Printer","🖨️")},
        {"784B87",("HP","Printer","🖨️")},
        {"AC3BA4",("Epson","Printer","🖨️")},
        {"C83A35",("Canon","Printer","🖨️")},
        {"E0D057",("Brother","Printer","🖨️")},

        // ── NAS ────────────────────────────────────────────────────────────────
        {"001132",("Synology","NAS","💾")},
        {"0090A9",("Western Digital","NAS","💾")},
        {"245EBE",("QNAP","NAS","💾")},
        {"24FDAC",("QNAP","NAS","💾")},

        // ── LG / Sony / Hisense / TCL Smart TVs ───────────────────────────────
        {"10683F",("LG","Smart TV","📺")},
        {"3071AF",("Sony","Smart TV","📺")},
        {"700B4F",("LG","Smart TV","📺")},
        {"8C8D28",("Hisense","Smart TV","📺")},
        {"949F3E",("TCL","Smart TV","📺")},
        {"98BCA6",("LG","Smart TV","📺")},
        {"A89FEC",("LG","Smart TV","📺")},
        {"BC303D",("Sony","Smart TV","📺")},
        {"C808E9",("LG","Smart TV","📺")},

        // ── Game Consoles ──────────────────────────────────────────────────────
        {"00041F",("PlayStation","Game Console","🎮")},
        {"000F86",("Nintendo","Game Console","🎮")},
        {"0009BF",("Nintendo","Game Console","🎮")},
        {"002659",("Nintendo","Game Console","🎮")},
        {"0019C5",("PlayStation","Game Console","🎮")},
        {"28396F",("PlayStation","Game Console","🎮")},
        {"603D26",("Xbox","Game Console","🎮")},
        {"709E29",("PlayStation","Game Console","🎮")},
        {"7CBB8A",("Xbox","Game Console","🎮")},
        {"985FD3",("Xbox","Game Console","🎮")},
        {"98415C",("Nintendo","Game Console","🎮")},
        {"98B6E9",("Nintendo","Game Console","🎮")},
        {"A8CCEF",("PlayStation","Game Console","🎮")},
        {"E84ECE",("Nintendo","Game Console","🎮")},

        // ── Streaming devices ──────────────────────────────────────────────────
        {"B0A737",("Roku","Streaming Device","📺")},
        {"C83A7B",("Roku","Streaming Device","📺")},
        {"D88196",("Roku","Streaming Device","📺")},

        // ── Security / Cameras ─────────────────────────────────────────────────
        {"0C47C9",("Ring","Security Camera","🔔")},
        {"4419B6",("Hikvision","IP Camera","📷")},
        {"641660",("Nest","Smart Thermostat","🏠")},
        {"9002A9",("Dahua","IP Camera","📷")},
        {"B009DA",("Ring","Security Camera","🔔")},
        {"C42F90",("Hikvision","IP Camera","📷")},
        {"EC71DB",("Reolink","IP Camera","📷")},

        // ── Smart Home ─────────────────────────────────────────────────────────
        {"18B430",("Nest","Smart Home","🏠")},
        {"5CAAFD",("Sonos","Smart Speaker","🔊")},
        {"94900B",("Sonos","Smart Speaker","🔊")},
        {"B8E937",("Sonos","Smart Speaker","🔊")},
        {"D052A8",("SmartThings","Smart Home Hub","🏠")},

        // ── PCs / NICs ─────────────────────────────────────────────────────────
        {"001B21",("Intel","Windows PC","🖥️")},
        {"001E4F",("Dell","Windows PC","🖥️")},
        {"002168",("Realtek","Windows PC","🖥️")},
        {"002564",("Dell","Windows PC","🖥️")},
        {"0890E6",("Intel","Windows PC","🖥️")},
        {"5CF9DD",("Dell","Windows PC","🖥️")},
        {"78E7D1",("Dell","Windows PC","🖥️")},
        {"84A938",("Lenovo","Windows PC","🖥️")},
        {"8C16C9",("Intel","Windows PC","🖥️")},
        {"A89FC9",("Intel","Windows PC","🖥️")},
        {"D4258B",("Intel","Windows PC","🖥️")},
        {"D4BE66",("Dell","Windows PC","🖥️")},
        {"E09D31",("Realtek","Windows PC","🖥️")},
        {"F4CE46",("Dell","Windows PC","🖥️")},
        {"544AB1",("Lenovo","Windows PC","🖥️")},
    };

    // ── Public entry point ────────────────────────────────────────────────────
    /// <summary>
    /// Runs all fingerprinting probes concurrently and returns the best result.
    /// </summary>
    public static async Task<FingerprintResult> FingerprintAsync(
        string ip, string mac, int ttl, int timeoutMs = 1200)
    {
        var (vendor, devType, devIcon) = LookupOui(mac, ttl);
        string? hostname = await ResolveHostnameAsync(ip, mac, timeoutMs);
        return new FingerprintResult(hostname, vendor, devType, devIcon);
    }

    // ── OUI lookup ────────────────────────────────────────────────────────────
    public static (string Vendor, string DeviceType, string DeviceIcon) LookupOui(
        string mac, int ttl = 0)
    {
        if (string.IsNullOrWhiteSpace(mac) || mac == "—")
            return FallbackFromTtl(ttl);

        string clean = mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length < 6) return FallbackFromTtl(ttl);

        // Check locally-administered (MAC randomisation) — bit 1 of first octet
        if (byte.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out byte b0)
            && (b0 & 0x02) != 0)
            return FallbackFromTtl(ttl, randomMac: true);

        string prefix = clean[..6];
        if (_oui.TryGetValue(prefix, out var e))
            return (e.V, e.T, e.I);

        return FallbackFromTtl(ttl);
    }

    private static (string, string, string) FallbackFromTtl(int ttl, bool randomMac = false)
    {
        string sfx = randomMac ? " (random MAC)" : "";
        return ttl switch
        {
            > 0 and <= 64   => ("Unknown" + sfx, "Linux / Android", "🐧"),
            > 64 and <= 128 => ("Unknown" + sfx, "Windows Device",  "🪟"),
            > 128           => ("Unknown" + sfx, "Network Device",  "🌐"),
            _               => ("Unknown",        "Unknown Device",  "❓")
        };
    }

    // ── Hostname resolution (parallel probes) ─────────────────────────────────
    public static async Task<string?> ResolveHostnameAsync(
        string ip, string mac, int timeoutMs)
    {
        // Run all probes concurrently; return first successful result
        var tasks = new List<Task<string?>>
        {
            QueryNetBiosNameAsync(ip, Math.Min(timeoutMs, 900)),
            QueryMdnsAsync(ip, Math.Min(timeoutMs, 900)),
            GrabHttpTitleAsync(ip, Math.Min(timeoutMs, 1000)),
            ReverseDnsAsync(ip, Math.Min(timeoutMs, 800)),
        };

        // Android: try android-<mac>.local hostname
        if (!string.IsNullOrWhiteSpace(mac) && mac != "—")
            tasks.Add(ResolveAndroidMdnsAsync(ip, mac, Math.Min(timeoutMs, 600)));

        using var cts = new CancellationTokenSource(timeoutMs + 200);
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            try
            {
                string? result = await done.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result) && result != ip)
                    return SanitizeHostname(result);
            }
            catch { /* swallow individual probe failures */ }
        }
        return null;
    }

    // ── Probe 1: NetBIOS Node Status (UDP 137) ────────────────────────────────
    private static async Task<string?> QueryNetBiosNameAsync(string ip, int timeoutMs)
    {
        try
        {
            // NBSTAT request: Transaction ID + flags + 1 question + encoded wildcard + NBSTAT type
            byte[] req =
            {
                0x00, 0x01,                         // Transaction ID
                0x00, 0x00,                         // Flags: standard query
                0x00, 0x01,                         // QDCOUNT = 1
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // ANCOUNT / NSCOUNT / ARCOUNT
                // QNAME: len=32 + "CKAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" + null
                0x20,
                0x43,0x4B,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,
                0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,
                0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,0x41,
                0x41,0x41,
                0x00,
                0x00, 0x21,                         // QTYPE  = NBSTAT
                0x00, 0x01                          // QCLASS = IN
            };

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            var ep = new IPEndPoint(IPAddress.Parse(ip), 137);

            using var cts = new CancellationTokenSource(timeoutMs + 200);
            await udp.SendAsync(req, req.Length, ep).WaitAsync(cts.Token).ConfigureAwait(false);
            var resp = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseNetBiosResponse(resp.Buffer);
        }
        catch { return null; }
    }

    private static string? ParseNetBiosResponse(byte[] data)
    {
        try
        {
            // Must be a response (QR bit) with at least one answer
            if (data.Length < 57 || (data[2] & 0x80) == 0) return null;
            int ancount = (data[6] << 8) | data[7];
            if (ancount == 0) return null;

            // Skip header (12) + question section (38 = 34-byte name + 4 type/class)
            int pos = 50;

            // Skip answer name (compressed pointer = 2 bytes, or full name)
            if (pos >= data.Length) return null;
            if ((data[pos] & 0xC0) == 0xC0) { pos += 2; }
            else { while (pos < data.Length && data[pos] != 0) pos += data[pos] + 1; pos++; }

            if (pos + 10 > data.Length) return null;
            pos += 10; // type(2)+class(2)+ttl(4)+rdlength(2)

            if (pos >= data.Length) return null;
            int numNames = data[pos++];

            for (int i = 0; i < numNames && pos + 18 <= data.Length; i++)
            {
                byte[] nameBytes = new byte[15];
                Array.Copy(data, pos, nameBytes, 0, 15);
                byte suffix = data[pos + 15];
                int  flags  = (data[pos + 16] << 8) | data[pos + 17];
                pos += 18;

                // Workstation service: suffix=0x00, group bit clear
                if (suffix == 0x00 && (flags & 0x8000) == 0)
                {
                    string name = Encoding.ASCII.GetString(nameBytes).TrimEnd();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch { }
        return null;
    }

    // ── Probe 2: mDNS reverse PTR (224.0.0.251:5353) ─────────────────────────
    private static async Task<string?> QueryMdnsAsync(string ip, int timeoutMs)
    {
        try
        {
            var parts = ip.Split('.');
            if (parts.Length != 4) return null;
            string ptr = $"{parts[3]}.{parts[2]}.{parts[1]}.{parts[0]}.in-addr.arpa";
            byte[] query = BuildDnsQuery(ptr, 0x000C /* PTR */);

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            var mdnsEp = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);

            using var cts = new CancellationTokenSource(timeoutMs + 200);
            await udp.SendAsync(query, query.Length, mdnsEp).WaitAsync(cts.Token).ConfigureAwait(false);
            var resp = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return ParseDnsPtrResponse(resp.Buffer);
        }
        catch { return null; }
    }

    // ── Probe 3: Android android-<mac>.local hostname ─────────────────────────
    private static async Task<string?> ResolveAndroidMdnsAsync(
        string ip, string mac, int timeoutMs)
    {
        try
        {
            string macClean = mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
            if (macClean.Length != 12) return null;
            string androidHost = $"android-{macClean}.local";

            using var cts = new CancellationTokenSource(timeoutMs);
            var entry = await Dns.GetHostEntryAsync(androidHost)
                .WaitAsync(cts.Token).ConfigureAwait(false);

            // Only accept if resolved address matches
            bool ipMatch = Array.Exists(entry.AddressList,
                a => a.ToString() == ip);

            return ipMatch ? $"android-{macClean}" : null;
        }
        catch { return null; }
    }

    // ── Probe 4: HTTP title / Server header ───────────────────────────────────
    private static async Task<string?> GrabHttpTitleAsync(string ip, int timeoutMs)
    {
        try
        {
            using var tcp = new TcpClient();
            using var connCts = new CancellationTokenSource(timeoutMs);
            await tcp.ConnectAsync(ip, 80).WaitAsync(connCts.Token).ConfigureAwait(false);

            using var stream = tcp.GetStream();
            stream.ReadTimeout  = timeoutMs;
            stream.WriteTimeout = 1000;

            byte[] req = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.0\r\nHost: {ip}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(req).ConfigureAwait(false);

            var buf   = new byte[2048];
            int total = 0;
            int read;
            while (total < buf.Length &&
                   (read = await stream.ReadAsync(buf.AsMemory(total)).ConfigureAwait(false)) > 0)
                total += read;

            string response = Encoding.UTF8.GetString(buf, 0, total);

            // Extract <title>
            var tm = Regex.Match(response,
                @"<title[^>]*>\s*([^<]{1,80})\s*</title>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (tm.Success) return tm.Groups[1].Value.Trim();

            // Fallback: Server header
            var sm = Regex.Match(response, @"Server:\s*([^\r\n]{1,60})");
            if (sm.Success) return sm.Groups[1].Value.Trim();
        }
        catch { }
        return null;
    }

    // ── Probe 5: Standard reverse DNS ─────────────────────────────────────────
    private static async Task<string?> ReverseDnsAsync(string ip, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var entry = await Dns.GetHostEntryAsync(ip).WaitAsync(cts.Token).ConfigureAwait(false);
            string h = entry.HostName;
            return string.IsNullOrWhiteSpace(h) || h == ip ? null : h;
        }
        catch { return null; }
    }

    // ── DNS helpers ───────────────────────────────────────────────────────────
    private static byte[] BuildDnsQuery(string name, ushort qtype)
    {
        var buf = new List<byte>
        {
            0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        foreach (string part in name.Split('.'))
        {
            buf.Add((byte)part.Length);
            buf.AddRange(Encoding.ASCII.GetBytes(part));
        }
        buf.Add(0x00);
        buf.Add((byte)(qtype >> 8)); buf.Add((byte)(qtype & 0xFF));
        buf.Add(0x00); buf.Add(0x01);
        return buf.ToArray();
    }

    private static string? ParseDnsPtrResponse(byte[] data)
    {
        try
        {
            if (data.Length < 13 || (data[2] & 0x80) == 0) return null;
            int ancount = (data[6] << 8) | data[7];
            if (ancount == 0) return null;

            int pos = 12;
            // Skip question section
            while (pos < data.Length && data[pos] != 0)
                pos += data[pos] + 1;
            pos += 5; // null + type(2) + class(2)

            for (int a = 0; a < ancount; a++)
            {
                if (pos >= data.Length) break;
                if ((data[pos] & 0xC0) == 0xC0) pos += 2;
                else { while (pos < data.Length && data[pos] != 0) pos++; pos++; }

                if (pos + 10 > data.Length) break;
                ushort rtype    = (ushort)((data[pos] << 8) | data[pos + 1]);
                int    rdlength = (data[pos + 8] << 8) | data[pos + 9];
                pos += 10;

                if (rtype == 0x000C /* PTR */ && pos + rdlength <= data.Length)
                {
                    string name = DecodeDnsName(data, pos);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                pos += rdlength;
            }
        }
        catch { }
        return null;
    }

    private static string DecodeDnsName(byte[] data, int pos)
    {
        var sb    = new StringBuilder();
        int steps = 0;
        while (pos < data.Length && steps++ < 20)
        {
            byte len = data[pos];
            if (len == 0) break;
            if ((len & 0xC0) == 0xC0)
            {
                int ptr = ((len & 0x3F) << 8) | data[pos + 1];
                sb.Append(DecodeDnsName(data, ptr));
                break;
            }
            if (sb.Length > 0) sb.Append('.');
            pos++;
            sb.Append(Encoding.ASCII.GetString(data, pos, Math.Min(len, data.Length - pos)));
            pos += len;
        }
        return sb.ToString();
    }

    private static string SanitizeHostname(string h)
    {
        h = h.Trim().TrimEnd('.');
        if (h.EndsWith(".local",   StringComparison.OrdinalIgnoreCase)) h = h[..^6];
        if (h.EndsWith(".home",    StringComparison.OrdinalIgnoreCase)) h = h[..^5];
        if (h.EndsWith(".lan",     StringComparison.OrdinalIgnoreCase)) h = h[..^4];
        if (h.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase)) h = h[..^12];
        return h;
    }
}
