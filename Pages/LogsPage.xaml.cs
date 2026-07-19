using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.UI;

namespace WinNetControl.Pages;

public class EventLogRow
{
    public string TimeStr  { get; set; } = "";
    public string Level    { get; set; } = "";
    public string Source   { get; set; } = "";
    public string Message  { get; set; } = "";
    public SolidColorBrush LevelBrush => Level switch
    {
        "Error"   => new SolidColorBrush(Color.FromArgb(255, 224, 32, 32)),
        "Warning" => new SolidColorBrush(Color.FromArgb(255, 251, 188, 5)),
        _         => new SolidColorBrush(Color.FromArgb(255, 16, 124, 16))
    };
}

public sealed partial class LogsPage : Page
{
    private readonly ObservableCollection<HistoryLogEntry> _appLogView  = new();
    private readonly ObservableCollection<EventLogRow>     _evtLogView  = new();

    public LogsPage()
    {
        this.InitializeComponent();
        AppLogList.ItemsSource  = _appLogView;
        EventLogList.ItemsSource = _evtLogView;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Mirror HistoryLogService.Logs
        HistoryLogService.Logs.CollectionChanged += OnLogsChanged;
        RefreshAppLog();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        HistoryLogService.Logs.CollectionChanged -= OnLogsChanged;
    }

    // ── App log ───────────────────────────────────────────────────────────────
    private void OnLogsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (LiveTailSwitch.IsOn)
            DispatcherQueue.TryEnqueue(RefreshAppLog);
    }

    private void RefreshAppLog()
    {
        if (LogSearchBox == null || EventTypeFilter == null || LogCountText == null) return;
        string q       = LogSearchBox.Text.Trim().ToLowerInvariant();
        string typeFilter = (EventTypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Types";

        _appLogView.Clear();
        foreach (var entry in HistoryLogService.Logs)
        {
            if (typeFilter != "All Types" &&
                !entry.EventType.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.Length > 0 &&
                !entry.AppName.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !entry.Details.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !entry.EventType.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            _appLogView.Add(entry);
        }
        LogCountText.Text = $"{_appLogView.Count} entries";
    }

    private void OnLogSearch(object sender, TextChangedEventArgs e) => RefreshAppLog();
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshAppLog();
    private void OnLiveTailToggled(object sender, RoutedEventArgs e) { if (LiveTailSwitch.IsOn) RefreshAppLog(); }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        HistoryLogService.Clear();
        RefreshAppLog();
    }

    private async void OnExportLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Type,App,Details");
            foreach (var entry in HistoryLogService.Logs)
                sb.AppendLine($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss},{entry.EventType},{entry.AppName},{entry.Details}");

            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"WNC_Log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            await File.WriteAllTextAsync(path, sb.ToString());
            LogCountText.Text = $"Exported to Desktop: {Path.GetFileName(path)}";
        }
        catch (Exception ex) { LogCountText.Text = $"Export error: {ex.Message}"; }
    }

    // ── Windows Event Log via PowerShell Get-WinEvent (no extra assembly needed) ──
    private async void OnLoadEventLog(object sender, RoutedEventArgs e)
    {
        _evtLogView.Clear();
        var rows = await Task.Run(() =>
        {
            var result = new List<EventLogRow>();

            // Query each log source with Get-WinEvent — portable across all Windows editions
            var sources = new[]
            {
                ("System",      "7036,7040"),     // service state changes
                ("Application", "1000,1001,1002") // app errors/crashes
            };

            foreach (var (logName, ids) in sources)
            {
                try
                {
                    string script =
                        $"-NoProfile -Command \"Get-WinEvent -LogName '{logName}' -MaxEvents 30 " +
                        $"| Select-Object TimeCreated,LevelDisplayName,ProviderName,Message " +
                        $"| ConvertTo-Json -Compress\"";

                    var psi = new ProcessStartInfo("powershell", script)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    };
                    using var proc = Process.Start(psi)!;
                    string json = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();

                    if (string.IsNullOrWhiteSpace(json)) continue;

                    // Lightweight JSON parse without System.Text.Json dependency on array root
                    // Normalise: wrap bare object in array
                    if (json.TrimStart().StartsWith("{")) json = $"[{json}]";

                    // Extract TimeCreated, LevelDisplayName, ProviderName, first line of Message
                    var timeMatches  = Regex.Matches(json, @"\""TimeCreated\"":\""([^\""]+)\""");
                    var levelMatches = Regex.Matches(json, @"\""LevelDisplayName\"":\""([^\""]+)\""");
                    var srcMatches   = Regex.Matches(json, @"\""ProviderName\"":\""([^\""]+)\""");
                    var msgMatches   = Regex.Matches(json, @"\""Message\"":\""([^\""]{0,200})\""");

                    int count = Math.Min(timeMatches.Count, Math.Min(levelMatches.Count,
                                Math.Min(srcMatches.Count, msgMatches.Count)));

                    for (int i = 0; i < count; i++)
                    {
                        string rawTime = timeMatches[i].Groups[1].Value;
                        // Parse ISO date from PowerShell JSON: "/Date(ticks)/" or ISO string
                        string timeStr = rawTime.Length >= 16 ? rawTime.Substring(0, 16).Replace("T", " ") : rawTime;

                        result.Add(new EventLogRow
                        {
                            TimeStr = timeStr,
                            Level   = levelMatches[i].Groups[1].Value,
                            Source  = srcMatches[i].Groups[1].Value,
                            Message = msgMatches[i].Groups[1].Value
                                .Replace("\\n", " ").Replace("\\r", "").Trim()
                        });
                    }
                }
                catch { /* log may be restricted on this edition */ }
            }

            return result.OrderByDescending(r => r.TimeStr).Take(100).ToList();
        });

        foreach (var r in rows) _evtLogView.Add(r);
    }
}
