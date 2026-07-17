using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinNetControl.Core;
using WinNetControl.Models;

namespace WinNetControl.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NetworkMonitorService _networkMonitor;
    private readonly NetworkSpeedMonitorService _speedMonitor;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public AppConfig CurrentConfig { get; }
    public HttpProxyService ProxyService { get; }

    // Session tracking
    public DateTime SessionStartTime { get; } = DateTime.Now;
    public string SessionDuration => FormatDuration(DateTime.Now - SessionStartTime);

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }

    // EMA smoothing factor (0 = no smoothing, 1 = use only new value)
    private const double EmaAlpha = 0.3;

    public MainViewModel()
    {
        CurrentConfig = ConfigService.Load();
        _selectedFilter = CurrentConfig.SelectedFilter;
        _selectedSort   = CurrentConfig.SelectedSort;
        _startWithWindows = CurrentConfig.StartWithWindows;
        _showOfflineBlockedApps = CurrentConfig.ShowOfflineBlockedApps;
        _blockNewApps = CurrentConfig.BlockNewApps;

        _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        ProxyService = new HttpProxyService(this);

        _networkMonitor = new NetworkMonitorService();

        // Seed phantom blocked entries so they persist even without active internet
        foreach (var name in CurrentConfig.BlockedApps)
            _networkMonitor.KnownBlockedNames.Add(name);

        _networkMonitor.OnConnectionsUpdated += OnConnectionsUpdated;
        _networkMonitor.StartMonitoring();

        _speedMonitor = new NetworkSpeedMonitorService();
        _speedMonitor.OnSpeedUpdated += OnSpeedUpdated;
        _speedMonitor.StartMonitoring();

        // Make sure firewall is enabled once at startup
        System.Threading.Tasks.Task.Run(() => FirewallService.EnsureFirewallEnabled());

        // Create offline phantom entries for blocked apps not yet seen
        CreatePhantomBlockedEntries();

        LoadNetworkAdapters();

        // Session timer — updates SessionDuration every second
        var sessionTimer = new System.Threading.Timer(_ =>
        {
            _dispatcherQueue?.TryEnqueue(() => OnPropertyChanged(nameof(SessionDuration)));
        }, null, 1000, 1000);
    }

    // ── Startup phantom entries ───────────────────────────────────────────────
    private void CreatePhantomBlockedEntries()
    {
        foreach (var name in CurrentConfig.BlockedApps)
        {
            if (!Processes.Any(p => string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)))
            {
                var phantom = new ProcessNetworkInfo
                {
                    ProcessId   = -(Processes.Count + 1),
                    ProcessName = name,
                    ProcessPath = string.Empty,
                    AppType     = "Blocked (Offline)",
                    IsBlocked   = true,
                    IsPhantom   = true,
                    BlockInbound  = CurrentConfig.BlockedAppsInbound.Contains(name),
                    BlockOutbound = CurrentConfig.BlockedAppsOutbound.Contains(name)
                };
                if (!phantom.BlockInbound && !phantom.BlockOutbound)
                {
                    phantom.BlockInbound  = true;
                    phantom.BlockOutbound = true;
                }
                Processes.Add(phantom);
            }
        }
        ApplyFilterAndSort();
    }

    // ── Network adapters ──────────────────────────────────────────────────────
    private void LoadNetworkAdapters()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            {
                try
                {
                    var ipProps = ni.GetIPProperties();
                    if (ipProps?.GetIPv4Properties() != null)
                    {
                        int index = ipProps.GetIPv4Properties().Index;
                        NetworkAdapters.Add(new NetworkAdapterInfo { Name = ni.Name, InterfaceIndex = index, IsSelected = true });
                    }
                }
                catch { }
            }
        }
    }

    // ── Observable properties ─────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<NetworkAdapterInfo> _networkAdapters = new();
    [ObservableProperty] private double _globalUploadSpeed;
    [ObservableProperty] private double _globalDownloadSpeed;

    // Pre-formatted stats bar strings
    public string GlobalUploadText   => FormatSpeed(GlobalUploadSpeed);
    public string GlobalDownloadText => FormatSpeed(GlobalDownloadSpeed);
    public string GlobalTotalText    => FormatSize(GlobalTotalDataUsed);
    public string BlockedCountText   => $"{Processes.Count(p => p.IsBlocked)} blocked";

    partial void OnGlobalUploadSpeedChanged(double value)  => OnPropertyChanged(nameof(GlobalUploadText));
    partial void OnGlobalDownloadSpeedChanged(double value) => OnPropertyChanged(nameof(GlobalDownloadText));
    partial void OnGlobalTotalDataUsedChanged(long value)  => OnPropertyChanged(nameof(GlobalTotalText));

    internal static string FormatSpeed(double kbps)
    {
        if (kbps >= 1024 * 1024) return $"{kbps / (1024.0 * 1024):F2} GB/s";
        if (kbps >= 1024)        return $"{kbps / 1024.0:F1} MB/s";
        return $"{kbps:F1} KB/s";
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024)                 return $"{bytes} B";
        if (bytes < 1024 * 1024)          return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024)  return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    [ObservableProperty] private ObservableCollection<ProcessNetworkInfo> _processes = new();
    [ObservableProperty] private ObservableCollection<ProcessNetworkInfo> _filteredProcesses = new();
    [ObservableProperty] private long _globalTotalDataUsed;

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ApplyFilterAndSort(); }
    }

    public ObservableCollection<string> Filters { get; } = new()
        { "All", "Windows System", "Windows App", "Windows Component", "Windows Service", "Third-Party App", "Blocked Only", "Phantom (Offline)" };
    public ObservableCollection<string> SortOptions { get; } = new()
        { "Data Used (High-Low)", "Upload Speed", "Download Speed", "Name (A-Z)" };

    private string _selectedFilter = "All";
    public string SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                CurrentConfig.SelectedFilter = value;
                SaveConfig();
                ApplyFilterAndSort();
            }
        }
    }

    private string _selectedSort = "Data Used (High-Low)";
    public string SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value))
            {
                CurrentConfig.SelectedSort = value;
                SaveConfig();
                ApplyFilterAndSort();
            }
        }
    }

    // ── Startup with Windows ──────────────────────────────────────────────────
    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                CurrentConfig.StartWithWindows = value;
                SaveConfig();
                if (value)
                    FirewallService.CreateStartupTask(FirewallService.GetCurrentExePath());
                else
                    FirewallService.RemoveStartupTask();
            }
        }
    }

    // ── Show offline blocked apps ─────────────────────────────────────────────
    private bool _showOfflineBlockedApps;
    public bool ShowOfflineBlockedApps
    {
        get => _showOfflineBlockedApps;
        set
        {
            if (SetProperty(ref _showOfflineBlockedApps, value))
            {
                CurrentConfig.ShowOfflineBlockedApps = value;
                SaveConfig();
                ApplyFilterAndSort();
            }
        }
    }

    // ── Block New Apps (auto-block any new process) ───────────────────────────
    private bool _blockNewApps;
    public bool BlockNewApps
    {
        get => _blockNewApps;
        set
        {
            if (SetProperty(ref _blockNewApps, value))
            {
                CurrentConfig.BlockNewApps = value;
                SaveConfig();
            }
        }
    }

    // ── Connections update handler ────────────────────────────────────────────
    private void OnConnectionsUpdated(System.Collections.Generic.IEnumerable<ProcessNetworkInfo> activeProcesses)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            var existingPids = new System.Collections.Generic.HashSet<int>();

            foreach (var newProc in activeProcesses)
            {
                existingPids.Add(newProc.ProcessId);

                var existingProc = Processes.FirstOrDefault(p => p.ProcessId == newProc.ProcessId);
                if (existingProc != null)
                {
                    existingProc.IsPhantom = newProc.IsPhantom;

                    // Merge connections
                    var newConnKeys = new System.Collections.Generic.HashSet<string>();
                    foreach (var c in newProc.CurrentConnections)
                    {
                        string key = $"{c.Protocol}-{c.LocalPort}-{c.RemotePort}";
                        newConnKeys.Add(key);
                        var existing = existingProc.Connections.FirstOrDefault(
                            o => o.Protocol == c.Protocol && o.LocalPort == c.LocalPort && o.RemotePort == c.RemotePort);
                        if (existing != null)
                            existing.State = c.State;
                        else
                            existingProc.Connections.Add(c);
                    }

                    for (int j = existingProc.Connections.Count - 1; j >= 0; j--)
                    {
                        var o   = existingProc.Connections[j];
                        string key = $"{o.Protocol}-{o.LocalPort}-{o.RemotePort}";
                        if (!newConnKeys.Contains(key))
                            existingProc.Connections.RemoveAt(j);
                    }
                }
                else
                {
                    // Restore saved config states
                    string name = newProc.ProcessName;
                    newProc.IsPinned          = CurrentConfig.PinnedApps.Contains(name);
                    newProc.IsHttpCaptureEnabled = CurrentConfig.HttpCaptureApps.Contains(name);

                    bool isBlocked = CurrentConfig.BlockedApps.Contains(name);
                    newProc.IsBlocked    = isBlocked;
                    newProc.BlockInbound  = CurrentConfig.BlockedAppsInbound.Contains(name);
                    newProc.BlockOutbound = CurrentConfig.BlockedAppsOutbound.Contains(name);
                    if (isBlocked && !newProc.BlockInbound && !newProc.BlockOutbound)
                    {
                        newProc.BlockInbound  = true;
                        newProc.BlockOutbound = true;
                    }

                    // Auto-block new apps if the global mode is enabled
                    if (!isBlocked && BlockNewApps && !string.IsNullOrWhiteSpace(newProc.ProcessPath))
                    {
                        newProc.IsBlocked    = true;
                        newProc.BlockInbound  = true;
                        newProc.BlockOutbound = true;
                        if (!CurrentConfig.BlockedApps.Contains(name)) CurrentConfig.BlockedApps.Add(name);
                        CurrentConfig.BlockedAppsInbound.Add(name);
                        CurrentConfig.BlockedAppsOutbound.Add(name);
                        _networkMonitor.KnownBlockedNames.Add(name);
                        string snapPath = newProc.ProcessPath;
                        System.Threading.Tasks.Task.Run(() => FirewallService.BlockApp(name, snapPath, true, true));
                    }
                    else if (!isBlocked)
                    {
                        // (#30) Alert: notify user about a new network process they haven't seen before
                        ShowNewAppToast(name, newProc.ProcessPath ?? "");
                    }

                    // Restore per-connection blocks
                    foreach (var c in newProc.CurrentConnections)
                    {
                        var rec = CurrentConfig.BlockedConnections.FirstOrDefault(r =>
                            string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase) &&
                            r.RemoteAddress == c.RemoteAddress && r.RemotePort == c.RemotePort);
                        if (rec != null) { c.IsBlocked = true; c.BlockInbound = rec.BlockInbound; c.BlockOutbound = rec.BlockOutbound; }
                    }

                    newProc.Connections.Clear();
                    foreach (var c in newProc.CurrentConnections)
                        newProc.Connections.Add(c);

                    // Remove phantom placeholder if the real process appeared
                    var phantom = Processes.FirstOrDefault(p => p.IsPhantom &&
                        string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                    if (phantom != null) Processes.Remove(phantom);

                    Processes.Add(newProc);

                    // Re-apply firewall rule off the UI thread
                    if (newProc.IsBlocked && !string.IsNullOrWhiteSpace(newProc.ProcessPath))
                    {
                        string snapPath = newProc.ProcessPath;
                        bool snapIn = newProc.BlockInbound, snapOut = newProc.BlockOutbound;
                        System.Threading.Tasks.Task.Run(() =>
                            FirewallService.BlockApp(name, snapPath, snapIn, snapOut));
                    }
                }
            }

            // Remove dead non-blocked non-phantom processes
            for (int i = Processes.Count - 1; i >= 0; i--)
            {
                var p = Processes[i];
                if (!existingPids.Contains(p.ProcessId) && !p.IsBlocked && !p.IsPhantom)
                    Processes.RemoveAt(i);
            }

            OnPropertyChanged(nameof(BlockedCountText));
            ApplyFilterAndSort();
        });
    }

    // ── Filter & Sort ─────────────────────────────────────────────────────────
    public void ApplyFilterAndSort()
    {
        System.Collections.Generic.IEnumerable<ProcessNetworkInfo> query = Processes;

        if (!ShowOfflineBlockedApps)
            query = query.Where(p => !p.IsPhantom);

        if (!string.IsNullOrWhiteSpace(SearchText))
            query = query.Where(p => p.ProcessName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        if (SelectedFilter == "Blocked Only")
            query = query.Where(p => p.IsBlocked);
        else if (SelectedFilter == "Phantom (Offline)")
            query = query.Where(p => p.IsPhantom);
        else if (SelectedFilter != "All")
            query = query.Where(p => p.AppType == SelectedFilter);

        IOrderedEnumerable<ProcessNetworkInfo> orderedQuery = SelectedSort switch
        {
            "Data Used (High-Low)" => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.TotalDataUsed),
            "Upload Speed"         => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.UploadSpeed),
            "Download Speed"       => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.DownloadSpeed),
            _                      => query.OrderByDescending(p => p.IsPinned).ThenBy(p => p.ProcessName)
        };

        var sortedList = orderedQuery.ToList();

        for (int i = FilteredProcesses.Count - 1; i >= 0; i--)
            if (!sortedList.Contains(FilteredProcesses[i]))
                FilteredProcesses.RemoveAt(i);

        for (int i = 0; i < sortedList.Count; i++)
        {
            var item = sortedList[i];
            int cur  = FilteredProcesses.IndexOf(item);
            if (cur == -1) FilteredProcesses.Insert(i, item);
            else if (cur != i) FilteredProcesses.Move(cur, i);
        }
    }

    // ── Block / Unblock ───────────────────────────────────────────────────────
    public void ToggleBlock(ProcessNetworkInfo process, bool? blockInbound = null, bool? blockOutbound = null)
    {
        if (process == null) return;

        string name = process.ProcessName;
        string path = process.ProcessPath;

        if (process.IsBlocked)
        {
            bool doIn  = blockInbound  ?? process.BlockInbound;
            bool doOut = blockOutbound ?? process.BlockOutbound;

            if (!doIn && !doOut) { doIn = true; doOut = true; }

            process.BlockInbound  = doIn;
            process.BlockOutbound = doOut;

            if (!CurrentConfig.BlockedApps.Contains(name))                  CurrentConfig.BlockedApps.Add(name);
            if (doIn  && !CurrentConfig.BlockedAppsInbound.Contains(name))  CurrentConfig.BlockedAppsInbound.Add(name);
            if (!doIn)  CurrentConfig.BlockedAppsInbound.Remove(name);
            if (doOut && !CurrentConfig.BlockedAppsOutbound.Contains(name)) CurrentConfig.BlockedAppsOutbound.Add(name);
            if (!doOut) CurrentConfig.BlockedAppsOutbound.Remove(name);

            _networkMonitor.KnownBlockedNames.Add(name);

            if (!string.IsNullOrWhiteSpace(path))
                System.Threading.Tasks.Task.Run(() => FirewallService.BlockApp(name, path, doIn, doOut));
        }
        else
        {
            process.BlockInbound  = false;
            process.BlockOutbound = false;

            CurrentConfig.BlockedApps.Remove(name);
            CurrentConfig.BlockedAppsInbound.Remove(name);
            CurrentConfig.BlockedAppsOutbound.Remove(name);
            _networkMonitor.KnownBlockedNames.Remove(name);

            System.Threading.Tasks.Task.Run(() => FirewallService.UnblockApp(name));

            if (process.IsPhantom)
                _dispatcherQueue?.TryEnqueue(() => { Processes.Remove(process); ApplyFilterAndSort(); });
        }

        OnPropertyChanged(nameof(BlockedCountText));
        SaveConfig();
    }

    public void ToggleBlockInbound(ProcessNetworkInfo process)
    {
        if (process == null) return;

        string name = process.ProcessName;
        string path = process.ProcessPath;
        bool   val  = process.BlockInbound;

        process.IsBlocked = process.BlockInbound || process.BlockOutbound;

        if (process.IsBlocked)
        {
            if (!CurrentConfig.BlockedApps.Contains(name)) CurrentConfig.BlockedApps.Add(name);
            _networkMonitor.KnownBlockedNames.Add(name);
        }
        else
        {
            // Both directions now off — prune from BlockedApps
            CurrentConfig.BlockedApps.Remove(name);
            _networkMonitor.KnownBlockedNames.Remove(name);
        }

        if (val) { if (!CurrentConfig.BlockedAppsInbound.Contains(name)) CurrentConfig.BlockedAppsInbound.Add(name); }
        else CurrentConfig.BlockedAppsInbound.Remove(name);

        OnPropertyChanged(nameof(BlockedCountText));
        SaveConfig();

        if (string.IsNullOrWhiteSpace(path)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            if (val) FirewallService.BlockAppInbound(name, path);
            else     FirewallService.UnblockAppInbound(name);
        });
    }

    public void ToggleBlockOutbound(ProcessNetworkInfo process)
    {
        if (process == null) return;

        string name = process.ProcessName;
        string path = process.ProcessPath;
        bool   val  = process.BlockOutbound;

        process.IsBlocked = process.BlockInbound || process.BlockOutbound;

        if (process.IsBlocked)
        {
            if (!CurrentConfig.BlockedApps.Contains(name)) CurrentConfig.BlockedApps.Add(name);
            _networkMonitor.KnownBlockedNames.Add(name);
        }
        else
        {
            // Both directions now off — prune from BlockedApps
            CurrentConfig.BlockedApps.Remove(name);
            _networkMonitor.KnownBlockedNames.Remove(name);
        }

        if (val) { if (!CurrentConfig.BlockedAppsOutbound.Contains(name)) CurrentConfig.BlockedAppsOutbound.Add(name); }
        else CurrentConfig.BlockedAppsOutbound.Remove(name);

        OnPropertyChanged(nameof(BlockedCountText));
        SaveConfig();

        if (string.IsNullOrWhiteSpace(path)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            if (val) FirewallService.BlockAppOutbound(name, path);
            else     FirewallService.UnblockAppOutbound(name);
        });
    }

    public void ToggleConnectionBlock(ProcessConnection connection, bool? blockInbound = null, bool? blockOutbound = null)
    {
        if (connection == null) return;
        var process = Processes.FirstOrDefault(p => p.ProcessId == connection.ProcessId);
        if (process == null) return;

        bool doIn  = blockInbound  ?? connection.BlockInbound;
        bool doOut = blockOutbound ?? connection.BlockOutbound;

        // If toggling the main block switch, align In/Out directions
        if (blockInbound == null && blockOutbound == null)
        {
            if (connection.IsBlocked)
            {
                if (!doIn && !doOut) { doIn = true; doOut = true; }
            }
            else
            {
                doIn = false;
                doOut = false;
            }
        }

        connection.BlockInbound  = doIn;
        connection.BlockOutbound = doOut;
        connection.IsBlocked     = doIn || doOut;

        string procPath   = process.ProcessPath;
        string procName   = process.ProcessName;
        string remoteAddr = connection.RemoteAddress;
        int remotePort    = connection.RemotePort;
        int localPort     = connection.LocalPort;

        if (connection.IsBlocked)
        {
            System.Threading.Tasks.Task.Run(() =>
                FirewallService.BlockConnection(procPath, remoteAddr, remotePort, localPort, doIn, doOut));

            var rec = CurrentConfig.BlockedConnections.FirstOrDefault(r =>
                r.ProcessName == procName && r.RemoteAddress == remoteAddr &&
                r.RemotePort == remotePort && r.LocalPort == localPort);
            if (rec == null)
            {
                CurrentConfig.BlockedConnections.Add(new BlockedConnectionRecord
                {
                    ProcessName = procName, ProcessPath = procPath,
                    RemoteAddress = remoteAddr, RemotePort = remotePort, LocalPort = localPort,
                    BlockInbound = doIn, BlockOutbound = doOut
                });
            }
            else { rec.BlockInbound = doIn; rec.BlockOutbound = doOut; }
        }
        else
        {
            System.Threading.Tasks.Task.Run(() =>
                FirewallService.UnblockConnection(procPath, remoteAddr, remotePort, localPort));
            CurrentConfig.BlockedConnections.RemoveAll(r =>
                r.ProcessName == procName && r.RemoteAddress == remoteAddr &&
                r.RemotePort == remotePort && r.LocalPort == localPort);
        }
        SaveConfig();
    }

    // ── Pin ───────────────────────────────────────────────────────────────────
    public void TogglePin(ProcessNetworkInfo process)
    {
        if (process == null) return;
        if (process.IsPinned) { if (!CurrentConfig.PinnedApps.Contains(process.ProcessName)) CurrentConfig.PinnedApps.Add(process.ProcessName); }
        else CurrentConfig.PinnedApps.Remove(process.ProcessName);
        SaveConfig();
        ApplyFilterAndSort();
    }

    // ── HTTP Capture ──────────────────────────────────────────────────────────
    public void ToggleHttpCapture(ProcessNetworkInfo process)
    {
        if (process == null) return;
        if (process.IsHttpCaptureEnabled)
        {
            try { ProxyService.Start(CurrentConfig.EnableSystemProxy); } catch { }
            if (!CurrentConfig.HttpCaptureApps.Contains(process.ProcessName))
                CurrentConfig.HttpCaptureApps.Add(process.ProcessName);
        }
        else CurrentConfig.HttpCaptureApps.Remove(process.ProcessName);
        SaveConfig();
    }

    // ── Kill Process ──────────────────────────────────────────────────────────
    public (bool ok, string msg) KillProcess(ProcessNetworkInfo process)
    {
        if (process == null || process.IsPhantom) return (false, "Cannot kill phantom process.");
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(process.ProcessId);
            proc.Kill(entireProcessTree: false);
            return (true, $"Process '{process.ProcessName}' (PID {process.ProcessId}) terminated.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to kill process: {ex.Message}");
        }
    }

    // ── Save / Reset ──────────────────────────────────────────────────────────
    public void SaveConfig() => ConfigService.Save(CurrentConfig);

    public void ResetAllData()
    {
        foreach (var p in Processes)
        {
            p.TotalDataUsed = 0; p.UploadSpeed = 0; p.DownloadSpeed = 0;
            foreach (var c in p.Connections) { c.TotalDataUsed = 0; c.UploadSpeed = 0; c.DownloadSpeed = 0; }
        }
        GlobalTotalDataUsed = 0; GlobalUploadSpeed = 0; GlobalDownloadSpeed = 0;
        // Also reset the ETW accumulator so speeds don't jump back on next tick
        _speedMonitor.ResetTotals();
    }

    // ── CSV Export ────────────────────────────────────────────────────────────
    public (bool ok, string path) ExportToCsv()
    {
        try
        {
            string dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
            string file = System.IO.Path.Combine(dir, $"WinNetControl_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var w = new System.IO.StreamWriter(file, false, System.Text.Encoding.UTF8);
            w.WriteLine("Process Name,PID,Type,Upload KB/s,Download KB/s,Total Data (bytes),Connections,Blocked,Block Inbound,Block Outbound");
            foreach (var p in Processes.OrderByDescending(x => x.TotalDataUsed))
            {
                w.WriteLine($"\"{p.ProcessName}\",{p.ProcessId},\"{p.AppType}\",{p.UploadSpeed:F2},{p.DownloadSpeed:F2},{p.TotalDataUsed},{p.ConnectionCount},{p.IsBlocked},{p.BlockInbound},{p.BlockOutbound}");
            }
            return (true, file);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ── Windows network tools ─────────────────────────────────────────────────
    public void OpenWindowsFirewall()         => FirewallService.OpenWindowsFirewall();
    public void OpenNetworkConnections()      => FirewallService.OpenNetworkConnections();
    public void OpenNetworkSettings()         => FirewallService.OpenNetworkSettings();
    public void OpenNetworkTroubleshooter()   => FirewallService.OpenNetworkTroubleshooter();

    public (bool ok, string output) RunFlushDns()           => FirewallService.FlushDns();
    public (bool ok, string output) RunResetWinsock()       => FirewallService.ResetWinsock();
    public (bool ok, string output) RunResetTcpIp()         => FirewallService.ResetTcpIp();
    public (bool ok, string output) RunReleaseIp()          => FirewallService.ReleaseIp();
    public (bool ok, string output) RunRenewIp()            => FirewallService.RenewIp();
    public (bool ok, string output) RunFlushArpCache()      => FirewallService.FlushArpCache();
    public (bool ok, string output) RunResetFirewall()      => FirewallService.ResetFirewallDefaults();
    public void ClearAllWinNetControlRules()                 => FirewallService.DeleteAllWinNetControlRules();

    // ── Speed update handler ──────────────────────────────────────────────────
    private void OnSpeedUpdated(System.Collections.Generic.Dictionary<int, NetworkSpeedInfo> speedData,
                                System.Collections.Generic.Dictionary<string, NetworkSpeedInfo> connSpeedData)
    {
        _dispatcherQueue?.TryEnqueue(() =>
        {
            double totalUp = 0, totalDown = 0;
            double maxUp = 1, maxDown = 1;

            foreach (var process in Processes)
            {
                if (process.IsPhantom) continue;

                if (speedData.TryGetValue(process.ProcessId, out var speedInfo))
                {
                    // EMA smoothing to reduce jitter
                    double newUp   = EmaAlpha * speedInfo.UploadSpeedKBps   + (1 - EmaAlpha) * process.UploadSpeed;
                    double newDown = EmaAlpha * speedInfo.DownloadSpeedKBps + (1 - EmaAlpha) * process.DownloadSpeed;

                    process.UploadSpeed   = newUp;
                    process.DownloadSpeed = newDown;
                    process.TotalDataUsed = speedInfo.TotalUploadBytes + speedInfo.TotalDownloadBytes;
                    totalUp   += newUp;
                    totalDown += newDown;
                    if (newUp   > maxUp)   maxUp   = newUp;
                    if (newDown > maxDown) maxDown = newDown;

                    // Push sparkline sample (#13)
                    process.PushSpeedSample(newUp + newDown);

                    // Usage alert (#16) — warn at 500 MB even without a set limit
                    if (process.TotalDataUsed > 500L * 1024 * 1024 && !process.IsDataLimitExceeded
                        && !CurrentConfig.DataLimits.ContainsKey(process.ProcessName))
                    {
                        process.IsDataLimitExceeded = true;
                        ShowDataLimitToast(process.ProcessName, process.TotalDataUsed, 500L * 1024 * 1024);
                    }

                    // Check explicit data limit
                    if (CurrentConfig.DataLimits.TryGetValue(process.ProcessName, out long limit) &&
                        limit > 0 && process.TotalDataUsed > limit && !process.IsDataLimitExceeded)
                    {
                        process.IsDataLimitExceeded = true;
                        ShowDataLimitToast(process.ProcessName, process.TotalDataUsed, limit);
                    }

                    // Suspicious process detector (#26)
                    if (!process.IsSuspicious)
                    {
                        bool noPath   = string.IsNullOrEmpty(process.ProcessPath);
                        bool tempPath = process.ProcessPath?.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) == true
                                     || process.ProcessPath?.Contains("\\AppData\\Local\\Temp\\", StringComparison.OrdinalIgnoreCase) == true;
                        bool randName = process.ProcessName.Length >= 8
                                     && System.Text.RegularExpressions.Regex.IsMatch(process.ProcessName, @"^[a-z0-9]{8,}$");
                        process.IsSuspicious = noPath || tempPath || randName;
                    }
                }
                else { process.UploadSpeed = 0; process.DownloadSpeed = 0; }

                // Register ETW-observed connections
                foreach (var kvp in connSpeedData)
                {
                    string[] parts = kvp.Key.Split('|');
                    if (parts.Length != 4) continue;
                    if (!int.TryParse(parts[0], out int pid) || pid != process.ProcessId) continue;

                    string protocol = parts[1];
                    string[] local  = parts[2].Split(':');
                    string[] remote = parts[3].Split(':');
                    if (local.Length != 2 || remote.Length != 2) continue;
                    if (!int.TryParse(local[1],  out int localPort))  continue;
                    if (!int.TryParse(remote[1], out int remotePort)) continue;

                    var ec = process.Connections.FirstOrDefault(c =>
                        c.Protocol == protocol && c.LocalAddress == local[0] && c.LocalPort == localPort &&
                        c.RemoteAddress == remote[0] && c.RemotePort == remotePort);

                    if (ec == null)
                    {
                        process.Connections.Add(new ProcessConnection
                        {
                            ProcessId = pid, Protocol = protocol,
                            LocalAddress = local[0], LocalPort = localPort,
                            RemoteAddress = remote[0], RemotePort = remotePort,
                            State = "Observed", IsActive = true, LastActiveTime = DateTime.Now
                        });
                    }
                }

                // Update connection speeds, age out stale ones
                for (int i = process.Connections.Count - 1; i >= 0; i--)
                {
                    var conn    = process.Connections[i];
                    string cKey = $"{process.ProcessId}|{conn.Protocol}|{conn.LocalAddress}:{conn.LocalPort}|{conn.RemoteAddress}:{conn.RemotePort}";

                    if (connSpeedData.TryGetValue(cKey, out var cs) && (cs.UploadSpeedKBps > 0 || cs.DownloadSpeedKBps > 0))
                    {
                        conn.UploadSpeed = cs.UploadSpeedKBps; conn.DownloadSpeed = cs.DownloadSpeedKBps;
                        conn.TotalDataUsed = cs.TotalUploadBytes + cs.TotalDownloadBytes;
                        conn.LastActiveTime = DateTime.Now; conn.IsActive = true;
                        if (conn.State == "Inactive") conn.State = "Observed";
                    }
                    else { conn.UploadSpeed = 0; conn.DownloadSpeed = 0; }

                    double ageSeconds = (DateTime.Now - conn.LastActiveTime).TotalSeconds;
                    // Don't age out pinned connections
                    if (!conn.IsPinned)
                    {
                        if (ageSeconds > 60) process.Connections.RemoveAt(i);
                        else if (ageSeconds > 2) { conn.IsActive = false; if (conn.State == "Observed") conn.State = "Inactive"; }
                    }
                }

                // Sort connections: pinned → most data used
                var sortedConns = process.Connections.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.TotalDataUsed).ToList();
                for (int i = 0; i < sortedConns.Count; i++)
                {
                    int cur = process.Connections.IndexOf(sortedConns[i]);
                    if (cur != i) process.Connections.Move(cur, i);
                }
            }

            // Update speed ratios for all processes (mini speed bars)
            foreach (var p in Processes)
                p.SetMaxSpeeds(maxUp, maxDown);

            GlobalUploadSpeed   = totalUp;
            GlobalDownloadSpeed = totalDown;
            // Exclude phantoms from total data (they have no real traffic)
            GlobalTotalDataUsed = Processes.Where(p => !p.IsPhantom).Sum(p => p.TotalDataUsed);

            if (SelectedSort is "Upload Speed" or "Download Speed" or "Data Used (High-Low)")
                ApplyFilterAndSort();
        });
    }

    private static void ShowDataLimitToast(string processName, long usedBytes, long limitBytes)
    {
        // Best-effort: append to a notification log; full toast integration requires MSIX
        try
        {
            string msg = $"[{DateTime.Now:HH:mm:ss}] DATA LIMIT: '{processName}' used {usedBytes / (1024.0 * 1024):F1} MB of {limitBytes / (1024.0 * 1024):F0} MB limit\n";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data_limits.log"), msg);
        }
        catch { }
    }
}
