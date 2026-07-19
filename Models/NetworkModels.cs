using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinNetControl.Models;

public partial class ProcessConnection : ObservableObject
{
    [ObservableProperty] private string _protocol     = string.Empty;
    [ObservableProperty] private string _localAddress = string.Empty;
    [ObservableProperty] private int    _localPort;
    [ObservableProperty] private string _remoteAddress = string.Empty;
    [ObservableProperty] private int    _remotePort;
    [ObservableProperty] private string _state = string.Empty;
    [ObservableProperty] private bool   _isBlocked;
    [ObservableProperty] private int    _processId;
    [ObservableProperty] private System.DateTime _lastActiveTime = System.DateTime.Now;
    [ObservableProperty] private bool   _isActive = true;
    [ObservableProperty] private bool   _isPinned;

    // Direction-aware blocking
    [ObservableProperty] private bool _blockInbound;
    [ObservableProperty] private bool _blockOutbound;

    // Geo-IP country label (#18) — e.g. "🇺🇸 US"
    [ObservableProperty] private string _geoCountry = string.Empty;

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
    [ObservableProperty] private int    _processId;
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private string _processPath = string.Empty;
    [ObservableProperty] private string _appType     = string.Empty;
    [ObservableProperty] private Microsoft.UI.Xaml.Media.ImageSource? _appIcon;

    // Is this a phantom (offline / not currently running) entry kept for blocked-app display?
    [ObservableProperty] private bool _isPhantom;

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

    private double _uploadSpeed;
    public  double UploadSpeed   { get => _uploadSpeed;   set { if (SetProperty(ref _uploadSpeed, System.Math.Round(value, 1))) OnPropertyChanged(nameof(UploadSpeedRatio)); } }

    private double _downloadSpeed;
    public  double DownloadSpeed { get => _downloadSpeed; set { if (SetProperty(ref _downloadSpeed, System.Math.Round(value, 1))) OnPropertyChanged(nameof(DownloadSpeedRatio)); } }

    private long _totalDataUsed;
    public  long TotalDataUsed
    {
        get => _totalDataUsed;
        set
        {
            if (SetProperty(ref _totalDataUsed, value))
                OnPropertyChanged(nameof(DataUsageTier));
        }
    }

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
    public void RefreshConnectionStats() => OnPropertyChanged(nameof(ConnectionStatsText));

    // ── Launch Count and Parent Process (#31, #32) ───────────────────────────
    private int _launchCount = 1;
    public int LaunchCount { get => _launchCount; set => SetProperty(ref _launchCount, value); }

    private string _parentProcessName = "Unknown";
    public string ParentProcessName { get => _parentProcessName; set => SetProperty(ref _parentProcessName, value); }

    // ── Sparkline history (#13) ───────────────────────────────────────────────
    // Rolling 30-point window of combined (upload+download) KB/s readings
    private readonly double[] _speedHistory = new double[30];
    private int _speedHistoryIdx;
    public System.Collections.Generic.IReadOnlyList<double> SpeedHistory => _speedHistory;

    public void PushSpeedSample(double combinedKbps)
    {
        _speedHistory[_speedHistoryIdx % 30] = combinedKbps;
        _speedHistoryIdx++;
        if (combinedKbps > 0)
        {
            LastActiveTime = System.DateTime.Now;
        }
        OnPropertyChanged(nameof(SpeedHistory));
        OnPropertyChanged(nameof(IsIdle));
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

    public ProcessNetworkInfo()
    {
        Connections.CollectionChanged += (s, e) => OnPropertyChanged(nameof(ConnectionCount));
    }
}

public partial class HttpRequestInfo : ObservableObject
{
    [ObservableProperty] private System.Guid   _id;
    [ObservableProperty] private string _url         = string.Empty;
    [ObservableProperty] private string _method      = string.Empty;
    [ObservableProperty] private string _host        = string.Empty;
    [ObservableProperty] private int    _processId;
    [ObservableProperty] private string _processName = string.Empty;
    [ObservableProperty] private System.DateTime _timestamp = System.DateTime.Now;
    [ObservableProperty] private int    _statusCode;
    [ObservableProperty] private string _contentType = string.Empty;
    [ObservableProperty] private long   _responseSize;

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
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private int    _interfaceIndex;

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


