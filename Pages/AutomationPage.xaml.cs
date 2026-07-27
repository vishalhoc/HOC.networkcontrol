using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinNetControl.Core;
using WinNetControl.ViewModels;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace WinNetControl.Pages;

// ── Scheduled rule model ──────────────────────────────────────────────────────
public class AutoRule
{
    public Guid   Id            { get; } = Guid.NewGuid();
    public string Label         { get; set; } = "";
    public string Action        { get; set; } = "";   // tag string
    public string Param         { get; set; } = "";
    public TimeSpan TriggerTime { get; set; }
    public string Repeat        { get; set; } = "daily"; // once|daily|weekdays|weekends
    public bool   IsEnabled     { get; set; } = true;
    public DateTime NextRun     { get; set; }

    public string ActionDisplay => Action switch
    {
        "flush_dns"  => "Flush DNS",
        "kill_on"    => "Kill Internet",
        "kill_off"   => "Restore Internet",
        "reset_net"  => "Reset Network",
        "block_app"  => $"Block {Param}",
        "unblock_app"=> $"Unblock {Param}",
        "dns_bench"  => "DNS Benchmark",
        "clear_log"  => "Clear Log",
        _            => Action
    };

    public string TimeDisplay   => TriggerTime.ToString(@"hh\:mm");
    public string RepeatDisplay => Repeat switch
    {
        "once"     => "Once",
        "daily"    => "Daily",
        "weekdays" => "Weekdays",
        "weekends" => "Weekends",
        _          => Repeat
    };
}

public sealed partial class AutomationPage : Page
{
    private MainViewModel? _vm;
    private readonly ObservableCollection<AutoRule> _rules = new();
    private Windows.System.Threading.ThreadPoolTimer? _ticker;
    private bool _rulesLoaded;

