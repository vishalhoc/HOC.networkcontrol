using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNetControl.Models;

public partial class ProcessConnection : ObservableObject
{
    [ObservableProperty] public partial string Protocol      { get; set; } = string.Empty;
    [ObservableProperty] public partial string LocalAddress  { get; set; } = string.Empty;
    [ObservableProperty] public partial int    LocalPort     { get; set; }
    [ObservableProperty] public partial string RemoteAddress { get; set; } = string.Empty;
    [ObservableProperty] public partial int    RemotePort    { get; set; }
    [ObservableProperty] public partial string State         { get; set; } = string.Empty;
    [ObservableProperty] public partial bool   IsBlocked     { get; set; }
    [ObservableProperty] public partial int             ProcessId      { get; set; }
    [ObservableProperty] public partial System.DateTime LastActiveTime { get; set; } = System.DateTime.Now;
    [ObservableProperty] public partial bool             IsActive       { get; set; } = true;
    [ObservableProperty] public partial bool             IsPinned       { get; set; }

    // Direction-aware blocking
    [ObservableProperty] public partial bool   BlockInbound  { get; set; }
    [ObservableProperty] public partial bool   BlockOutbound { get; set; }

    // Geo-IP country label (#18) — e.g. "🇺🇸 US"
    [ObservableProperty] public partial string GeoCountry    { get; set; } = string.Empty;

    // Is this a listening / inbound connection (no remote address)
    public bool IsInbound => string.IsNullOrWhiteSpace(RemoteAddress)
                          || RemoteAddress == "0.0.0.0"
                          || RemoteAddress == "::"
                          || RemoteAddress == "*"
                          || State == "LISTEN"
                          || State == "LISTENING";

    private long   _totalDataUsed;
    public  long   TotalDataUsed  { get => _totalDataUsed;  set => SetProperty(ref _totalDataUsed, value); }

    private double _uploadSpeed;
    public  double UploadSpeed    { get => _uploadSpeed;    set => SetProperty(ref _uploadSpeed,   System.Math.Round(value, 1)); }

    private double _downloadSpeed;
    public  double DownloadSpeed  { get => _downloadSpeed;  set => SetProperty(ref _downloadSpeed, System.Math.Round(value, 1)); }

    public string LocalAddressPort  => $"{LocalAddress}:{LocalPort}";
    public string RemoteAddressPort => RemotePort > 0
        ? $"{RemoteAddress}:{RemotePort}"
        : (string.IsNullOrWhiteSpace(RemoteAddress) || RemoteAddress == "0.0.0.0" || RemoteAddress == "::" ? "*" : RemoteAddress);

    // Human-readable direction badge text
    public string DirectionText => IsInbound ? "IN" : "OUT";

    /// <summary>Converts numeric TCP state codes to named strings (e.g. "5" → "ESTABLISHED").</summary>
    public string NormalizedState => State.Trim() switch
    {
        "1"  => "CLOSED",
        "2"  => "LISTEN",
        "3"  => "SYN_SENT",
        "4"  => "SYN_RCVD",
        "5"  => "ESTABLISHED",
        "6"  => "FIN_WAIT",
        "7"  => "CLOSE_WAIT",
        "8"  => "CLOSING",
        "9"  => "LAST_ACK",
        "10" => "TIME_WAIT",
        "11" => "DELETE_TCB",
        "12" => "INACTIVE",
        _    => string.IsNullOrWhiteSpace(State) ? "N/A" : State.ToUpperInvariant()
    };

    public bool IsSuspicious
    {
        get
        {
            if (string.IsNullOrEmpty(RemoteAddress) || RemoteAddress == "*") return false;
            // Simple heuristic for demo: Ports associated with suspicious activity or direct IP without DNS
            int[] suspiciousPorts = { 4444, 3389, 22, 23, 6667 };
            foreach (var p in suspiciousPorts)
                if (RemotePort == p) return true;
            return false;
        }
    }
}

public partial class ProcessNetworkInfo : ObservableObject
{
    [ObservableProperty] public partial int    ProcessId   { get; set; }
    [ObservableProperty] public partial string ProcessName { get; set; } = string.Empty;
    [ObservableProperty] public partial string ProcessPath { get; set; } = string.Empty;
    [ObservableProperty] public partial string                                    AppType { get; set; } = string.Empty;
    [ObservableProperty] public partial Microsoft.UI.Xaml.Media.ImageSource?     AppIcon { get; set; }

