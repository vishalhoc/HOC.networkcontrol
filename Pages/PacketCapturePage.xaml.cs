using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

// ── Data model ───────────────────────────────────────────────────────────────
public class CapturedPacket
{
    public int    Index    { get; set; }
    public string Time     { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string SrcIp    { get; set; } = "";
    public string SrcPort  { get; set; } = "";
    public string DstIp    { get; set; } = "";
    public string DstPort  { get; set; } = "";
    public int    Length   { get; set; }
    public string Info     { get; set; } = "";
    public byte[] Payload  { get; set; } = Array.Empty<byte>();

    // Colour coding per protocol for readability
    public Brush RowBrush => Protocol switch
    {
        "TCP"  => new SolidColorBrush(Color.FromArgb(10, 33, 150, 243)),
        "UDP"  => new SolidColorBrush(Color.FromArgb(10, 76, 175, 80)),
        "ICMP" => new SolidColorBrush(Color.FromArgb(10, 255, 152, 0)),
        "DNS"  => new SolidColorBrush(Color.FromArgb(10, 156, 39, 176)),
        "HTTP" => new SolidColorBrush(Color.FromArgb(10, 244, 67, 54)),
        _      => new SolidColorBrush(Colors.Transparent)
    };

    public Brush ProtoColor => Protocol switch
    {
        "TCP"  => new SolidColorBrush(Color.FromArgb(255, 33, 150, 243)),
        "UDP"  => new SolidColorBrush(Color.FromArgb(255, 76, 175, 80)),
        "ICMP" => new SolidColorBrush(Color.FromArgb(255, 255, 152, 0)),
        "DNS"  => new SolidColorBrush(Color.FromArgb(255, 156, 39, 176)),
        "HTTP" => new SolidColorBrush(Color.FromArgb(255, 244, 67, 54)),
        _      => new SolidColorBrush(Colors.Gray)
    };
}

public sealed class CaptureInterface
{
    public string DisplayName { get; init; } = "";
    public IPAddress Address { get; init; } = IPAddress.None;
}

// ── Page code-behind ──────────────────────────────────────────────────────────
public sealed partial class PacketCapturePage : Page
{
    public MainViewModel? ViewModel { get; private set; }

    private readonly ObservableCollection<CapturedPacket> _allPackets  = new();
    private readonly ObservableCollection<CapturedPacket> _viewPackets = new();

    private CancellationTokenSource? _cts;
    private bool   _capturing;
    private int    _pktIndex;
    private long   _totalBytes;
    private int    _maxRows    = 500;
    private bool   _showHex;
    private string _protoFilter = "all";
    private string _textFilter  = "";
    private readonly DateTime _sessionStart = DateTime.Now;
    private readonly Dictionary<string, int> _protocolCounts = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _captureStarted;
    private IPAddress? _selectedInterfaceAddress;

    // ── Init ─────────────────────────────────────────────────────────────────
    public PacketCapturePage()
    {
        this.InitializeComponent();
        PacketList.ItemsSource = _viewPackets;
    }

