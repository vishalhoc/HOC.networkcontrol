using Microsoft.UI.Xaml;
using WinNetControl.Core;

namespace WinNetControl.Views;

public sealed partial class HistoryLogWindow : Window
{
    public HistoryLogWindow()
    {
        this.InitializeComponent();
        this.ExtendsContentIntoTitleBar = true;
        
        LogList.ItemsSource = HistoryLogService.Logs;
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        HistoryLogService.Clear();
    }
}
