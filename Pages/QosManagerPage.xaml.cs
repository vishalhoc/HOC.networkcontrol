using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public sealed class QosPolicyRow
{
    public string Name        { get; init; } = "";
    public string Lifetime    { get; set;  } = "";
    public string Application { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Dscp        { get; set;  } = "";
    public string Limit       { get; init; } = "";
    public DateTime? ExpiresAt { get; set; }

    public string ExpiresText => ExpiresAt.HasValue
        ? (ExpiresAt.Value > DateTime.Now
            ? ExpiresAt.Value.ToString("HH:mm:ss")
            : "Expired")
        : "—";

    public SolidColorBrush ExiresBrush => ExpiresAt.HasValue
        ? new SolidColorBrush(Microsoft.UI.Colors.Goldenrod)
        : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    public SolidColorBrush LifetimeBrush => Lifetime switch
    {
        "Session only" => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
        "Persistent"   => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray)
    };
}

public sealed partial class QosManagerPage : Page
{
    private readonly ObservableCollection<QosPolicyRow> _policies = new();
    // In-memory timers for auto-expire: policyName → CancellationTokenSource
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _timers = new();

    public QosManagerPage()
    {
        this.InitializeComponent();
        PolicyList.ItemsSource = _policies;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = RefreshPoliciesAsync();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────
    private void OnRefreshPolicies(object sender, RoutedEventArgs e)
        => _ = RefreshPoliciesAsync(requireAdministrator: true);

    private async void OnChooseApplication(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        PolicyApp.Text = file.Path;
        if (string.IsNullOrWhiteSpace(PolicyName.Text))
            PolicyName.Text = QosPolicyService.BuildPolicyName(System.IO.Path.GetFileName(file.Path));
        PolicyStatus.Text = "Application selected. Pick a priority, then click Apply priority.";
    }

    private async Task RefreshPoliciesAsync(bool requireAdministrator = false)
    {
        PolicyOutput.Text = "Loading Windows QoS policies…";
        PolicyListStatus.Text = requireAdministrator ? "Requesting administrator access…" : "";
        NoPoliciesText.Text = "No effective QoS policies found.";
        const string query = "& { $ErrorActionPreference='Stop'; $local=@(Get-NetQosPolicy -PolicyStore localhost -ErrorAction SilentlyContinue | ForEach-Object Name); $active=@(Get-NetQosPolicy -PolicyStore ActiveStore); $active | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Lifetime=$(if($local -contains $_.Name){'Persistent'}elseif($_.Name -like 'WNC_QoS_*'){'Session only'}else{'Effective / managed'}); Application=$(if($_.AppPathName){$_.AppPathName}else{'All traffic'}); Destination=$(if($_.IPDstPrefix){$_.IPDstPrefix + $(if($_.IPDstPort){':' + $_.IPDstPort}else{''})}else{'Any'}); Dscp=$(if($null -ne $_.DSCPValue){[string]$_.DSCPValue}else{'—'}); Limit=$(if($_.ThrottleRateAction){[string]$_.ThrottleRateAction}else{'Unlimited'}) } } | ConvertTo-Json -Compress }";
        string output = requireAdministrator
            ? await RunElevatedPsWithOutputAsync(query)
            : await RunAsync("powershell", $"-NoProfile -NonInteractive -Command \"{query.Replace("\"", "\\\"")}\"");