    // Used beneath the extracted executable icon, and remains visible for
    // protected Windows processes or offline entries that have no file path.
    public string AppIconGlyph => ProcessName.ToLowerInvariant() switch
    {
        var name when name.Contains("chrome") || name.Contains("msedge") || name.Contains("firefox") || name.Contains("opera") => "\uE774",
        var name when name.Contains("explorer") => "\uE8B7",
        var name when name.Contains("service") || name.Contains("system") || name.Contains("svchost") => "\uE713",
        var name when name.Contains("powershell") || name.Contains("cmd") || name.Contains("terminal") => "\uE756",
        _ => "\uE8A5"
    };

    // Is this a phantom (offline / not currently running) entry kept for blocked-app display?
    [ObservableProperty] public partial bool IsPhantom { get; set; }

    private bool _isBlocked;
    public  bool IsBlocked   { get => _isBlocked;   set => SetProperty(ref _isBlocked, value); }

    // Direction-aware blocking
    private bool _blockInbound;
    public  bool BlockInbound  { get => _blockInbound;  set { if (SetProperty(ref _blockInbound,  value)) OnPropertyChanged(nameof(BlockStatusText)); } }

    private bool _blockOutbound;
    public  bool BlockOutbound { get => _blockOutbound; set { if (SetProperty(ref _blockOutbound, value)) OnPropertyChanged(nameof(BlockStatusText)); } }

    public string BlockStatusText => (BlockInbound, BlockOutbound) switch
    {
        (true,  true)  => "Blocked (In+Out)",
        (true,  false) => "Blocked Inbound",
        (false, true)  => "Blocked Outbound",
        _              => "Not Blocked"
    };

    // Dead-band: only fire PropertyChanged when speed changes by more than this threshold.
    // Prevents per-row re-renders for tiny fluctuations (e.g. 1.2 → 1.3 KB/s).
    private const double SpeedDeadBandKbps = 2.0;

    private double _uploadSpeed;
    public  double UploadSpeed
    {
        get => _uploadSpeed;
        set
        {
            double rounded = System.Math.Round(value, 1);
            if (System.Math.Abs(rounded - _uploadSpeed) >= SpeedDeadBandKbps || (rounded == 0 && _uploadSpeed != 0))
            {
                if (SetProperty(ref _uploadSpeed, rounded))
                    OnPropertyChanged(nameof(UploadSpeedRatio));
            }
            else
            {
                _uploadSpeed = rounded; // update backing field silently (keeps sorting accurate)
            }
        }
    }

    private double _downloadSpeed;
    public  double DownloadSpeed
    {
        get => _downloadSpeed;
        set
        {
            double rounded = System.Math.Round(value, 1);
            if (System.Math.Abs(rounded - _downloadSpeed) >= SpeedDeadBandKbps || (rounded == 0 && _downloadSpeed != 0))
            {
                if (SetProperty(ref _downloadSpeed, rounded))
                    OnPropertyChanged(nameof(DownloadSpeedRatio));
            }
            else
            {
                _downloadSpeed = rounded;
            }
        }
    }


    private long _totalDataUsed;
    public  long TotalDataUsed
    {
        get => _totalDataUsed;
        set
        {
            // Only notify when tier changes OR value crosses a 4 KB display threshold.
            // Byte-level changes fire thousands of notifications per minute — all invisible.
            bool tierWillChange = DataUsageTierFor(value) != DataUsageTierFor(_totalDataUsed);
            bool thresholdCrossed = System.Math.Abs(value - _totalDataUsed) >= 4096;
            _totalDataUsed = value;
            if (tierWillChange)
            {
                OnPropertyChanged(nameof(TotalDataUsed));
                OnPropertyChanged(nameof(DataUsageTier));
            }
            else if (thresholdCrossed)
            {
                OnPropertyChanged(nameof(TotalDataUsed));
            }
        }
    }

    private static int DataUsageTierFor(long bytes) =>
        bytes < 1024L * 1024 * 10   ? 0 :
        bytes < 1024L * 1024 * 100  ? 1 :
        bytes < 1024L * 1024 * 1024 ? 2 : 3;


