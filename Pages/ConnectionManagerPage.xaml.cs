using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinNetControl.ViewModels;
using WinNetControl.Models;
using WinNetControl.Core;
using WinNetControl.Views;
using System;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml.Navigation;

namespace WinNetControl.Pages;

public sealed partial class ConnectionManagerPage : Page
{
    public MainViewModel ViewModel { get; private set; } = null!;

    // Context menu target
    private ProcessNetworkInfo? _ctxProcess;

    // Search debounce timer
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    // Temp block timers
    private readonly System.Collections.Generic.Dictionary<int, DispatcherTimer> _tempBlockTimers = new();

    // Sub-window references
    private SpeedWidgetWindow? _globalWidget;
    private readonly System.Collections.Generic.Dictionary<int, SpeedWidgetWindow> _appWidgets = new();
    private AdapterManagerWindow? _adapterWindow;
    private NetworkToolsWindow? _netToolsWindow;
    private HistoryLogWindow? _historyLogWindow;
    private RuleManagerWindow? _ruleManagerWindow;

    private bool _allSelected;

    public ConnectionManagerPage()
    {
        this.InitializeComponent();

        _searchDebounce.Tick += (s, e) =>
        {
            _searchDebounce.Stop();
            ViewModel.SearchText = SearchBox.Text;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm)
            ViewModel = vm;
        BlockedConnectionStore.ConnectionBlockChanged += OnExternalBlockChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        BlockedConnectionStore.ConnectionBlockChanged -= OnExternalBlockChanged;
    }

