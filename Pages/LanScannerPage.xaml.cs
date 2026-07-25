using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNetControl.Core;
using System;
using System.Collections.Concurrent;
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
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl.Pages;

// ── LanDevice model ──────────────────────────────────────────────────────────
public partial class LanDevice : ObservableObject
{
    public string Ip        { get; set; } = "";
    public string Mac       { get; set; } = "—";
    public string OpenPorts { get; set; } = "";

    [ObservableProperty] private string _hostname = "—";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingBrush))]
    private long _pingRaw;
    [ObservableProperty] private string _pingMs = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessText), nameof(AccessIcon), nameof(AccessBrush))]
    private bool _isBlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InternetText), nameof(InternetIcon), nameof(InternetBrush))]
    private bool _isInternetCut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OsGuess), nameof(OsIcon), nameof(TypeVendorText))]
    private string _deviceType = "Unknown";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeVendorText), nameof(VendorVisibility))]
    private string _vendor = "";

    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _discoverySource = "ARP / Ping";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OsGuess), nameof(OsIcon))]
    private int _ttl;

    /// <summary>Icon set by OUI / DeviceFingerprinter (overrides TTL-based icon).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OsIcon))]
    private string _fingerprintIcon = "";

    /// <summary>Device type label set by OUI / DeviceFingerprinter (overrides TTL-based label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OsGuess), nameof(TypeVendorText))]
    private string _fingerprintType = "";

    // ── Computed ─────────────────────────────────────────────────────────────
    public SolidColorBrush PingBrush => new(PingRaw < 0
        ? Windows.UI.Color.FromArgb(255, 128, 128, 128)
        : PingRaw < 30
            ? Windows.UI.Color.FromArgb(255,  16, 124,  16)
            : PingRaw < 100
                ? Windows.UI.Color.FromArgb(255, 251, 188,   5)
                : Windows.UI.Color.FromArgb(255, 224,  32,  32));

    public string AccessText  => IsBlocked ? "Blocked"     : "Allow";
    public string AccessIcon  => IsBlocked ? "\uE72E"      : "\uF140";
    public SolidColorBrush AccessBrush => new(IsBlocked
        ? Windows.UI.Color.FromArgb(255, 204,  51,   0)
        : Windows.UI.Color.FromArgb(255,  16, 124,  16));

    public string InternetText  => IsInternetCut ? "Cut off"    : "Connected";
    public string InternetIcon  => IsInternetCut ? "\uE814"     : "\uE774";
    public SolidColorBrush InternetBrush => new(IsInternetCut
        ? Windows.UI.Color.FromArgb(255, 204,  51,   0)
        : Windows.UI.Color.FromArgb(255,  16, 124,  16));

    public Visibility VendorVisibility => string.IsNullOrWhiteSpace(Vendor)
        ? Visibility.Collapsed : Visibility.Visible;

    public string TypeVendorText => string.IsNullOrWhiteSpace(Vendor)
        ? DeviceType : $"{DeviceType} · {Vendor}";

    /// <summary>
    /// Device type label: uses fingerprinted OUI result if available,
    /// otherwise falls back to TTL-based OS guess.
    /// </summary>
    public string OsGuess =>
        !string.IsNullOrEmpty(FingerprintType) ? FingerprintType :
        Ttl switch
        {
            > 0 and <= 64   => "Linux / macOS",
            > 64 and <= 128 => "Windows",
            > 128           => "Router / Network",
            _               => DeviceType is "Unknown" or "" ? "—" : DeviceType
        };

    /// <summary>
    /// Device icon: uses fingerprinted OUI icon if available,
    /// otherwise falls back to TTL-based emoji.
    /// </summary>
    public string OsIcon =>
        !string.IsNullOrEmpty(FingerprintIcon) ? FingerprintIcon :
        Ttl switch
        {
            > 0 and <= 64   => "🐧",
            > 64 and <= 128 => "🪟",
            > 128           => "🌐",
            _               => "❓"
        };

    public string DeviceDetails =>
        $"IP: {Ip}\nHostname: {Hostname}\nMAC: {Mac}\n" +
        $"Vendor: {(string.IsNullOrWhiteSpace(Vendor) ? "—" : Vendor)}\n" +
        $"Model: {(string.IsNullOrWhiteSpace(Model) ? "—" : Model)}\n" +
        $"OS guess: {OsGuess} (TTL={Ttl})\n" +
        $"Discovery: {DiscoverySource}";
}

