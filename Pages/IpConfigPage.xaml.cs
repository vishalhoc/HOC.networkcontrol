using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace WinNetControl.Pages;

public sealed partial class IpConfigPage : Page
{
    public IpConfigPage() { this.InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RunCommand("ipconfig", "/all");
    }

    private async void OnCommandSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CmdLabel == null) return; // guard: fires during XAML load before InitializeComponent completes
        if (CommandList.SelectedItem is ListViewItem item && item.Tag is string tag)
        {
            var parts = tag.Split(' ', 2);
            string exe  = parts[0];
            string args = parts.Length > 1 ? parts[1] : "";
            CmdLabel.Text = tag;
            await RunCommand(exe, args);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (CommandList.SelectedItem is ListViewItem item && item.Tag is string tag)
        {
            var parts = tag.Split(' ', 2);
            await RunCommand(parts[0], parts.Length > 1 ? parts[1] : "");
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(OutputText.Text);
        Clipboard.SetContent(dp);
    }

    private async Task RunCommand(string exe, string args)
    {
        CmdProgress.Visibility = Visibility.Visible;
        OutputText.Text = "";

        try
        {
            string output = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string o = proc.StandardOutput.ReadToEnd();
                string er = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return o + (er.Length > 0 ? $"\n[stderr]\n{er}" : "");
            });

            OutputText.Text = output;
        }
        catch (Exception ex)
        {
            OutputText.Text = $"Error running '{exe} {args}':\n{ex.Message}";
        }
        finally
        {
            CmdProgress.Visibility = Visibility.Collapsed;
        }
    }
}
