using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

public class FirewallRuleItem
{
    public string Name      { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Action    { get; set; } = "";
    public Microsoft.UI.Xaml.Media.SolidColorBrush ActionBrush => Action.Equals("Allow", StringComparison.OrdinalIgnoreCase) 
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
    public string RuleType => Name.Contains("Conn", StringComparison.OrdinalIgnoreCase)
        ? "Connection Block"
        : Name.Contains("Port", StringComparison.OrdinalIgnoreCase)
            ? "Port Block"
            : "App Block";
    public Microsoft.UI.Xaml.Media.SolidColorBrush TypeBrush => RuleType switch
    {
        "Connection Block" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.IndianRed),
        "Port Block" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SteelBlue)
    };
}

public sealed partial class FirewallPage : Page
{
    private MainViewModel? _vm;
    private readonly ObservableCollection<FirewallRuleItem> _rules    = new();
    private readonly ObservableCollection<FirewallRuleItem> _filtered = new();
    private readonly DispatcherTimer _syncDebounce = new() { Interval = TimeSpan.FromSeconds(1) };

    public FirewallPage()
    {
        this.InitializeComponent();
        RulesList.ItemsSource = _filtered;
        _syncDebounce.Tick += (_, _) => { _syncDebounce.Stop(); _ = LoadRulesAsync(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        BlockedConnectionStore.ConnectionBlockChanged += OnConnectionBlockChanged;
        _ = LoadRulesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        BlockedConnectionStore.ConnectionBlockChanged -= OnConnectionBlockChanged;
        _syncDebounce.Stop();
    }

    private void OnConnectionBlockChanged(string _, string __, int ___, int ____, bool _____)
        => DispatcherQueue.TryEnqueue(() => { _syncDebounce.Stop(); _syncDebounce.Start(); });

    // ── Load WNC rules via netsh ──────────────────────────────────────────────
    private async Task LoadRulesAsync()
    {
        RulesProgress.Visibility = Visibility.Visible;
        RulesStatusBar.Text = "Loading rules…";

        try
        {
            var items = await Task.Run(() =>
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    "netsh", "advfirewall firewall show rule name=all")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var proc = System.Diagnostics.Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                // Parse blocks — only keep WinNetControl_ rules
                var result = new System.Collections.Generic.List<FirewallRuleItem>();
                var blocks = output.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var block in blocks)
                {
                    string name = Regex.Match(block, @"Rule Name:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    if (!name.StartsWith("WinNetControl", StringComparison.OrdinalIgnoreCase)) continue;

                    string dir    = Regex.Match(block, @"Direction:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                    string action = Regex.Match(block, @"Action:\s*(.+)", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

                    result.Add(new FirewallRuleItem { Name = name, Direction = dir, Action = action });
                }
                return result;
            });

            _rules.Clear();
            foreach (var r in items) _rules.Add(r);
            ApplySearch(RuleSearchBox.Text);
            RulesStatusBar.Text = $"{_rules.Count} WinNetControl rules found";
        }
        catch (Exception ex) { RulesStatusBar.Text = $"Error: {ex.Message}"; }
        finally { RulesProgress.Visibility = Visibility.Collapsed; }
    }

    private void ApplySearch(string query)
    {
        _filtered.Clear();
        var q = query.Trim().ToLowerInvariant();
        var ordered = RuleSortBox?.SelectedItem is ComboBoxItem { Tag: "type" }
            ? _rules.OrderBy(r => r.RuleType).ThenBy(r => r.Name)
            : _rules.OrderBy(r => r.Name);
        foreach (var r in ordered)
            if (q.Length == 0 || r.Name.ToLowerInvariant().Contains(q))
                _filtered.Add(r);
    }

    private void OnRuleSearch(object sender, TextChangedEventArgs e)
        => ApplySearch(RuleSearchBox.Text);

    private void OnRuleSortChanged(object sender, SelectionChangedEventArgs e)
        => ApplySearch(RuleSearchBox?.Text ?? string.Empty);

    // ── Profile control ───────────────────────────────────────────────────────
    private async void OnProfileOn(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string profile)
        {
            RuleStatus.Text = $"Enabling {profile} profile…";
            await RunElevatedAsync("netsh", $"advfirewall set {profile}profile state on");
            RuleStatus.Text = $"{profile} profile enabled.";
        }
    }

    private async void OnProfileOff(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string profile)
        {
            var dlg = new ContentDialog
            {
                Title   = "Disable Firewall Profile?",
                Content = $"This will disable the {profile} firewall profile. Continue?",
                PrimaryButtonText   = "Disable",
                SecondaryButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            RuleStatus.Text = $"Disabling {profile} profile…";
            await RunElevatedAsync("netsh", $"advfirewall set {profile}profile state off");
            RuleStatus.Text = $"{profile} profile disabled.";
        }
    }

    private async void OnEnableAll(object sender, RoutedEventArgs e)
    {
        RuleStatus.Text = "Enabling all profiles…";
        await RunElevatedAsync("netsh", "advfirewall set allprofiles state on");
        RuleStatus.Text = "All profiles enabled.";
    }