// ── Page ─────────────────────────────────────────────────────────────────────
public sealed partial class LanScannerPage : Page
{
    private bool _scanning;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<LanDevice> _devices        = new();
    private readonly ObservableCollection<LanDevice> _filteredDevices = new();
    private readonly Stopwatch       _elapsed      = new();
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _autoRefreshOn;

    private readonly ArpSpoofService _arpSpoof = new();
    private bool _arpInitialized;

    private static readonly int[] CommonPorts = { 22, 80, 443, 445, 3389, 8080, 8443 };

    public LanScannerPage()
    {
        this.InitializeComponent();
        DeviceList.ItemsSource = _filteredDevices;

        _elapsedTimer.Tick += (_, _) =>
            StatElapsed.Text = $"{_elapsed.Elapsed.TotalSeconds:F0} s";

        _autoRefreshTimer.Tick += async (_, _) =>
        {
            if (!_scanning) await RefreshPingAllAsync();
        };
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
        _autoRefreshTimer.Stop();
        _arpSpoof.Dispose();
    }

    // ── ARP spoof init ───────────────────────────────────────────────────────
    private bool EnsureArpInit()
    {
        if (_arpInitialized) return _arpSpoof.IsAvailable;
        _arpInitialized = true;
        _arpSpoof.Initialize();
        if (!_arpSpoof.IsAvailable)
            ScanStatusText.Text = $"ARP spoof unavailable: {_arpSpoof.InitError}";
        return _arpSpoof.IsAvailable;
    }

    private void UpdateArpBanner()
    {
        int count = _arpSpoof.ActiveCount;
        ArpWarningBanner.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ArpBannerDetail.Text = $"Internet access is being cut for {count} device(s) via ARP poisoning.";
        StatCutOff.Text = count.ToString();
    }