    // Speed ratios relative to the global max — set externally by ViewModel each update cycle
    private double _maxUploadKbps  = 1;
    private double _maxDownloadKbps = 1;

    public void SetMaxSpeeds(double maxUp, double maxDown)
    {
        _maxUploadKbps  = System.Math.Max(maxUp,  1);
        _maxDownloadKbps = System.Math.Max(maxDown, 1);
        OnPropertyChanged(nameof(UploadSpeedRatio));
        OnPropertyChanged(nameof(DownloadSpeedRatio));
    }

    /// <summary>0.0–1.0 ratio of this process's upload vs the global max (for mini speed bar).</summary>
    public double UploadSpeedRatio   => System.Math.Clamp(UploadSpeed   / _maxUploadKbps,  0, 1);
    /// <summary>0.0–1.0 ratio of this process's download vs the global max (for mini speed bar).</summary>
    public double DownloadSpeedRatio => System.Math.Clamp(DownloadSpeed / _maxDownloadKbps, 0, 1);

    private bool _showFloatingWidget;
    public  bool ShowFloatingWidget { get => _showFloatingWidget; set => SetProperty(ref _showFloatingWidget, value); }

    private bool _isPinned;
    public  bool IsPinned { get => _isPinned; set => SetProperty(ref _isPinned, value); }

    private bool _isHttpCaptureEnabled;
    public  bool IsHttpCaptureEnabled { get => _isHttpCaptureEnabled; set => SetProperty(ref _isHttpCaptureEnabled, value); }

    // Data limit exceeded flag (set by ViewModel when limit is hit)
    private bool _isDataLimitExceeded;
    public  bool IsDataLimitExceeded { get => _isDataLimitExceeded; set => SetProperty(ref _isDataLimitExceeded, value); }

    // Network adapter name used by this app's connections (e.g. "Ethernet", "Wi-Fi")
    private string _adapterName = string.Empty;
    public  string AdapterName { get => _adapterName; set => SetProperty(ref _adapterName, value); }

    public System.Collections.ObjectModel.ObservableCollection<ProcessConnection> Connections { get; } = new();

    // Background-thread-safe snapshot
    public System.Collections.Generic.List<ProcessConnection> CurrentConnections { get; set; } = new();

    public int ConnectionCount => Connections.Count;
    public int BlockedConnectionCount => System.Linq.Enumerable.Count(Connections, c => c.IsBlocked);
    public string ConnectionStatsText => $"{ConnectionCount} sockets ({BlockedConnectionCount} blocked)";