    private void OnCloseDetailPane(object sender, RoutedEventArgs e)
        => DetailPane.Visibility = Visibility.Collapsed;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) ViewModel = vm;
        LoadCaptureInterfaces();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopCapture();
    }

    // ── Capture control ───────────────────────────────────────────────────────
    private void OnStartCapture(object sender, RoutedEventArgs e)
    {
        if (_capturing) { StopCapture(); return; }
        StartCapture();
    }

    private void OnPacketJourneyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MenuFlyoutItem item && item.Tag is CapturedPacket packet)
        {
            NavigateToJourney(packet.DstIp);
        }
    }

    private void OnTraceSourceIp(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: CapturedPacket packet }) NavigateToJourney(packet.SrcIp);
    }

    private void OnCopyPacketIp(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: CapturedPacket packet }) CopyToClipboard(packet.DstIp);
    }

    private void OnCopyPacketRow(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: CapturedPacket packet })
            CopyToClipboard($"{packet.Index}\t{packet.Time}\t{packet.Protocol}\t{packet.SrcIp}:{packet.SrcPort}\t{packet.DstIp}:{packet.DstPort}\t{packet.Length}\t{packet.Info}");
    }

    private void NavigateToJourney(string ip)
    {
        if (ViewModel == null || string.IsNullOrWhiteSpace(ip)) return;
        ViewModel.TargetPacketJourneyIp = ip;
        (App.MainWindow)?.NavigateTo("Journey");
    }

    private static void CopyToClipboard(string value)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(value);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void StartCapture()
    {
        _capturing = true;
        _cts = new CancellationTokenSource();
        _captureStarted = DateTime.Now;
        StartIcon.Glyph = "\uE103";         // Stop icon
        StartBtnText.Text = "Stop";
        CaptureLed.Fill = new SolidColorBrush(Color.FromArgb(255, 244, 67, 54));
        CaptureStateText.Text = "Capturing…";
        CaptureProgress.IsActive = true;
        CaptureProgress.Visibility = Visibility.Visible;
        StatusBar.Text = "Capture running — packets will appear in real-time.";

        _ = CaptureLoopAsync(_cts.Token);
    }

    private void StopCapture()
    {
        _cts?.Cancel();
        _capturing = false;
        DispatcherQueue.TryEnqueue(() =>
        {
            StartIcon.Glyph = "\uE102";
            StartBtnText.Text = "Start Capture";
            CaptureLed.Fill = new SolidColorBrush(Color.FromArgb(255, 85, 85, 85));
            CaptureStateText.Text = "Stopped";
            CaptureProgress.IsActive = false;
            CaptureProgress.Visibility = Visibility.Collapsed;
            StatusBar.Text = $"Capture stopped. {_allPackets.Count} packets captured.";
        });
    }

    // ── Raw socket capture loop (IP-level sniffer, no WinDivert needed) ───────
    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        // Use a raw socket to capture all IP traffic on the primary interface
        await Task.Run(() =>
        {
            Socket? rawSocket = null;
            try
            {
                var localIp = _selectedInterfaceAddress ?? GetLocalIpAddress();
                rawSocket = new Socket(AddressFamily.InterNetwork,
                                       SocketType.Raw,
                                       ProtocolType.IP);
                rawSocket.Bind(new IPEndPoint(localIp, 0));
                // Enable promiscuous mode (SIO_RCVALL)
                rawSocket.SetSocketOption(SocketOptionLevel.IP,
                                          SocketOptionName.HeaderIncluded, true);
                rawSocket.IOControl(IOControlCode.ReceiveAll,
                                    new byte[] { 1, 0, 0, 0 },
                                    new byte[] { 1, 0, 0, 0 });

                byte[] buf = new byte[65536];

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        rawSocket.ReceiveTimeout = 500;
                        int received = rawSocket.Receive(buf);
                        if (received < 20) continue;   // Too small to be a valid IP packet

                        byte[] pktBytes = buf[..received];
                        var pkt = ParsePacket(pktBytes);
                        if (pkt == null) continue;

                        // Protocol filter
                        if (_protoFilter != "all" &&
                            !pkt.Protocol.Equals(_protoFilter, StringComparison.OrdinalIgnoreCase)) continue;

                        // Text filter
                        if (_textFilter.Length > 0 &&
                            !pkt.SrcIp.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) &&
                            !pkt.DstIp.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) &&
                            !pkt.SrcPort.Contains(_textFilter) &&
                            !pkt.DstPort.Contains(_textFilter) &&
                            !pkt.Info.Contains(_textFilter, StringComparison.OrdinalIgnoreCase)) continue;

                        Interlocked.Add(ref _totalBytes, received);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            pkt.Index = Interlocked.Increment(ref _pktIndex);
                            _allPackets.Add(pkt);

                            // Enforce max rows
                            if (_maxRows > 0 && _viewPackets.Count >= _maxRows)
                                _viewPackets.RemoveAt(0);
                            _viewPackets.Add(pkt);

                            StatPackets.Text = $"{_pktIndex} packets";
                            StatBytes.Text   = FormatBytes(_totalBytes);
                            UpdateLiveStatistics(pkt.Protocol);

                            // Auto-scroll
                            if (AutoScrollBtn.IsChecked == true && _viewPackets.Count > 0)
                                PacketList.ScrollIntoView(_viewPackets[^1]);
                        });
                    }
                    catch (SocketException sx) when (sx.SocketErrorCode == SocketError.TimedOut) { }
                    catch (SocketException) { break; }  // Interface gone
                }
            }
            catch (Exception ex)
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    StatusBar.Text = $"Capture error: {ex.Message}. Try running as Administrator.";
                    StopCapture();
                });
            }
            finally
            {
                try { rawSocket?.Close(); } catch { }
            }
        }, ct);
    }

    // ── Packet parser (IPv4, TCP/UDP/ICMP) ───────────────────────────────────
    private static CapturedPacket? ParsePacket(byte[] buf)
    {
        try
        {
            // IP version check
            int version = (buf[0] >> 4) & 0xF;
            if (version != 4) return null;  // Skip IPv6 on raw-IPv4 socket

            int ihl     = (buf[0] & 0xF) * 4;
            int protocol = buf[9];

            string srcIp = $"{buf[12]}.{buf[13]}.{buf[14]}.{buf[15]}";
            string dstIp = $"{buf[16]}.{buf[17]}.{buf[18]}.{buf[19]}";

            string proto = "Other";
            string srcPort = "—", dstPort = "—";
            string info = "";

            if (protocol == 6 && buf.Length >= ihl + 20)   // TCP
            {
                proto   = "TCP";
                srcPort = $"{(buf[ihl] << 8) | buf[ihl + 1]}";
                dstPort = $"{(buf[ihl + 2] << 8) | buf[ihl + 3]}";
                int flags = buf[ihl + 13];
                string flagStr = "";
                if ((flags & 0x02) != 0) flagStr += "SYN ";
                if ((flags & 0x01) != 0) flagStr += "FIN ";
                if ((flags & 0x04) != 0) flagStr += "RST ";
                if ((flags & 0x10) != 0) flagStr += "ACK";
                info = flagStr.Trim();
                // Detect HTTP
                if (dstPort == "80" || srcPort == "80" || dstPort == "8080" || srcPort == "8080")
                    proto = "HTTP";
            }
            else if (protocol == 17 && buf.Length >= ihl + 8)  // UDP
            {
                proto   = "UDP";
                srcPort = $"{(buf[ihl] << 8) | buf[ihl + 1]}";
                dstPort = $"{(buf[ihl + 2] << 8) | buf[ihl + 3]}";
                // Detect DNS
                if (dstPort == "53" || srcPort == "53")
                {
                    proto = "DNS";
                    info = "DNS Query/Response";
                }
                else info = "UDP datagram";
            }
            else if (protocol == 1)  // ICMP
            {
                proto = "ICMP";
                if (buf.Length >= ihl + 2)
                {
                    int icmpType = buf[ihl];
                    info = icmpType switch
                    {
                        0  => "Echo Reply",
                        8  => "Echo Request (ping)",
                        3  => "Destination Unreachable",
                        11 => "Time Exceeded",
                        _  => $"ICMP type {icmpType}"
                    };
                }
            }
            else return null;   // Skip non-essential protos (keep list clean)

            int totalLen = (buf[2] << 8) | buf[3];
            int payloadStart = ihl + (proto == "TCP" && buf.Length >= ihl + 13
                ? ((buf[ihl + 12] >> 4) * 4) : 8);
            byte[] payload = payloadStart < buf.Length ? buf[payloadStart..] : Array.Empty<byte>();

            return new CapturedPacket
            {
                Time     = DateTime.Now.ToString("HH:mm:ss.fff"),
                Protocol = proto,
                SrcIp    = srcIp,
                SrcPort  = srcPort,
                DstIp    = dstIp,
                DstPort  = dstPort,
                Length   = totalLen,
                Info     = info,
                Payload  = payload
            };
        }
        catch { return null; }
    }

    // ── UI events ─────────────────────────────────────────────────────────────
    private void OnClear(object sender, RoutedEventArgs e)
    {
        _allPackets.Clear();
        _viewPackets.Clear();
        _pktIndex = 0;
        _totalBytes = 0;
        _protocolCounts.Clear();
        StatPackets.Text = "0 packets";
        StatBytes.Text   = "0 B captured";
        StatRate.Text = "0 pkt/s";
        StatProtocols.Text = "TCP: 0% | UDP: 0% | DNS: 0% | ICMP: 0%";
        StatusBar.Text   = "Cleared.";
        DetailSummary.Text = "";
        HexBlock.Text = "";
    }

    private void OnFilterChanged(object sender, object e)
    {
        if (FilterBox == null || ProtoFilter == null) return;
        _protoFilter = (ProtoFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        _textFilter  = FilterBox.Text.Trim();

        // Re-apply filter to accumulated packets
        _viewPackets.Clear();
        var filtered = _allPackets.Where(p =>
            (_protoFilter == "all" || p.Protocol.Equals(_protoFilter, StringComparison.OrdinalIgnoreCase)) &&
            (_textFilter.Length == 0 ||
             p.SrcIp.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) ||
             p.DstIp.Contains(_textFilter, StringComparison.OrdinalIgnoreCase) ||
             p.SrcPort.Contains(_textFilter) ||
             p.DstPort.Contains(_textFilter) ||
             p.Info.Contains(_textFilter, StringComparison.OrdinalIgnoreCase)));

        foreach (var pkt in filtered) _viewPackets.Add(pkt);
        StatusBar.Text = $"Filter applied — {_viewPackets.Count} of {_allPackets.Count} packets shown.";
    }

    private void OnMaxRowsChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((MaxRowsBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() is string tag)
            _maxRows = int.TryParse(tag, out int n) ? n : 500;
    }

    private void OnHexToggle(object sender, RoutedEventArgs e)
    {
        _showHex = HexViewBtn.IsChecked == true;
        HexHeader.Visibility = _showHex ? Visibility.Visible : Visibility.Collapsed;
        HexBlock.Visibility  = _showHex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPacketSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PacketList.SelectedItem is not CapturedPacket pkt) return;

        // Show the detail pane
        DetailPane.Visibility = Visibility.Visible;

        DetailSummary.Text =
            $"Index:    {pkt.Index}\n" +
            $"Time:     {pkt.Time}\n" +
            $"Protocol: {pkt.Protocol}\n" +
            $"Source:   {pkt.SrcIp}:{pkt.SrcPort}\n" +
            $"Dest:     {pkt.DstIp}:{pkt.DstPort}\n" +
            $"Length:   {pkt.Length} bytes\n" +
            $"Info:     {pkt.Info}\n" +
            $"Payload:  {pkt.Payload.Length} bytes";

        if (_showHex)
            HexBlock.Text = ToHexDump(pkt.Payload);
    }

    // ── Export ────────────────────────────────────────────────────────────────
    private async void OnExportPcap(object sender, RoutedEventArgs e)
    {
        if (_allPackets.Count == 0) { StatusBar.Text = "Nothing to export."; return; }
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.pcap");
        await Task.Run(() => WritePcapFile(path, _allPackets.ToList()));
        StatusBar.Text = $"✅ Saved {path}";
        HistoryLogService.AddLog("PacketCapture", "Export", $"Saved {_allPackets.Count} packets → {path}");
    }

    private async void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_allPackets.Count == 0) { StatusBar.Text = "Nothing to export."; return; }
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        await Task.Run(() =>
        {
            var sb = new StringBuilder("Index,Time,Protocol,SrcIP,SrcPort,DstIP,DstPort,Length,Info\n");
            foreach (var p in _allPackets.ToList())
                sb.AppendLine($"{p.Index},{p.Time},{p.Protocol},{p.SrcIp},{p.SrcPort},{p.DstIp},{p.DstPort},{p.Length},\"{p.Info}\"");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        });
        StatusBar.Text = $"✅ CSV saved: {path}";
        HistoryLogService.AddLog("PacketCapture", "Export", $"CSV → {path}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static IPAddress GetLocalIpAddress()
    {
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var uni in iface.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                    return uni.Address;
            }
        }
        return IPAddress.Loopback;
    }

    private void LoadCaptureInterfaces()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => new CaptureInterface { DisplayName = $"{n.Name} ({a.Address})", Address = a.Address }))
            .ToList();
        InterfaceBox.ItemsSource = interfaces;
        if (interfaces.Count > 0) InterfaceBox.SelectedIndex = 0;
        else StatusBar.Text = "No active IPv4 capture interface was found.";
    }

    private void OnCaptureInterfaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InterfaceBox.SelectedItem is CaptureInterface selected)
        {
            _selectedInterfaceAddress = selected.Address;
            if (!_capturing) StatusBar.Text = $"Ready to capture on {selected.DisplayName}.";
        }
    }

    private void UpdateLiveStatistics(string protocol)
    {
        _protocolCounts[protocol] = _protocolCounts.TryGetValue(protocol, out int count) ? count + 1 : 1;
        int total = Math.Max(1, _pktIndex);
        int CountFor(string name) => _protocolCounts.TryGetValue(name, out int value) ? value : 0;
        int Percent(string name) => (int)Math.Round(100.0 * CountFor(name) / total);
        StatProtocols.Text = $"TCP: {Percent("TCP")}% | UDP: {Percent("UDP")}% | DNS: {Percent("DNS")}% | ICMP: {Percent("ICMP")}%";
        double seconds = Math.Max(1, (DateTime.Now - _captureStarted).TotalSeconds);
        StatRate.Text = $"{_pktIndex / seconds:F1} pkt/s";
    }

    private static string ToHexDump(byte[] data)
    {
        if (data.Length == 0) return "(empty payload)";
        var sb = new StringBuilder();
        for (int i = 0; i < Math.Min(data.Length, 512); i += 16)
        {
            sb.Append($"{i:X4}  ");
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length) sb.Append($"{data[i + j]:X2} ");
                else sb.Append("   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append(" |");
            for (int j = 0; j < 16 && i + j < data.Length; j++)
            {
                byte b = data[i + j];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            sb.AppendLine("|");
        }
        if (data.Length > 512) sb.AppendLine($"… {data.Length - 512} more bytes");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)       return $"{bytes} B captured";
        if (bytes < 1024*1024)  return $"{bytes/1024.0:F1} KB captured";
        return $"{bytes/(1024.0*1024):F1} MB captured";
    }

    // ── pcap file writer (libpcap format, opens in Wireshark) ─────────────────
    private static void WritePcapFile(string path, List<CapturedPacket> packets)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        // Global header
        bw.Write(0xa1b2c3d4u);  // magic
        bw.Write((ushort)2);    // major
        bw.Write((ushort)4);    // minor
        bw.Write(0);            // thiszone
        bw.Write(0u);           // sigfigs
        bw.Write(65535u);       // snaplen
        bw.Write(1u);           // linktype = LINKTYPE_ETHERNET (we write raw IP, so LINKTYPE_RAW=101)
        // Re-write with correct link type
        fs.Seek(20, SeekOrigin.Begin);
        bw.Write(101u);         // LINKTYPE_RAW
        fs.Seek(0, SeekOrigin.End);

        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var pkt in packets)
        {
            if (!DateTime.TryParse(pkt.Time, out var ts)) ts = DateTime.Now;
            var unixTs = (ts.ToUniversalTime() - epoch).TotalSeconds;
            uint tsSec  = (uint)Math.Floor(unixTs);
            uint tsUsec = (uint)((unixTs - tsSec) * 1_000_000);
            uint pktLen = (uint)pkt.Length;
            bw.Write(tsSec);
            bw.Write(tsUsec);
            bw.Write(pktLen);   // captured length
            bw.Write(pktLen);   // original length
            bw.Write(pkt.Payload.Length > 0 ? pkt.Payload : new byte[pkt.Length]);
        }
    }
}
