using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

/// <summary>View model for a network adapter list item.</summary>
public class AdapterItem
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public string TypeGlyph   { get; set; } = "\uE839";
    public string Id          { get; set; } = "";
    public NetworkInterfaceType InterfaceType { get; set; }
    public bool   IsUp        { get; set; }

    public SolidColorBrush StatusBrush => new(IsUp
        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
        : Windows.UI.Color.FromArgb(255, 150, 150, 150));

    public SolidColorBrush StatusDotBrush => new(IsUp
        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
        : Windows.UI.Color.FromArgb(255, 200, 50, 50));
}

public sealed partial class NetworkInterfacesPage : Page
{
    private MainViewModel? _vm;
    private readonly ObservableCollection<AdapterItem> _adapters = new();
    private AdapterItem? _selected;

    public NetworkInterfacesPage()
    {
        this.InitializeComponent();
        AdapterListView.ItemsSource = _adapters;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        RefreshAdapters();
    }

    // ── Refresh adapter list ──────────────────────────────────────────────────
    private void OnRefreshAdapters(object sender, RoutedEventArgs e) => RefreshAdapters();

    private void RefreshAdapters()
    {
        _adapters.Clear();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderBy(n => n.OperationalStatus != OperationalStatus.Up)
            .ThenBy(n => n.Name))
        {
            string glyph = ni.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "\uEC3B",
                NetworkInterfaceType.Ethernet      => "\uE839",
                NetworkInterfaceType.Loopback      => "\uE9F5",
                NetworkInterfaceType.Tunnel        => "\uE701",
                _                                  => "\uE839"
            };