    /// <summary>Refreshes all connection-derived computed properties.</summary>
    public void RefreshConnectionStats()
    {
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(BlockedConnectionCount));
        OnPropertyChanged(nameof(ConnectionStatsText));
    }


    // ── Launch Count and Parent Process (#31, #32) ───────────────────────────
    private int _launchCount = 1;
    public int LaunchCount { get => _launchCount; set => SetProperty(ref _launchCount, value); }

    private string _parentProcessName = "Unknown";
    public string ParentProcessName { get => _parentProcessName; set => SetProperty(ref _parentProcessName, value); }

    // ── Sparkline history (#13) ───────────────────────────────────────────────
    // Rolling 30-point window of combined (upload+download) KB/s readings.
    // Stored as a ring buffer; _speedHistoryIdx points to the NEXT write slot.
    private readonly double[] _speedHistory = new double[30];
    private int _speedHistoryIdx;

    /// <summary>
    /// Returns the 30 speed samples in chronological order (oldest first).
    /// Returns a NEW array copy every call so the SparklineControl DependencyProperty
    /// always sees a reference change and triggers a canvas redraw.
    /// </summary>
    public IReadOnlyList<double> SpeedHistory
    {
        get
        {
            int n   = _speedHistory.Length;   // 30

            // BUG#19 FIX: until the ring buffer has wrapped at least once,
            // _speedHistoryIdx is the EXACT count of samples pushed.
            // Reading from _speedHistoryIdx % n would start in the middle of
            // uninitialised zero-slots and return phantom data-points.
            if (_speedHistoryIdx < n)
            {
                // Buffer not yet full: return only the samples we have so far,
                // oldest (index 0) → newest (index _speedHistoryIdx-1).
                var partial = new double[_speedHistoryIdx];
                for (int i = 0; i < _speedHistoryIdx; i++)
                    partial[i] = _speedHistory[i];
                return partial;
            }

            // Buffer full: unwrap ring — start is the oldest slot.
            var arr   = new double[n];
            int start = _speedHistoryIdx % n;
            for (int i = 0; i < n; i++)
                arr[i] = _speedHistory[(start + i) % n];
            return arr;
        }
    }

    public void PushSpeedSample(double combinedKbps)
    {
        _speedHistory[_speedHistoryIdx % 30] = combinedKbps;
        _speedHistoryIdx++;
        if (combinedKbps > 0) LastActiveTime = System.DateTime.Now;

        // Always fire — SpeedHistory returns a new clone each read, so the
        // SparklineControl DependencyProperty sees a reference change every tick
        // and calls OnSamplesChanged → Redraw().
        OnPropertyChanged(nameof(SpeedHistory));

        bool wasIdle = IsIdle;
        if (wasIdle != IsIdle) OnPropertyChanged(nameof(IsIdle));
    }

    private System.DateTime _lastActiveTime = System.DateTime.Now;
    public System.DateTime LastActiveTime { get => _lastActiveTime; set => SetProperty(ref _lastActiveTime, value); }
    
    public bool IsIdle => (System.DateTime.Now - LastActiveTime).TotalMinutes > 5;

    // ── Suspicious flag (#26) ─────────────────────────────────────────────────
    private bool _isSuspicious;
    public  bool IsSuspicious { get => _isSuspicious; set => SetProperty(ref _isSuspicious, value); }

    // ── Bulk-select checkbox (#36) ────────────────────────────────────────────
    private bool _isSelected;
    public  bool IsSelected   { get => _isSelected;   set => SetProperty(ref _isSelected, value); }

    // ── Data usage tier for badge color (#33) ────────────────────────────────
    /// <summary>0=none, 1=low(green), 2=medium(yellow), 3=high(red)</summary>
    public int DataUsageTier =>
        TotalDataUsed < 1024L * 1024 * 10           ? 0 :   // < 10 MB → no badge
        TotalDataUsed < 1024L * 1024 * 100          ? 1 :   // 10–100 MB → green
        TotalDataUsed < 1024L * 1024 * 1024         ? 2 :   // 100 MB–1 GB → yellow
        3;                                                   // > 1 GB → red

    // ── App Notes (#11) — user-editable annotation, persisted via AppConfig ───
    private string _notes = string.Empty;
    public  string Notes { get => _notes; set => SetProperty(ref _notes, value); }

    // ── Data limit in MB (#16) — 0 = no limit ────────────────────────────────
    private double _dataLimitMb;
    public  double DataLimitMb { get => _dataLimitMb; set => SetProperty(ref _dataLimitMb, value); }

    // ── Newly-discovered badge ──────────────────────────────────────────────────
    private bool _isNewlyDiscovered;
    public  bool IsNewlyDiscovered { get => _isNewlyDiscovered; set => SetProperty(ref _isNewlyDiscovered, value); }
    public DateTime DiscoveredAt { get; set; } = DateTime.MinValue;

    // ── VirusTotal scan result ─────────────────────────────────────────────────
    private Core.VtStatus _vtStatus = Core.VtStatus.Unknown;
    public  Core.VtStatus VtStatus
    {
        get => _vtStatus;
        set
        {
            if (SetProperty(ref _vtStatus, value))
            {
                OnPropertyChanged(nameof(VtBadgeText));
                OnPropertyChanged(nameof(VtTooltip));
            }
        }
    }

    private string _vtScore = string.Empty;
    public  string VtScore
    {
        get => _vtScore;
        set
        {
            if (SetProperty(ref _vtScore, value))
            {
                OnPropertyChanged(nameof(VtBadgeText));
                OnPropertyChanged(nameof(VtTooltip));
            }
        }
    }

    /// <summary>Short badge label shown in the process row (e.g. "✓ 0/72", "✗ 3/72", "VT?").</summary>
    public string VtBadgeText => VtStatus switch
    {
        Core.VtStatus.Clean      => $"\u2713 {VtScore}",
        Core.VtStatus.Suspicious => $"\u26A0 {VtScore}",
        Core.VtStatus.Malicious  => $"\u2717 {VtScore}",
        Core.VtStatus.NotFound   => "N/A",
        Core.VtStatus.Checking   => "\u29D7",  // ⏷ hourglass
        Core.VtStatus.Error      => "Err",
        _                        => "VT?"
    };

    /// <summary>Rich tooltip shown when hovering the VT badge button.</summary>
    public string VtTooltip => VtStatus switch
    {
        Core.VtStatus.Clean      => $"\u2705 Clean — {VtScore} engines flagged this file\nClick to re-scan",
        Core.VtStatus.Suspicious => $"\u26A0\uFE0F Suspicious — {VtScore} detections\nClick to re-scan",
        Core.VtStatus.Malicious  => $"\u274C Malicious — {VtScore} engines flagged this file as malware!\nClick to re-scan",
        Core.VtStatus.NotFound   => "File not found in VirusTotal database.\nIt may be too new or very rare.\nClick to re-scan.",
        Core.VtStatus.Checking   => "Scanning on VirusTotal…",
        Core.VtStatus.Error      => $"Scan error: {VtScore}\nClick to retry.",
        _                        => "Click to scan this file on VirusTotal.\nRequires an API key — add one in Settings \u2192 API Keys."
    };


    public ProcessNetworkInfo()
    {
        Connections.CollectionChanged += (s, e) => OnPropertyChanged(nameof(ConnectionCount));
    }
}

