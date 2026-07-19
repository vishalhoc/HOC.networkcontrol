using System;
using System.Collections.ObjectModel;

namespace WinNetControl.Models
{
    public class HistoryLogEntry
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TimestampString => Timestamp.ToString("HH:mm:ss");
        public string EventType { get; set; } = "";
        public string AppName { get; set; } = "";
        public string Details { get; set; } = "";
    }
}

namespace WinNetControl.Core
{
    using WinNetControl.Models;

    public static class HistoryLogService
    {
        public static ObservableCollection<HistoryLogEntry> Logs { get; } = new();

        public static void AddLog(string type, string app, string details)
        {
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                Logs.Insert(0, new HistoryLogEntry
                {
                    EventType = type,
                    AppName = app,
                    Details = details
                });

                if (Logs.Count > 1000)
                {
                    Logs.RemoveAt(Logs.Count - 1);
                }
            });
        }

        public static void Clear()
        {
            Logs.Clear();
        }
    }
}
