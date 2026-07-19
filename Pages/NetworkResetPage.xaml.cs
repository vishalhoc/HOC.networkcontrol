using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed partial class NetworkResetPage : Page
{
    public NetworkResetPage() { this.InitializeComponent(); }
    protected override void OnNavigatedTo(NavigationEventArgs e) { base.OnNavigatedTo(e); }

    private async void OnRunReset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        var dlg = new ContentDialog
        {
            Title   = "Confirm Network Reset",
            Content = "This will reset network components. Some operations require a restart. Continue?",
            PrimaryButtonText   = "Yes, Reset",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        ResetProgress.IsIndeterminate = true;
        ResetProgress.Visibility = Visibility.Visible;
        ResetLog.Text = "";

        var steps = new List<(string label, string exe, string args)>
        {
            ("Resetting Winsock catalog…",     "netsh", "winsock reset"),
            ("Resetting TCP/IP stack…",         "netsh", "int ip reset resetlog.txt"),
            ("Resetting IPv6 stack…",           "netsh", "int ipv6 reset"),
            ("Flushing DNS resolver cache…",    "ipconfig", "/flushdns"),
            ("Registering DNS…",                "ipconfig", "/registerdns"),
            ("Releasing DHCP…",                 "ipconfig", "/release"),
            ("Renewing DHCP…",                  "ipconfig", "/renew"),
            ("Resetting firewall policy…",      "netsh", "advfirewall reset"),
        };

        foreach (var (label, exe, args) in steps)
        {
            AppendLog($"▶  {label}");
            try
            {
                await RunAsync(exe, args);
                AppendLog($"   ✅  Done.\n");
            }
            catch (Exception ex) { AppendLog($"   ⚠  {ex.Message}\n"); }
        }

        AppendLog("════════════════════════════════");
        AppendLog("✅  Network stack reset complete.");
        AppendLog("   A system restart is recommended.");
        ResetProgress.IsIndeterminate = false;
        ResetProgress.Visibility = Visibility.Collapsed;
    }

    private void AppendLog(string text)
        => DispatcherQueue.TryEnqueue(() => ResetLog.Text += text + "\n");

    private static Task RunAsync(string exe, string args) => Task.Run(() =>
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            UseShellExecute  = true,
            Verb             = "runas",
            WindowStyle      = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow   = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
    });
}