public partial class HttpRequestInfo : ObservableObject
{
    [ObservableProperty] public partial System.Guid   Id     { get; set; }
    [ObservableProperty] public partial string         Url    { get; set; } = string.Empty;
    [ObservableProperty] public partial string         Method { get; set; } = string.Empty;
    [ObservableProperty] public partial string          Host        { get; set; } = string.Empty;
    [ObservableProperty] public partial int             ProcessId   { get; set; }
    [ObservableProperty] public partial string          ProcessName { get; set; } = string.Empty;
    [ObservableProperty] public partial System.DateTime Timestamp   { get; set; } = System.DateTime.Now;
    [ObservableProperty] public partial int             StatusCode  { get; set; }
    [ObservableProperty] public partial string ContentType  { get; set; } = string.Empty;
    [ObservableProperty] public partial long   ResponseSize { get; set; }

    /// <summary>"200 OK", "404 Not Found" etc., empty if not yet received.</summary>
    public string StatusText => StatusCode > 0 ? $"{StatusCode}" : "—";
    /// <summary>Formatted timestamp HH:mm:ss.</summary>
    public string TimeText   => Timestamp.ToString("HH:mm:ss");
    /// <summary>Formatted response size.</summary>
    public string SizeText   => ResponseSize > 0
        ? (ResponseSize < 1024 ? $"{ResponseSize} B" : $"{ResponseSize / 1024.0:F1} KB") : "—";

    // Cascade notifications to computed properties when backing fields change
    partial void OnStatusCodeChanged(int value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusColorHex));
        OnPropertyChanged(nameof(RowColorHex));
    }
    partial void OnResponseSizeChanged(long value)         => OnPropertyChanged(nameof(SizeText));
    partial void OnTimestampChanged(System.DateTime value) => OnPropertyChanged(nameof(TimeText));
    partial void OnMethodChanged(string value)             => OnPropertyChanged(nameof(MethodColorHex));

    // ── Colour hex strings for XAML bindings (no WinUI dependency in model) ──
    public string RowColorHex    => StatusCode >= 400 ? "#0EF44336" : StatusCode >= 300 ? "#0EFF9800" : "Transparent";
    public string MethodColorHex => Method switch
    {
        "GET"    => "#2196F3",
        "POST"   => "#4CAF50",
        "PUT"    => "#FF9800",
        "DELETE" => "#F44336",
        "PATCH"  => "#9C27B0",
        _        => "#808080"
    };
    public string StatusColorHex => StatusCode switch
    {
        >= 500 => "#F44336",
        >= 400 => "#FF9800",
        >= 300 => "#2196F3",
        >= 200 => "#4CAF50",
        _      => "#808080"
    };
}

public partial class NetworkAdapterInfo : ObservableObject
{
    [ObservableProperty] public partial string Name            { get; set; } = string.Empty;
    [ObservableProperty] public partial int    InterfaceIndex  { get; set; }

    private bool _isSelected;
    public  bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke();
        }
    }

    public System.Action? SelectionChanged { get; set; }
}

