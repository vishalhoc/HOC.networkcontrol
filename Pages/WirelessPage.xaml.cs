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

        // Get IP from adapter
        string ip = "—";
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 &&
                    ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    var addr = ni.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (addr != null) ip = addr.Address.ToString();
                    break;
                }
            }
        }
        catch { }

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