    public AutomationPage()
    {
        this.InitializeComponent();
        RulesList.ItemsSource = _rules;
        ActionCombo.SelectedIndex = 0;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is MainViewModel vm) _vm = vm;
        // Reload persisted rules once per session
        if (!_rulesLoaded)
        {
            _rulesLoaded = true;
            LoadRulesFromConfig();
        }
        UpdateRuleCount();
        StartTicker();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _ticker?.Cancel();
        _ticker = null;
    }

    // ── Ticker — checks every 30s ─────────────────────────────────────────────
    private void StartTicker()
    {
        _ticker = Windows.System.Threading.ThreadPoolTimer.CreatePeriodicTimer(
            _ => DispatcherQueue.TryEnqueue(CheckRules),
            TimeSpan.FromSeconds(30));
    }

    private void CheckRules()
    {
        var now = DateTime.Now;
        foreach (var rule in _rules.ToList())
        {
            if (!rule.IsEnabled) continue;
            if (now < rule.NextRun) continue;

            // Fire
            _ = ExecuteRuleAsync(rule);

            // Schedule next
            rule.NextRun = rule.Repeat switch
            {
                "once"     => DateTime.MaxValue,
                "weekdays" => NextWeekday(now.Date.Add(rule.TriggerTime)),
                "weekends" => NextWeekend(now.Date.Add(rule.TriggerTime)),
                _          => now.Date.AddDays(1).Add(rule.TriggerTime) // daily
            };
        }
    }

    // ── Execute a rule ────────────────────────────────────────────────────────
    private async Task ExecuteRuleAsync(AutoRule rule)
    {
        string msg = $"[{DateTime.Now:HH:mm:ss}] ▶ {rule.Label} ({rule.ActionDisplay})";
        Log(msg);
        HistoryLogService.AddLog("Automation", rule.Label, rule.ActionDisplay);

        switch (rule.Action)
        {
            case "flush_dns":
                await Task.Run(() => NetworkOptimizeService.FlushDns());
                Log("  → DNS flushed ✅"); break;

            case "kill_on":
                await Task.Run(() => NetworkOptimizeService.EnableKillSwitch());
                Log("  → Kill switch ON ✅"); break;

            case "kill_off":
                await Task.Run(() => NetworkOptimizeService.DisableKillSwitch());
                Log("  → Internet restored ✅"); break;

            case "reset_net":
                await Task.Run(() => NetworkOptimizeService.ResetAll());
                Log("  → Network reset ✅"); break;

            case "block_app":
                if (_vm != null)
                {
                    var proc = _vm.Processes.FirstOrDefault(p =>
                        p.ProcessName.Equals(rule.Param, StringComparison.OrdinalIgnoreCase));
                    if (proc != null && !string.IsNullOrEmpty(proc.ProcessPath))
                    {
                        await Task.Run(() => FirewallService.BlockApp(
                            proc.ProcessName, proc.ProcessPath, true, true));
                        Log($"  → {rule.Param} blocked ✅");
                    }
                    else Log($"  → {rule.Param} not found or path unknown — cannot create rule");
                }
                break;

            case "unblock_app":
                if (_vm != null)
                {
                    await Task.Run(() => FirewallService.UnblockApp(rule.Param));
                    Log($"  → {rule.Param} unblocked ✅");
                }
                break;

            case "dns_bench":
                Log("  → DNS benchmark: navigating to DNS page...");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (App.MainWindow is MainWindow mw) mw.NavigateTo("Dns");
                });
                break;

            case "clear_log":
                HistoryLogService.Clear();
                Log("  → History log cleared ✅"); break;
        }
    }

    // ── Add rule ──────────────────────────────────────────────────────────────
    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        string label  = RuleLabel.Text.Trim();
        string action = (ActionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        string param  = ActionParam.Text.Trim();
        string repeat = (RepeatCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "daily";
        bool   enabled = RuleEnabledToggle.IsOn;

        if (string.IsNullOrEmpty(label)) { AutoStatus.Text = "Enter a rule name."; return; }
        if (string.IsNullOrEmpty(action)) { AutoStatus.Text = "Select an action."; return; }

        var time    = TriggerTime.Time;
        var nextRun = CalculateNextRun(time, repeat);

        var rule = new AutoRule
        {
            Label       = label,
            Action      = action,
            Param       = param,
            TriggerTime = time,
            Repeat      = repeat,
            IsEnabled   = enabled,
            NextRun     = nextRun
        };

        _rules.Add(rule);
        SaveRulesToConfig();
        UpdateRuleCount();
        AutoStatus.Text = $"✅ Rule '{label}' added. Next run: {nextRun:HH:mm dd/MM}";
        RuleLabel.Text = ActionParam.Text = "";
    }

    private void OnDeleteRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Guid id)
        {
            var rule = _rules.FirstOrDefault(r => r.Id == id);
            if (rule != null) { _rules.Remove(rule); SaveRulesToConfig(); UpdateRuleCount(); }
        }
    }

    // ── Presets ───────────────────────────────────────────────────────────────
    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string tag = btn.Tag?.ToString() ?? "";
        switch (tag)
        {
            case "midnight_flush":
                _rules.Add(new AutoRule
                {
                    Label = "Flush DNS at Midnight", Action = "flush_dns",
                    TriggerTime = new TimeSpan(0, 0, 0), Repeat = "daily",
                    NextRun = CalculateNextRun(new TimeSpan(0,0,0), "daily")
                });
                break;
            case "night_kill":
                _rules.Add(new AutoRule
                {
                    Label = "Kill Internet 11PM", Action = "kill_on",
                    TriggerTime = new TimeSpan(23, 0, 0), Repeat = "daily",
                    NextRun = CalculateNextRun(new TimeSpan(23,0,0), "daily")
                });
                _rules.Add(new AutoRule
                {
                    Label = "Restore Internet 6AM", Action = "kill_off",
                    TriggerTime = new TimeSpan(6, 0, 0), Repeat = "daily",
                    NextRun = CalculateNextRun(new TimeSpan(6,0,0), "daily")
                });
                break;
            case "hourly_log":
                _rules.Add(new AutoRule
                {
                    Label = "Clear Log 24h", Action = "clear_log",
                    TriggerTime = new TimeSpan(0, 0, 0), Repeat = "daily",
                    NextRun = CalculateNextRun(new TimeSpan(0,0,0), "daily")
                });
                break;
        }
        SaveRulesToConfig();
        UpdateRuleCount();
        AutoStatus.Text = $"Preset '{tag}' added.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void UpdateRuleCount()
        => RuleCountText.Text = $"{_rules.Count} rule(s)";

    private void Log(string msg) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            string combined = $"{msg}\n{ExecLog.Text}";
            ExecLog.Text = combined.Length > 3000 ? combined[..3000] : combined;
        });

    private static DateTime CalculateNextRun(TimeSpan triggerTime, string repeat)
    {
        var now     = DateTime.Now;
        var today   = now.Date.Add(triggerTime);
        if (today > now) return today;

        return repeat switch
        {
            "once"     => today.AddDays(1),
            "weekdays" => NextWeekday(today.AddDays(1)),
            "weekends" => NextWeekend(today.AddDays(1)),
            _          => today.AddDays(1) // daily
        };
    }

    private static DateTime NextWeekday(DateTime from)
    {
        var d = from;
        while (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }

    private static DateTime NextWeekend(DateTime from)
    {
        var d = from;
        while (d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }

    // ── Rule persistence ──────────────────────────────────────────────────────
    private static string AutoRulesPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "WinNetControl_AutoRules.json");

    private void SaveRulesToConfig()
    {
        try
        {
            var snapshots = _rules.Select(r => new AutoRuleSnapshot
            {
                Label        = r.Label,
                Action       = r.Action,
                Param        = r.Param,
                TriggerHour  = r.TriggerTime.Hours,
                TriggerMin   = r.TriggerTime.Minutes,
                Repeat       = r.Repeat,
                IsEnabled    = r.IsEnabled,
                NextRunTicks = r.NextRun == DateTime.MaxValue ? 0L : r.NextRun.Ticks
            }).ToList();
            string json = System.Text.Json.JsonSerializer.Serialize(snapshots,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(AutoRulesPath, json);
        }
        catch { /* non-fatal */ }
    }

    private void LoadRulesFromConfig()
    {
        try
        {
            if (!System.IO.File.Exists(AutoRulesPath)) return;
            string json = System.IO.File.ReadAllText(AutoRulesPath);
            var snapshots = System.Text.Json.JsonSerializer.Deserialize<List<AutoRuleSnapshot>>(json);
            if (snapshots == null) return;
            _rules.Clear();
            foreach (var s in snapshots)
            {
                _rules.Add(new AutoRule
                {
                    Label       = s.Label,
                    Action      = s.Action,
                    Param       = s.Param,
                    TriggerTime = new TimeSpan(s.TriggerHour, s.TriggerMin, 0),
                    Repeat      = s.Repeat,
                    IsEnabled   = s.IsEnabled,
                    NextRun     = s.NextRunTicks > 0
                        ? new DateTime(s.NextRunTicks)
                        : CalculateNextRun(new TimeSpan(s.TriggerHour, s.TriggerMin, 0), s.Repeat)
                });
            }
        }
        catch { /* silently ignore corrupt file */ }
    }
}

/// <summary>Serialisation-only snapshot of an AutoRule (Guid not needed for storage).</summary>
public class AutoRuleSnapshot
{
    public string Label        { get; set; } = "";
    public string Action       { get; set; } = "";
    public string Param        { get; set; } = "";
    public int    TriggerHour  { get; set; }
    public int    TriggerMin   { get; set; }
    public string Repeat       { get; set; } = "daily";
    public bool   IsEnabled    { get; set; } = true;
    public long   NextRunTicks { get; set; }
}
