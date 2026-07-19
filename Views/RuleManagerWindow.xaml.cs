using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using WinNetControl.Core;

namespace WinNetControl.Views;

public sealed partial class RuleManagerWindow : Window
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _scheduleTimer;

    public RuleManagerWindow()
    {
        this.InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;

        // Initialize timer for scheduled rules
        _scheduleTimer = this.DispatcherQueue.CreateTimer();
        _scheduleTimer.Interval = TimeSpan.FromMinutes(1);
        _scheduleTimer.Tick += OnScheduleTick;
    }

    private void OnTemplateChecked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb)
        {
            bool isChecked = cb.IsChecked == true;
            if (cb == BlockTelemetryCheck)
                HistoryLogService.AddLog("Rule Template", "System", isChecked ? "Blocked Windows Telemetry" : "Unblocked Windows Telemetry");
            else if (cb == BlockSocialMediaCheck)
                HistoryLogService.AddLog("Rule Template", "System", isChecked ? "Blocked Social Media" : "Unblocked Social Media");
            else if (cb == BlockAdsCheck)
                HistoryLogService.AddLog("Rule Template", "System", isChecked ? "Blocked Ads/Tracking" : "Unblocked Ads/Tracking");
        }
    }

    private void OnApplySchedule(object sender, RoutedEventArgs e)
    {
        _scheduleTimer.Start();
        ScheduleStatusText.Text = $"Schedule active: Block from {StartTimePicker.Time:hh\\:mm} to {EndTimePicker.Time:hh\\:mm}";
        HistoryLogService.AddLog("Scheduled Rule", "System", ScheduleStatusText.Text);
    }

    private void OnScheduleTick(object sender, object e)
    {
        var now = DateTime.Now.TimeOfDay;
        var start = StartTimePicker.Time;
        var end = EndTimePicker.Time;

        bool shouldBlock = false;
        if (start < end)
        {
            shouldBlock = now >= start && now <= end;
        }
        else // Wraps around midnight
        {
            shouldBlock = now >= start || now <= end;
        }

        if (shouldBlock)
        {
            // Apply block-all logic if not already applied
            // FirewallService.BlockAllTraffic(); // Placeholder
        }
        else
        {
            // Remove block-all logic if currently applied
            // FirewallService.UnblockAllTraffic(); // Placeholder
        }
    }
}
