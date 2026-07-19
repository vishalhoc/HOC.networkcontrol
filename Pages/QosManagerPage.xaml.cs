using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.ViewModels;
using System;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed partial class QosManagerPage : Page
{
    public QosManagerPage() { this.InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshPoliciesAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    private void OnRefreshPolicies(object sender, RoutedEventArgs e)
        => _ = RefreshPoliciesAsync();

    private async Task RefreshPoliciesAsync()
    {
        PolicyOutput.Text = "Loading…";
        // netsh qos show policy lists user-space QoS policies
        string output = await RunAsync("netsh", "qos show policy");
        if (string.IsNullOrWhiteSpace(output) || output.Contains("not recognized", StringComparison.OrdinalIgnoreCase))
        {
            // Fallback: show via PowerShell Get-NetQosPolicy
            output = await RunAsync("powershell",
                "-NoProfile -NonInteractive -Command \"Get-NetQosPolicy | Format-List\"");
        }
        PolicyOutput.Text = string.IsNullOrWhiteSpace(output) ? "No QoS policies found or not supported." : output;
    }

    // ── Create policy ─────────────────────────────────────────────────────────
    private async void OnCreatePolicy(object sender, RoutedEventArgs e)
    {
        string name   = PolicyName.Text.Trim();
        string app    = PolicyApp.Text.Trim();
        int    dscp   = int.Parse(((ComboBoxItem)PriorityCombo.SelectedItem).Tag?.ToString() ?? "0");
        int    kbps   = (int)ThrottleKbps.Value;
        string destIp = PolicyDestIp.Text.Trim();
        int    port   = (int)PolicyPort.Value;

        if (string.IsNullOrEmpty(name)) { PolicyStatus.Text = "Enter a policy name."; return; }

        // Build PowerShell New-NetQosPolicy command
        string cmd = $"New-NetQosPolicy -Name '{name}' -DSCPAction {dscp}";
        if (!string.IsNullOrEmpty(app))    cmd += $" -AppPathNameMatchCondition '{app}'";
        if (!string.IsNullOrEmpty(destIp)) cmd += $" -IPDstPrefixMatchCondition '{destIp}'";
        if (port > 0)                      cmd += $" -IPPortMatchCondition {port}";
        if (kbps > 0)                      cmd += $" -ThrottleRateActionBitsPerSecond {kbps * 1000}";
        cmd += " -PolicyStore ActiveStore";

        PolicyStatus.Text = "Creating policy…";
        string result = await RunElevatedPsAsync(cmd);
        PolicyStatus.Text = result.Length > 0 ? result.Trim() : $"Policy '{name}' created (DSCP {dscp}).";
        await RefreshPoliciesAsync();
    }

    // ── Remove policy ─────────────────────────────────────────────────────────
    private async void OnRemovePolicy(object sender, RoutedEventArgs e)
    {
        string name = RemovePolicyName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { PolicyStatus.Text = "Enter a policy name to remove."; return; }

        PolicyStatus.Text = "Removing…";
        string result = await RunElevatedPsAsync($"Remove-NetQosPolicy -Name '{name}' -Confirm:$false");
        PolicyStatus.Text = result.Length > 0 ? result.Trim() : $"Policy '{name}' removed.";
        await RefreshPoliciesAsync();
    }

    // ── Presets ───────────────────────────────────────────────────────────────
    private async void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        PolicyStatus.Text = $"Applying preset: {tag}…";

        string cmd = tag switch
        {
            "gaming"     => "New-NetQosPolicy -Name 'WNC_Gaming'     -DSCPAction 46 -NetworkProfile All -PolicyStore ActiveStore",
            "video"      => "New-NetQosPolicy -Name 'WNC_Video'      -DSCPAction 40 -NetworkProfile All -PolicyStore ActiveStore",
            "voip"       => "New-NetQosPolicy -Name 'WNC_VoIP'       -DSCPAction 46 -NetworkProfile All -PolicyStore ActiveStore",
            "background" => "New-NetQosPolicy -Name 'WNC_Background' -DSCPAction 8  -NetworkProfile All -PolicyStore ActiveStore",
            _            => ""
        };

        if (cmd.Length == 0) return;
        string result = await RunElevatedPsAsync(cmd);
        PolicyStatus.Text = result.Trim().Length > 0 ? result.Trim() : $"Preset '{tag}' applied.";
        await RefreshPoliciesAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Task<string> RunAsync(string exe, string args) => Task.Run(() =>
    {
        try
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
            proc.WaitForExit();
            return o;
        }
        catch (Exception ex) { return ex.Message; }
    });

    private static Task<string> RunElevatedPsAsync(string cmd) => Task.Run(() =>
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "powershell",
                $"-NoProfile -NonInteractive -Command \"{cmd.Replace("\"", "\\\"")}\"")
            {
                Verb                   = "runas",
                UseShellExecute        = true,
                WindowStyle            = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = false
            };
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    });
}
