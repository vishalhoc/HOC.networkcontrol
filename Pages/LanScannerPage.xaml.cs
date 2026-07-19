using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl.Pages;

public partial class LanDevice : ObservableObject
{
    public string Ip        { get; set; } = "";
    [ObservableProperty] private string _hostname = "—";
    public string Mac       { get; set; } = "—";
    [ObservableProperty] private string _pingMs = "";
    public string OpenPorts { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingBrush))]
    private long _pingRaw;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessText), nameof(AccessIcon), nameof(AccessBrush))]
    private bool _isBlocked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeVendorText), nameof(DeviceDetails))]
    private string _deviceType = "Unknown";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeVendorText), nameof(DeviceDetails))]
    private string _vendor = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceDetails))]
    private string _model = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeviceDetails))]
    private string _discoverySource = "ARP / Ping";
    public SolidColorBrush PingBrush => new(PingRaw < 0
        ? Windows.UI.Color.FromArgb(255, 128, 128, 128)
        : PingRaw < 30
            ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
            : PingRaw < 100
                ? Windows.UI.Color.FromArgb(255, 251, 188, 5)
                : Windows.UI.Color.FromArgb(255, 224, 32, 32));
    public string AccessText => IsBlocked ? "Blocked" : "Allow access";
    public string AccessIcon => IsBlocked ? "\uE72E" : "\uF140";
    public SolidColorBrush AccessBrush => new(IsBlocked
        ? Windows.UI.Color.FromArgb(255, 204, 51, 0)
        : Windows.UI.Color.FromArgb(255, 16, 124, 16));
    public string TypeVendorText => string.IsNullOrWhiteSpace(Vendor) ? DeviceType : $"{DeviceType} · {Vendor}";
    public string DeviceDetails => $"Name: {Hostname}\nType: {DeviceType}\nVendor: {Vendor}\nModel: {Model}\nSource: {DiscoverySource}";
}

public sealed partial class LanScannerPage : Page
{
    private bool _scanning;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<LanDevice> _devices = new();
    private readonly ObservableCollection<LanDevice> _filteredDevices = new();
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // Common ports to probe if ScanPorts is on
    private static readonly int[] CommonPorts = { 22, 80, 443, 445, 3389, 8080 };

    public LanScannerPage()
    {
        this.InitializeComponent();
        DeviceList.ItemsSource = _filteredDevices;
        _elapsedTimer.Tick += (_, __) =>
            StatElapsed.Text = $"{_elapsed.Elapsed.TotalSeconds:F0} s";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AutoDetectSubnet();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        _elapsedTimer.Stop();
    }

