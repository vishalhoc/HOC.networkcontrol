using System.Collections.Generic;

namespace WinNetControl.Models;

public class BlockedConnectionRecord
{
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public string RemoteAddress { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public int LocalPort { get; set; }
    public bool BlockInbound { get; set; } = true;
    public bool BlockOutbound { get; set; } = true;
}

public class AppConfig
{
    // Per-app blocking (both directions together - legacy)
    public List<string> BlockedApps { get; set; } = new();

    // Per-direction app blocking
    public List<string> BlockedAppsInbound  { get; set; } = new();
    public List<string> BlockedAppsOutbound { get; set; } = new();

    // Pinned / HTTP capture
    public List<string> PinnedApps { get; set; } = new();
    public List<string> HttpCaptureApps { get; set; } = new();

    // Per-connection blocking records
    public List<BlockedConnectionRecord> BlockedConnections { get; set; } = new();

    // Filter & sort state
    public string SelectedFilter { get; set; } = "All";
    public string SelectedSort   { get; set; } = "Data Used (High-Low)";

    // Widget settings
    public double WidgetOpacity              { get; set; } = 85.0;
    public int    WidgetRefreshRateMs        { get; set; } = 1000;
    public bool   WidgetDisableTransparency  { get; set; } = false;
    public double WidgetFontSize             { get; set; } = 14.0;
    public double WidgetWidth                { get; set; } = 280;
    public double WidgetHeight               { get; set; } = 120;
    public string WidgetLayout               { get; set; } = "Vertical";

    // Proxy
    public bool EnableSystemProxy { get; set; } = false;

    // Startup & display
    public bool StartWithWindows        { get; set; } = false;
    public bool ShowOfflineBlockedApps  { get; set; } = true;

    // Global blocking mode — auto-block any newly detected process
    public bool BlockNewApps { get; set; } = false;

    // App theme: "System" | "Light" | "Dark"
    public string AppTheme { get; set; } = "System";

    // Appearance extras
    public bool EnableAcrylic    { get; set; } = false;
    public bool EnableAnimations { get; set; } = true;

    // Startup behaviour
    public bool StartMinimized { get; set; } = false;

    // Notifications
    public bool   NotifyOnBlock       { get; set; } = true;
    public bool   NotifyOnHighUsage   { get; set; } = false;
    public bool   NotifyOnQos         { get; set; } = false;
    public double BandwidthThresholdMBps { get; set; } = 10.0;

    // Per-process data limits in bytes (0 = no limit)
    public Dictionary<string, long> DataLimits { get; set; } = new();

    // Per-app user notes (#11)
    public Dictionary<string, string> AppNotes { get; set; } = new();

    // VirusTotal API key (free API from https://www.virustotal.com/gui/my-apikey)
    public string VirusTotalApiKey { get; set; } = string.Empty;

    // Window position / size persistence (UI Imp#29)
    // -1 means "not set" → let OS decide initial placement
    public double WindowX      { get; set; } = -1;
    public double WindowY      { get; set; } = -1;
    public double WindowWidth  { get; set; } = 1280;
    public double WindowHeight { get; set; } = 800;

    // Nav pane display mode (IMP#28)
    public string NavPaneMode { get; set; } = "left";
}