            _adapters.Add(new AdapterItem
            {
                Id            = ni.Id,
                Name          = ni.Name,
                Description   = ni.Description,
                IsUp          = ni.OperationalStatus == OperationalStatus.Up,
                StatusLabel   = ni.OperationalStatus.ToString(),
                InterfaceType = ni.NetworkInterfaceType,
                TypeGlyph     = glyph
            });
        }
    }

    // ── Selection changed ─────────────────────────────────────────────────────
    private void OnAdapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterListView.SelectedItem is not AdapterItem item) return;
        _selected = item;

        AdapterNameText.Text = item.Name;
        AdapterDescText.Text = item.Description;

        EnableBtn.IsEnabled  = !item.IsUp;
        DisableBtn.IsEnabled = item.IsUp;
        RestartBtn.IsEnabled = true;
        RenewDhcpBtn.IsEnabled = item.IsUp;
        SetMtuBtn.IsEnabled    = true;

        // Load IP details
        LoadAdapterDetails(item);
    }

    private void LoadAdapterDetails(AdapterItem item)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id == item.Id);
            if (ni == null) return;

            var props = ni.GetIPProperties();

            string ipv4 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "—";

            string subnet = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.IPv4Mask.ToString() ?? "—";

            string ipv6 = props.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                ?.Address.ToString() ?? "—";

            string gw = props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "—";

            string mac = string.Join(":",
                ni.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
            if (mac == "") mac = "—";

            string dns = string.Join("  |  ",
                props.DnsAddresses.Select(a => a.ToString()));
            if (dns == "") dns = "—";

            string speed = ni.Speed > 0
                ? (ni.Speed >= 1_000_000_000
                    ? $"{ni.Speed / 1_000_000_000} Gbps"
                    : $"{ni.Speed / 1_000_000} Mbps")
                : "—";

            InfoIPv4.Text    = ipv4;
            InfoSubnet.Text  = subnet;
            InfoIPv6.Text    = ipv6.Length > 40 ? ipv6[..40] + "…" : ipv6;
            InfoGateway.Text = gw;
            InfoMac.Text     = mac;
            InfoDns.Text     = dns;
            InfoSpeed.Text   = speed;
            InfoType.Text    = ni.NetworkInterfaceType.ToString();

            // MTU via netsh (rough read — default 1500 for Ethernet)
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    "netsh", $"interface ipv4 show subinterface \"{item.Name}\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                var proc = System.Diagnostics.Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var match = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\s+\d+\s+\d+");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int mtu))
                {
                    InfoMtu.Text = $"{mtu}";
                    MtuBox.Value = mtu;
                }
                else { InfoMtu.Text = "1500 (default)"; }
            }
            catch { InfoMtu.Text = "1500"; }
        }
        catch (Exception ex)
        {
            ActionStatus.Text = $"Error: {ex.Message}";
        }
    }

    // ── Enable / Disable / Restart ────────────────────────────────────────────
    private async void OnEnable(object sender, RoutedEventArgs e)
        => await RunNetshAdapterCmd("enable");

    private async void OnDisable(object sender, RoutedEventArgs e)
        => await RunNetshAdapterCmd("disable");

    private async void OnRestart(object sender, RoutedEventArgs e)
    {
        await RunNetshAdapterCmd("disable");
        await Task.Delay(1500);
        await RunNetshAdapterCmd("enable");
    }

    private async Task RunNetshAdapterCmd(string action)
    {
        if (_selected == null) return;
        ActionStatus.Text = $"{action}ing adapter…";
        try
        {
            await RunElevatedAsync("netsh",
                $"interface set interface \"{_selected.Name}\" {action}");
            await Task.Delay(1200);
            RefreshAdapters();
            ActionStatus.Text = $"Adapter {action}d successfully.";
        }
        catch (Exception ex) { ActionStatus.Text = $"Error: {ex.Message}"; }
    }

    // ── DHCP / Static IP ──────────────────────────────────────────────────────
    private void OnIpModeChanged(object sender, RoutedEventArgs e)
    {
        // Guard: Checked fires during InitializeComponent before StaticIpPanel is created
        if (StaticIpPanel == null || StaticRadio == null) return;
        StaticIpPanel.Visibility = StaticRadio.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnApplyStaticIp(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        string ip   = StaticIpBox.Text.Trim();
        string mask = StaticMaskBox.Text.Trim();
        string gw   = StaticGwBox.Text.Trim();
        string dns1 = StaticDns1Box.Text.Trim();
        string dns2 = StaticDns2Box.Text.Trim();

        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(mask)) return;

        ActionStatus.Text = "Applying static IP…";
        try
        {
            await RunElevatedAsync("netsh",
                $"interface ip set address name=\"{_selected.Name}\" static {ip} {mask} {gw}");
            if (!string.IsNullOrEmpty(dns1))
                await RunElevatedAsync("netsh",
                    $"interface ip set dns name=\"{_selected.Name}\" static {dns1}");
            if (!string.IsNullOrEmpty(dns2))
                await RunElevatedAsync("netsh",
                    $"interface ip add dns name=\"{_selected.Name}\" {dns2} index=2");

            ActionStatus.Text = "Static IP applied.";
            LoadAdapterDetails(_selected);
        }
        catch (Exception ex) { ActionStatus.Text = $"Error: {ex.Message}"; }
    }

    private async void OnRenewDhcp(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        ActionStatus.Text = "Renewing DHCP…";
        try
        {
            await RunElevatedAsync("ipconfig", $"/release \"{_selected.Name}\"");
            await Task.Delay(800);
            await RunElevatedAsync("ipconfig", $"/renew \"{_selected.Name}\"");
            ActionStatus.Text = "DHCP renewed.";
            LoadAdapterDetails(_selected);
        }
        catch (Exception ex) { ActionStatus.Text = $"Error: {ex.Message}"; }
    }

    // ── MTU ───────────────────────────────────────────────────────────────────
    private void OnMtuPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val && int.TryParse(val, out int mtu))
            MtuBox.Value = mtu;
    }

    private async void OnSetMtu(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        int mtu = (int)MtuBox.Value;
        ActionStatus.Text = $"Setting MTU to {mtu}…";
        try
        {
            await RunElevatedAsync("netsh",
                $"interface ipv4 set subinterface \"{_selected.Name}\" mtu={mtu} store=persistent");
            ActionStatus.Text = $"MTU set to {mtu}.";
            InfoMtu.Text = $"{mtu}";
        }
        catch (Exception ex) { ActionStatus.Text = $"Error: {ex.Message}"; }
    }

    // ── Quick actions ─────────────────────────────────────────────────────────
    private async void OnFlushDns(object sender, RoutedEventArgs e)
    {
        QuickActionStatus.Text = "Flushing DNS…";
        await RunElevatedAsync("ipconfig", "/flushdns");
        QuickActionStatus.Text = "DNS cache flushed.";
    }

    private async void OnReleaseRenew(object sender, RoutedEventArgs e)
    {
        QuickActionStatus.Text = "Releasing IP…";
        await RunElevatedAsync("ipconfig", "/release");
        await Task.Delay(1000);
        QuickActionStatus.Text = "Renewing IP…";
        await RunElevatedAsync("ipconfig", "/renew");
        QuickActionStatus.Text = "Release + Renew complete.";
    }

    private async void OnResetWinsock(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title            = "Reset Winsock?",
            Content          = "This will reset Winsock and require a restart. Continue?",
            PrimaryButtonText   = "Reset",
            SecondaryButtonText = "Cancel",
            XamlRoot         = this.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        QuickActionStatus.Text = "Resetting Winsock…";
        await RunElevatedAsync("netsh", "winsock reset");
        QuickActionStatus.Text = "Winsock reset. Please restart your PC.";
    }

    // ── Shell helper ──────────────────────────────────────────────────────────
    private static Task RunElevatedAsync(string exe, string args)
    {
        return Task.Run(() =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                Verb              = "runas",
                UseShellExecute   = true,
                CreateNoWindow    = true,
                WindowStyle       = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
        });
    }
}