    private void AutoDetectSubnet()
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.GetIPProperties().GatewayAddresses.Any());

            if (iface == null) return;

            var unicast = iface.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (unicast == null) return;

            byte[] addr = unicast.Address.GetAddressBytes();
            byte[] mask = unicast.IPv4Mask.GetAddressBytes();
            byte[] network = new byte[4];
            for (int i = 0; i < 4; i++) network[i] = (byte)(addr[i] & mask[i]);

            int cidr = 0;
            foreach (byte b in mask)
                for (int bit = 7; bit >= 0; bit--)
                    if ((b >> bit & 1) == 1) cidr++;

            SubnetBox.Text = $"{network[0]}.{network[1]}.{network[2]}.{network[3]}/{cidr}";
        }
        catch { }
    }

    private async void OnScanToggle(object sender, RoutedEventArgs e)
    {
        if (_scanning) { _cts?.Cancel(); return; }

        _scanning = true;
        ScanBtnText.Text = "Stop";
        ScanIcon.Glyph   = "\uE711";
        _devices.Clear();
        _filteredDevices.Clear();
        _elapsed.Restart();
        _elapsedTimer.Start();
        StatHosts.Text   = "0";
        StatScanned.Text = "0 / 0";
        ScanProgressBar.Value = 0;

        _cts = new CancellationTokenSource();

        try
        {
            var targets = ParseSubnet(SubnetBox.Text.Trim());
            int total   = targets.Count;
            if (total == 0)
            {
                ScanStatusText.Text = "Enter a valid IPv4 CIDR range, for example 192.168.1.0/24.";
                return;
            }
            int timeout = (int)TimeoutBox.Value;
            bool resolve = ResolveNames.IsOn;
            bool portScan = ScanPorts.IsOn;
            bool discoverServices = DiscoverServices.IsOn;

            StatScanned.Text = $"0 / {total}";

            var foundBag = new ConcurrentBag<LanDevice>();
            int scanned  = 0;

            var semaphore = new SemaphoreSlim(64); // max 64 parallel pings

            var tasks = targets.Select(async ip =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    if (_cts.Token.IsCancellationRequested) return;

                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(ip, timeout);

                    if (reply.Status == IPStatus.Success)
                    {
                        var dev = new LanDevice
                        {
                            Ip     = ip,
                            PingMs = $"{reply.RoundtripTime} ms",
                            PingRaw = reply.RoundtripTime,
                            Mac    = GetArpMac(ip),
                            IsBlocked = LocalNetworkScannerService.IsDeviceBlocked(ip)
                        };

                        if (resolve)
                        {
                            try
                            {
                                var host = await Dns.GetHostEntryAsync(ip).WaitAsync(TimeSpan.FromMilliseconds(800));
                                dev.Hostname = host.HostName;
                            }
                            catch { dev.Hostname = "—"; }
                        }

                        if (portScan)
                            dev.OpenPorts = await ScanCommonPortsAsync(ip, timeout);

                        foundBag.Add(dev);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            // Sort live devices by last octet for consistent ordering
                            var sortedDevices = foundBag
                                .OrderBy(d => int.TryParse(d.Ip.Split('.').Last(), out int n) ? n : 0)
                                .ToList();
                            _devices.Clear();
                            foreach (var dd in sortedDevices) _devices.Add(dd);
                            ApplyDeviceFilter();
                            StatHosts.Text = _devices.Count.ToString();
                        });
                    }

                    int done = Interlocked.Increment(ref scanned);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatScanned.Text  = $"{done} / {total}";
                        ScanProgressBar.Value = (double)done / total * 100;
                        ScanStatusText.Text   = $"{done}/{total} scanned";
                    });
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);

            // SSDP supplements ARP/ping/DNS with names and model data exposed by
            // smart TVs, media devices, cameras, routers, and other UPnP devices.
            if (discoverServices && _cts is { IsCancellationRequested: false } scanCancellation)
            {
                ScanStatusText.Text = "Discovering smart devices…";
                await EnrichWithSsdpAsync(scanCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatusText.Text = "Scan stopped.";
        }
        finally
        {
            _scanning = false;
            _elapsed.Stop();
            _elapsedTimer.Stop();
            DispatcherQueue.TryEnqueue(() =>
            {
                ScanBtnText.Text = "Scan Network";
                ScanIcon.Glyph   = "\uE968";
                ScanProgressBar.Value = 100;
                ScanStatusText.Text   = $"Done — {_devices.Count} hosts found in {_elapsed.Elapsed.TotalSeconds:F1}s";
            });
        }
    }

    private static System.Collections.Generic.List<string> ParseSubnet(string cidr)
    {
        var result = new System.Collections.Generic.List<string>();
        try
        {
            var parts = cidr.Split('/');
            var baseIp = IPAddress.Parse(parts[0]).GetAddressBytes();
            int prefix = parts.Length > 1 ? int.Parse(parts[1]) : 24;
            int hostBits = 32 - prefix;
            int count = 1 << hostBits;

            uint baseInt = (uint)(baseIp[0] << 24 | baseIp[1] << 16 | baseIp[2] << 8 | baseIp[3]);

            for (int i = 1; i < count - 1; i++) // skip network + broadcast
            {
                uint ipInt = baseInt + (uint)i;
                result.Add($"{(ipInt >> 24) & 0xFF}.{(ipInt >> 16) & 0xFF}.{(ipInt >> 8) & 0xFF}.{ipInt & 0xFF}");
            }
        }
        catch { }
        return result;
    }

    private static string GetArpMac(string ip)
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
            var match = System.Text.RegularExpressions.Regex.Match(output,
                @"([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}");
            return match.Success ? match.Value.ToUpper() : "—";
        }
        catch { return "—"; }
    }

    private static async Task<string> ScanCommonPortsAsync(string ip, int timeout)
    {
        var open = new System.Collections.Generic.List<int>();
        var tasks = CommonPorts.Select(async port =>
        {
            try
            {
                using var tcp = new TcpClient();
                var conn = tcp.ConnectAsync(ip, port);
                if (await Task.WhenAny(conn, Task.Delay(timeout)) == conn && !conn.IsFaulted)
                    lock (open) open.Add(port);
            }
            catch { }
        });
        await Task.WhenAll(tasks);
        return open.Any() ? string.Join(", ", open.OrderBy(p => p)) : "—";
    }

    private async Task EnrichWithSsdpAsync(CancellationToken ct)
    {
        var discoveries = await LocalNetworkScannerService.DiscoverSsdpAsync(ct);
        foreach (var discovery in discoveries.Values)
        {
            var device = _devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Ip, discovery.IpAddress, StringComparison.OrdinalIgnoreCase));

            // Some smart devices intentionally ignore ICMP ping.  If they advertise
            // through SSDP, still show them and clearly identify the discovery source.
            if (device == null)
            {
                device = new LanDevice
                {
                    Ip = discovery.IpAddress,
                    Hostname = string.IsNullOrWhiteSpace(discovery.FriendlyName) ? "UPnP device" : discovery.FriendlyName,
                    Mac = GetArpMac(discovery.IpAddress),
                    PingMs = "SSDP",
                    PingRaw = -1,
                    OpenPorts = "—",
                    IsBlocked = LocalNetworkScannerService.IsDeviceBlocked(discovery.IpAddress)
                };
                _devices.Add(device);
            }

            if (!string.IsNullOrWhiteSpace(discovery.FriendlyName)) device.Hostname = discovery.FriendlyName;
            if (!string.IsNullOrWhiteSpace(discovery.Manufacturer)) device.Vendor = discovery.Manufacturer;
            if (!string.IsNullOrWhiteSpace(discovery.ModelName)) device.Model = discovery.ModelName;
            if (!string.IsNullOrWhiteSpace(discovery.DeviceType)) device.DeviceType = discovery.DeviceType;
            device.DiscoverySource = "UPnP / SSDP";
        }
        ApplyDeviceFilter();
    }

    // ── Device filtering and actions ─────────────────────────────────────────
    private void OnDeviceSearchChanged(object sender, TextChangedEventArgs e) => ApplyDeviceFilter();
    private void OnBlockedOnlyToggled(object sender, RoutedEventArgs e) => ApplyDeviceFilter();

    private void ApplyDeviceFilter()
    {
        if (DeviceSearchBox == null || BlockedOnlyToggle == null) return;
        string query = DeviceSearchBox.Text.Trim();
        bool blockedOnly = BlockedOnlyToggle.IsOn;
        var visible = _devices.Where(device =>
            (!blockedOnly || device.IsBlocked) &&
            (string.IsNullOrWhiteSpace(query) ||
             device.Ip.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             device.Hostname.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             device.Mac.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             device.DeviceType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             device.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             device.Model.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(device => device.Ip)
            .ToList();

        _filteredDevices.Clear();
        foreach (var device in visible) _filteredDevices.Add(device);
    }

    private async void OnDeviceAccessToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LanDevice device }) return;
        bool shouldBlock = !device.IsBlocked;
        if (shouldBlock)
        {
            var confirm = new ContentDialog
            {
                Title = "Block device access from this PC?",
                Content = $"Windows Firewall will block traffic between this computer and {device.Ip}. " +
                          "This does not disable the device's internet access through the router.",
                PrimaryButtonText = "Block",
                CloseButtonText = "Cancel",
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }

        await SetDeviceAccessAsync(device, shouldBlock);
    }

    private async void OnBlockSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(device => !device.IsBlocked).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select one or more unblocked devices first."; return; }

        var confirm = new ContentDialog
        {
            Title = "Block selected devices from this PC?",
            Content = $"Windows Firewall will block this computer's traffic to and from {targets.Count} selected device(s).",
            PrimaryButtonText = "Block",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        await Task.Run(() => { foreach (var device in targets) LocalNetworkScannerService.BlockDevice(device.Ip); });
        foreach (var device in targets) device.IsBlocked = true;
        ApplyDeviceFilter();
        ScanStatusText.Text = $"Blocked this PC's access to {targets.Count} device(s).";
        HistoryLogService.AddLog("LAN Scanner", "Device access", $"Blocked {targets.Count} device(s)");
    }

    private async void OnUnblockSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(device => device.IsBlocked).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select one or more blocked devices first."; return; }

        await Task.Run(() => { foreach (var device in targets) LocalNetworkScannerService.UnblockDevice(device.Ip); });
        foreach (var device in targets) device.IsBlocked = false;
        ApplyDeviceFilter();
        ScanStatusText.Text = $"Restored this PC's access to {targets.Count} device(s).";
        HistoryLogService.AddLog("LAN Scanner", "Device access", $"Unblocked {targets.Count} device(s)");
    }

    private async Task SetDeviceAccessAsync(LanDevice device, bool block)
    {
        ScanStatusText.Text = block ? $"Blocking {device.Ip}…" : $"Restoring access to {device.Ip}…";
        await Task.Run(() =>
        {
            if (block) LocalNetworkScannerService.BlockDevice(device.Ip);
            else LocalNetworkScannerService.UnblockDevice(device.Ip);
        });
        device.IsBlocked = block;
        ApplyDeviceFilter();
        ScanStatusText.Text = block
            ? $"Blocked this PC's traffic to and from {device.Ip}."
            : $"Restored this PC's traffic to and from {device.Ip}.";
        HistoryLogService.AddLog("LAN Scanner", "Device access", $"{(block ? "Blocked" : "Unblocked")} {device.Ip}");
    }

    private async void OnPingSelected(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not LanDevice device) { ScanStatusText.Text = "Select a device to ping."; return; }
        await PingDeviceAsync(device);
    }

    private async void OnPingDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice device }) await PingDeviceAsync(device);
    }

    private async void OnShowDeviceDetails(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LanDevice device }) return;

        var details = new ContentDialog
        {
            Title = device.Hostname is "" or "—" ? device.Ip : device.Hostname,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    DetailLine("IP address", device.Ip),
                    DetailLine("MAC address", device.Mac),
                    DetailLine("Device type", device.DeviceType),
                    DetailLine("Vendor", string.IsNullOrWhiteSpace(device.Vendor) ? "Not identified" : device.Vendor),
                    DetailLine("Model", string.IsNullOrWhiteSpace(device.Model) ? "Not advertised" : device.Model),
                    DetailLine("Discovery", device.DiscoverySource),
                    DetailLine("Ping", string.IsNullOrWhiteSpace(device.PingMs) ? "Not tested" : device.PingMs),
                    DetailLine("Open ports", string.IsNullOrWhiteSpace(device.OpenPorts) ? "Not tested" : device.OpenPorts),
                    DetailLine("This PC's access", device.AccessText)
                }
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await details.ShowAsync();
    }

    private static Grid DetailLine(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)) });
        var valueText = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    private async Task PingDeviceAsync(LanDevice device)
    {
        ScanStatusText.Text = $"Pinging {device.Ip}…";
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(device.Ip, (int)TimeoutBox.Value);
            if (reply.Status != IPStatus.Success) { ScanStatusText.Text = $"{device.Ip} did not reply."; return; }
            device.PingRaw = reply.RoundtripTime;
            device.PingMs = $"{reply.RoundtripTime} ms";
            ScanStatusText.Text = $"{device.Ip} replied in {reply.RoundtripTime} ms.";
        }
        catch (Exception ex) { ScanStatusText.Text = $"Ping failed: {ex.Message}"; }
    }

    private void OnCopyDeviceIp(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice device }) CopyToClipboard(device.Ip);
    }

    private void OnCopyDeviceMac(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice device }) CopyToClipboard(device.Mac);
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private async void OnExportDevices(object sender, RoutedEventArgs e)
    {
        if (_filteredDevices.Count == 0) { ScanStatusText.Text = "No visible devices to export."; return; }
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"WinNetControl_LAN_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var devices = _filteredDevices.ToList();
        await Task.Run(() =>
        {
            static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
            var csv = new StringBuilder("IP Address,Hostname,Device Type,Vendor,Model,Discovery Source,MAC Address,Ping,Open Ports,Access\n");
            foreach (var device in devices)
                csv.AppendLine(string.Join(',', Csv(device.Ip), Csv(device.Hostname), Csv(device.DeviceType), Csv(device.Vendor), Csv(device.Model), Csv(device.DiscoverySource), Csv(device.Mac), Csv(device.PingMs), Csv(device.OpenPorts), Csv(device.AccessText)));
            File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
        });
        ScanStatusText.Text = $"Exported {devices.Count} devices to {path}";
    }
}