    // ── Add custom rule ───────────────────────────────────────────────────────
    private async void OnAddRule(object sender, RoutedEventArgs e)
    {
        string name      = RuleNameBox.Text.Trim();
        string remoteIp  = RuleRemoteIp.Text.Trim();
        int    port      = (int)RulePort.Value;
        string program   = RuleProgram.Text.Trim();
        bool   outbound  = RuleDirection.SelectedIndex == 0;
        string dir       = outbound ? "out" : "in";

        if (string.IsNullOrEmpty(name)) { RuleStatus.Text = "Enter a rule name."; return; }

        string ipPart   = remoteIp.Length > 0  ? $"remoteip=\"{remoteIp}\" "   : "";
        string portPart = port > 0             ? $"remoteport={port} protocol=TCP " : "";
        string progPart = program.Length > 0   ? $"program=\"{program}\" "      : "";

        string args = $"advfirewall firewall add rule name=\"{name}\" dir={dir} action=block {ipPart}{portPart}{progPart}enable=yes";

        RuleStatus.Text = "Adding rule…";
        await RunElevatedAsync("netsh", args);
        RuleStatus.Text = $"Rule '{name}' added.";
        await LoadRulesAsync();
    }

    private async void OnQuickBlockIp(object sender, RoutedEventArgs e)
    {
        string ip = QuickBlockIpBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            RuleStatus.Text = "Enter a valid IPv4 or IPv6 address.";
            return;
        }

        string safeIp = ip.Replace(':', '_').Replace('.', '_');
        string ruleName = $"WinNetControl_ConnOut_QuickIp_{safeIp}";
        RuleStatus.Text = $"Blocking {ip}…";
        await RunElevatedAsync("netsh", $"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block remoteip=\"{ip}\" enable=yes");
        QuickBlockIpBox.Text = string.Empty;
        RuleStatus.Text = $"Outbound traffic to {ip} is blocked.";
        await LoadRulesAsync();
    }

    // ── Per-rule delete ───────────────────────────────────────────────────────
    private async void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string ruleName)
        {
            RulesProgress.Visibility = Visibility.Visible;
            if (TryParseConnectionRule(ruleName, out string remoteIp, out int remotePort, out int localPort))
            {
                // A connection block is represented by two firewall rules. Removing either
                // row removes the pair so every module can correctly show it as unblocked.
                string tag = $"{remoteIp}_{remotePort}_{localPort}";
                await RunElevatedAsync("netsh", $"advfirewall firewall delete rule name=\"WinNetControl_ConnIn_{tag}\"");
                await RunElevatedAsync("netsh", $"advfirewall firewall delete rule name=\"WinNetControl_ConnOut_{tag}\"");

                if (_vm != null)
                    _vm.RemoveConnectionBlockByEndpoint(remoteIp, remotePort, localPort);
                else
                    BlockedConnectionStore.NotifyBlockChange(string.Empty, remoteIp, remotePort, localPort, false);
            }
            else
            {
                await RunElevatedAsync("netsh", $"advfirewall firewall delete rule name=\"{ruleName}\"");
            }
            await LoadRulesAsync();
        }
    }

    private static bool TryParseConnectionRule(string ruleName, out string remoteIp, out int remotePort, out int localPort)
    {
        remoteIp = string.Empty;
        remotePort = 0;
        localPort = 0;
        var match = Regex.Match(ruleName, @"^WinNetControl_Conn(?:In|Out)_(.+)_(\d+)_(\d+)$", RegexOptions.IgnoreCase);
        return match.Success &&
               int.TryParse(match.Groups[2].Value, out remotePort) &&
               int.TryParse(match.Groups[3].Value, out localPort) &&
               System.Net.IPAddress.TryParse(remoteIp = match.Groups[1].Value, out _);
    }

    // ── Quick actions ─────────────────────────────────────────────────────────
    private async void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title   = "Reset Firewall to Defaults?",
            Content = "This will reset Windows Firewall to its default policy. All custom rules will be removed.",
            PrimaryButtonText   = "Reset",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        RuleStatus.Text = "Resetting firewall defaults…";
        await RunElevatedAsync("netsh", "advfirewall reset");
        RuleStatus.Text = "Firewall reset to defaults.";
        await LoadRulesAsync();
    }

    private async void OnDeleteAllWnc(object sender, RoutedEventArgs e)
    {
        var dlg = new ContentDialog
        {
            Title   = "Delete All WinNetControl Rules?",
            Content = $"This will delete all {_rules.Count} WinNetControl firewall rules.",
            PrimaryButtonText   = "Delete All",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        RulesProgress.Visibility = Visibility.Visible;
        foreach (var rule in _rules.ToList())
            await RunElevatedAsync("netsh", $"advfirewall firewall delete rule name=\"{rule.Name}\"");
        await LoadRulesAsync();
    }

    private void OnExportRules(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        string? path = FirewallService.ExportRules(_vm.Processes);
        RuleStatus.Text = path != null
            ? $"Exported to: {path}"
            : "Export failed.";
    }

    private void OnOpenWindowsFirewall(object sender, RoutedEventArgs e)
        => FirewallService.OpenWindowsFirewall();

    private void OnRefreshRules(object sender, RoutedEventArgs e)
        => _ = LoadRulesAsync();

    // ── Helper ────────────────────────────────────────────────────────────────
    private static Task RunElevatedAsync(string exe, string args) => Task.Run(() =>
    {
        // ElevatedRunner detects existing admin elevation and avoids a second UAC prompt.
        if (exe.Equals("netsh", StringComparison.OrdinalIgnoreCase))
            ElevatedRunner.RunNetsh(args);
        else
            ElevatedRunner.RunPowerShell(args);
    });
}