        PolicyOutput.Text = output;
        _policies.Clear();
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in document.RootElement.EnumerateArray()) AddPolicyRow(entry);
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object) AddPolicyRow(document.RootElement);
        }
        catch { /* The technical details expander retains the PowerShell error text. */ }

        NoPoliciesText.Visibility = _policies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_policies.Count > 0)
            PolicyListStatus.Text = $"{_policies.Count} effective policy(s)";
        else if (output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) && !requireAdministrator)
        {
            NoPoliciesText.Text = "Administrator access is required to list active QoS policies. Click Refresh and approve the prompt.";
            PolicyListStatus.Text = "Refresh requires admin";
        }
        else
            PolicyListStatus.Text = "No policies found";
    }

    private static string JsonText(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.ToString() : "—";

    private void ShowCreatedPolicy(string name, string app, string destinationIp, int port, int dscp, int kbps,
        DateTime? expiresAt = null, string lifetime = "Session only")
    {
        RemoveShownPolicy(name, updateEmptyState: false);
        _policies.Add(new QosPolicyRow
        {
            Name = name,
            Lifetime = lifetime,
            Application = string.IsNullOrWhiteSpace(app) ? "All traffic" : app,
            Destination = string.IsNullOrWhiteSpace(destinationIp)
                ? "Any"
                : destinationIp + (port > 0 ? $":{port}" : ""),
            Dscp = dscp.ToString(),
            Limit = kbps > 0 ? $"{kbps:N0} Kbps" : "Unlimited",
            ExpiresAt = expiresAt
        });
        NoPoliciesText.Visibility = Visibility.Collapsed;
        PolicyListStatus.Text = $"{_policies.Count} effective policy(s)";
        PolicyOutput.Text = "This policy was just created. Click Refresh and approve the prompt to retrieve the complete effective Windows QoS policy list.";
    }

    private void RemoveShownPolicy(string name, bool updateEmptyState = true)
    {
        var shown = _policies.FirstOrDefault(policy => string.Equals(policy.Name, name, StringComparison.OrdinalIgnoreCase));
        if (shown != null) _policies.Remove(shown);
        if (!updateEmptyState) return;

        NoPoliciesText.Visibility = _policies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PolicyListStatus.Text = _policies.Count > 0 ? $"{_policies.Count} effective policy(s)" : "No policies found";
    }

    private void AddPolicyRow(JsonElement entry)
    {
        _policies.Add(new QosPolicyRow
        {
            Name = JsonText(entry, "Name"),
            Lifetime = JsonText(entry, "Lifetime"),
            Application = JsonText(entry, "Application"),
            Destination = JsonText(entry, "Destination"),
            Dscp = JsonText(entry, "Dscp"),
            Limit = JsonText(entry, "Limit")
        });
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
        bool   persist = MakePersistent.IsOn;
        int    expiry  = (int)ExpiryMinutes.Value;

        bool allTraffic = ApplyAllTraffic.IsOn;
        if (string.IsNullOrEmpty(app) && !allTraffic && string.IsNullOrEmpty(destIp) && port == 0)
        {
            PolicyStatus.Text = "Choose an application executable path, a destination IP/port, or enable 'Apply to all traffic'.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(app) && (!string.IsNullOrWhiteSpace(destIp) || port > 0))
        {
            PolicyStatus.Text = "Choose either an app policy or a destination-only policy. Windows QoS cannot combine an app with an IP or port filter.";
            return;
        }

        PolicyStatus.Text = "Applying Windows QoS policy…";
        var result = await QosPolicyService.SetPriorityAsync(
            app,
            string.IsNullOrWhiteSpace(app) ? "all traffic" : System.IO.Path.GetFileName(app),
            dscp, destIp, port, kbps, name, allTraffic);

        PolicyStatus.Text = result.Success ? $"✓ {result.Message}" : $"Policy was not applied: {result.Message}";
        if (result.Success)
        {
            string effectiveName = string.IsNullOrWhiteSpace(name)
                ? QosPolicyService.BuildPolicyName(
                    string.IsNullOrWhiteSpace(app) ? "all traffic" : System.IO.Path.GetFileName(app), destIp, port)
                : name;

            // If persistent — also write to localhost store
            if (persist)
            {
                string cmd = $"$n={Ps(effectiveName)}; " +
                    $"Get-NetQosPolicy -Name $n -PolicyStore localhost -ErrorAction SilentlyContinue | " +
                    $"Remove-NetQosPolicy -PolicyStore localhost -Confirm:$false; " +
                    $"Get-NetQosPolicy -Name $n -PolicyStore ActiveStore -ErrorAction SilentlyContinue | " +
                    $"Copy-NetQosPolicy -PolicyStore localhost -Confirm:$false -ErrorAction Stop";
                var (persOk, persErr) = await ElevatedRunner.RunPowerShellWithErrorAsync(cmd);
                if (!persOk)
                    PolicyStatus.Text += $" (persistent save failed: {persErr})";
            }

            // Start auto-expire timer if requested
            if (expiry > 0)
                StartExpiryTimer(effectiveName, TimeSpan.FromMinutes(expiry));

            ShowCreatedPolicy(effectiveName, app, destIp, port, dscp, kbps,
                expiry > 0 ? DateTime.Now.AddMinutes(expiry) : (DateTime?)null,
                persist ? "Persistent" : "Session only");
        }
    }

    // ── Edit policy DSCP ────────────────────────────────────────────────
    private async void OnEditPolicyRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QosPolicyRow policy }) return;

        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        int[] dscpValues = { 0, 8, 16, 24, 32, 40, 46, 48 };
        string[] dscpLabels = { "0 — Best effort", "8 — Low", "16 — Standard",
            "24 — Video", "32 — Interactive", "40 — Streaming", "46 — High", "48 — Network control" };
        int selectedIdx = 0;
        for (int i = 0; i < dscpValues.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = dscpLabels[i], Tag = dscpValues[i] });
            if (dscpValues[i].ToString() == policy.Dscp) selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;

        var expiryBox = new NumberBox
        {
            Header = "New auto-expire (minutes, 0 = keep current)",
            Value = 0, Minimum = 0, Maximum = 10080,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"Editing: {policy.Name}", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(combo);
        panel.Children.Add(expiryBox);

        var dlg = new ContentDialog
        {
            Title = "Edit QoS Policy",
            Content = panel,
            PrimaryButtonText   = "Apply",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (combo.SelectedItem is not ComboBoxItem { Tag: int newDscp }) return;

        PolicyStatus.Text = $"Updating '{policy.Name}' to DSCP {newDscp}…";
        // Re-apply the policy with the new DSCP by calling SetPriority with the same name
        bool isAppPolicy  = policy.Application != "All traffic" && !policy.Application.Contains('\\') == false;
        bool hasDestIp    = policy.Destination != "Any";
        string destIp     = "";
        int destPort      = 0;
        if (hasDestIp)
        {
            var parts = policy.Destination.Split(':');
            destIp = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out int p)) destPort = p;
        }

        var result = await QosPolicyService.SetPriorityAsync(
            processPath: policy.Application == "All traffic" ? "" : policy.Application,
            processName: policy.Name,
            dscp: newDscp,
            destinationIp: hasDestIp ? destIp : null,
            destinationPort: destPort,
            policyName: policy.Name,
            allowAllTraffic: !isAppPolicy && !hasDestIp);

        if (result.Success)
        {
            policy.Dscp = newDscp.ToString();
            PolicyStatus.Text = $"✓ '{policy.Name}' updated to DSCP {newDscp} ({QosPolicyService.PriorityLabel(newDscp)}).";
            // Update expiry timer if requested
            int newExpiry = (int)expiryBox.Value;
            if (newExpiry > 0) StartExpiryTimer(policy.Name, TimeSpan.FromMinutes(newExpiry));
        }
        else
        {
            PolicyStatus.Text = $"Edit failed: {result.Message}";
        }
    }

    // ── Remove policy ─────────────────────────────────────────────────────────────
    private async void OnRemovePolicy(object sender, RoutedEventArgs e)
    {
        string name = RemovePolicyName.Text.Trim();
        if (string.IsNullOrEmpty(name)) { PolicyStatus.Text = "Enter a policy name to remove."; return; }

        PolicyStatus.Text = "Removing…";
        var result = await QosPolicyService.RemovePolicyAsync(name);
        PolicyStatus.Text = result.Success ? $"Policy '{name}' removed." : $"Policy was not removed: {result.Message}";
        if (result.Success) RemoveShownPolicy(name);
    }

    private async void OnRemovePolicyRow(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QosPolicyRow policy }) return;
        RemovePolicyName.Text = policy.Name;
        PolicyStatus.Text = $"Removing '{policy.Name}'…";
        var result = await QosPolicyService.RemovePolicyAsync(policy.Name);
        PolicyStatus.Text = result.Success ? $"Policy '{policy.Name}' removed." : $"Policy was not removed: {result.Message}";
        if (result.Success) RemoveShownPolicy(policy.Name);
    }

    // ── Presets ───────────────────────────────────────────────────────────────
    private async void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        int dscp = tag switch
        {
            "gaming"     => QosPolicyService.HighPriorityDscp,
            "video"      => 40,
            "voip"       => QosPolicyService.HighPriorityDscp,
            "background" => QosPolicyService.LowPriorityDscp,
            _            => -1
        };
        if (dscp < 0) return;

        foreach (var item in PriorityCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == dscp.ToString())
            {
                PriorityCombo.SelectedItem = item;
                break;
            }
        }
        PolicyName.Text = "";
        PolicyStatus.Text = $"{QosPolicyService.PriorityLabel(dscp)} preset selected. Choose an application, then click Apply priority.";
    }


    // ── Helpers ───────────────────────────────────────────────────────────────
    private static Task<string> RunAsync(string exe, string args)
        => Task.Run(() =>
        {
            var (_, output) = ElevatedRunner.RunNetsh(args.StartsWith("advfirewall") ? args : args);
            // For generic commands (not netsh) fall back to direct run
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
                string o   = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                return string.IsNullOrWhiteSpace(o) ? err : string.IsNullOrWhiteSpace(err) ? o : $"{o}\n{err}";
            }
            catch (Exception ex) { return ex.Message; }
        });

    private static async Task<string> RunElevatedPsWithOutputAsync(string command)
    {
        var (ok, output) = await ElevatedRunner.RunPowerShellAsync(command);
        return output;
    }

    private void StartExpiryTimer(string policyName, TimeSpan delay)
    {
        if (_timers.TryRemove(policyName, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _timers[policyName] = cts;

        // Update the shown row's ExpiresAt
        var row = _policies.FirstOrDefault(p =>
            string.Equals(p.Name, policyName, StringComparison.OrdinalIgnoreCase));
        if (row != null) row.ExpiresAt = DateTime.Now.Add(delay);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                await QosPolicyService.RemovePolicyAsync(policyName);
                _timers.TryRemove(policyName, out _);
                DispatcherQueue.TryEnqueue(() =>
                {
                    RemoveShownPolicy(policyName);
                    PolicyStatus.Text = $"⏰ QoS policy '{policyName}' expired and was removed automatically.";
                });
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);
    }

    private static string Ps(string value) => $"'{value.Replace("'", "''")}'";
}
