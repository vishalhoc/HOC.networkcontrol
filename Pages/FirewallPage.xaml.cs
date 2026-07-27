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

    // IMP#9: debounce search keystrokes (200 ms) + cache sorted order
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private List<FirewallRuleItem> _sortedRules = new();

    // IMP#6: tracks an in-flight batch delete so it can be cancelled
    private System.Threading.CancellationTokenSource? _batchDeleteCts;

    public FirewallPage()
    {
        this.InitializeComponent();
        RulesList.ItemsSource = _filtered;
        // FIX Bug#46: use a named handler so it can be removed in OnNavigatedFrom
        _syncDebounce.Tick += OnSyncDebounceTick;
        // IMP#9: search debounce — fires ApplySearch only when typing pauses
        _searchDebounce.Tick += OnSearchDebounceTick;
    }

    // Named debounce tick handler
    private void OnSyncDebounceTick(object? sender, object e)
    {
        _syncDebounce.Stop();
        _ = LoadRulesAsync();
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
        // FIX Bug#46: remove the named debounce tick handler to prevent accumulation
        _syncDebounce.Tick -= OnSyncDebounceTick;
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
            RebuildSortCache();                          // IMP#9: refresh sort cache
            ApplySearch(RuleSearchBox.Text);
            RulesStatusBar.Text = $"{_rules.Count} WinNetControl rules found";
        }
        catch (Exception ex) { RulesStatusBar.Text = $"Error: {ex.Message}"; }
        finally { RulesProgress.Visibility = Visibility.Collapsed; }
    }

    // IMP#9: separate sort cache — only re-sort when sort order changes
    private void RebuildSortCache()
    {
        _sortedRules = (RuleSortBox?.SelectedItem is ComboBoxItem { Tag: "type" }
            ? _rules.OrderBy(r => r.RuleType).ThenBy(r => r.Name)
            : (IEnumerable<FirewallRuleItem>)_rules.OrderBy(r => r.Name)).ToList();
    }

    private void ApplySearch(string query)
    {
        _filtered.Clear();
        var q = query.Trim().ToLowerInvariant();
        foreach (var r in _sortedRules)
            if (q.Length == 0 || r.Name.ToLowerInvariant().Contains(q))
                _filtered.Add(r);
    }

    // IMP#9: debounced search — reset timer on each keystroke
    private void OnRuleSearch(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, object e)
    {
        _searchDebounce.Stop();
        ApplySearch(RuleSearchBox?.Text ?? string.Empty);
    }

    private void OnRuleSortChanged(object sender, SelectionChangedEventArgs e)
    {
        RebuildSortCache();
        ApplySearch(RuleSearchBox?.Text ?? string.Empty);
    }

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

        // FIX Bug#10: sanitize IP for the rule name (colons → dashes for IPv6,
        // dots → dashes for IPv4) so the rule name is always valid in netsh.
        string safeIp = ip.Replace(':', '-').Replace('.', '-');
        string ruleName = $"WinNetControl_ConnOut_QuickIp_{safeIp}";
        RuleStatus.Text = $"Blocking {ip}…";
        await RunElevatedAsync("netsh", $"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block remoteip=\"{ip}\" enable=yes");
        // FIX Bug#11: clear the input AFTER the command succeeds, not before
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
        // FIX Bug#10: The IP in the rule name is stored with dashes (see safeIp
        // construction above). IPv6 groups look like "2001-db8-..." so we
        // restore colons after extraction to get a parseable IPv6 string.
        // Pattern: WinNetControl_ConnIn_<ip-with-dashes>_<remotePort>_<localPort>
        var match = Regex.Match(ruleName, @"^WinNetControl_Conn(?:In|Out)_(.+?)_(\d+)_(\d+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups[2].Value, out remotePort)) return false;
        if (!int.TryParse(match.Groups[3].Value, out localPort))  return false;

        // Try restoring an IPv6 address: replace dashes in groups-of-4-hex back to colons.
        string candidate = match.Groups[1].Value;
        // First try as-is (IPv4 with dashes, or simple IPv6)
        if (System.Net.IPAddress.TryParse(candidate.Replace('-', '.'), out _))
            remoteIp = candidate.Replace('-', '.');
        else if (System.Net.IPAddress.TryParse(candidate.Replace('-', ':'), out _))
            remoteIp = candidate.Replace('-', ':');
        else
            remoteIp = candidate; // leave as-is; TryParse will reject below

        return System.Net.IPAddress.TryParse(remoteIp, out _);
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

    // IMP#6: Parallel batch delete with progress bar and cancel support
    private async void OnDeleteAllWnc(object sender, RoutedEventArgs e)
    {
        var snapshot = _rules.ToList();
        if (snapshot.Count == 0) return;

        var dlg = new ContentDialog
        {
            Title   = "Delete All WinNetControl Rules?",
            Content = $"This will delete all {snapshot.Count} WinNetControl firewall rules. You can cancel mid-way.",
            PrimaryButtonText   = "Delete All",
            SecondaryButtonText = "Cancel",
            XamlRoot = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        // Show progress UI
        _batchDeleteCts = new System.Threading.CancellationTokenSource();
        var cts   = _batchDeleteCts;
        int total = snapshot.Count;
        int done  = 0;

        RulesProgress.Maximum   = total;
        RulesProgress.Value     = 0;
        RulesProgress.Visibility        = Visibility.Visible;
        CancelBatchDelete.Visibility    = Visibility.Visible;
        RulesStatusBar.Text             = $"Deleting 0 / {total}…";

        // Parallel delete — max 4 concurrent netsh calls
        var sem = new System.Threading.SemaphoreSlim(4);
        var tasks = snapshot.Select(async rule =>
        {
            await sem.WaitAsync(cts.Token).ConfigureAwait(false);
            try
            {
                if (cts.Token.IsCancellationRequested) return;
                await RunElevatedAsync("netsh",
                    $"advfirewall firewall delete rule name=\"{rule.Name}\"");
            }
            finally
            {
                sem.Release();
                int n = System.Threading.Interlocked.Increment(ref done);
                DispatcherQueue.TryEnqueue(() =>
                {
                    RulesProgress.Value = n;
                    RulesStatusBar.Text = $"Deleting {n} / {total}…";
                });
            }
        });

        try
        {
            await Task.WhenAll(tasks);
            DispatcherQueue.TryEnqueue(() =>
            {
                RulesStatusBar.Text = $"Deleted {done} rule(s).";
                App.MainWindow?.ShowToast("Firewall", $"Deleted {done} rule(s).", "success");
            });
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RulesStatusBar.Text = $"Cancelled after {done} deletion(s).";
                App.MainWindow?.ShowToast("Firewall", $"Batch delete cancelled ({done} deleted).", "warning");
            });
        }
        finally
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RulesProgress.Visibility     = Visibility.Collapsed;
                CancelBatchDelete.Visibility = Visibility.Collapsed;
            });
            _batchDeleteCts?.Dispose();
            _batchDeleteCts = null;
        }

        await LoadRulesAsync();
    }

    // IMP#6: Cancel button handler
    private void OnCancelBatchDelete(object sender, RoutedEventArgs e)
    {
        _batchDeleteCts?.Cancel();
        CancelBatchDelete.IsEnabled = false;
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