    private void OnExternalBlockChanged(string processName, string remoteIp, int remotePort, int localPort, bool isBlocked)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ViewModel == null) return;
            foreach (var process in ViewModel.Processes.Where(p =>
                         string.IsNullOrWhiteSpace(processName) ||
                         string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var connection in process.Connections.Where(c =>
                             string.Equals(c.RemoteAddress, remoteIp, StringComparison.OrdinalIgnoreCase) &&
                             c.RemotePort == remotePort && c.LocalPort == localPort))
                {
                    connection.IsBlocked = isBlocked;
                    if (!isBlocked) { connection.BlockInbound = false; connection.BlockOutbound = false; }
                }

                foreach (var connection in process.CurrentConnections.Where(c =>
                             string.Equals(c.RemoteAddress, remoteIp, StringComparison.OrdinalIgnoreCase) &&
                             c.RemotePort == remotePort && c.LocalPort == localPort))
                {
                    connection.IsBlocked = isBlocked;
                    if (!isBlocked) { connection.BlockInbound = false; connection.BlockOutbound = false; }
                }

                process.RefreshConnectionStats();
            }
        });
    }

    // ── DNS Flush ─────────────────────────────────────────────────────────────
    private async void OnFlushDnsClicked(object sender, RoutedEventArgs e)
    {
        var (ok, output) = await System.Threading.Tasks.Task.Run(
            () => NetworkAdapterService.FlushDns());
        var dlg = new ContentDialog
        {
            Title           = ok ? "✓  DNS Cache Flushed" : "✗  DNS Flush Failed",
            Content         = ok
                ? "DNS resolver cache has been cleared. New lookups will use fresh DNS results."
                : $"Error: {output}",
            CloseButtonText = "OK",
            XamlRoot        = this.XamlRoot
        };
        await dlg.ShowAsync();
    }

    // ── Search (debounced) ────────────────────────────────────────────────────
    private void OnSearchTextChanged_Auto(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // ── Clear Filters ─────────────────────────────────────────────────────────
    private void OnClearSearchClicked(object sender, RoutedEventArgs e)
    {
        SearchBox.Text             = string.Empty;
        ViewModel.SearchText       = string.Empty;
        ViewModel.SelectedFilter   = "All";
        ViewModel.SelectedSort     = "Data Used (High-Low)";
        ViewModel.SelectedProtocol = "All Proto";
    }

    // ── Reset Data ────────────────────────────────────────────────────────────
    private void OnResetDataClicked(object sender, RoutedEventArgs e) => ViewModel.ResetAllData();

    // ── Export CSV ───────────────────────────────────────────────────────────
    private async void OnExportCsvClicked(object sender, RoutedEventArgs e)
    {
        var (ok, result) = ViewModel.ExportToCsv();
        var dialog = new ContentDialog
        {
            Title           = ok ? "Export Complete" : "Export Failed",
            Content         = ok ? $"Saved to:\n{result}" : result,
            CloseButtonText = "OK",
            XamlRoot        = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Global Speed Widget ────────────────────────────────────────────────────
    private void OnToggleGlobalWidget(object sender, RoutedEventArgs e)
    {
        if (_globalWidget == null)
        {
            _globalWidget = new SpeedWidgetWindow(null, ViewModel);
            _globalWidget.Closed += (s, a) => _globalWidget = null;
            _globalWidget.Activate();
        }
        else { _globalWidget.Close(); _globalWidget = null; }
    }

    // ── Per-App Widget ────────────────────────────────────────────────────────
    private void OnAppWidgetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
        {
            if (_appWidgets.TryGetValue(process.ProcessId, out var ew))
            {
                ew.Close();
                _appWidgets.Remove(process.ProcessId);
                process.ShowFloatingWidget = false;
            }
            else
            {
                var w = new SpeedWidgetWindow(process);
                w.Closed += (s, a) => { _appWidgets.Remove(process.ProcessId); process.ShowFloatingWidget = false; };
                _appWidgets[process.ProcessId] = w;
                process.ShowFloatingWidget = true;
                w.Activate();
            }
        }
    }

    // ── Block (all directions) ────────────────────────────────────────────────
    private void OnBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlock(process);
    }

    // ── Block Inbound / Outbound (per-process) ────────────────────────────────
    private void OnBlockInboundToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlockInbound(process);
    }

    private void OnBlockOutboundToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.ToggleBlockOutbound(process);
    }

    // ── Block (per-connection) ────────────────────────────────────────────────
    private void OnConnectionBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
            ViewModel.ToggleConnectionBlock(conn);
    }

    private void OnConnectionInboundBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
        {
            bool newVal = tb.IsChecked == true;
            conn.BlockInbound = newVal;
            ViewModel.ToggleConnectionBlock(conn, blockInbound: newVal, blockOutbound: conn.BlockOutbound);
        }
    }

    private void OnConnectionOutboundBlockToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessConnection conn)
        {
            bool newVal = tb.IsChecked == true;
            conn.BlockOutbound = newVal;
            ViewModel.ToggleConnectionBlock(conn, blockInbound: conn.BlockInbound, blockOutbound: newVal);
        }
    }

    // ── Copy Connection Address ───────────────────────────────────────────────
    private void OnCopyConnectionAddress(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.DataContext is ProcessConnection conn)
        {
            var dp = new DataPackage();
            dp.SetText(conn.RemoteAddressPort);
            Clipboard.SetContent(dp);
        }
    }

    // ── Pin ───────────────────────────────────────────────────────────────────
    private void OnPinToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
            ViewModel.TogglePin(process);
    }

    // ── HTTP Capture ──────────────────────────────────────────────────────────
    private void OnHttpCaptureToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.DataContext is ProcessNetworkInfo process)
        {
            ViewModel.ToggleHttpCapture(process);
            if (process.IsHttpCaptureEnabled)
            {
                var w = new HttpInspectorWindow(ViewModel, process.ProcessId, process.ProcessName);
                w.Activate();
            }
        }
    }

    // ── HTTP Inspector (global) ───────────────────────────────────────────────
    private void OnHttpInspectorClicked(object sender, RoutedEventArgs e)
    {
        var w = new HttpInspectorWindow(ViewModel);
        w.Activate();
    }

    // ── Windows Network Tools ─────────────────────────────────────────────────
    private void OnOpenWindowsFirewall(object sender, RoutedEventArgs e)       => ViewModel.OpenWindowsFirewall();
    private void OnOpenNetworkConnections(object sender, RoutedEventArgs e)    => ViewModel.OpenNetworkConnections();
    private void OnOpenNetworkSettings(object sender, RoutedEventArgs e)       => ViewModel.OpenNetworkSettings();
    private void OnOpenNetworkTroubleshooter(object sender, RoutedEventArgs e) => ViewModel.OpenNetworkTroubleshooter();

    // ── Internet Reset Dialog ─────────────────────────────────────────────────
    private async void OnResetInternetClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new InternetResetDialog(ViewModel) { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }

    // ── Adapter Manager ───────────────────────────────────────────────────────
    private void OnAdapterManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_adapterWindow != null)
        {
            try { _adapterWindow.Activate(); return; } catch { }
        }
        _adapterWindow = new AdapterManagerWindow();
        _adapterWindow.Closed += (_, __) => _adapterWindow = null;
        _adapterWindow.Activate();
    }

    // ── Network Tools window ──────────────────────────────────────────────────
    private void OnNetworkToolsClicked(object sender, RoutedEventArgs e)
    {
        if (_netToolsWindow != null)
        {
            try { _netToolsWindow.Activate(); return; } catch { }
        }
        _netToolsWindow = new NetworkToolsWindow();
        _netToolsWindow.Closed += (_, __) => _netToolsWindow = null;
        _netToolsWindow.Activate();
    }

    // ── Hosts File Manager ────────────────────────────────────────────────────
    private void OnHostsManagerClicked(object sender, RoutedEventArgs e)
    {
        // Navigate via shell to the Hosts page
        if (App.Window is MainWindow mw)
            mw.NavigateTo("Hosts");
    }

    // ── Optimize Dialog ───────────────────────────────────────────────────────
    private async void OnOptimizeClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OptimizeDialog { XamlRoot = this.XamlRoot };
        await dialog.ShowAsync();
    }

    // ── Settings ──────────────────────────────────────────────────────────────
    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow mw)
            mw.NavigateTo("Settings");
    }

    // ── Bulk Select & Block ───────────────────────────────────────────────────
    private void OnRowCheckboxTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => e.Handled = true;

    private void OnBulkSelectAll(object sender, RoutedEventArgs e)
    {
        _allSelected = !_allSelected;
        foreach (var p in ViewModel.FilteredProcesses)
            p.IsSelected = _allSelected;
    }

    private void OnBulkBlock(object sender, RoutedEventArgs e)
    {
        var targets = ViewModel.FilteredProcesses.Where(p => p.IsSelected).ToList();
        foreach (var p in targets)
        {
            if (!p.IsBlocked) { p.IsBlocked = true; ViewModel.ToggleBlock(p); }
        }
    }

    private void OnBulkUnblock(object sender, RoutedEventArgs e)
    {
        var targets = ViewModel.FilteredProcesses.Where(p => p.IsSelected).ToList();
        foreach (var p in targets)
        {
            if (p.IsBlocked) { p.IsBlocked = false; ViewModel.ToggleBlock(p); }
        }
    }

    // ── Process Right-Click Context Menu ─────────────────────────────────────
    private void OnProcessContextMenuOpening(object sender, object e)
    {
        if (sender is MenuFlyout flyout)
        {
            _ctxProcess = null;
            if (flyout.Target is FrameworkElement fe)
            {
                DependencyObject? current = fe;
                while (current != null)
                {
                    if (current is FrameworkElement f && f.DataContext is ProcessNetworkInfo pni)
                    {
                        _ctxProcess = pni;
                        break;
                    }
                    current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current);
                }
            }
        }
    }

    private void OnCtxCopyName(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null) { var dp = new DataPackage(); dp.SetText(_ctxProcess.ProcessName); Clipboard.SetContent(dp); }
    }

    private void OnCtxCopyPath(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null && !string.IsNullOrWhiteSpace(_ctxProcess.ProcessPath))
        {
            var dp = new DataPackage();
            dp.SetText(_ctxProcess.ProcessPath);
            Clipboard.SetContent(dp);
        }
    }

    private void OnCtxBlock(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null) { _ctxProcess.IsBlocked = true; ViewModel.ToggleBlock(_ctxProcess); }
    }

    private void OnCtxUnblock(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess != null) { _ctxProcess.IsBlocked = false; ViewModel.ToggleBlock(_ctxProcess); }
    }

    private void OnCtxOpenFirewall(object sender, RoutedEventArgs e) => ViewModel.OpenWindowsFirewall();

    private async void OnCtxKill(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var confirm = new ContentDialog
        {
            Title             = "Kill Process?",
            Content           = $"Terminate '{_ctxProcess.ProcessName}' (PID {_ctxProcess.ProcessId})?",
            PrimaryButtonText = "Kill",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };
        if (await confirm.ShowAsync() == ContentDialogResult.Primary)
        {
            var (ok, msg) = ViewModel.KillProcess(_ctxProcess);
            var info = new ContentDialog
            {
                Title           = ok ? "Process Terminated" : "Error",
                Content         = msg,
                CloseButtonText = "OK",
                XamlRoot        = this.XamlRoot
            };
            await info.ShowAsync();
        }
    }

    // ── Details dialog ────────────────────────────────────────────────────────
    private async void OnCtxDetails(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var dialog = new AppDetailDialog(
            _ctxProcess,
            blockCallback:   p => { p.IsBlocked = true;  ViewModel.ToggleBlock(p); },
            unblockCallback: p => { p.IsBlocked = false; ViewModel.ToggleBlock(p); })
        {
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }

    // ── Block domain in Hosts ─────────────────────────────────────────────────
    private async void OnCtxBlockInHosts(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var hostBox = new TextBox
        {
            Text            = _ctxProcess.ProcessName.ToLowerInvariant(),
            PlaceholderText = "hostname to block in hosts file"
        };
        var dialog = new ContentDialog
        {
            Title              = "Block Domain in Hosts File",
            Content            = hostBox,
            PrimaryButtonText  = "Block",
            CloseButtonText    = "Cancel",
            XamlRoot           = this.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string hostname = hostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var (ok, error) = HostsFileService.BlockDomain(hostname);
        var result = new ContentDialog
        {
            Title           = ok ? "✓ Blocked in Hosts" : "✗ Error",
            Content         = ok ? $"'{hostname}' → 0.0.0.0 added and DNS cache flushed." : error,
            CloseButtonText = "OK",
            XamlRoot        = this.XamlRoot
        };
        await result.ShowAsync();
    }

    // ── Temporary Block ───────────────────────────────────────────────────────
    private void OnCtxBlockTemp(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null || sender is not MenuFlyoutItem item) return;
        int minutes = int.TryParse(item.Tag?.ToString(), out int m) ? m : 30;
        StartTempBlock(_ctxProcess, minutes);
    }

    private async void OnCtxBlockTempCustom(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        var box = new NumberBox { Header = "Block duration (minutes)", Value = 30, Minimum = 1, Maximum = 1440 };
        var dlg = new ContentDialog
        {
            Title             = $"Block '{_ctxProcess.ProcessName}' temporarily",
            Content           = box,
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        StartTempBlock(_ctxProcess, (int)box.Value);
    }

    private void StartTempBlock(ProcessNetworkInfo process, int minutes)
    {
        if (_tempBlockTimers.TryGetValue(process.ProcessId, out var old))
        {
            old.Stop();
            _tempBlockTimers.Remove(process.ProcessId);
        }
        if (!process.IsBlocked) { process.IsBlocked = true; ViewModel.ToggleBlock(process); }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(minutes) };
        timer.Tick += (_, __) =>
        {
            timer.Stop();
            _tempBlockTimers.Remove(process.ProcessId);
            process.IsBlocked = false;
            ViewModel.ToggleBlock(process);
        };
        _tempBlockTimers[process.ProcessId] = timer;
        timer.Start();
    }

    // ── VirusTotal lookup ─────────────────────────────────────────────────────
    private void OnCtxVirusTotal(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        string path = _ctxProcess.ProcessPath ?? "";
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs  = System.IO.File.OpenRead(path);
                byte[] hash   = sha.ComputeHash(fs);
                string hex    = BitConverter.ToString(hash).Replace("-", "").ToLower();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    $"https://www.virustotal.com/gui/file/{hex}") { UseShellExecute = true });
                return;
            }
            catch { }
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            $"https://www.virustotal.com/gui/search/{Uri.EscapeDataString(_ctxProcess.ProcessName)}")
            { UseShellExecute = true });
    }

    // ── Ping selected process host ────────────────────────────────────────────
    private void OnCtxPingHost(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        string? host = _ctxProcess.Connections.FirstOrDefault()?.RemoteAddress;
        if (string.IsNullOrEmpty(host) || host == "*") host = _ctxProcess.ProcessName;
        if (_netToolsWindow == null)
        {
            _netToolsWindow = new NetworkToolsWindow();
            _netToolsWindow.Closed += (_, __) => _netToolsWindow = null;
        }
        _netToolsWindow.Activate();
        try { _netToolsWindow.SetPingHost(host); } catch { }
    }

    // ── Local Network Scanner ─────────────────────────────────────────────────
    private void OnLocalScannerClicked(object sender, RoutedEventArgs e)
    {
        if (App.Window is MainWindow mw)
            mw.NavigateTo("Lan");
    }

    // ── Block Entire Subnet ───────────────────────────────────────────────────
    private async void OnBlockSubnetClicked(object sender, RoutedEventArgs e)
    {
        var inputTextBox = new TextBox
        {
            PlaceholderText = "e.g., 192.168.1.0/24 or 10.0.0.0/8",
            Width           = 300,
            Margin          = new Thickness(0, 10, 0, 0)
        };
        var dialog = new ContentDialog
        {
            Title             = "Block Entire Subnet",
            Content           = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Enter the subnet to block (CIDR notation):", TextWrapping = TextWrapping.Wrap },
                    inputTextBox
                }
            },
            PrimaryButtonText = "Block Subnet",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(inputTextBox.Text)) return;

        string subnet = inputTextBox.Text.Trim();
        try
        {
            if (!subnet.Contains("/") || !subnet.Contains("."))
                throw new Exception("Please use valid CIDR notation (e.g. 192.168.1.0/24).");

            string ruleName = $"WinNetControl_BlockSubnet_{subnet.Replace("/", "_")}";
            var psiOut = new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{ruleName}_Out\" dir=out action=block remoteip={subnet}")
                { CreateNoWindow = true, UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psiOut)?.WaitForExit();

            var psiIn = new System.Diagnostics.ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{ruleName}_In\" dir=in action=block remoteip={subnet}")
                { CreateNoWindow = true, UseShellExecute = true, Verb = "runas" };
            System.Diagnostics.Process.Start(psiIn)?.WaitForExit();

            await new ContentDialog
            {
                Title = "Subnet Blocked", Content = $"Successfully blocked {subnet} in Windows Firewall.",
                CloseButtonText = "OK", XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "Error Blocking Subnet", Content = ex.Message,
                CloseButtonText = "OK", XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
    }

    // ── Network Map ───────────────────────────────────────────────────────────
    private void OnCtxNetworkMap(object sender, RoutedEventArgs e)
    {
        if (_ctxProcess == null) return;
        new NetworkMapWindow(_ctxProcess).Activate();
    }

    // ── History Log ───────────────────────────────────────────────────────────
    private void OnHistoryLogClicked(object sender, RoutedEventArgs e)
    {
        if (_historyLogWindow == null)
        {
            _historyLogWindow = new HistoryLogWindow();
            _historyLogWindow.Closed += (_, __) => _historyLogWindow = null;
        }
        _historyLogWindow.Activate();
    }

    // ── Rule Manager ──────────────────────────────────────────────────────────
    private void OnRuleManagerClicked(object sender, RoutedEventArgs e)
    {
        if (_ruleManagerWindow == null)
        {
            _ruleManagerWindow = new RuleManagerWindow();
            _ruleManagerWindow.Closed += (_, __) => _ruleManagerWindow = null;
        }
        _ruleManagerWindow.Activate();
    }

    // ── Per-connection Pin ────────────────────────────────────────────────────
    private void OnConnectionPinToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        if (tb.DataContext is not ProcessConnection conn) return;
        var proc = ViewModel.FilteredProcesses.FirstOrDefault(p => p.Connections.Contains(conn));
        if (proc == null) return;
        var pinned   = proc.Connections.Where(c => c.IsPinned).ToList();
        var unpinned = proc.Connections.Where(c => !c.IsPinned).ToList();
        proc.Connections.Clear();
        foreach (var c in pinned.Concat(unpinned)) proc.Connections.Add(c);
    }

    // ── Per-connection Block in Hosts ─────────────────────────────────────────
    private async void OnConnectionBlockInHosts(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ProcessConnection conn) return;

        string rawRemote = conn.RemoteAddress?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(rawRemote) || rawRemote == "*" || rawRemote == "0.0.0.0" || rawRemote == "::")
        {
            await new ContentDialog
            {
                Title = "Cannot Block", Content = "This connection has no remote address to block.",
                CloseButtonText = "OK", XamlRoot = this.XamlRoot
            }.ShowAsync();
            return;
        }

        string appName = ViewModel.FilteredProcesses
            .FirstOrDefault(p => p.Connections.Contains(conn))?.ProcessName ?? string.Empty;

        var hostBox = new TextBox { Text = rawRemote, PlaceholderText = "hostname or IP to block" };
        var dlg = new ContentDialog
        {
            Title             = "Block in Hosts File",
            Content           = hostBox,
            PrimaryButtonText = "Block",
            CloseButtonText   = "Cancel",
            XamlRoot          = this.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string hostname = hostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hostname)) return;

        var (ok, error) = HostsFileService.BlockDomain(hostname, appName);
        await new ContentDialog
        {
            Title           = ok ? "✓ Blocked in Hosts" : "✗ Error",
            Content         = ok ? $"'{hostname}' → 0.0.0.0 added. Tagged: {appName}" : error,
            CloseButtonText = "OK",
            XamlRoot        = this.XamlRoot
        }.ShowAsync();
    }

    // ── Export / Import Rules ─────────────────────────────────────────────────
    private void OnExportRulesClicked(object sender, RoutedEventArgs e)
    {
        string? path = FirewallService.ExportRules(ViewModel.Processes);
        ShowMessage(path != null ? "Rules Exported" : "Export Failed",
                    path != null ? $"Firewall rules backed up to:\n{path}"
                                 : "Could not export firewall rules. Check permissions.");
    }

    private async void OnImportRulesClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);
        picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.List;
        picker.FileTypeFilter.Add(".json");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            int count = FirewallService.ImportRules(file.Path, config =>
            {
                if (config.BlockInbound && !ViewModel.CurrentConfig.BlockedAppsInbound.Contains(config.ProcessName))
                    ViewModel.CurrentConfig.BlockedAppsInbound.Add(config.ProcessName);
                if (config.BlockOutbound && !ViewModel.CurrentConfig.BlockedAppsOutbound.Contains(config.ProcessName))
                    ViewModel.CurrentConfig.BlockedAppsOutbound.Add(config.ProcessName);
                if (!ViewModel.CurrentConfig.BlockedApps.Contains(config.ProcessName))
                    ViewModel.CurrentConfig.BlockedApps.Add(config.ProcessName);
                if (!string.IsNullOrEmpty(config.Notes))
                    ViewModel.CurrentConfig.AppNotes[config.ProcessName] = config.Notes;
                if (config.DataLimitMb > 0)
                    ViewModel.CurrentConfig.DataLimits[config.ProcessName] = (long)(config.DataLimitMb * 1024 * 1024);
            });
            ViewModel.SaveConfig();
            ShowMessage("Import Complete", $"Successfully imported {count} rules.");
        }
    }

    // ── Column Sorting ────────────────────────────────────────────────────────
    private void OnSortHeaderTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string sortKey && !string.IsNullOrEmpty(sortKey))
            ViewModel.SelectedSort = sortKey;
    }

    // ── App Notes ────────────────────────────────────────────────────────────
    private void OnAppNotesLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ProcessNetworkInfo info)
        {
            ViewModel.CurrentConfig.AppNotes[info.ProcessName] = info.Notes ?? "";
            ViewModel.SaveConfig();
        }
    }

    // ── Helper ────────────────────────────────────────────────────────────────
    private async void ShowMessage(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                Title = title, Content = message,
                CloseButtonText = "OK", XamlRoot = this.XamlRoot
            }.ShowAsync();
        }
        catch { }
    }
}
