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
    private readonly VirusTotalService _vtService = new();
    private int _speedSortTickCount; // throttles speed-based re-sorts to every ~4.5 s

    // FIX Bug#18: keep a hard reference so the timer is not garbage-collected
    // immediately (local 'var' in the constructor was eligible for GC at the
    // first collection after construction, causing the session clock to stop).
    private System.Threading.Timer? _sessionTimer;

    // FIX Bug#14: track in-flight VT scans by file path so a second button-click
    // while a scan is already running (or queued in the rate-limiter) is a no-op
    // rather than launching another concurrent API call that wastes quota.
    private readonly System.Collections.Generic.HashSet<string> _vtScanningPaths
        = new(StringComparer.OrdinalIgnoreCase);

    // FIX Bug#30: track in-flight block/unblock operations by process name.
    // Rapid double-clicks on the block toggle can fire two parallel firewall
    // rule additions, creating duplicate WinNetControl_Block_<name> rules that
    // are never cleaned up. Drop the second call while the first is in-flight.
    private readonly System.Collections.Generic.HashSet<string> _blockingInProgress
        = new(StringComparer.OrdinalIgnoreCase);


    public AppConfig CurrentConfig { get; internal set; }
    public HttpProxyService ProxyService { get; }
    public WinNetControl.Core.AppIconCache IconCache { get; } = new();

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

        _networkMonitor = new NetworkMonitorService(_dispatcherQueue);

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

        // FIX Bug#18: assign to a field (not a local var) so the timer is never
        // collected by the GC between constructor return and actual use.
        _sessionTimer = new System.Threading.Timer(_ =>
        {
            _dispatcherQueue?.TryEnqueue(() => OnPropertyChanged(nameof(SessionDuration)));
        }, null, 1000, 1000);

        // IMP#11: Apply saved VT cache TTL from config
        if (CurrentConfig.VtCacheTtlDays > 0)
            _vtService.CacheTtl = TimeSpan.FromDays(CurrentConfig.VtCacheTtlDays);
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
                    BlockOutbound = CurrentConfig.BlockedAppsOutbound.Contains(name),
                    Notes         = CurrentConfig.AppNotes.TryGetValue(name, out string? notes) ? notes : string.Empty
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
    [ObservableProperty] private long _globalTotalSent;
    [ObservableProperty] private Type? _currentPageType;

    // Payload for passing IP to PacketJourneyPage
    [ObservableProperty] private string _targetPacketJourneyIp = "";

    [ObservableProperty] private long _globalTotalReceived; // bytes downloaded this session

    // Pre-formatted stats bar strings
    public string GlobalUploadText       => FormatSpeed(GlobalUploadSpeed);
    public string GlobalDownloadText     => FormatSpeed(GlobalDownloadSpeed);
    public string GlobalTotalText        => FormatSize(GlobalTotalDataUsed);
    public string GlobalTotalSentText    => FormatSize(GlobalTotalSent);
    public string GlobalTotalReceivedText => FormatSize(GlobalTotalReceived);
    public string BlockedCountText       => $"{Processes.Count(p => p.IsBlocked)} blocked";

    partial void OnGlobalUploadSpeedChanged(double value)    => OnPropertyChanged(nameof(GlobalUploadText));
    partial void OnGlobalDownloadSpeedChanged(double value)  => OnPropertyChanged(nameof(GlobalDownloadText));
    partial void OnGlobalTotalDataUsedChanged(long value)    => OnPropertyChanged(nameof(GlobalTotalText));
    partial void OnGlobalTotalSentChanged(long value)        => OnPropertyChanged(nameof(GlobalTotalSentText));
    partial void OnGlobalTotalReceivedChanged(long value)    => OnPropertyChanged(nameof(GlobalTotalReceivedText));

    // Last-refreshed indicator — updated at the end of every OnConnectionsUpdated tick
    [ObservableProperty] private DateTime _lastRefreshed = DateTime.Now;
    public string LastRefreshedText
    {
        get
        {
            var elapsed = DateTime.Now - LastRefreshed;  // use generated property, not backing field
            if (elapsed.TotalSeconds <  5)  return "Updated just now";
            if (elapsed.TotalSeconds < 60)  return $"Updated {(int)elapsed.TotalSeconds}s ago";
            return $"Updated {(int)elapsed.TotalMinutes}m ago";
        }
    }
    partial void OnLastRefreshedChanged(DateTime value) => OnPropertyChanged(nameof(LastRefreshedText));

    /// <summary>Triggers an immediate poll without waiting for the 1.5-second cycle.</summary>
    public void ForceRefresh() => _networkMonitor.ForceRefresh();

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

    /// <summary>Top 5 apps by total data used — updated each refresh cycle (#12).</summary>
    [ObservableProperty] private ObservableCollection<ProcessNetworkInfo> _topConsumers = new();

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

    // ── Protocol filter (#9) ──────────────────────────────────────────────────
    public ObservableCollection<string> ProtocolFilters { get; } = new()
        { "All Proto", "TCP only", "UDP only", "Has TCP+UDP" };

    private string _selectedProtocol = "All Proto";
    public string SelectedProtocol
    {
        get => _selectedProtocol;
        set { if (SetProperty(ref _selectedProtocol, value)) ApplyFilterAndSort(); }
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
        // Snapshot connections on background thread before dispatching (thread-safety)
        var frozenSnapshot = activeProcesses
            .Select(p => (proc: p, conns: p.CurrentConnections.ToList()))
            .ToList();

        _dispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                var existingPids = new System.Collections.Generic.HashSet<int>();
                bool anyStructuralChange = false; // tracks add/remove/block visibility change

                foreach (var (newProc, currentConns) in frozenSnapshot)
                {
                    existingPids.Add(newProc.ProcessId);

                    var existingProc = Processes.FirstOrDefault(p =>
                        p.ProcessId == newProc.ProcessId &&
                        string.Equals(p.ProcessName, newProc.ProcessName, StringComparison.OrdinalIgnoreCase));

                    if (existingProc != null)
                    {
                        // Track visibility change (phantom toggle affects filter)
                        if (existingProc.IsPhantom != newProc.IsPhantom)
                        {
                            existingProc.IsPhantom = newProc.IsPhantom;
                            anyStructuralChange = true;
                        }

                        // FLICKER FIX: skip connection merging for blocked/phantom processes.
                        // Blocked processes have no live TCP connections (firewalled) so
                        // currentConns is empty every tick. Merging would RemoveAt() every
                        // existing connection, firing CollectionChanged repeatedly → shimmer.
                        // Their connections are already correct from when they were active.
                        if (!existingProc.IsBlocked && !existingProc.IsPhantom)
                        {
                            var newConnKeys = new System.Collections.Generic.HashSet<string>();
                            foreach (var c in currentConns)
                            {
                                string key = $"{c.Protocol}-{c.LocalPort}-{c.RemotePort}";
                                newConnKeys.Add(key);
                                var existing = existingProc.Connections.FirstOrDefault(
                                    o => o.Protocol == c.Protocol && o.LocalPort == c.LocalPort && o.RemotePort == c.RemotePort);
                                if (existing != null)
                                    existing.State = c.State;
                                else
                                {
                                    existingProc.Connections.Add(c);
                                    EnrichGeoIp(c);
                                }
                            }

                            for (int j = existingProc.Connections.Count - 1; j >= 0; j--)
                            {
                                var o   = existingProc.Connections[j];
                                string key = $"{o.Protocol}-{o.LocalPort}-{o.RemotePort}";
                                if (!newConnKeys.Contains(key))
                                    existingProc.Connections.RemoveAt(j);
                            }

                            existingProc.RefreshConnectionStats();
                        }
                    }
                    else
                    {
                        // ── New process ─────────────────────────────────────────────────
                        anyStructuralChange = true;

                        // If PID exists with different name (OS reuse), remove stale entry
                        var stale = Processes.FirstOrDefault(p =>
                            p.ProcessId == newProc.ProcessId && !p.IsPhantom);
                        if (stale != null) Processes.Remove(stale);

                        string name = newProc.ProcessName;
                        newProc.IsPinned             = CurrentConfig.PinnedApps.Contains(name);
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
                            ShowNewAppToast(name, newProc.ProcessPath ?? "");
                        }

                        foreach (var c in currentConns)
                        {
                            var rec = CurrentConfig.BlockedConnections.FirstOrDefault(r =>
                                string.Equals(r.ProcessName, name, StringComparison.OrdinalIgnoreCase) &&
                                r.RemoteAddress == c.RemoteAddress && r.RemotePort == c.RemotePort);
                            if (rec != null) { c.IsBlocked = true; c.BlockInbound = rec.BlockInbound; c.BlockOutbound = rec.BlockOutbound; }
                        }

                        newProc.Connections.Clear();
                        foreach (var c in currentConns) { newProc.Connections.Add(c); EnrichGeoIp(c); }
                        newProc.RefreshConnectionStats();

                        newProc.IsNewlyDiscovered = true;
                        newProc.DiscoveredAt      = DateTime.Now;

                        var phantom = Processes.FirstOrDefault(p => p.IsPhantom &&
                            string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase));
                        if (phantom != null) Processes.Remove(phantom);

                        if (CurrentConfig.AppNotes.TryGetValue(name, out string? appNotes))
                            newProc.Notes = appNotes;

                        Processes.Add(newProc);

                        // Restore cached VT result (no API call — instant from disk)
                        if (!string.IsNullOrWhiteSpace(newProc.ProcessPath))
                        {
                            var cached = _vtService.TryGetCachedByPath(newProc.ProcessPath);
                            if (cached != null)
                            {
                                newProc.VtStatus = cached.Status;
                                newProc.VtScore  = cached.Message;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(newProc.ProcessPath))
                        {
                            System.Threading.Tasks.Task.Run(async () =>
                            {
                                var img = await IconCache.GetIconAsync(newProc.ProcessPath);
                                if (img != null)
                                    _dispatcherQueue.TryEnqueue(() => newProc.AppIcon = img);
                            });
                        }

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
                    {
                        Processes.RemoveAt(i);
                        anyStructuralChange = true;
                    }
                }

                // Expire "NEW" badge after 30 s
                foreach (var p in Processes)
                    if (p.IsNewlyDiscovered && (DateTime.Now - p.DiscoveredAt).TotalSeconds > 30)
                        p.IsNewlyDiscovered = false;

                LastRefreshed = DateTime.Now;
                OnPropertyChanged(nameof(LastRefreshedText));
                if (anyStructuralChange) OnPropertyChanged(nameof(BlockedCountText));

                // Speed-sort throttle: re-sorting every 1.5 s causes continuous Move() calls
                // on FilteredProcesses → animation on every tick → visible jitter.
                // Structural changes always sort immediately; speed re-sort is capped to every 4 s.
                _speedSortTickCount++;
                bool speedSort = (SelectedSort == "Upload Speed" ||
                                  SelectedSort == "Download Speed" ||
                                  SelectedSort == "Data Used (High-Low)") &&
                                  _speedSortTickCount % 3 == 0; // every 3rd tick ≈ 4.5 s
                if (anyStructuralChange || speedSort)
                    ApplyFilterAndSort();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionManager] OnConnectionsUpdated: {ex.Message}\n{ex.StackTrace}");
            }
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

        if (SelectedProtocol == "TCP only")
            query = query.Where(p => p.Connections.Any(c => c.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                                  && !p.Connections.Any(c => c.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)));
        else if (SelectedProtocol == "UDP only")
            query = query.Where(p => p.Connections.Any(c => c.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase))
                                  && !p.Connections.Any(c => c.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)));
        else if (SelectedProtocol == "Has TCP+UDP")
            query = query.Where(p => p.Connections.Any(c => c.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
                                  && p.Connections.Any(c => c.Protocol.StartsWith("UDP", StringComparison.OrdinalIgnoreCase)));

        IOrderedEnumerable<ProcessNetworkInfo> orderedQuery = SelectedSort switch
        {
            "Data Used (High-Low)" => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.TotalDataUsed),
            "Upload Speed"         => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.UploadSpeed),
            "Download Speed"       => query.OrderByDescending(p => p.IsPinned).ThenByDescending(p => p.DownloadSpeed),
            _                      => query.OrderByDescending(p => p.IsPinned).ThenBy(p => p.ProcessName)
        };

        var sortedList = orderedQuery.ToList();

        // FLICKER FIX: if the resulting sorted list is already identical to FilteredProcesses
        // (same items in same order), skip all ObservableCollection mutations entirely.
        // Each Insert/Move/Remove fires CollectionChanged which causes the ListView to
        // re-render. On a 1.5-second tick with nothing changing this was causing constant
        // visual shimmer. Reference-equality is O(n) and extremely fast.
        if (sortedList.Count == FilteredProcesses.Count)
        {
            bool identical = true;
            for (int i = 0; i < sortedList.Count; i++)
            {
                if (!ReferenceEquals(sortedList[i], FilteredProcesses[i])) { identical = false; break; }
            }
            if (identical)
            {
                // Still update TopConsumers (it uses TotalDataUsed which changes every tick)
                UpdateTopConsumers();
                return;
            }
        }

        // Apply minimum-diff update to avoid full ListView rebuild
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

        UpdateTopConsumers();
    }

    private void UpdateTopConsumers()
    {
        // Update Top 5 consumers
        var top5 = Processes
            .Where(p => !p.IsPhantom && p.TotalDataUsed > 0)
            .OrderByDescending(p => p.TotalDataUsed)
            .Take(5).ToList();
        for (int i = TopConsumers.Count - 1; i >= 0; i--)
            if (!top5.Contains(TopConsumers[i])) TopConsumers.RemoveAt(i);
        for (int i = 0; i < top5.Count; i++)
        {
            var item = top5[i];
            int cur  = TopConsumers.IndexOf(item);
            if (cur == -1) TopConsumers.Insert(i, item);
            else if (cur != i) TopConsumers.Move(cur, i);
        }
    }

    // ── Block / Unblock ───────────────────────────────────────────────────────
    public void ToggleBlock(ProcessNetworkInfo process, bool? blockInbound = null, bool? blockOutbound = null)
    {
        if (process == null) return;

        string name = process.ProcessName;
        string path = process.ProcessPath;

        // BUG#30: drop duplicate calls while a firewall rule operation is in-flight
        lock (_blockingInProgress)
        {
            if (!_blockingInProgress.Add(name)) return;
        }

        try
        {
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
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { FirewallService.BlockApp(name, path, doIn, doOut); }
                        finally { lock (_blockingInProgress) _blockingInProgress.Remove(name); }
                    });
                else
                    lock (_blockingInProgress) _blockingInProgress.Remove(name);
            }
            else
            {
                process.BlockInbound  = false;
                process.BlockOutbound = false;

                CurrentConfig.BlockedApps.Remove(name);
                CurrentConfig.BlockedAppsInbound.Remove(name);
                CurrentConfig.BlockedAppsOutbound.Remove(name);
                _networkMonitor.KnownBlockedNames.Remove(name);

                System.Threading.Tasks.Task.Run(() =>
                {
                    try { FirewallService.UnblockApp(name); }
                    finally { lock (_blockingInProgress) _blockingInProgress.Remove(name); }
                });

                if (process.IsPhantom)
                    _dispatcherQueue?.TryEnqueue(() => { Processes.Remove(process); ApplyFilterAndSort(); });
            }
        }
        catch
        {
            // If we fail before launching the Task, make sure we release the lock
            lock (_blockingInProgress) _blockingInProgress.Remove(name);
            throw;
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

        process.RefreshConnectionStats();

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
        BlockedConnectionStore.NotifyBlockChange(procName, remoteAddr, remotePort, localPort, connection.IsBlocked);
    }

    /// <summary>
    /// Keeps the live connection model and persisted state aligned when a
    /// connection rule is removed directly from the Firewall page.
    /// </summary>
    public void RemoveConnectionBlockByEndpoint(string remoteAddress, int remotePort, int localPort)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress)) return;

        CurrentConfig.BlockedConnections.RemoveAll(record =>
            string.Equals(record.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase) &&
            record.RemotePort == remotePort && record.LocalPort == localPort);

        foreach (var process in Processes)
        {
            foreach (var connection in process.Connections.Where(connection =>
                         string.Equals(connection.RemoteAddress, remoteAddress, StringComparison.OrdinalIgnoreCase) &&
                         connection.RemotePort == remotePort && connection.LocalPort == localPort))
            {
                connection.IsBlocked = false;
                connection.BlockInbound = false;
                connection.BlockOutbound = false;
            }
            process.RefreshConnectionStats();
        }

        SaveConfig();
        // An empty process name intentionally targets every process with this endpoint.
        BlockedConnectionStore.NotifyBlockChange(string.Empty, remoteAddress, remotePort, localPort, false);
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
                else
                {
                    // SPARKLINE FIX: always push a 0-speed sample for processes not seen
                    // in this ETW window. Without this, idle processes skip ticks entirely
                    // and the 30-point ring buffer advances unevenly — making the graph
                    // look like it updates at an irregular rate.
                    process.UploadSpeed   = 0;
                    process.DownloadSpeed = 0;
                    process.PushSpeedSample(0);
                }


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
            var nonPhantoms = Processes.Where(p => !p.IsPhantom).ToList();
            GlobalTotalDataUsed  = nonPhantoms.Sum(p => p.TotalDataUsed);
            GlobalTotalSent     = nonPhantoms.Sum(p =>
                speedData.TryGetValue(p.ProcessId, out var si) ? si.TotalUploadBytes   : 0L);
            GlobalTotalReceived = nonPhantoms.Sum(p =>
                speedData.TryGetValue(p.ProcessId, out var si) ? si.TotalDownloadBytes : 0L);
            // Note: speed-based re-sort is handled by the throttle in OnConnectionsUpdated
        });
    }

    // ── VirusTotal ───────────────────────────────────────────────────────────────

    /// <summary>VirusTotal API key — read from AppConfig, editable in Settings.</summary>
    public string VtApiKey
    {
        get => CurrentConfig?.VirusTotalApiKey ?? string.Empty;
        set
        {
            if (CurrentConfig != null)
            {
                CurrentConfig.VirusTotalApiKey = value;
                OnPropertyChanged(nameof(VtApiKey));
            }
        }
    }

    /// <summary>
    /// VT cache TTL in days (Imp#11). Changing this updates the live service
    /// and persists to config so the next launch loads the user preference.
    /// </summary>
    public int VtCacheTtlDays
    {
        get => CurrentConfig?.VtCacheTtlDays ?? 30;
        set
        {
            int days = Math.Max(1, Math.Min(365, value));
            if (CurrentConfig != null)
            {
                CurrentConfig.VtCacheTtlDays = days;
                _vtService.CacheTtl = TimeSpan.FromDays(days);
                OnPropertyChanged(nameof(VtCacheTtlDays));
            }
        }
    }

    /// <summary>
    /// Wipes the in-memory and on-disk VT cache. All files will be re-scanned
    /// on the next VirusTotal check. Called from the Settings "Clear Cache" button.
    /// </summary>
    public void ClearVtCache() => _vtService.ClearCache();

    /// <summary>
    /// Kicks off a VirusTotal scan for the given process.
    /// - Cache hit  → instant result, no API call, no spinner.
    /// - Cache miss → shows Checking…, fires API, updates to real result.
    /// - Error/NotFound cache → forces a fresh API call so user can retry.
    /// </summary>
    public async System.Threading.Tasks.Task CheckVirusTotalAsync(Models.ProcessNetworkInfo process)
    {
        if (process == null || string.IsNullOrWhiteSpace(process.ProcessPath)) return;
        if (string.IsNullOrWhiteSpace(VtApiKey))
        {
            process.VtStatus = Core.VtStatus.Error;
            process.VtScore  = "No API key — add one in Settings";
            return;
        }

        // Fast-path: return cached result for clean/suspicious/malicious hits
        // (skip cache for Error/NotFound so the user can force a retry by clicking again)
        var cached = _vtService.TryGetCachedByPath(process.ProcessPath);
        if (cached != null &&
            cached.Status != Core.VtStatus.Error &&
            cached.Status != Core.VtStatus.NotFound)
        {
            process.VtStatus = cached.Status;
            process.VtScore  = cached.Message;
            return;
        }

        // FIX Bug#14: bail if an in-flight scan for the same file is already running.
        // Without this guard a rapid double-click would queue two concurrent API calls
        // for the same hash, wasting one of the 4 req/min quota slots.
        string filePath = process.ProcessPath;
        lock (_vtScanningPaths)
        {
            if (_vtScanningPaths.Contains(filePath)) return;
            _vtScanningPaths.Add(filePath);
        }

        // Show checking state synchronously (already on UI thread via async void caller)
        process.VtStatus = Core.VtStatus.Checking;
        process.VtScore  = string.Empty;

        VtResult result;
        try
        {
            result = await _vtService.CheckFileAsync(filePath, VtApiKey);
        }
        catch (Exception ex)
        {
            result = new VtResult { Status = Core.VtStatus.Error, Message = $"Unexpected: {ex.Message}" };
        }
        finally
        {
            // Always release the scan lock so the user can retry later
            lock (_vtScanningPaths) _vtScanningPaths.Remove(filePath);
        }

        // Friendly score labels for non-numeric statuses
        string displayScore = result.Status switch
        {
            Core.VtStatus.NotFound => "Not in DB",
            Core.VtStatus.Error    => result.Message switch
            {
                var m when m.Contains("Rate limit") => "Quota exceeded",
                var m when m.Contains("Invalid API") => "Bad API key",
                var m when m.Contains("Network")    => "Network error",
                _                                   => result.Message
            },
            _ => result.Message
        };

        _dispatcherQueue?.TryEnqueue(() =>
        {
            process.VtStatus = result.Status;
            process.VtScore  = displayScore;
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

    // ── New-app alert toast (#30) ─────────────────────────────────────────────
    private static readonly System.Collections.Generic.HashSet<string> _toastedApps = new();
    private static void ShowNewAppToast(string processName, string path)
    {
        // Only alert once per app per session
        if (!_toastedApps.Add(processName)) return;
        try
        {
            string shortPath = string.IsNullOrEmpty(path) ? "unknown path"
                : System.IO.Path.GetFileName(path);
            string msg = $"[{DateTime.Now:HH:mm:ss}] NEW APP: '{processName}' ({shortPath}) accessed the network\n";
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "new_apps.log"), msg);
        }
        catch { }
    }

    // ── Geo-IP enrichment (#18) ───────────────────────────────────────────────
    private void EnrichGeoIp(WinNetControl.Models.ProcessConnection conn)
    {
        string ip = conn.RemoteAddress;
        if (string.IsNullOrEmpty(ip)) return;

        // Synchronous fast-path from cache
        string cached = WinNetControl.Core.GeoIpService.GetCountryLabel(ip);
        if (!string.IsNullOrEmpty(cached)) { conn.GeoCountry = cached; return; }

        // Async fetch → marshal to UI thread
        System.Threading.Tasks.Task.Run(async () =>
        {
            string label = await WinNetControl.Core.GeoIpService.GetCountryLabelAsync(ip);
            if (!string.IsNullOrEmpty(label))
                _dispatcherQueue?.TryEnqueue(() => conn.GeoCountry = label);
        });
    }
}
