using System.Text;
using Microsoft.UI.Xaml.Controls;
using WinNetControl.ViewModels;

namespace WinNetControl;

public sealed partial class InternetResetDialog : ContentDialog
{
    private readonly MainViewModel _viewModel;

    public InternetResetDialog(MainViewModel viewModel)
    {
        this.InitializeComponent();
        _viewModel = viewModel;
    }

    private async void OnRunClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Defer closing so we can await async work
        var deferral = args.GetDeferral();

        var sb = new StringBuilder();
        bool needRestart = false;

        try
        {
            if (ChkFlushDns.IsChecked == true)
            {
                var (ok, out1) = _viewModel.RunFlushDns();
                sb.AppendLine($"[DNS] {(ok ? "✓" : "✗")} {out1.Trim()}");
            }

            if (ChkResetWinsock.IsChecked == true)
            {
                var (ok, out2) = _viewModel.RunResetWinsock();
                sb.AppendLine($"[Winsock] {(ok ? "✓" : "✗")} {out2.Trim()}");
                needRestart = true;
            }

            if (ChkResetTcpIp.IsChecked == true)
            {
                var (ok, out3) = _viewModel.RunResetTcpIp();
                sb.AppendLine($"[TCP/IP] {(ok ? "✓" : "✗")} {out3.Trim()}");
                needRestart = true;
            }

            if (ChkReleaseRenew.IsChecked == true)
            {
                var (ok1, out4a) = _viewModel.RunReleaseIp();
                sb.AppendLine($"[Release] {(ok1 ? "✓" : "✗")} {out4a.Trim()}");
                var (ok2, out4b) = _viewModel.RunRenewIp();
                sb.AppendLine($"[Renew]   {(ok2 ? "✓" : "✗")} {out4b.Trim()}");
            }

            if (ChkFlushArp.IsChecked == true)
            {
                var (ok, out5) = _viewModel.RunFlushArpCache();
                sb.AppendLine($"[ARP] {(ok ? "✓" : "✗")} {out5.Trim()}");
            }

            if (ChkResetFW.IsChecked == true)
            {
                var (ok, out6) = _viewModel.RunResetFirewall();
                sb.AppendLine($"[Firewall] {(ok ? "✓" : "✗")} {out6.Trim()}");
            }

            if (ChkClearRules.IsChecked == true)
            {
                _viewModel.ClearAllWinNetControlRules();
                sb.AppendLine("[WinNetControl Rules] Cleared.");
            }

            if (sb.Length == 0) sb.Append("No operations selected.");
            if (needRestart)    sb.AppendLine("\n⚠ A system restart is required for changes to take effect.");

            OutputLog.Text = sb.ToString();

            // Show the result and wait — don't close yet
            args.Cancel = true; // prevent closing so user sees output
        }
        finally
        {
            deferral.Complete();
        }
    }
}