    // ── Subnet detection ─────────────────────────────────────────────────────
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
            byte[] net  = new byte[4];
            for (int i = 0; i < 4; i++) net[i] = (byte)(addr[i] & mask[i]);
            int cidr = mask.Aggregate(0, (acc, b) =>
            {
                for (int bit = 7; bit >= 0; bit--)
                    if ((b >> bit & 1) == 1) acc++;
                return acc;
            });
            SubnetBox.Text = $"{net[0]}.{net[1]}.{net[2]}.{net[3]}/{cidr}";
        }
        catch { }
    }

    // ── Auto-refresh ─────────────────────────────────────────────────────────
    private void OnAutoRefreshToggle(object sender, RoutedEventArgs e)
    {
        _autoRefreshOn = !_autoRefreshOn;
        if (_autoRefreshOn)
        {
            _autoRefreshTimer.Start();
            AutoRefreshIcon.Glyph = "\uE72C"; // hourglass
            AutoRefreshText.Text  = "Auto (30s)";
        }
        else
        {
            _autoRefreshTimer.Stop();
            AutoRefreshIcon.Glyph = "\uE895";
            AutoRefreshText.Text  = "Auto-refresh";
        }
    }

    private async Task RefreshPingAllAsync()
    {
        var snapshot = _devices.ToList();
        int timeout = (int)TimeoutBox.Value;
        await Task.WhenAll(snapshot.Select(async dev =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(dev.Ip, timeout);
                if (reply.Status == IPStatus.Success)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        dev.PingRaw = reply.RoundtripTime;
                        dev.PingMs  = $"{reply.RoundtripTime} ms";
                        if (reply.Options != null && FingerprintOS.IsOn)
                            dev.Ttl = reply.Options.Ttl;
                    });
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() => { dev.PingMs = "timeout"; dev.PingRaw = -1; });
                }
            }
            catch { }
        }));
        DispatcherQueue.TryEnqueue(() => ScanStatusText.Text = $"Refreshed {snapshot.Count} devices.");
    }

    // ── Scan ─────────────────────────────────────────────────────────────────
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
                ScanStatusText.Text = "Enter a valid IPv4 CIDR range, e.g. 192.168.1.0/24.";
                return;
            }

            int timeout          = (int)TimeoutBox.Value;
            bool resolve         = ResolveNames.IsOn;
            bool portScan        = ScanPorts.IsOn;
            bool discoverSvc     = DiscoverServices.IsOn;
            bool fingerprint     = FingerprintOS.IsOn;

            StatScanned.Text = $"0 / {total}";
            var foundBag = new ConcurrentBag<LanDevice>();
            int scanned  = 0;
            var semaphore = new SemaphoreSlim(64);

            var tasks = targets.Select(async ip =>
            {
                await semaphore.WaitAsync(_cts.Token);
                try
                {
                    if (_cts.Token.IsCancellationRequested) return;

                    using var ping = new Ping();
                    PingOptions opts = new() { DontFragment = true };
                    var reply = await ping.SendPingAsync(ip, timeout, new byte[32], opts);

                    if (reply.Status == IPStatus.Success)
                    {
                        var dev = new LanDevice
                        {
                            Ip        = ip,
                            PingMs    = $"{reply.RoundtripTime} ms",
                            PingRaw   = reply.RoundtripTime,
                            Mac       = GetArpMac(ip),
                            IsBlocked = LocalNetworkScannerService.IsDeviceBlocked(ip),
                            IsInternetCut = _arpSpoof.IsActive(ip)
                        };

                        // OS fingerprint via TTL
                        if (fingerprint && reply.Options != null)
                            dev.Ttl = reply.Options.Ttl;

                        // ── Device fingerprinting: OUI + hostname (parallel probes) ──────────
                        // Runs NetBIOS NBNS (UDP 137), mDNS PTR (UDP 5353), Android .local,
                        // HTTP banner grab (port 80), and reverse DNS concurrently.
                        // Returns the first hostname that responds + OUI-based vendor/type.
                        if (resolve)
                        {
                            try
                            {
                                int fpTimeout = Math.Max(timeout, 1200);
                                var fp = await DeviceFingerprinter.FingerprintAsync(
                                    dev.Ip, dev.Mac, dev.Ttl, fpTimeout);

                                if (!string.IsNullOrWhiteSpace(fp.Hostname))
                                    dev.Hostname = fp.Hostname;

                                if (!string.IsNullOrWhiteSpace(fp.Vendor))
                                    dev.Vendor = fp.Vendor;

                                dev.FingerprintType = fp.DeviceType;
                                dev.FingerprintIcon = fp.DeviceIcon;

                                // Keep existing DeviceType if SSDP already enriched it
                                if (dev.DeviceType is "Unknown" or "")
                                    dev.DeviceType = fp.DeviceType;
                            }
                            catch { /* non-fatal — device shown without hostname */ }
                        }
                        else
                        {
                            // Even without hostname resolve, do OUI lookup for device type
                            var (v, t, i) = DeviceFingerprinter.LookupOui(dev.Mac, dev.Ttl);
                            dev.Vendor         = v;
                            dev.FingerprintType = t;
                            dev.FingerprintIcon = i;
                            if (dev.DeviceType is "Unknown" or "") dev.DeviceType = t;
                        }

                        // Port scan
                        if (portScan)
                            dev.OpenPorts = await ScanCommonPortsAsync(ip, timeout);

                        foundBag.Add(dev);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            var sorted = foundBag
                                .OrderBy(d => d.Ip.Split('.').Select(int.Parse).Last())
                                .ToList();
                            _devices.Clear();
                            foreach (var d in sorted) _devices.Add(d);
                            ApplyDeviceFilter();
                            StatHosts.Text = _devices.Count.ToString();
                        });
                    }

                    int done = Interlocked.Increment(ref scanned);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        StatScanned.Text      = $"{done} / {total}";
                        ScanProgressBar.Value = (double)done / total * 100;
                        ScanStatusText.Text   = $"{done}/{total} scanned";
                    });
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);

            if (discoverSvc && !(_cts?.IsCancellationRequested ?? true))
            {
                ScanStatusText.Text = "Discovering UPnP/SSDP devices…";
                await EnrichWithSsdpAsync(_cts.Token);
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
                ScanBtnText.Text      = "Scan Network";
                ScanIcon.Glyph        = "\uE968";
                ScanProgressBar.Value = 100;
                ScanStatusText.Text   = $"Done — {_devices.Count} hosts in {_elapsed.Elapsed.TotalSeconds:F1}s";
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static List<string> ParseSubnet(string cidr)
    {
        var result = new List<string>();
        try
        {
            var parts   = cidr.Split('/');
            var baseIp  = IPAddress.Parse(parts[0]).GetAddressBytes();
            int prefix  = parts.Length > 1 ? int.Parse(parts[1]) : 24;
            int hostBits = 32 - prefix;
            int count   = 1 << hostBits;
            uint baseInt = (uint)(baseIp[0] << 24 | baseIp[1] << 16 | baseIp[2] << 8 | baseIp[3]);
            for (int i = 1; i < count - 1; i++)
            {
                uint ip = baseInt + (uint)i;
                result.Add($"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}");
            }
        }
        catch { }
        return result;
    }

    private static string GetArpMac(string ip)
    {
        try
        {
            var mac = ArpSpoofService.ResolveArpMac(ip);
            return mac?.ToString() ?? "—";
        }
        catch { return "—"; }
    }

    private static async Task<string> ScanCommonPortsAsync(string ip, int timeout)
    {
        var open  = new ConcurrentBag<int>();
        var tasks = CommonPorts.Select(async port =>
        {
            try
            {
                using var tcp = new TcpClient();
                var conn = tcp.ConnectAsync(ip, port);
                if (await Task.WhenAny(conn, Task.Delay(timeout)) == conn && !conn.IsFaulted)
                    open.Add(port);
            }
            catch { }
        });
        await Task.WhenAll(tasks);
        return open.Any() ? string.Join(", ", open.OrderBy(p => p)) : "—";
    }

    private async Task EnrichWithSsdpAsync(CancellationToken ct)
    {
        var discoveries = await LocalNetworkScannerService.DiscoverSsdpAsync(ct);
        foreach (var d in discoveries.Values)
        {
            // Find existing device or create new one
            var dev = _devices.FirstOrDefault(x =>
                string.Equals(x.Ip, d.IpAddress, StringComparison.OrdinalIgnoreCase));

            if (dev == null)
            {
                dev = new LanDevice
                {
                    Ip             = d.IpAddress,
                    Hostname       = d.FriendlyName ?? "UPnP device",
                    Mac            = GetArpMac(d.IpAddress),
                    PingMs         = "SSDP",
                    PingRaw        = -1,
                    OpenPorts      = "—",
                    IsBlocked      = LocalNetworkScannerService.IsDeviceBlocked(d.IpAddress),
                    IsInternetCut  = _arpSpoof.IsActive(d.IpAddress),
                    DiscoverySource = "UPnP / SSDP"
                };
                DispatcherQueue.TryEnqueue(() => _devices.Add(dev));
            }

            if (!string.IsNullOrWhiteSpace(d.FriendlyName))  dev.Hostname = d.FriendlyName;
            if (!string.IsNullOrWhiteSpace(d.Manufacturer))  dev.Vendor   = d.Manufacturer;
            if (!string.IsNullOrWhiteSpace(d.ModelName))     dev.Model    = d.ModelName;
            if (!string.IsNullOrWhiteSpace(d.DeviceType))    dev.DeviceType = d.DeviceType;
            dev.DiscoverySource = "UPnP / SSDP";
        }
        DispatcherQueue.TryEnqueue(ApplyDeviceFilter);
    }

    // ── Filtering ─────────────────────────────────────────────────────────────
    private void OnDeviceSearchChanged(object sender, TextChangedEventArgs e) => ApplyDeviceFilter();
    private void OnBlockedOnlyToggled(object sender, RoutedEventArgs e)       => ApplyDeviceFilter();

    private void ApplyDeviceFilter()
    {
        if (DeviceSearchBox == null || BlockedOnlyToggle == null) return;
        string q    = DeviceSearchBox.Text.Trim();
        bool blocked = BlockedOnlyToggle.IsOn;

        var visible = _devices.Where(d =>
            (!blocked || d.IsInternetCut || d.IsBlocked) &&
            (string.IsNullOrWhiteSpace(q) ||
             d.Ip.Contains(q, StringComparison.OrdinalIgnoreCase)       ||
             d.Hostname.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             d.Mac.Contains(q, StringComparison.OrdinalIgnoreCase)      ||
             d.Vendor.Contains(q, StringComparison.OrdinalIgnoreCase)   ||
             d.DeviceType.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(d =>
            {
                var parts = d.Ip.Split('.');
                return parts.Length == 4
                    ? int.Parse(parts[2]) * 1000 + int.Parse(parts[3])
                    : 0;
            })
            .ToList();

        _filteredDevices.Clear();
        foreach (var d in visible) _filteredDevices.Add(d);
    }

    // ── Internet cut-off (ARP spoof) ─────────────────────────────────────────
    private async void OnInternetToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LanDevice dev }) return;
        if (dev.IsInternetCut) { DoRestoreInternet(dev); return; }
        await DoCutInternetAsync(dev);
    }

    private async void OnCutInternetDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await DoCutInternetAsync(dev);
    }
    private void OnRestoreInternetDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) DoRestoreInternet(dev);
    }

    private async void OnCutInternetSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(d => !d.IsInternetCut).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select devices that are currently connected."; return; }
        foreach (var dev in targets) await DoCutInternetAsync(dev);
    }

    private void OnRestoreInternetSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(d => d.IsInternetCut).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select devices that are cut off."; return; }
        foreach (var dev in targets) DoRestoreInternet(dev);
    }

    private void OnStopAllArpSpoof(object sender, RoutedEventArgs e)
    {
        _arpSpoof.StopAll();
        foreach (var d in _devices.Where(d => d.IsInternetCut))
            d.IsInternetCut = false;
        UpdateArpBanner();
        ScanStatusText.Text = "Restored internet for all devices.";
    }

    private async Task DoCutInternetAsync(LanDevice dev)
    {
        if (dev.Mac == "—" || string.IsNullOrWhiteSpace(dev.Mac))
        {
            // Try resolving MAC first
            dev.Mac = await Task.Run(() => GetArpMac(dev.Ip));
            if (dev.Mac == "—")
            {
                await ShowError("Cannot cut internet",
                    $"MAC address for {dev.Ip} could not be resolved. " +
                    "Try scanning the device first so it appears in the ARP cache.");
                return;
            }
        }

        if (!EnsureArpInit() || !_arpSpoof.IsAvailable)
        {
            await ShowError("ARP Spoof Unavailable", _arpSpoof.InitError +
                "\n\nInstall Npcap from https://npcap.com/ and restart the app.");
            return;
        }

        // Warn on first use
        var confirm = new ContentDialog
        {
            Title   = "Cut internet — ARP spoofing",
            Content = new StackPanel { Spacing = 8, Children =
            {
                new TextBlock
                {
                    Text = $"This will cut internet access for {dev.Ip} by sending forged ARP " +
                           "packets to both the device and the gateway.\n\n" +
                           "• Only use on networks you own or have permission to control.\n" +
                           "• The device will still be able to reach other LAN devices.\n" +
                           "• Internet is restored instantly when you click Restore.",
                    TextWrapping = TextWrapping.Wrap
                }
            }},
            PrimaryButtonText = "Cut Internet",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        bool ok = _arpSpoof.StartSpoofing(dev.Ip, dev.Mac);
        if (ok)
        {
            dev.IsInternetCut = true;
            UpdateArpBanner();
            ScanStatusText.Text = $"Internet cut for {dev.Ip} (ARP spoof active).";
            HistoryLogService.AddLog("LAN Scanner", "ARP Spoof", $"Cut internet: {dev.Ip}");
        }
        else
        {
            ScanStatusText.Text = "Failed to start ARP spoofing — check status bar.";
        }
    }

    private void DoRestoreInternet(LanDevice dev)
    {
        _arpSpoof.StopSpoofing(dev.Ip, dev.Mac);
        dev.IsInternetCut = false;
        UpdateArpBanner();
        ScanStatusText.Text = $"Internet restored for {dev.Ip}.";
        HistoryLogService.AddLog("LAN Scanner", "ARP Spoof", $"Restored internet: {dev.Ip}");
    }

    // ── Firewall block (this PC) ──────────────────────────────────────────────
    private async void OnDeviceAccessToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LanDevice dev }) return;
        bool block = !dev.IsBlocked;
        if (block)
        {
            var confirm = new ContentDialog
            {
                Title             = "Block device from this PC?",
                Content           = $"Windows Firewall will block traffic between this PC and {dev.Ip}. " +
                                    "This only affects this PC — other devices can still reach it.",
                PrimaryButtonText = "Block",
                CloseButtonText   = "Cancel",
                XamlRoot          = XamlRoot
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        }
        await SetDeviceAccessAsync(dev, block);
    }

    private async void OnBlockDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await SetDeviceAccessAsync(dev, true);
    }
    private async void OnUnblockDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await SetDeviceAccessAsync(dev, false);
    }

    private async void OnBlockSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(d => !d.IsBlocked).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select unblocked devices first."; return; }
        var confirm = new ContentDialog
        {
            Title             = "Block selected from this PC?",
            Content           = $"Firewall will block traffic to/from {targets.Count} device(s).",
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot          = XamlRoot
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await Task.Run(() => { foreach (var d in targets) LocalNetworkScannerService.BlockDevice(d.Ip); });
        foreach (var d in targets) d.IsBlocked = true;
        ApplyDeviceFilter();
        ScanStatusText.Text = $"Blocked {targets.Count} device(s) from this PC.";
        HistoryLogService.AddLog("LAN Scanner", "Firewall", $"Blocked {targets.Count} device(s)");
    }

    private async void OnUnblockSelected(object sender, RoutedEventArgs e)
    {
        var targets = DeviceList.SelectedItems.Cast<LanDevice>().Where(d => d.IsBlocked).ToList();
        if (targets.Count == 0) { ScanStatusText.Text = "Select blocked devices first."; return; }
        await Task.Run(() => { foreach (var d in targets) LocalNetworkScannerService.UnblockDevice(d.Ip); });
        foreach (var d in targets) d.IsBlocked = false;
        ApplyDeviceFilter();
        ScanStatusText.Text = $"Unblocked {targets.Count} device(s).";
        HistoryLogService.AddLog("LAN Scanner", "Firewall", $"Unblocked {targets.Count} device(s)");
    }

    private async Task SetDeviceAccessAsync(LanDevice dev, bool block)
    {
        ScanStatusText.Text = block ? $"Blocking {dev.Ip}…" : $"Restoring {dev.Ip}…";
        await Task.Run(() =>
        {
            if (block) LocalNetworkScannerService.BlockDevice(dev.Ip);
            else       LocalNetworkScannerService.UnblockDevice(dev.Ip);
        });
        dev.IsBlocked = block;
        ApplyDeviceFilter();
        ScanStatusText.Text = block
            ? $"This PC's access to {dev.Ip} blocked."
            : $"This PC's access to {dev.Ip} restored.";
        HistoryLogService.AddLog("LAN Scanner", "Firewall",
            $"{(block ? "Blocked" : "Unblocked")} this PC ↔ {dev.Ip}");
    }

    // ── Ping ─────────────────────────────────────────────────────────────────
    private async void OnPingSelected(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not LanDevice dev)
        { ScanStatusText.Text = "Select a device first."; return; }
        await PingDeviceAsync(dev);
    }
    private async void OnPingDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await PingDeviceAsync(dev);
    }

    private async Task PingDeviceAsync(LanDevice dev)
    {
        ScanStatusText.Text = $"Pinging {dev.Ip}…";
        try
        {
            using var ping = new Ping();
            var opts  = new PingOptions { DontFragment = true };
            var reply = await ping.SendPingAsync(dev.Ip, (int)TimeoutBox.Value, new byte[32], opts);
            if (reply.Status != IPStatus.Success)
            { ScanStatusText.Text = $"{dev.Ip} did not reply ({reply.Status})."; return; }
            dev.PingRaw = reply.RoundtripTime;
            dev.PingMs  = $"{reply.RoundtripTime} ms";
            if (reply.Options != null && FingerprintOS.IsOn) dev.Ttl = reply.Options.Ttl;
            ScanStatusText.Text = $"{dev.Ip} replied in {reply.RoundtripTime} ms (TTL={reply.Options?.Ttl}).";
        }
        catch (Exception ex) { ScanStatusText.Text = $"Ping error: {ex.Message}"; }
    }

    // ── Traceroute ────────────────────────────────────────────────────────────
    private async void OnTracerouteSelected(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not LanDevice dev)
        { ScanStatusText.Text = "Select a device first."; return; }
        await TracerouteDeviceAsync(dev);
    }
    private async void OnTracerouteDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await TracerouteDeviceAsync(dev);
    }

    private async Task TracerouteDeviceAsync(LanDevice dev)
    {
        ScanStatusText.Text = $"Traceroute to {dev.Ip}…";
        var sb = new StringBuilder();
        await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("tracert", $"-d -w 1000 {dev.Ip}")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi)!;
                sb.Append(proc.StandardOutput.ReadToEnd());
                proc.WaitForExit(30_000);
            }
            catch (Exception ex) { sb.Append($"Error: {ex.Message}"); }
        });

        var dialog = new ContentDialog
        {
            Title          = $"Traceroute → {dev.Ip}",
            Content        = new ScrollViewer
            {
                Content    = new TextBlock
                {
                    Text               = sb.ToString(),
                    FontFamily         = new FontFamily("Consolas"),
                    FontSize           = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping       = TextWrapping.NoWrap
                },
                MaxHeight  = 400,
                HorizontalScrollMode = ScrollMode.Auto
            },
            CloseButtonText = "Close",
            XamlRoot        = XamlRoot
        };
        await dialog.ShowAsync();
        ScanStatusText.Text = $"Traceroute to {dev.Ip} complete.";
    }

    // ── Wake-on-LAN ───────────────────────────────────────────────────────────
    private async void OnWakeOnLanSelected(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not LanDevice dev)
        { ScanStatusText.Text = "Select a device first."; return; }
        await SendWakeOnLanAsync(dev);
    }
    private async void OnWakeOnLanDevice(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) await SendWakeOnLanAsync(dev);
    }

    private async Task SendWakeOnLanAsync(LanDevice dev)
    {
        if (dev.Mac == "—" || string.IsNullOrWhiteSpace(dev.Mac))
        {
            await ShowError("Wake-on-LAN", $"MAC address for {dev.Ip} is unknown. Scan the device first.");
            return;
        }
        try
        {
            string macClean = dev.Mac.Replace(":", "").Replace("-", "");
            if (macClean.Length != 12) throw new InvalidOperationException("Invalid MAC length.");
            byte[] macBytes = new byte[6];
            for (int i = 0; i < 6; i++)
                macBytes[i] = Convert.ToByte(macClean.Substring(i * 2, 2), 16);

            // Build magic packet: 6× 0xFF + 16× MAC
            var magic = new byte[102];
            for (int i = 0; i < 6; i++) magic[i] = 0xFF;
            for (int j = 0; j < 16; j++) macBytes.CopyTo(magic, 6 + j * 6);

            await Task.Run(() =>
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Broadcast, 9));
                // Also send to subnet broadcast port 7
                udp.Send(magic, magic.Length, new IPEndPoint(IPAddress.Broadcast, 7));
            });

            ScanStatusText.Text = $"Wake-on-LAN magic packet sent to {dev.Mac} ({dev.Ip}).";
            HistoryLogService.AddLog("LAN Scanner", "WoL", $"Sent WoL to {dev.Ip} ({dev.Mac})");
        }
        catch (Exception ex)
        {
            await ShowError("Wake-on-LAN failed", ex.Message);
        }
    }

    // ── Device details dialog ─────────────────────────────────────────────────
    private async void OnShowDeviceDetails(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: LanDevice dev }) return;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(DetailLine("IP address",  dev.Ip));
        panel.Children.Add(DetailLine("MAC address", dev.Mac));
        panel.Children.Add(DetailLine("Hostname",    dev.Hostname));
        panel.Children.Add(DetailLine("OS guess",    $"{dev.OsGuess} (TTL={dev.Ttl})"));
        panel.Children.Add(DetailLine("Device type", dev.DeviceType));
        panel.Children.Add(DetailLine("Vendor",      string.IsNullOrWhiteSpace(dev.Vendor)  ? "—" : dev.Vendor));
        panel.Children.Add(DetailLine("Model",       string.IsNullOrWhiteSpace(dev.Model)   ? "—" : dev.Model));
        panel.Children.Add(DetailLine("Discovery",   dev.DiscoverySource));
        panel.Children.Add(DetailLine("Ping",        string.IsNullOrWhiteSpace(dev.PingMs)  ? "—" : dev.PingMs));
        panel.Children.Add(DetailLine("Open ports",  string.IsNullOrWhiteSpace(dev.OpenPorts) ? "—" : dev.OpenPorts));
        panel.Children.Add(DetailLine("This PC",     dev.AccessText));
        panel.Children.Add(DetailLine("Internet",    dev.InternetText));

        var dlg = new ContentDialog
        {
            Title           = dev.Hostname is "" or "—" ? dev.Ip : dev.Hostname,
            Content         = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "Close",
            XamlRoot        = XamlRoot
        };
        await dlg.ShowAsync();
    }

    private static Grid DetailLine(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text       = label,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)),
            FontSize   = 12
        });
        var val = new TextBlock
        {
            Text                   = value,
            TextWrapping           = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize               = 12
        };
        Grid.SetColumn(val, 1);
        grid.Children.Add(val);
        return grid;
    }

    // ── Copy / Export ─────────────────────────────────────────────────────────
    private void OnCopyDeviceIp(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) CopyToClipboard(dev.Ip);
    }
    private void OnCopyDeviceMac(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: LanDevice dev }) CopyToClipboard(dev.Mac);
    }

    private static void CopyToClipboard(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
    }

    private async void OnExportDevices(object sender, RoutedEventArgs e)
    {
        if (_filteredDevices.Count == 0) { ScanStatusText.Text = "No devices to export."; return; }
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"WinNetControl_LAN_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
        var devices = _filteredDevices.ToList();
        await Task.Run(() =>
        {
            static string Csv(string v) => $"\"{v.Replace("\"", "\"\"")}\"";
            var csv = new StringBuilder(
                "IP,Hostname,OS Guess,TTL,Vendor,Model,Discovery,MAC,Ping,Open Ports,This PC,Internet\n");
            foreach (var d in devices)
                csv.AppendLine(string.Join(',',
                    Csv(d.Ip), Csv(d.Hostname), Csv(d.OsGuess), Csv(d.Ttl.ToString()),
                    Csv(d.Vendor), Csv(d.Model), Csv(d.DiscoverySource),
                    Csv(d.Mac), Csv(d.PingMs), Csv(d.OpenPorts),
                    Csv(d.AccessText), Csv(d.InternetText)));
            File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
        });
        ScanStatusText.Text = $"Exported {devices.Count} devices → {path}";
    }

    // ── Utility ───────────────────────────────────────────────────────────────
    private async Task ShowError(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title           = title,
            Content         = message,
            CloseButtonText = "OK",
            XamlRoot        = XamlRoot
        };
        await dlg.ShowAsync();
    }
}
