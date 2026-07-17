using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinNetControl.Core;

namespace WinNetControl;

public sealed partial class OptimizeDialog : ContentDialog
{
    public OptimizeDialog()
    {
        this.InitializeComponent();
        this.PrimaryButtonClick += OnApplyClicked;
    }

    // ── Apply all checked options ─────────────────────────────────────────────
    private async void OnApplyClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // keep dialog open while applying
        var output = new System.Text.StringBuilder();

        await Task.Run(() =>
        {
            if (TcpAutoTuningCheck.IsChecked == true)
            {
                string level = (TcpAutoTuningLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "normal";
                var (ok, o) = NetworkOptimizeService.SetTcpAutoTuning(level);
                output.AppendLine($"[TCP AutoTuning → {level}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (CongestionCheck.IsChecked == true)
            {
                string prov = (CongestionProvider.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "ctcp";
                var (ok, o) = NetworkOptimizeService.SetCongestionProvider(prov);
                output.AppendLine($"[Congestion → {prov}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (RssCheck.IsChecked == true)
            {
                var (ok, o) = NetworkOptimizeService.EnableRss();
                output.AppendLine($"[RSS Enable]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (ChimneyCheck.IsChecked == true)
            {
                var (ok, o) = NetworkOptimizeService.EnableChimneyOffload();
                output.AppendLine($"[Chimney Enable]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (TimestampsCheck.IsChecked == true)
            {
                var (ok, o) = NetworkOptimizeService.EnableTimestamps();
                output.AppendLine($"[Timestamps Enable]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (EcnCheck.IsChecked == true)
            {
                var (ok, o) = NetworkOptimizeService.EnableEcn();
                output.AppendLine($"[ECN Enable]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (InitialRtoCheck.IsChecked == true)
            {
                int rto = (int)InitialRtoValue.Value;
                var (ok, o) = NetworkOptimizeService.SetInitialRto(rto);
                output.AppendLine($"[Initial RTO → {rto}ms]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (FlushDnsCheck.IsChecked == true)
            {
                var (ok, o) = NetworkOptimizeService.FlushDns();
                output.AppendLine($"[Flush DNS]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
            }
            if (SetDnsCheck.IsChecked == true)
            {
                string primary   = DnsPrimaryBox.Text.Trim();
                string secondary = DnsSecondaryBox.Text.Trim();
                if (!string.IsNullOrEmpty(primary))
                {
                    foreach (var adapter in new[] { "Wi-Fi", "Ethernet" })
                    {
                        var (ok, o) = NetworkOptimizeService.SetDnsServers(adapter, primary, secondary);
                        output.AppendLine($"[Set DNS {adapter} {primary}/{secondary}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
                    }
                }
            }
            if (SetMtuCheck.IsChecked == true)
            {
                string adapterAlias = MtuAdapterBox.Text.Trim();
                int mtu = (int)MtuValueBox.Value;
                if (!string.IsNullOrEmpty(adapterAlias))
                {
                    var (ok, o) = NetworkOptimizeService.SetMtu(adapterAlias, mtu);
                    output.AppendLine($"[MTU {adapterAlias} → {mtu}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
                }
            }
        });

        ShowOutput(output.ToString());
    }

    // ── TCP Query ─────────────────────────────────────────────────────────────
    private async void OnQueryTcp(object sender, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.QueryTcpGlobal);
        ShowOutput("[TCP Global State]\n" + o);
    }

    // ── DNS presets ───────────────────────────────────────────────────────────
    private void OnSetCloudflare(object s, RoutedEventArgs e) { DnsPrimaryBox.Text = "1.1.1.1";  DnsSecondaryBox.Text = "1.0.0.1";          SetDnsCheck.IsChecked = true; }
    private void OnSetGoogle(object s, RoutedEventArgs e)     { DnsPrimaryBox.Text = "8.8.8.8";  DnsSecondaryBox.Text = "8.8.4.4";          SetDnsCheck.IsChecked = true; }
    private void OnSetQuad9(object s, RoutedEventArgs e)      { DnsPrimaryBox.Text = "9.9.9.9";  DnsSecondaryBox.Text = "149.112.112.112";  SetDnsCheck.IsChecked = true; }

    // ── MTU query ─────────────────────────────────────────────────────────────
    private async void OnShowMtu(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.ShowMtu);
        ShowOutput("[MTU / Subinterfaces]\n" + o);
    }

    // ── Kill Switch ───────────────────────────────────────────────────────────
    private async void OnKillSwitchOn(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.EnableKillSwitch);
        ShowOutput(o);
    }

    private async void OnKillSwitchOff(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.DisableKillSwitch);
        ShowOutput(o);
    }

    // ── Port block ────────────────────────────────────────────────────────────
    private async void OnBlockPort(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(BlockPortBox.Text.Trim(), out int port)) return;
        string dir = (BlockPortDir.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Split(' ')[0] ?? "out";
        var (ok, o) = await Task.Run(() => NetworkOptimizeService.BlockPort(port, "any", dir));
        ShowOutput($"[Block port {port} {dir}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
    }

    private async void OnUnblockPort(object s, RoutedEventArgs e)
    {
        if (!int.TryParse(BlockPortBox.Text.Trim(), out int port)) return;
        string dir = (BlockPortDir.SelectedItem as ComboBoxItem)?.Content?.ToString()?.Split(' ')[0] ?? "out";
        var (ok, o) = await Task.Run(() => NetworkOptimizeService.UnblockPort(port, dir));
        ShowOutput($"[Unblock port {port} {dir}]: {(ok ? "✓ OK" : "✗ FAIL")}\n{o}");
    }

    // ── Rule list ─────────────────────────────────────────────────────────────
    private async void OnListRules(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.ListWinNetControlRules);
        ShowOutput("[WinNetControl Firewall Rules]\n" + (string.IsNullOrWhiteSpace(o) ? "(none found)" : o));
    }

    private async void OnDeleteAllRules(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.DeleteAllWinNetControlRules);
        ShowOutput(o);
    }

    // ── Full Reset ────────────────────────────────────────────────────────────
    private async void OnFullReset(object s, RoutedEventArgs e)
    {
        var (_, o) = await Task.Run(NetworkOptimizeService.ResetAll);
        ShowOutput("[Full Network Reset]\n" + o + "\n⚠ A system restart may be required.");
    }

    private void ShowOutput(string text)
    {
        OutputText.Text = text;
        OutputExpander.IsExpanded = true;
    }
}
